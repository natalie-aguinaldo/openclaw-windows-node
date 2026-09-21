using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenClaw.Connection.Migration;
using OpenClawTray.Services;

namespace OpenClawTray.Helpers;

internal static class InnoMigrationStartupGuard
{
    public static bool ShouldStopLaunch()
    {
        if (AppIdentity.IsDev || PackageHelper.IsPackaged)
            return false;

        var binding = new MigrationBinding
        {
            InstallDirectory = AppContext.BaseDirectory,
            RoamingDirectory = AppIdentity.ResolveRoamingDataDirectory(),
            LocalDirectory = AppIdentity.ResolveSetupLocalDataDirectory(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            UserSid = WindowsIdentity.GetCurrent().User?.Value ?? ""
        };
        var path = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName,
            MigrationRecordCodec.CompletionFileName);
        string resourceKey;
        try
        {
            MigrationRecordCodec.ReadCompletion(path, binding, DateTime.UtcNow);
            Logger.Info("Completed Store migration blocks normal Inno startup.");
            resourceKey = "Migration_InnoCompleted";
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException or
                                   EndOfStreamException or ArgumentException or DecoderFallbackException)
        {
            Logger.Warn($"Migration completion receipt rejected ({ex.GetType().Name}).");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"Migration completion check unavailable ({ex.GetType().Name}); Inno startup blocked.");
            resourceKey = "Migration_InnoCheckFailed";
        }

        if (MessageBoxW(IntPtr.Zero, LocalizationHelper.GetString(resourceKey),
                AppIdentity.DisplayName, 0x00000040) == 0)
            Logger.Error($"Could not show migration startup guidance (Win32 {Marshal.GetLastWin32Error()}).");
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
