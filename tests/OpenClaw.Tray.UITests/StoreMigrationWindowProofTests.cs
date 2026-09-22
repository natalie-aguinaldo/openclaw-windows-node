using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Mounted production-window proof with in-memory operations only, not migration/package proof.
/// Set OPENCLAW_VISUAL_TEST=1 and OPENCLAW_VISUAL_TEST_DIR to capture rendered states.
/// Never invokes Installed apps or constructs the production migration operations.
/// </summary>
[Collection(UICollection.Name)]
public sealed class StoreMigrationWindowProofTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Fact]
    public Task Consent_DefaultsToNotNow_AndDismissesWithoutMigration()
    {
        var operations = new Operations();
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.Consent);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "Consent", consent: true);
                var root = Root(window);
                Assert.Same(Control<Button>(window, "Dismiss"), FocusManager.GetFocusedElement(root.XamlRoot));
                Assert.Equal(new[] { "inspect", "consent?" }, operations.Calls);
            });
            await CaptureAsync(window, "Consent");
            await InvokeAsync(window, "Dismiss");
            Assert.False(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
            await ui.RunOnUIAsync(() =>
                Assert.Equal(new[] { "inspect", "consent?" }, operations.Calls));
        });
    }

    [Fact]
    public Task ExplicitConsent_BusyClose_ManualRetry_OffersRemovalOnlyAfterCompletion()
    {
        var operations = new Operations
        {
            CloseGate = NewGate<bool>(),
            CompleteGate = NewGate<StoreMigrationCompletionState>(),
        };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.Consent);
            await InvokeAsync(window, "Primary");
            await WaitForStageAsync(workflow, StoreMigrationStage.ClosingSource, busy: true);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "Closing", busy: true);
                Assert.Equal(new[] { "inspect", "consent?", "inspect", "grant", "close" }, operations.Calls);
                Assert.False(completion.IsCompleted);
            });
            await CaptureAsync(window, "ClosingSource");

            await ui.RunOnUIAsync(() => operations.CloseGate!.SetResult(false));
            await WaitForStageAsync(workflow, StoreMigrationStage.CloseSource);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "CloseInno");
                Assert.DoesNotContain("prepare", operations.Calls);
                Assert.DoesNotContain("complete", operations.Calls);
                Assert.Same(Control<Button>(window, "Primary"),
                    FocusManager.GetFocusedElement(Root(window).XamlRoot));
                operations.CloseGate = null;
            });
            await CaptureAsync(window, "CloseSource");

            await InvokeAsync(window, "Primary");
            await WaitForStageAsync(workflow, StoreMigrationStage.Completing, busy: true);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "Completing", busy: true);
                Assert.Equal(1, operations.Calls.Count(call => call == "grant"));
                Assert.Equal(2, operations.Calls.Count(call => call == "close"));
                Assert.Equal(1, operations.Calls.Count(call => call == "prepare"));
                Assert.Equal(1, operations.Calls.Count(call => call == "complete"));
                Assert.False(completion.IsCompleted);
                operations.CompleteGate!.SetResult(StoreMigrationCompletionState.Completed);
            });
            await WaitForStageAsync(workflow, StoreMigrationStage.AwaitingRemoval);
            await ui.RunOnUIAsync(() => AssertState(window, "AwaitingRemoval", removal: true));
            await CaptureAsync(window, "AwaitingRemoval");

            await ui.RunOnUIAsync(() =>
                operations.Admission = new(StoreMigrationStartupState.FinalizationRequired));
            await InvokeAsync(window, "Primary");
            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(StoreMigrationStage.Ready, workflow.Stage);
                Assert.Equal(1, operations.Calls.Count(call => call == "finalize"));
                Assert.Equal(1, operations.Calls.Count(call => call == "complete"));
            });
        });
    }

    [Fact]
    public Task FailedPreparation_ShowsRecoveryCopy_AndRetryCanComplete()
    {
        var operations = new Operations
        {
            Consent = true,
            Prepared = StoreMigrationPreparationState.ValidationFailed,
        };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.ValidationFailed);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "ValidationFailed");
                Assert.DoesNotContain("complete", operations.Calls);
                Assert.False(completion.IsCompleted);
            });
            await CaptureAsync(window, "ValidationFailed");
            await ui.RunOnUIAsync(() => operations.Prepared = StoreMigrationPreparationState.Prepared);
            await InvokeAsync(window, "Primary");
            await WaitForStageAsync(workflow, StoreMigrationStage.AwaitingRemoval);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "AwaitingRemoval", removal: true);
                Assert.Equal(2, operations.Calls.Count(call => call == "prepare"));
                Assert.Equal(1, operations.Calls.Count(call => call == "complete"));
                Assert.DoesNotContain("grant", operations.Calls);
                Assert.False(completion.IsCompleted);
            });
        });
    }

    [Fact]
    public Task FinalizationFailed_RetriesWithoutRepeatingAdoption()
    {
        var operations = new Operations
        {
            Admission = new(StoreMigrationStartupState.FinalizationRequired),
            Finalized = StoreMigrationFinalizationState.RecordCleanupFailed,
        };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.FinalizationFailed);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "FinalizationFailed");
                Assert.Equal(new[] { "inspect", "finalize" }, operations.Calls);
                Assert.False(completion.IsCompleted);
            });
            await CaptureAsync(window, "FinalizationFailed");
            await ui.RunOnUIAsync(() => operations.Finalized = StoreMigrationFinalizationState.Finalized);
            await InvokeAsync(window, "Primary");
            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
            await ui.RunOnUIAsync(() =>
                Assert.Equal(new[] { "inspect", "finalize", "inspect", "finalize" }, operations.Calls));
        });
    }

    [Theory]
    [InlineData(StoreMigrationStartupState.InspectionFailed, "InspectionFailed")]
    [InlineData(StoreMigrationStartupState.UnsupportedInstallation, "Unsupported")]
    [InlineData(StoreMigrationStartupState.UpdateInno, "UpdateRequired")]
    public Task BlockedAdmission_HasAccessibleRetry_WithoutRemovalOrMutation(
        StoreMigrationStartupState admission, string status)
    {
        var operations = new Operations { Admission = new(admission) };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitUntilAsync(() => !workflow.IsBusy && operations.Calls.Count == 1,
                "initial admission to render");
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, status);
                Assert.Equal(new[] { "inspect" }, operations.Calls);
                Assert.False(completion.IsCompleted);
            });
            await InvokeAsync(window, "Primary");
            await WaitUntilAsync(() => !workflow.IsBusy && operations.Calls.Count == 2,
                "retry to inspect admission again");
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, status);
                Assert.Equal(new[] { "inspect", "inspect" }, operations.Calls);
                Assert.False(completion.IsCompleted);
            });
        });
    }

    [Fact]
    public Task Recovery_OffersOnlyClose_WithoutRetryRemovalOrMutation()
    {
        var operations = new Operations { Admission = new(StoreMigrationStartupState.RecoveryRequired) };
        return WithWindowAsync(operations, async (window, workflow, completion) =>
        {
            await WaitForStageAsync(workflow, StoreMigrationStage.Recovery);
            await ui.RunOnUIAsync(() =>
            {
                AssertState(window, "Recovery", retry: false);
                Assert.Same(Control<Button>(window, "Dismiss"),
                    FocusManager.GetFocusedElement(Root(window).XamlRoot));
                Assert.Equal(new[] { "inspect" }, operations.Calls);
                Assert.False(completion.IsCompleted);
            });
            await CaptureAsync(window, "Recovery");
            await InvokeAsync(window, "Dismiss");
            Assert.False(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
            await ui.RunOnUIAsync(() => Assert.Equal(new[] { "inspect" }, operations.Calls));
        });
    }

    private async Task WithWindowAsync(
        Operations operations,
        Func<StoreMigrationWindow, StoreMigrationWorkflow, Task<bool>, Task> test)
    {
        StoreMigrationWindow? window = null;
        StoreMigrationWorkflow? workflow = null;
        Task<bool>? completion = null;
        var closed = false;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                workflow = new StoreMigrationWorkflow(operations, NullLogger.Instance);
                window = new StoreMigrationWindow(workflow);
                window.Closed += (_, _) => closed = true;
                completion = window.ShowAsync();
            });
            await test(window!, workflow!, completion!);
        }
        finally
        {
            if (window is not null)
            {
                // Release only fake work so the real busy-close guard cannot leak a window.
                await ui.RunOnUIAsync(() =>
                {
                    operations.CloseGate?.TrySetResult(false);
                    operations.CompleteGate?.TrySetResult(StoreMigrationCompletionState.Completed);
                });
                await WaitUntilAsync(() => !workflow!.IsBusy, "fake operations to settle for cleanup");
                await ui.RunOnUIAsync(() =>
                {
                    if (!closed)
                        window.Close();
                });
            }
        }
    }

    private Task WaitForStageAsync(StoreMigrationWorkflow workflow, StoreMigrationStage stage, bool busy = false) =>
        WaitUntilAsync(() => workflow.Stage == stage && workflow.IsBusy == busy, $"{stage}, busy={busy}");

    private async Task WaitUntilAsync(Func<bool> predicate, string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await ui.YieldToRenderAsync();
            if (await ui.RunOnUIAsync(() => Task.FromResult(predicate())))
                return;
            await Task.Delay(25);
        }
        Assert.Fail($"Timed out waiting for {description}.");
    }

    private Task InvokeAsync(StoreMigrationWindow window, string name) => ui.RunOnUIAsync(() =>
    {
        var button = Control<Button>(window, name);
        Assert.True(button.IsEnabled);
        Assert.Equal(Visibility.Visible, button.Visibility);
        var peer = new ButtonAutomationPeer(button);
        var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        invoke.Invoke();
    });

    private static void AssertState(
        StoreMigrationWindow window, string status, bool consent = false, bool busy = false,
        bool removal = false, bool retry = true)
    {
        var root = Root(window);
        root.UpdateLayout();
        Assert.NotNull(root.XamlRoot);
        Assert.True(root.ActualWidth > 0 && root.ActualHeight > 0);
        var statusText = Control<TextBlock>(window, "Status");
        Assert.Equal(Localized("Migration2_" + status), statusText.Text);
        Assert.True(statusText.IsTextSelectionEnabled);
        Assert.Equal("MigrationStatus", AutomationProperties.GetAutomationId(statusText));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(statusText));
        AssertButton(window, "Primary", "MigrationPrimary",
            consent ? "Migration_StoreMigrate" : "Migration_StoreRetry", enabled: !busy, visible: retry);
        AssertButton(window, "Dismiss", "MigrationDismiss",
            consent ? "Migration_StoreNotNow" : "Migration2_Close", enabled: !busy);
        AssertButton(window, "InstalledApps", "MigrationInstalledApps", "Migration2_InstalledApps",
            enabled: !busy, visible: removal);
        var progress = Control<ProgressRing>(window, "Progress");
        Assert.Equal(busy, progress.IsActive);
        Assert.Equal(busy ? Visibility.Visible : Visibility.Collapsed, progress.Visibility);
        Assert.Equal(Localized("Migration2_Busy"), AutomationProperties.GetName(progress));
        Assert.False(Assert.Single(TestSupport.FindLogical<InfoBar>(root)).IsOpen);
    }

    private static void AssertButton(
        StoreMigrationWindow window, string name, string automationId, string resourceKey,
        bool enabled, bool visible = true)
    {
        var button = Control<Button>(window, name);
        Assert.Equal(automationId, AutomationProperties.GetAutomationId(button));
        Assert.Equal(Localized(resourceKey), new ButtonAutomationPeer(button).GetName());
        Assert.Equal(enabled, button.IsEnabled);
        Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, button.Visibility);
        if (visible)
        {
            Assert.True(button.ActualWidth > 0 && button.ActualHeight > 0);
            var position = button.TransformToVisual(Root(window)).TransformPoint(new Windows.Foundation.Point());
            Assert.InRange(position.Y + button.ActualHeight, 1, Root(window).ActualHeight + 1);
        }
    }

    private static string Localized(string key)
    {
        var value = LocalizationHelper.GetString(key);
        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.NotEqual(key, value);
        return value;
    }

    private async Task CaptureAsync(StoreMigrationWindow window, string state)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") != "1")
            return;

        var directory = Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR");
        Assert.False(string.IsNullOrWhiteSpace(directory), "Screenshot proof requires OPENCLAW_VISUAL_TEST_DIR.");
        var surface = $"StoreMigrationWindow-FakeOperations-{state}-{Guid.NewGuid():N}";
        var surfaceDirectory = Path.Combine(Path.GetFullPath(directory!), surface);
        await ui.YieldToRenderAsync();
        await ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(Root(window), surface));
        Assert.True(Directory.Exists(surfaceDirectory), "The screenshot helper did not create the proof directory.");
        var screenshot = Assert.Single(Directory.GetFiles(surfaceDirectory, "*.png"));
        Assert.True(new FileInfo(screenshot).Length > 0, "The screenshot helper produced an empty artifact.");
        output.WriteLine($"UI-only proof, fake operations, not migration/package proof: {state}; screenshot={screenshot}");
    }

    private static FrameworkElement Root(StoreMigrationWindow window) =>
        Assert.IsAssignableFrom<FrameworkElement>(window.Content);

    private static T Control<T>(StoreMigrationWindow window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(Root(window).FindName(name));

    private static TaskCompletionSource<T> NewGate<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Operations : IStoreMigrationOperations
    {
        public List<string> Calls { get; } = [];
        public StoreMigrationStartupDecision Admission { get; set; } = new(
            StoreMigrationStartupState.ConsentRequired,
            new InnoInstallation(@"C:\ui-proof-only", @"C:\ui-proof-only\app.exe",
                @"C:\ui-proof-only\unins000.exe", "x64", new Version(2026, 9, 5)));
        public bool Consent { get; set; }
        public TaskCompletionSource<bool>? CloseGate { get; set; }
        public TaskCompletionSource<StoreMigrationCompletionState>? CompleteGate { get; set; }
        public StoreMigrationPreparationState Prepared { get; set; } = StoreMigrationPreparationState.Prepared;
        public StoreMigrationFinalizationState Finalized { get; set; } = StoreMigrationFinalizationState.Finalized;

        public StoreMigrationStartupDecision Inspect()
        {
            Calls.Add("inspect");
            return Admission;
        }

        public bool HasConsent(InnoInstallation installation)
        {
            Calls.Add("consent?");
            return Consent;
        }

        public void GrantConsent(InnoInstallation installation)
        {
            Calls.Add("grant");
            Consent = true;
        }

        public Task<bool> CloseSourceAsync(StoreMigrationStartupDecision admission, CancellationToken cancellationToken)
        {
            Calls.Add("close");
            return CloseGate?.Task ?? Task.FromResult(true);
        }

        public Task<StoreMigrationPreparationState> PrepareAsync(InnoInstallation installation)
        {
            Calls.Add("prepare");
            return Task.FromResult(Prepared);
        }

        public Task<StoreMigrationCompletionState> CompleteAsync(InnoInstallation installation)
        {
            Calls.Add("complete");
            return CompleteGate?.Task ?? Task.FromResult(StoreMigrationCompletionState.Completed);
        }

        public Task<StoreMigrationFinalizationDecision> FinalizeAsync()
        {
            Calls.Add("finalize");
            return Task.FromResult(new StoreMigrationFinalizationDecision(Finalized));
        }
    }
}
