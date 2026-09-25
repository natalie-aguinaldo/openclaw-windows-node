using System.Runtime.Versioning;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Migration;

public enum StoreMigrationDiscardState
{
    Discarded,
    RecordsAreReadable,
    Busy,
    Failed
}

/// <summary>
/// The escape from <see cref="StoreMigrationStartupState.RecoveryRequired"/> when the records
/// themselves cannot be decoded. Without it a receipt that stops decoding, after a DPAPI key loss
/// or a profile move, blocks every launch with no in-app way back.
/// <para>
/// Only unreadable records are removed. A record that still decodes carries a real migration
/// decision, so it is never discarded; recovery for that case is to finish or undo the migration.
/// The status is re-read under the lock because admission ran earlier and may be stale.
/// </para>
/// <para>
/// Discarding the receipt does not expose user data. Nothing was ever moved, and while the Store
/// package stays registered <c>scripts/Test-InnoMigration.ps1</c> answers a missing receipt with
/// exit 11 and an undecodable one with exit 2. Both preserve generated state and the gateway.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StoreMigrationRecoveryDiscard(
    MigrationBinding binding,
    IMigrationStartupRecordReader records,
    IOpenClawLogger logger)
{
    public StoreMigrationDiscardState Discard()
    {
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        try
        {
            MigrationRecordCodec.RejectReparsePoints(directory);
            var lockPath = Path.Combine(directory, "prepare.lock");
            MigrationRecordCodec.RejectReparsePoints(lockPath);
            // Exclusive: uninstall reads these same records to decide whether to preserve data.
            using var discardLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            if (records.Read().Status != MigrationStartupRecordStatus.Invalid)
                return StoreMigrationDiscardState.RecordsAreReadable;

            // The receipt is deleted last so an interrupted discard still looks like a handoff
            // that needs recovery rather than one that never happened.
            foreach (var name in new[]
            {
                InnoMigrationConsentStore.WriterLockFileName,
                MigrationRecordCodec.ConsentFileName,
                MigrationRecordCodec.IntentFileName,
                MigrationRecordCodec.CompletionFileName
            })
            {
                var path = Path.Combine(directory, name);
                MigrationRecordCodec.RejectReparsePoints(path);
                File.Delete(path);
            }

            logger.Info("Discarded unreadable store migration records.");
            return StoreMigrationDiscardState.Discarded;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Nothing is left to discard, which is the outcome the user asked for.
            logger.Info($"No store migration records to discard ({exception.GetType().Name}).");
            return StoreMigrationDiscardState.Discarded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.Warn($"Store migration records are in use ({exception.GetType().Name}).");
            return StoreMigrationDiscardState.Busy;
        }
        catch (Exception exception)
        {
            logger.Error($"Could not discard store migration records: {exception}");
            return StoreMigrationDiscardState.Failed;
        }
    }
}
