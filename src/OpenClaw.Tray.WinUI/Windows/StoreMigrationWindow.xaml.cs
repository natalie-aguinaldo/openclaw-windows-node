using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using WinUIEx;

namespace OpenClawTray.Windows;

public sealed partial class StoreMigrationWindow : Window
{
    private readonly StoreMigrationWorkflow _workflow;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _allowClose;
    private StoreMigrationStage? _renderedStage;

    internal StoreMigrationWindow(StoreMigrationWorkflow workflow)
    {
        InitializeComponent();
        _workflow = workflow;
        Title = Heading.Text = TitleBarText.Text = LocalizationHelper.GetString("Migration2_Title");
        InstalledApps.Content = LocalizationHelper.GetString("Migration2_InstalledApps");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDrag);
        SystemBackdrop = new MicaBackdrop();
        this.SetWindowSize(720, 820);
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(
            Math.Min(AppWindow.Size.Width, workArea.Width), Math.Min(AppWindow.Size.Height, workArea.Height)));
        this.CenterOnScreen();
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        _workflow.Changed += Render;
        Root.Loaded += OnLoaded;
        Render();
    }

    public Task<bool> ShowAsync()
    {
        Activate();
        return _finished.Task;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= OnLoaded;
        AsyncEventHandlerGuard.Run(() => _workflow.StartAsync(_lifetime.Token), new AppLogger(), nameof(OnLoaded));
    }

    private void OnPrimary(object sender, RoutedEventArgs args) =>
        AsyncEventHandlerGuard.Run(() => _workflow.ContinueAsync(_lifetime.Token), new AppLogger(), nameof(OnPrimary));

    private void Render()
    {
        if (_workflow.Stage == StoreMigrationStage.Ready && !_workflow.IsBusy)
        {
            _allowClose = true;
            _finished.TrySetResult(true);
            Close();
            return;
        }

        Status.Text = LocalizationHelper.GetString("Migration2_" + (_workflow.Stage switch
        {
            StoreMigrationStage.Consent => "Consent",
            StoreMigrationStage.ClosingSource => "Closing",
            StoreMigrationStage.Preparing => "Preparing",
            StoreMigrationStage.Completing => "Completing",
            StoreMigrationStage.Finalizing => "Finalizing",
            StoreMigrationStage.CloseSource => "CloseInno",
            StoreMigrationStage.AwaitingRemoval => "AwaitingRemoval",
            StoreMigrationStage.ValidationFailed => "ValidationFailed",
            StoreMigrationStage.CredentialUnavailable => "CredentialUnavailable",
            StoreMigrationStage.UpdateRequired => "UpdateRequired",
            StoreMigrationStage.Unsupported => "Unsupported",
            StoreMigrationStage.Recovery => "Recovery",
            StoreMigrationStage.FinalizationFailed => "FinalizationFailed",
            StoreMigrationStage.InspectionFailed => "InspectionFailed",
            _ => "Preparing"
        }));
        Progress.IsActive = _workflow.IsBusy;
        Progress.Visibility = _workflow.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        Primary.IsEnabled = Dismiss.IsEnabled = !_workflow.IsBusy;
        Primary.Visibility = _workflow.Stage == StoreMigrationStage.Recovery
            ? Visibility.Collapsed : Visibility.Visible;
        Primary.Content = LocalizationHelper.GetString(_workflow.Stage == StoreMigrationStage.Consent
            ? "Migration_StoreMigrate" : "Migration_StoreRetry");
        Dismiss.Content = LocalizationHelper.GetString(_workflow.Stage == StoreMigrationStage.Consent
            ? "Migration_StoreNotNow" : "Migration2_Close");
        InstalledApps.Visibility = _workflow.Stage == StoreMigrationStage.AwaitingRemoval
            ? Visibility.Visible : Visibility.Collapsed;
        InstalledApps.IsEnabled = !_workflow.IsBusy;
        AutomationProperties.SetName(Progress, LocalizationHelper.GetString("Migration2_Busy"));
        if (_renderedStage != _workflow.Stage)
        {
            _renderedStage = _workflow.Stage;
            FrameworkElementAutomationPeer.FromElement(Status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        if (!_workflow.IsBusy)
        {
            // Consent defaults to the non-destructive action; keyboard activation cannot auto-confirm.
            (_workflow.Stage is StoreMigrationStage.Consent or StoreMigrationStage.Recovery ? Dismiss : Primary)
                .Focus(FocusState.Programmatic);
        }
    }

    private void OnInstalledApps(object sender, RoutedEventArgs args) =>
        AsyncEventHandlerGuard.Run(OpenInstalledAppsAsync, new AppLogger(), nameof(OnInstalledApps));

    private async Task OpenInstalledAppsAsync()
    {
        try
        {
            if (!await global::Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:appsfeatures")))
                throw new InvalidOperationException("Windows declined the Installed apps URI.");
        }
        catch (Exception exception)
        {
            Logger.Error($"Could not open Installed apps: {exception.Message}");
            Error.Message = LocalizationHelper.GetString("Migration2_LaunchFailed");
            Error.IsOpen = true;
        }
    }

    private void OnDismiss(object sender, RoutedEventArgs args) => Close();

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose && _workflow.IsBusy)
            args.Cancel = true;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _lifetime.Cancel();
        _workflow.Changed -= Render;
        _finished.TrySetResult(false);
        _lifetime.Dispose();
    }
}
