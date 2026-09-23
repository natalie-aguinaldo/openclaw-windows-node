using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenClaw.Connection.Migration;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class MigrationRecordTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("intent")]
    [InlineData("completed")]
    public void ProtectedRecord_RoundTripsWithoutPlaintext(string kind)
    {
        using var temp = new TempDirectory();
        var record = CreateRecord(temp, kind);
        var bytes = MigrationRecordCodec.Encode(record, Now);
        Assert.DoesNotContain(record.InventoryJson.Length > 0 ? record.InventoryJson : record.MigrationId,
            Encoding.UTF8.GetString(bytes));
        var decoded = MigrationRecordCodec.Decode(bytes, record.Binding, Now);
        Assert.Equal(record.MigrationId, decoded.MigrationId);
        Assert.Equal(record.Kind, decoded.Kind);
        Assert.Equal(record.InventoryJson, decoded.InventoryJson);
        Assert.Equal(record.Fingerprint, decoded.Fingerprint);
        Assert.Equal(record.AutoStart, decoded.AutoStart);
    }

    [Fact]
    public void Intent_ExpiresAtThirtyDays_ButCanBeReadForNewConsent()
    {
        using var temp = new TempDirectory();
        var record = CreateRecord(temp, "intent");
        var bytes = MigrationRecordCodec.Encode(record, Now);
        Assert.Equal("intent", MigrationRecordCodec.Decode(bytes, record.Binding, Now.AddDays(30).AddTicks(-1)).Kind);
        Assert.Throws<InvalidDataException>(() => MigrationRecordCodec.Decode(bytes, record.Binding, Now.AddDays(30)));
        Assert.Equal(record.MigrationId,
            MigrationRecordCodec.DecodeForRenewedConsent(bytes, record.Binding, Now.AddDays(31)).MigrationId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-3600)]
    [InlineData(-315360000)]
    [InlineData(315360000)]
    public void Completion_RemainsValidRegardlessOfClockChanges(int secondsAfterCompletion)
    {
        using var temp = new TempDirectory();
        var record = CreateRecord(temp, "completed");
        var bytes = MigrationRecordCodec.Encode(record, Now);
        var receiptPath = temp.Combine("completed.dpapi");
        File.WriteAllBytes(receiptPath, bytes);

        var decoded = MigrationRecordCodec.ReadCompletion(
            receiptPath, record.Binding, Now.AddSeconds(secondsAfterCompletion));

        Assert.Equal("completed", decoded.Kind);
        Assert.Equal(record.MigrationId, decoded.MigrationId);
        Assert.Equal(record.CreatedUtc, decoded.CreatedUtc);
        Assert.Equal(bytes, File.ReadAllBytes(receiptPath));
    }

    [Fact]
    public void Intent_StillRejectsFutureCreationAfterClockRollback()
    {
        using var temp = new TempDirectory();
        var record = CreateRecord(temp, "intent");
        var bytes = MigrationRecordCodec.Encode(record, Now);

        Assert.Throws<InvalidDataException>(() => MigrationRecordCodec.Decode(bytes, record.Binding, Now.AddSeconds(-1)));
        Assert.Throws<InvalidDataException>(() =>
            MigrationRecordCodec.DecodeForRenewedConsent(bytes, record.Binding, Now.AddSeconds(-1)));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("install")]
    [InlineData("roaming")]
    [InlineData("local")]
    [InlineData("architecture")]
    public void Receipt_IsBoundToUserAndInstallation(string field)
    {
        using var temp = new TempDirectory();
        var record = CreateRecord(temp, "completed");
        var bytes = MigrationRecordCodec.Encode(record, Now);
        switch (field)
        {
            case "user": record.Binding.UserSid = "S-1-5-18"; break;
            case "install": record.Binding.InstallDirectory += "-other"; break;
            case "roaming": record.Binding.RoamingDirectory += "-other"; break;
            case "local": record.Binding.LocalDirectory += "-other"; break;
            case "architecture": record.Binding.Architecture = "arm64"; break;
        }
        Assert.Throws<InvalidDataException>(() => MigrationRecordCodec.Decode(bytes, record.Binding, Now));
        Assert.Throws<InvalidDataException>(() => MigrationRecordCodec.Decode(bytes, record.Binding, Now.AddSeconds(-1)));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("id")]
    [InlineData("fingerprint")]
    [InlineData("version")]
    [InlineData("future")]
    [InlineData("expiry")]
    public void MalformedReceipt_CannotBeEncoded(string field)
    {
        using var temp = new TempDirectory();
        var record = CreateRecord(temp, "completed");
        switch (field)
        {
            case "kind": record.Kind = "importing"; break;
            case "id": record.MigrationId = ""; break;
            case "fingerprint": record.Fingerprint = new string('z', 64); break;
            case "version": record.TargetVersion = ""; break;
            case "future": record.CreatedUtc = Now.AddDays(1); break;
            case "expiry": record.ExpiresUtc = Now.AddDays(30); break;
        }
        Assert.Throws<InvalidDataException>(() => MigrationRecordCodec.Encode(record, Now));
    }

    [Fact]
    public void CorruptProtectedReceipt_IsRejected()
    {
        using var temp = new TempDirectory();
        var record = CreateRecord(temp, "completed");
        var bytes = MigrationRecordCodec.Encode(record, Now);
        bytes[^1] ^= 0x80;
        Assert.Throws<CryptographicException>(() => MigrationRecordCodec.Decode(bytes, record.Binding, Now));
    }

    [Fact]
    public void WrongPackageContract_IsRejected()
    {
        using var temp = new TempDirectory();
        var record = CreateRecord(temp, "completed");
        var entropy = Encoding.UTF8.GetBytes("OpenClaw.InnoToStore.Migration.v1");
        var plain = ProtectedData.Unprotect(MigrationRecordCodec.Encode(record, Now), entropy, DataProtectionScope.CurrentUser);
        var name = Encoding.UTF8.GetBytes(MigrationRecordCodec.PackageName);
        var index = plain.AsSpan().IndexOf(name);
        Assert.True(index >= 0);
        plain[index] = (byte)'X';
        var bytes = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
        Assert.Throws<InvalidDataException>(() => MigrationRecordCodec.Decode(bytes, record.Binding, Now));
    }

    [Fact]
    public void Intent_CannotAuthorizeUninstall()
    {
        using var temp = new TempDirectory();
        var record = CreateRecord(temp, "intent");
        var path = temp.Combine("completed.dpapi");
        File.WriteAllBytes(path, MigrationRecordCodec.Encode(record, Now));
        Assert.Throws<InvalidDataException>(() => MigrationRecordCodec.ReadCompletion(path, record.Binding, Now));
    }

    [Theory]
    [InlineData("completed", 10, false)]
    [InlineData("intent", 0, false)]
    [InlineData("missing", 0, false)]
    [InlineData("corrupt", 0, false)]
    [InlineData("wrong-path", 0, false)]
    [InlineData("missing-codec", 2, false)]
    [InlineData("completed", 10, true)]
    [InlineData("intent", 0, true)]
    [InlineData("corrupt", 0, true)]
    [InlineData("wrong-path", 0, true)]
    public async Task WindowsPowerShellChecker_UsesTheSameContract(string kind, int expectedExit, bool clockRollback)
    {
        using var temp = new TempDirectory();
        var root = RepositoryRoot();
        var record = CreateRecord(temp, kind == "intent" ? "intent" : "completed");
        Directory.CreateDirectory(record.Binding.InstallDirectory);
        if (kind != "missing-codec")
            File.Copy(Path.Combine(root, "src", "OpenClaw.Connection", "Migration", "MigrationRecordCodec.cs"),
                Path.Combine(record.Binding.InstallDirectory, "MigrationRecordCodec.cs"));
        // Encode at an injected later time to simulate rollback without changing Windows' clock.
        record.CreatedUtc = clockRollback ? DateTime.UtcNow.AddHours(1) : DateTime.UtcNow.AddMinutes(-1);
        if (kind == "intent")
            record.ExpiresUtc = record.CreatedUtc.AddDays(30);
        var directory = Path.Combine(record.Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        Directory.CreateDirectory(directory);
        if (kind != "missing")
        {
            var bytes = kind == "corrupt" ? new byte[] { 1, 2, 3 } : MigrationRecordCodec.Encode(record, record.CreatedUtc);
            File.WriteAllBytes(Path.Combine(directory, MigrationRecordCodec.CompletionFileName), bytes);
        }
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(root, "scripts", "Test-InnoMigration.ps1"),
            "-AppRoot", record.Binding.InstallDirectory,
            "-Architecture", kind == "wrong-path" ? "arm64" : "x64",
            "-RoamingDirectory", record.Binding.RoamingDirectory,
            "-LocalDirectory", record.Binding.LocalDirectory
        })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await AssertScriptExitAsync(process, expectedExit);
    }

    [Theory]
    [InlineData(null, 10, false)]
    [InlineData(FileShare.Read, 10, false)]
    [InlineData(FileShare.None, 1, false)]
    [InlineData(null, 10, true)]
    [InlineData(FileShare.Read, 10, true)]
    public async Task CleanupScript_CompletedReceiptPreservesFilesWithoutCallingWsl(
        FileShare? parentShare, int expectedExit, bool clockRollback)
    {
        using var temp = new TempDirectory();
        var root = RepositoryRoot();
        var record = CreateRecord(temp, "completed");
        record.CreatedUtc = clockRollback ? DateTime.UtcNow.AddHours(1) : DateTime.UtcNow.AddMinutes(-1);
        Directory.CreateDirectory(record.Binding.InstallDirectory);
        Directory.CreateDirectory(record.Binding.LocalDirectory);
        File.Copy(Path.Combine(root, "src", "OpenClaw.Connection", "Migration", "MigrationRecordCodec.cs"),
            Path.Combine(record.Binding.InstallDirectory, "MigrationRecordCodec.cs"));
        File.Copy(Path.Combine(root, "scripts", "Test-InnoMigration.ps1"),
            Path.Combine(record.Binding.InstallDirectory, "Test-InnoMigration.ps1"));
        var directory = Path.Combine(record.Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, MigrationRecordCodec.CompletionFileName),
            MigrationRecordCodec.Encode(record, record.CreatedUtc));
        using var parentLock = parentShare is { } share
            ? new FileStream(Path.Combine(directory, "prepare.lock"), FileMode.OpenOrCreate,
                share == FileShare.Read ? FileAccess.Read : FileAccess.ReadWrite, share)
            : null;
        var sentinel = Path.Combine(record.Binding.RoamingDirectory, "settings.json");
        File.WriteAllText(sentinel, "{\"testSentinel\":true}");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment["OPENCLAW_TRAY_DATA_DIR"] = record.Binding.RoamingDirectory;
        start.Environment["OPENCLAW_TRAY_LOCAL_DATA_DIR"] = record.Binding.LocalDirectory;
        start.Environment["OPENCLAW_STATE_DIR"] = temp.Combine("approvals");
        start.Environment.Remove("OPENCLAW_TRAY_LOCALAPPDATA_DIR");
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(root, "scripts", "Uninstall-LocalGateway.ps1"),
            "-AppRoot", record.Binding.InstallDirectory, "-Architecture", "x64",
            "-AutoStartName", "OpenClawMigrationTest-" + Guid.NewGuid().ToString("N"),
            "-StartupTaskName", "OpenClawMigrationTest-" + Guid.NewGuid().ToString("N"),
            // Never point an integration fixture at the user's real gateway, even on regression.
            "-DistroName", "OpenClawMigrationTest-" + Guid.NewGuid().ToString("N")
        })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await AssertScriptExitAsync(process, expectedExit,
            Path.Combine(record.Binding.InstallDirectory, "uninstall-gateway-wsl.log"));
        Assert.Equal("{\"testSentinel\":true}", File.ReadAllText(sentinel));
        Assert.DoesNotContain("Starting local gateway cleanup", File.ReadAllText(
            Path.Combine(record.Binding.InstallDirectory, "uninstall-gateway-wsl.log")));
        parentLock?.Dispose();
        using var releasedLock = new FileStream(Path.Combine(directory, "prepare.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptTimeout_TerminatesProcessTreeAndPreservesFailureDiagnostics(bool lockLog)
    {
        using var temp = new TempDirectory();
        var logPath = temp.Combine("cleanup.log");
        File.WriteAllText(logPath, "cleanup checkpoint");
        using var logLock = lockLog ? File.Open(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var readyName = @"Local\OpenClawMigrationTest-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment["OPENCLAW_TEST_READY"] = readyName;
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-Command",
            """
            $ErrorActionPreference = 'Stop'
            $child = Start-Process -FilePath "$PSHOME\powershell.exe" -NoNewWindow -PassThru `
                -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60'
            [Console]::Out.WriteLine([string]$child.Id)
            [Console]::Out.WriteLine('timeout stdout')
            [Console]::Error.WriteLine('timeout stderr')
            $ready = [Threading.EventWaitHandle]::OpenExisting($env:OPENCLAW_TEST_READY)
            $null = $ready.Set()
            $ready.Dispose()
            Start-Sleep -Seconds 60
            """
        })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Process? child = null;
        try
        {
            Assert.True(await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(30))),
                "Timeout fixture did not signal readiness.");
            var childId = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(childId);
            child = Process.GetProcessById(int.Parse(childId));
            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                AssertScriptExitAsync(process, 0, logPath, TimeSpan.FromMilliseconds(250)));

            Assert.True(process.HasExited);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            Assert.Contains("timeout stdout", error.Message);
            Assert.Contains("timeout stderr", error.Message);
            Assert.Contains(lockLog ? "Script log unavailable: IOException" : "cleanup checkpoint", error.Message);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                if (child is { HasExited: false })
                    child.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (child is not null)
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { child?.Dispose(); }
        }
    }

    private static async Task AssertScriptExitAsync(Process process, int expectedExit,
        string? diagnosticLog = null, TimeSpan? deadline = null)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var limit = deadline ?? TimeSpan.FromSeconds(30);
        using var timeout = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            // Stop cleanup before the caller releases its lock or deletes the fixture.
            var cleanup = "Root process exit confirmed.";
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or
                InvalidOperationException or TimeoutException)
            {
                cleanup = $"Process cleanup failed: {error.GetType().Name}: {error.Message}";
            }
            var output = await Task.WhenAll(
                ReadTimeoutOutputAsync(stdout), ReadTimeoutOutputAsync(stderr));
            var log = "(no script log was requested)";
            if (diagnosticLog is not null)
            {
                try { log = File.ReadAllText(diagnosticLog); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    log = $"Script log unavailable: {error.GetType().Name}: {error.Message}";
                }
            }
            throw new TimeoutException(
                $"Migration script PID {process.Id} exceeded {limit.TotalSeconds:g} seconds.\n" +
                $"{cleanup}\nStandard output:\n{output[0]}\nStandard error:\n{output[1]}\nScript log:\n{log}",
                exception);
        }
        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(process.ExitCode == expectedExit, $"{await stdout}\n{await stderr}\nExit: {process.ExitCode}");
    }

    private static async Task<string> ReadTimeoutOutputAsync(Task<string> output)
    {
        try { return await output.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is TimeoutException or IOException or ObjectDisposedException)
        {
            // A pipe read can fault later when the caller disposes the timed-out process.
            _ = output.ContinueWith(completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return $"Output unavailable: {error.GetType().Name}: {error.Message}";
        }
    }

    internal static MigrationRecord CreateRecord(TempDirectory temp, string kind)
    {
        return new MigrationRecord
        {
            Kind = kind,
            MigrationId = Guid.NewGuid().ToString("D"),
            SourceVersion = "2026.9.17.0",
            TargetVersion = kind == "completed" ? "2026.9.18.0" : "",
            Fingerprint = new string('a', 64),
            InventoryJson = kind == "intent" ? "{\"sensitiveInventory\":\"test-only\"}" : "",
            AutoStart = true,
            CreatedUtc = Now,
            ExpiresUtc = kind == "completed" ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) : Now.AddDays(30),
            Binding = new MigrationBinding
            {
                InstallDirectory = temp.Combine("install"),
                RoamingDirectory = temp.Combine("roaming"),
                LocalDirectory = temp.Combine("local"),
                Architecture = "x64",
                UserSid = WindowsIdentity.GetCurrent().User!.Value
            }
        };
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "installer.iss")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
