using System.Runtime.Versioning;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.Connection.Tests;

[SupportedOSPlatform("windows")]
[Collection("Migration preparation")]
public sealed class StoreMigrationAdoptionPreparationCoordinatorTests
{
    [Fact]
    public void BusyInno_DoesNotInspectOrPrepare()
    {
        using var temp = new TempDirectory();
        var coordinator = new StoreMigrationAdoptionPreparationCoordinator(
            new LeaseProvider(null),
            new Detector(() => throw new InvalidOperationException("Must not inspect while source is busy.")),
            CreatePreparation(temp, out _),
            NullLogger.Instance);

        var result = coordinator.Prepare(Expected());

        Assert.Equal(StoreMigrationPreparationState.InnoRunning, result.State);
    }

    [Fact]
    public void ChangedSource_StopsBeforePreparation()
    {
        using var temp = new TempDirectory();
        var coordinator = new StoreMigrationAdoptionPreparationCoordinator(
            new LeaseProvider(new Lease()),
            new Detector(() => new(InnoInstallationStatus.Detected, Expected() with { Architecture = "arm64" })),
            CreatePreparation(temp, out var binding),
            NullLogger.Instance);

        var result = coordinator.Prepare(Expected());

        Assert.Equal(StoreMigrationPreparationState.SourceChanged, result.State);
        Assert.False(File.Exists(Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName,
            MigrationRecordCodec.IntentFileName)));
    }

    [Fact]
    public void ValidatedSource_PreparesIntentOnlyWhileLeaseIsHeld()
    {
        using var temp = new TempDirectory();
        using var environment = new EnvironmentScope().Set("OPENCLAW_STATE_DIR", null).Set("OPENCLAW_HOME", null);
        var lease = new Lease();
        var expected = Expected();
        var preparation = CreatePreparation(temp, out var binding);
        File.WriteAllText(Path.Combine(binding.RoamingDirectory, "settings.json"), """{"AutoStart":true}""");
        var coordinator = new StoreMigrationAdoptionPreparationCoordinator(
            new LeaseProvider(lease),
            new Detector(() => new(InnoInstallationStatus.Detected, expected)),
            preparation,
            NullLogger.Instance);

        var result = coordinator.Prepare(expected);

        Assert.Equal(StoreMigrationPreparationState.Prepared, result.State);
        Assert.NotNull(result.Intent);
        Assert.True(lease.Disposed);
        var intent = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName,
            MigrationRecordCodec.IntentFileName);
        Assert.True(File.Exists(intent));
        Assert.False(File.Exists(Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName,
            MigrationRecordCodec.CompletionFileName)));
        Assert.Contains("\"AutoStart\":true", File.ReadAllText(Path.Combine(binding.RoamingDirectory, "settings.json")));
    }

    private static MigrationPreparation CreatePreparation(TempDirectory temp, out MigrationBinding binding)
    {
        binding = MigrationRecordTests.CreateRecord(temp, "intent").Binding;
        Directory.CreateDirectory(binding.RoamingDirectory);
        Directory.CreateDirectory(binding.LocalDirectory);
        return new MigrationPreparation(binding);
    }

    private static InnoInstallation Expected() =>
        new(@"C:\fixture\OpenClawTray", @"C:\fixture\OpenClawTray\OpenClaw.Tray.WinUI.exe",
            @"C:\fixture\OpenClawTray\unins000.exe", "x64", new Version(2026, 9, 5, 0));

    private sealed class Detector(Func<InnoInstallationDetection> detect) : IInnoInstallationDetector
    {
        public InnoInstallationDetection Detect() => detect();
    }

    private sealed class LeaseProvider(IMigrationSourceLease? lease) : IMigrationSourceLeaseProvider
    {
        public IMigrationSourceLease? TryAcquire() => lease;
    }

    private sealed class Lease : IMigrationSourceLease
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
