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

    [Fact]
    public void Completion_DoesNotExpireWhileAwaitingUninstall()
    {
        using var temp = new TempDirectory();
        var record = CreateRecord(temp, "completed");
        var bytes = MigrationRecordCodec.Encode(record, Now);
        Assert.Equal("completed", MigrationRecordCodec.Decode(bytes, record.Binding, Now.AddYears(10)).Kind);
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
    [InlineData("completed", 10)]
    [InlineData("intent", 0)]
    [InlineData("missing", 0)]
    [InlineData("corrupt", 0)]
    [InlineData("wrong-path", 0)]
    [InlineData("missing-codec", 2)]
    public async Task WindowsPowerShellChecker_UsesTheSameContract(string kind, int expectedExit)
    {
        using var temp = new TempDirectory();
        var root = RepositoryRoot();
        var record = CreateRecord(temp, kind == "intent" ? "intent" : "completed");
        Directory.CreateDirectory(record.Binding.InstallDirectory);
        if (kind != "missing-codec")
            File.Copy(Path.Combine(root, "src", "OpenClaw.Connection", "Migration", "MigrationRecordCodec.cs"),
                Path.Combine(record.Binding.InstallDirectory, "MigrationRecordCodec.cs"));
        record.CreatedUtc = DateTime.UtcNow.AddMinutes(-1);
        if (kind == "intent")
            record.ExpiresUtc = record.CreatedUtc.AddDays(30);
        var directory = Path.Combine(record.Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        Directory.CreateDirectory(directory);
        if (kind != "missing")
        {
            var bytes = kind == "corrupt" ? new byte[] { 1, 2, 3 } : MigrationRecordCodec.Encode(record, DateTime.UtcNow);
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
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(root, "scripts", "Test-InnoMigration.ps1"),
            "-AppRoot", record.Binding.InstallDirectory,
            "-Architecture", kind == "wrong-path" ? "arm64" : "x64",
            "-RoamingDirectory", record.Binding.RoamingDirectory,
            "-LocalDirectory", record.Binding.LocalDirectory
        })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == expectedExit, $"{await stdout}\n{await stderr}\nExit: {process.ExitCode}");
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(FileShare.Read, 10)]
    [InlineData(FileShare.None, 1)]
    public async Task CleanupScript_CompletedReceiptPreservesFilesWithoutCallingWsl(FileShare? parentShare, int expectedExit)
    {
        using var temp = new TempDirectory();
        var root = RepositoryRoot();
        var record = CreateRecord(temp, "completed");
        record.CreatedUtc = DateTime.UtcNow.AddMinutes(-1);
        Directory.CreateDirectory(record.Binding.InstallDirectory);
        Directory.CreateDirectory(record.Binding.LocalDirectory);
        File.Copy(Path.Combine(root, "src", "OpenClaw.Connection", "Migration", "MigrationRecordCodec.cs"),
            Path.Combine(record.Binding.InstallDirectory, "MigrationRecordCodec.cs"));
        File.Copy(Path.Combine(root, "scripts", "Test-InnoMigration.ps1"),
            Path.Combine(record.Binding.InstallDirectory, "Test-InnoMigration.ps1"));
        var directory = Path.Combine(record.Binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, MigrationRecordCodec.CompletionFileName),
            MigrationRecordCodec.Encode(record, DateTime.UtcNow));
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
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(root, "scripts", "Uninstall-LocalGateway.ps1"),
            "-AppRoot", record.Binding.InstallDirectory, "-Architecture", "x64",
            "-AutoStartName", "OpenClawMigrationTest-" + Guid.NewGuid().ToString("N"),
            "-StartupTaskName", "OpenClawMigrationTest-" + Guid.NewGuid().ToString("N"),
            // Never point an integration fixture at the user's real gateway, even on regression.
            "-DistroName", "OpenClawMigrationTest-" + Guid.NewGuid().ToString("N")
        })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == expectedExit, $"{await stdout}\n{await stderr}\nExit: {process.ExitCode}");
        Assert.Equal("{\"testSentinel\":true}", File.ReadAllText(sentinel));
        Assert.DoesNotContain("Starting local gateway cleanup", File.ReadAllText(
            Path.Combine(record.Binding.InstallDirectory, "uninstall-gateway-wsl.log")));
        parentLock?.Dispose();
        using var releasedLock = new FileStream(Path.Combine(directory, "prepare.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
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
