using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenClaw.Connection.Migration;

[SupportedOSPlatform("windows")]
internal static class MigrationRecordStorage
{
    internal static void CreateProtectedDirectory(string directory)
    {
        MigrationRecordCodec.RejectReparsePoints(directory);
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

    internal static void WriteAtomic(string path, byte[] bytes)
    {
        MigrationRecordCodec.RejectReparsePoints(path);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
