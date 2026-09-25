using System.Runtime.Versioning;
using System.Security.Principal;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
public sealed class StoreMigrationRecoveryDiscardTests : IDisposable
{
    // The reader under test uses the system clock, so records must be dated against it.
    private static readonly DateTime Now = DateTime.UtcNow;
    private readonly TempDirectory _temp = new();
    private readonly MigrationBinding _binding;

    public StoreMigrationRecoveryDiscardTests()
    {
        _binding = new MigrationBinding
        {
            InstallDirectory = _temp.Combine("install"),
            RoamingDirectory = _temp.Combine("roaming"),
            LocalDirectory = _temp.Combine("local"),
            Architecture = "x64",
            UserSid = WindowsIdentity.GetCurrent().User!.Value
        };
    }

    private string Directory => Path.Combine(_binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);

    [Fact]
    public void UnreadableRecords_AreRemovedSoRecoveryCanBeCleared()
    {
        Seed();
        File.WriteAllBytes(Path.Combine(Directory, MigrationRecordCodec.CompletionFileName), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(Directory, MigrationRecordCodec.IntentFileName), [4, 5, 6]);
        File.WriteAllBytes(Path.Combine(Directory, MigrationRecordCodec.ConsentFileName), [7, 8, 9]);
        File.WriteAllBytes(Path.Combine(Directory, InnoMigrationConsentStore.WriterLockFileName), []);

        Assert.Equal(StoreMigrationDiscardState.Discarded, Discard());

        Assert.False(File.Exists(Path.Combine(Directory, MigrationRecordCodec.CompletionFileName)));
        Assert.False(File.Exists(Path.Combine(Directory, MigrationRecordCodec.IntentFileName)));
        Assert.False(File.Exists(Path.Combine(Directory, MigrationRecordCodec.ConsentFileName)));
        Assert.False(File.Exists(Path.Combine(Directory, InnoMigrationConsentStore.WriterLockFileName)));
        Assert.Equal(MigrationStartupRecordStatus.None, Read().Status);
    }

    [Fact]
    public void ADecodableReceipt_IsRefusedAndLeftIntact()
    {
        Seed();
        var path = WriteRecord("completed");
        var before = File.ReadAllBytes(path);

        Assert.Equal(StoreMigrationDiscardState.RecordsAreReadable, Discard());

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(MigrationStartupRecordStatus.Completed, Read().Status);
    }

    [Fact]
    public void ADecodableIntent_IsRefusedAndLeftIntact()
    {
        Seed();
        var path = WriteRecord("intent");
        var before = File.ReadAllBytes(path);

        Assert.Equal(StoreMigrationDiscardState.RecordsAreReadable, Discard());

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void RecordsHeldByAnotherOperation_AreNotDiscarded()
    {
        Seed();
        var completion = Path.Combine(Directory, MigrationRecordCodec.CompletionFileName);
        File.WriteAllBytes(completion, [1, 2, 3]);
        using var held = new FileStream(
            Path.Combine(Directory, "prepare.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(StoreMigrationDiscardState.Busy, Discard());

        Assert.True(File.Exists(completion));
    }

    [Fact]
    public void NoRecordDirectory_ReportsTheOutcomeTheUserAskedFor()
    {
        Assert.Equal(StoreMigrationDiscardState.Discarded, Discard());
        Assert.False(System.IO.Directory.Exists(Directory));
    }

    private void Seed()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(Path.Combine(Directory, "prepare.lock"), []);
    }

    private string WriteRecord(string kind)
    {
        var created = Now.AddMinutes(-1);
        var record = new MigrationRecord
        {
            Kind = kind,
            MigrationId = Guid.NewGuid().ToString("D"),
            SourceVersion = "2026.9.1",
            TargetVersion = kind == "completed" ? "2026.9.2" : "",
            CreatedUtc = created,
            ExpiresUtc = kind == "completed"
                ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
                : created.AddDays(30),
            Fingerprint = new string('a', 64),
            InventoryJson = kind == "intent" ? "{}" : "",
            Binding = _binding
        };
        var path = Path.Combine(Directory, kind == "intent"
            ? MigrationRecordCodec.IntentFileName : MigrationRecordCodec.CompletionFileName);
        File.WriteAllBytes(path, MigrationRecordCodec.Encode(record, created));
        return path;
    }

    private MigrationStartupRecord Read() =>
        new MigrationStartupRecordReader(_binding, NullLogger.Instance).Read();

    private StoreMigrationDiscardState Discard() =>
        new StoreMigrationRecoveryDiscard(
            _binding, new MigrationStartupRecordReader(_binding, NullLogger.Instance), NullLogger.Instance).Discard();

    public void Dispose() => _temp.Dispose();
}
