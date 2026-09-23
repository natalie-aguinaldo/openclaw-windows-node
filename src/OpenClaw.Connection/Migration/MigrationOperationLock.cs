using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenClaw.Connection.Migration;

/// <summary>
/// Coordinates same-user Inno runtime, uninstall, and Store writes across Windows sessions.
/// Runtime readers retain their handle until shutdown; Store writers require exclusive access.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MigrationOperationLock
{
    public static FileStream AcquireRuntime(MigrationBinding binding) => Open(binding, FileAccess.Read, FileShare.Read);

    internal static FileStream AcquireExclusive(MigrationBinding binding) => Open(binding, FileAccess.ReadWrite, FileShare.None);

    public static bool IsBusy(IOException exception) => (exception.HResult & 0xffff) is 32 or 33;

    private static FileStream Open(MigrationBinding binding, FileAccess access, FileShare share)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Cannot resolve the current Windows user.");
        if (binding.UserSid != owner.Value)
            throw new InvalidOperationException("Migration locking must use the current Windows user.");

        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        MigrationRecordCodec.RejectReparsePoints(directory);
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
        {
            if (RequiresHardening(info, owner))
                info.SetAccessControl(security);
            ScrubExistingEntries(info, owner);
        }

        var path = Path.Combine(directory, "prepare.lock");
        MigrationRecordCodec.RejectReparsePoints(path);
        return new FileStream(path, FileMode.OpenOrCreate, access, share);
    }

    /// <summary>
    /// Rewriting the DACL on every acquisition needs WRITE_DAC and WRITE_OWNER, which an ordinary
    /// launch can lack on a roaming or administratively hardened profile. Reading is cheap and
    /// always permitted to the owner, so only correct a directory that actually drifted.
    /// </summary>
    private static bool RequiresHardening(DirectoryInfo info, SecurityIdentifier owner)
    {
        try
        {
            var current = info.GetAccessControl();
            if (!current.AreAccessRulesProtected
                || current.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier actual
                || actual != owner)
                return true;

            return GrantsAnyForeignAccess(current, owner);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException
                                      or IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// A protected DACL with the expected owner can still carry an Allow rule for an unrelated
    /// principal. That principal could delete completed.dpapi, which the uninstall checker reads
    /// as an absent receipt and therefore as permission to destroy the local gateway. Treat any
    /// grant outside the three principals this type writes as drift worth correcting.
    /// </summary>
    private static bool GrantsAnyForeignAccess(FileSystemSecurity security, SecurityIdentifier owner)
    {
        var trusted = TrustedPrincipals(owner);
        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            if (rule.IdentityReference is SecurityIdentifier sid && Array.IndexOf(trusted, sid) >= 0)
                continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Hardening the directory rewrites inherited access only, so an explicit Allow rule placed
    /// directly on completed.dpapi or prepare.lock survives it. Deleting the receipt is the exact
    /// escalation this hardening exists to prevent, so the files carry the same guarantee.
    /// </summary>
    private static void ScrubExistingEntries(DirectoryInfo directory, SecurityIdentifier owner)
    {
        foreach (var file in directory.GetFiles())
        {
            FileSecurity current;
            try
            {
                current = file.GetAccessControl();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException
                                          or IOException)
            {
                current = null!;
            }

            if (current is not null
                && current.AreAccessRulesProtected
                && !GrantsAnyForeignAccess(current, owner))
                continue;

            var hardened = new FileSecurity();
            hardened.SetOwner(owner);
            hardened.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in TrustedPrincipals(owner))
            {
                hardened.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    AccessControlType.Allow));
            }

            file.SetAccessControl(hardened);
        }
    }

    private static SecurityIdentifier[] TrustedPrincipals(SecurityIdentifier owner) =>
    [
        owner,
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
    ];
}
