using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Migration;

public enum MigrationStartupRecordStatus
{
    None,
    Intent,
    Completed,
    Invalid,
    Unavailable
}

/// <summary>
/// <paramref name="CompletionPresent"/> reports that a completion receipt exists on disk even when
/// it could not be decoded. Data has already moved in that case, so admission must still protect
/// the handoff; <see cref="MigrationStartupRecordStatus.Invalid"/> alone cannot distinguish a
/// corrupt receipt from a corrupt intent.
/// </summary>
public sealed record MigrationStartupRecord(
    MigrationStartupRecordStatus Status,
    MigrationRecord? Record = null,
    bool CompletionPresent = false);

public interface IMigrationStartupRecordReader
{
    MigrationStartupRecord Read();
}

/// <summary>
/// Reads pending handoffs without preparing, renewing, or deleting any record.
/// An expired intent still requires an explicit migration decision, not fresh startup.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MigrationStartupRecordReader(
    MigrationBinding binding,
    IOpenClawLogger logger,
    TimeProvider? timeProvider = null) : IMigrationStartupRecordReader
{
    public MigrationStartupRecord Read()
    {
        try
        {
            var completed = ReadFile(MigrationRecordCodec.CompletionFileName, "completed");
            if (completed is not null)
                return new(MigrationStartupRecordStatus.Completed, completed, true);

            var intent = ReadFile(MigrationRecordCodec.IntentFileName, "intent");
            return intent is null
                ? new(MigrationStartupRecordStatus.None)
                : new(MigrationStartupRecordStatus.Intent, intent);
        }
        catch (MigrationPathRejectedException ex)
        {
            // A reparse point above the record directory is an environment shape, not a corrupt
            // record. Recovery cannot repair it, so report it as an inspection problem instead.
            logger.Error($"Store migration record path is unusable ({ex.Message}).");
            return new(MigrationStartupRecordStatus.Unavailable);
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or
                                   EndOfStreamException or ArgumentException or FormatException or
                                   DecoderFallbackException)
        {
            logger.Warn($"Store migration record requires recovery ({ex.GetType().Name}).");
            return new(MigrationStartupRecordStatus.Invalid, null, CompletionFileExists());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            logger.Error($"Store migration record inspection failed ({ex.GetType().Name}).");
            return new(MigrationStartupRecordStatus.Unavailable, null, CompletionFileExists());
        }
    }

    /// <summary>
    /// Existence only. A receipt that cannot be decoded still proves the handoff moved data, and
    /// this must never throw from inside a catch filter's handler.
    /// </summary>
    private bool CompletionFileExists()
    {
        try
        {
            return File.Exists(RecordPath(MigrationRecordCodec.CompletionFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or
                                   ArgumentException)
        {
            logger.Warn($"Could not confirm the migration receipt ({ex.GetType().Name}); assuming it exists.");
            return true;
        }
    }

    private string RecordPath(string fileName) =>
        Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName, fileName);

    private MigrationRecord? ReadFile(string fileName, string expectedKind)
    {
        var path = RecordPath(fileName);
        if (MigrationRecordCodec.HasReparsePointAncestor(path))
            throw new MigrationPathRejectedException("Migration paths must not contain reparse points.");
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        using (stream)
        {
            if (stream.Length == 0 || stream.Length > MigrationRecordCodec.MaximumRecordBytes)
                throw new InvalidDataException("Invalid migration startup record size.");
            using var reader = new BinaryReader(stream);
            var record = MigrationRecordCodec.DecodeForRenewedConsent(
                reader.ReadBytes((int)stream.Length), binding,
                (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
            if (record.Kind != expectedKind)
                throw new InvalidDataException("Unexpected migration startup record kind.");
            return record;
        }
    }
}
