using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace OpenClaw.Connection.Migration;

/// <summary>
/// Prepares a read-only state inventory after explicit migration consent.
/// This does not stop Inno, import data, or authorize migration-aware uninstall.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MigrationPreparation(MigrationBinding binding, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public MigrationRecord Prepare(string sourceVersion)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (binding.UserSid != identity.User?.Value)
            throw new InvalidOperationException("Migration preparation must use the current Windows user.");
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        MigrationRecordCodec.RejectReparsePoints(directory);
        CreateProtectedDirectory(directory);
        var lockPath = Path.Combine(directory, "prepare.lock");
        MigrationRecordCodec.RejectReparsePoints(lockPath);
        using var migrationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var completedPath = Path.Combine(directory, MigrationRecordCodec.CompletionFileName);
        // Even an invalid receipt needs explicit recovery, not an overwritten attempt.
        if (Path.Exists(completedPath))
            throw new InvalidOperationException("A completion receipt already exists. Resume or recover migration before preparing again.");

        var inventory = MigrationInventory.Capture(binding.RoamingDirectory, binding.LocalDirectory);
        var now = _clock.GetUtcNow().UtcDateTime;
        var intentPath = Path.Combine(directory, MigrationRecordCodec.IntentFileName);
        MigrationRecordCodec.RejectReparsePoints(intentPath);
        if (Path.Exists(intentPath))
        {
            using var stream = new FileStream(intentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MigrationRecordCodec.MaximumRecordBytes)
                throw new InvalidDataException("Invalid migration intent size.");
            using var reader = new BinaryReader(stream);
            var existing = MigrationRecordCodec.DecodeForRenewedConsent(reader.ReadBytes((int)stream.Length), binding, now);
            if (existing.Kind != "intent")
                throw new InvalidDataException("Expected a migration intent.");
            if (existing.ExpiresUtc > now && existing.Fingerprint == inventory.Fingerprint && existing.SourceVersion == sourceVersion)
                return existing;
        }

        var record = new MigrationRecord
        {
            Kind = "intent",
            MigrationId = Guid.NewGuid().ToString("D"),
            SourceVersion = sourceVersion,
            Binding = binding,
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(30),
            AutoStart = inventory.AutoStart,
            Fingerprint = inventory.Fingerprint,
            InventoryJson = JsonSerializer.Serialize(inventory)
        };
        var bytes = MigrationRecordCodec.Encode(record, now);
        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, intentPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        return record;
    }

    private static void CreateProtectedDirectory(string directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Cannot resolve the current Windows user.");
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
        {
            owner,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
            info.Create(security);
        else
            info.SetAccessControl(security);
    }
}
