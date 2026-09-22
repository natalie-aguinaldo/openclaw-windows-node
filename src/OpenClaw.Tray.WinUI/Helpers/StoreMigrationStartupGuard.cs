using Microsoft.UI.Xaml;
using OpenClaw.Connection.Migration;
using OpenClawTray.Services;
using OpenClawTray.Windows;

namespace OpenClawTray.Helpers;

internal static class StoreMigrationStartupGuard
{
    public static async Task<bool> ShouldStopLaunchAsync(string pipeName)
    {
#if !STORE_MIGRATION_PREVIEW
        return false;
#else
        if (!PackageHelper.IsPackaged || AppIdentity.IsDev)
            return false;

        var operations = new StoreMigrationOperations(pipeName);
        try
        {
            if (operations.Inspect().AllowsNormalStartup)
                return false;
        }
        catch (Exception exception)
        {
            Logger.Error($"Migration startup inspection failed: {exception}");
            // The workflow retries inspection and presents a blocking error.
        }

        // Bootstrap owns only migration UI. Closing it must not end the dispatcher
        // before a successful finalization resumes ordinary App composition.
        Application.Current.DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        var workflow = new StoreMigrationWorkflow(operations, new AppLogger());
        var window = new StoreMigrationWindow(workflow);
        return !await window.ShowAsync();
#endif
    }
}
