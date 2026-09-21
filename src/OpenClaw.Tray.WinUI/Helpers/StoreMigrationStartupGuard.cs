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

        if (decision.State == StoreMigrationStartupState.ConsentRequired)
        {
            RunConsentWorkflow(decision);
            return true;
        }

        ShowGuidance(decision.State switch
        {
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

    private static void RunConsentWorkflow(StoreMigrationStartupDecision admission)
    {
        var coordinator = new StoreMigrationConsentCoordinator(new InnoMutexProbe());
        var consent = coordinator.Begin(admission, ShowChoice(
            "Migration_StoreConsent", "Migration_StoreMigrate", "Migration_StoreNotNow"));

        while (consent.State == StoreMigrationConsentState.WaitingForInnoExit)
        {
            if (!ShowChoice("Migration_StoreCloseInno", "Migration_StoreRetry", "Migration_StoreNotNow"))
                return;

            consent = coordinator.Retry();
        }

        if (consent.State == StoreMigrationConsentState.ReadyForAdoption)
            ShowGuidance("Migration_StoreAdoptionPending");
    }

    private static bool ShowChoice(string contentKey, string primaryKey, string secondaryKey)
    {
        var content = $"{LocalizationHelper.GetString(contentKey)}\r\n\r\n" +
            $"{LocalizationHelper.GetString("Migration_StoreYes")} {LocalizationHelper.GetString(primaryKey)}\r\n" +
            $"{LocalizationHelper.GetString("Migration_StoreNo")} {LocalizationHelper.GetString(secondaryKey)}";
        return MessageBoxW(IntPtr.Zero, content, LocalizationHelper.GetString("Migration_StorePreviewTitle"),
            0x00000124) == 6;
    }

    private static void ShowGuidance(string resourceKey)
    {
        if (MessageBoxW(IntPtr.Zero, LocalizationHelper.GetString(resourceKey),
                LocalizationHelper.GetString("Migration_StorePreviewTitle"), 0x00000040) == 0)
            Logger.Error($"Could not show Store migration preview guidance (Win32 {Marshal.GetLastWin32Error()}).");
    }

    private sealed class InnoMutexProbe : IInnoInstanceProbe
    {
        public bool IsRunning()
        {
            try
            {
                using var mutex = Mutex.OpenExisting(AppIdentity.MutexBaseName);
                if (!mutex.WaitOne(0))
                    return true;
                mutex.ReleaseMutex();
                return false;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (AbandonedMutexException)
            {
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                Logger.Error($"Could not inspect the Inno instance mutex: {exception.Message}");
                return true;
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
#endif
}
