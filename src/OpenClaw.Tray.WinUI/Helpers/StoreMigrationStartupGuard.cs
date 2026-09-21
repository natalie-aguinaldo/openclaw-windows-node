using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using OpenClaw.Connection.Migration;
using OpenClawTray.Services;

namespace OpenClawTray.Helpers;

internal static class StoreMigrationStartupGuard
{
    public static bool ShouldStopLaunch()
    {
#if !STORE_MIGRATION_PREVIEW
        return false;
#else
        if (!PackageHelper.IsPackaged || AppIdentity.IsDev)
            return false;

        var identity = global::Windows.ApplicationModel.Package.Current.Id;
        if (identity.Name != MigrationRecordCodec.PackageName ||
            identity.Publisher != MigrationRecordCodec.PackagePublisher ||
            HasPathOverride())
        {
            Logger.Error("Store migration preview requires the production package identity and default data paths.");
            ShowGuidance("Migration_StoreUnsupported");
            return true;
        }

        var localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var binding = new MigrationBinding
        {
            InstallDirectory = Path.Combine(localRoot, AppIdentity.DataDirectoryName),
            RoamingDirectory = AppIdentity.ResolveRoamingDataDirectory(),
            LocalDirectory = AppIdentity.ResolveSetupLocalDataDirectory(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            UserSid = WindowsIdentity.GetCurrent().User?.Value ?? ""
        };
        var logger = new AppLogger();
        var minimum = typeof(StoreMigrationStartupGuard).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "StoreMigrationPreviewMinimumSourceVersion")?.Value;
        var coordinator = new StoreMigrationStartupCoordinator(
            new InnoInstallationDetector(localRoot, logger),
            new MigrationStartupRecordReader(binding, logger),
            logger);
        var decision = coordinator.Evaluate(true, minimum, binding.Architecture);
        if (decision.AllowsNormalStartup)
            return false;

        ShowGuidance(decision.State switch
        {
            StoreMigrationStartupState.ConsentRequired => "Migration_StorePreviewReady",
            StoreMigrationStartupState.UpdateInno => "Migration_StoreUpdateRequired",
            StoreMigrationStartupState.UnsupportedInstallation => "Migration_StoreUnsupported",
            StoreMigrationStartupState.InspectionFailed => "Migration_StoreInspectionFailed",
            _ => "Migration_StorePending"
        });
        return true;
#endif
    }

#if STORE_MIGRATION_PREVIEW
    private static bool HasPathOverride() =>
        new[]
        {
            "OPENCLAW_TRAY_DATA_DIR", "OPENCLAW_TRAY_APPDATA_DIR",
            "OPENCLAW_TRAY_LOCALAPPDATA_DIR", "OPENCLAW_TRAY_LOCAL_DATA_DIR"
        }.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));

    private static void ShowGuidance(string resourceKey)
    {
        if (MessageBoxW(IntPtr.Zero, LocalizationHelper.GetString(resourceKey),
                LocalizationHelper.GetString("Migration_StorePreviewTitle"), 0x00000040) == 0)
            Logger.Error($"Could not show Store migration preview guidance (Win32 {Marshal.GetLastWin32Error()}).");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
#endif
}
