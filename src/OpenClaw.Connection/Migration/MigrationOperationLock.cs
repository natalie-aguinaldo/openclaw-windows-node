using System.Runtime.Versioning;
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

    internal static bool IsBusy(IOException exception) => (exception.HResult & 0xffff) is 32 or 33;

    private static FileStream Open(MigrationBinding binding, FileAccess access, FileShare share)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Cannot resolve the current Windows user.");
        if (binding.UserSid != owner.Value)
            throw new InvalidOperationException("Migration locking must use the current Windows user.");

        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        MigrationRecordStorage.CreateProtectedDirectory(directory);

        var path = Path.Combine(directory, "prepare.lock");
        MigrationRecordCodec.RejectReparsePoints(path);
        return new FileStream(path, FileMode.OpenOrCreate, access, share);
    }
}
