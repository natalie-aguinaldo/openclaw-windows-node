using OpenClaw.Shared;
using System.Runtime.Versioning;

namespace OpenClaw.Connection.Migration;

public enum StoreMigrationPreparationState
{
    InnoRunning,
    SourceChanged,
    ValidationFailed,
    Prepared
}

public sealed record StoreMigrationPreparationDecision(
    StoreMigrationPreparationState State,
    MigrationRecord? Intent = null);

public interface IMigrationSourceLease : IDisposable;

public interface IMigrationSourceLeaseProvider
{
    IMigrationSourceLease? TryAcquire();
}

/// <summary>
/// Acquires exclusive source ownership, rechecks exact installation evidence,
/// and prepares a protected inventory. It never adopts, deletes, or completes state.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StoreMigrationAdoptionPreparationCoordinator(
    IMigrationSourceLeaseProvider leaseProvider,
    IInnoInstallationDetector detector,
    MigrationPreparation preparation,
    IOpenClawLogger logger)
{
    public StoreMigrationPreparationDecision Prepare(InnoInstallation expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        using var lease = leaseProvider.TryAcquire();
        if (lease is null)
            return new(StoreMigrationPreparationState.InnoRunning);

        var detected = detector.Detect();
        if (detected.Status != InnoInstallationStatus.Detected || detected.Installation != expected)
            return new(StoreMigrationPreparationState.SourceChanged);

        try
        {
            var intent = preparation.Prepare(expected.Version.ToString());
            logger.Info($"Store migration preparation completed: {intent.MigrationId}.");
            return new(StoreMigrationPreparationState.Prepared, intent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            logger.Error($"Store migration preparation failed: {exception.Message}");
            return new(StoreMigrationPreparationState.ValidationFailed);
        }
    }
}
