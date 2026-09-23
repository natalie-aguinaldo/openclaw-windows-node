using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using OpenClaw.Connection.Migration;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class MigrationOperationLockTests
{
    [Fact]
    public void FirstRuntime_CreatesProtectedLockWithoutMigrationRecords()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        using var runtime = MigrationOperationLock.AcquireRuntime(binding);
        using var secondRuntime = MigrationOperationLock.AcquireRuntime(binding);
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);

        Assert.Single(Directory.GetFiles(directory));
        Assert.True(new DirectoryInfo(directory).GetAccessControl().AreAccessRulesProtected);
        Assert.Equal(binding.UserSid, new DirectoryInfo(directory).GetAccessControl()
            .GetOwner(typeof(SecurityIdentifier))!.Value);
        Assert.Throws<IOException>(() => MigrationOperationLock.AcquireExclusive(binding));
    }

    [Fact]
    public void ExclusiveOwner_BlocksRuntimeWithoutChangingRecords()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        using (MigrationOperationLock.AcquireExclusive(binding))
            Assert.Throws<IOException>(() => MigrationOperationLock.AcquireRuntime(binding));

        using var released = MigrationOperationLock.AcquireRuntime(binding);
    }

    [Fact]
    public void WrongUserBinding_IsRejectedBeforeCreatingDirectory()
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        binding.UserSid = "S-1-5-18";
        Assert.Throws<InvalidOperationException>(() => MigrationOperationLock.AcquireRuntime(binding));
        Assert.False(Directory.Exists(binding.RoamingDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeInAnotherProcess_ExcludesStoreUntilNormalExitOrCrash(bool crash)
    {
        using var temp = new TempDirectory();
        var binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        using (MigrationOperationLock.AcquireRuntime(binding)) { }
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment["OPENCLAW_TEST_LOCK"] =
            Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName, "prepare.lock");
        const string script = """
            $ErrorActionPreference = 'Stop'
            $lease = [IO.File]::Open($env:OPENCLAW_TEST_LOCK, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            [Console]::Out.WriteLine('ready')
            [Console]::Out.Flush()
            [Console]::ReadLine() | Out-Null
            $lease.Dispose()
            """;
        foreach (var argument in new[] { "-NoProfile", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);

        using var child = Process.Start(start)!;
        var errors = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            Assert.Equal("ready", await child.StandardOutput.ReadLineAsync(timeout.Token));
            Assert.False(child.HasExited);
            using (MigrationOperationLock.AcquireRuntime(binding))
            {
                Assert.Throws<IOException>(() => MigrationOperationLock.AcquireExclusive(binding));
                // Independent uninstall readers coexist with a running source.
                using var uninstall = File.Open(start.Environment["OPENCLAW_TEST_LOCK"]!,
                    FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            Assert.Throws<IOException>(() => MigrationOperationLock.AcquireExclusive(binding));
            if (crash)
                child.Kill();
            else
                await child.StandardInput.WriteLineAsync("exit");
            await child.WaitForExitAsync(timeout.Token);
            if (!crash)
                Assert.True(child.ExitCode == 0, await errors);
            using var exclusive = await AcquireAfterExitAsync(binding);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync();
            }
        }
    }

    private static async Task<FileStream> AcquireAfterExitAsync(MigrationBinding binding)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return MigrationOperationLock.AcquireExclusive(binding);
            }
            catch (IOException exception) when (MigrationOperationLock.IsBusy(exception) &&
                                               elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                // Wait for kernel handle cleanup after forcible process termination.
                await Task.Delay(25);
            }
        }
    }
}
