using OpenClaw.Connection.Migration;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class StoreMigrationWorkflowTests
{
    [Fact]
    public async Task WithoutExplicitConsent_DoesNotCloseOrMutateSource()
    {
        var operations = new Operations();
        var workflow = Create(operations);
        await workflow.StartAsync(CancellationToken.None);
        Assert.Equal(StoreMigrationStage.Consent, workflow.Stage);
        Assert.Equal(new[] { "inspect", "consent?" }, operations.Calls);
        Assert.False(workflow.IsBusy);
    }

    [Fact]
    public async Task ExplicitConfirmation_RecordsConsentBeforeGracefulShutdownAndCompletion()
    {
        var operations = new Operations();
        var workflow = Create(operations);
        await workflow.StartAsync(CancellationToken.None);
        operations.Calls.Clear();
        await workflow.ContinueAsync(CancellationToken.None);
        Assert.Equal(new[] { "inspect", "grant", "close", "prepare", "complete" }, operations.Calls);
        Assert.Equal(StoreMigrationStage.AwaitingRemoval, workflow.Stage);
        Assert.DoesNotContain("finalize", operations.Calls);
    }

    [Fact]
    public async Task ValidHandoff_SkipsSecondPromptButNotAdoptionValidation()
    {
        var operations = new Operations { Consent = true };
        var workflow = Create(operations);
        await workflow.StartAsync(CancellationToken.None);
        Assert.Equal(new[] { "inspect", "consent?", "close", "prepare", "complete" }, operations.Calls);
        Assert.Equal(StoreMigrationStage.AwaitingRemoval, workflow.Stage);
    }

    [Fact]
    public async Task ChangedSourceAfterPrompt_RequiresNewConfirmation()
    {
        var operations = new Operations();
        var workflow = Create(operations);
        await workflow.StartAsync(CancellationToken.None);
        operations.Admission = operations.Admission with
        {
            Installation = operations.Admission.Installation! with { Version = new Version(2026, 10, 1) }
        };
        operations.Calls.Clear();
        await workflow.ContinueAsync(CancellationToken.None);
        Assert.Equal(StoreMigrationStage.Consent, workflow.Stage);
        Assert.Equal(new[] { "inspect" }, operations.Calls);
    }

    [Fact]
    public async Task ShutdownUnavailable_ManualRetryReinspectsAndThenCompletes()
    {
        var operations = new Operations { Consent = true, Closed = false };
        var workflow = Create(operations);
        await workflow.StartAsync(CancellationToken.None);
        Assert.Equal(StoreMigrationStage.CloseSource, workflow.Stage);
        Assert.DoesNotContain("prepare", operations.Calls);
        operations.Closed = true;
        await workflow.ContinueAsync(CancellationToken.None);
        Assert.Equal(StoreMigrationStage.AwaitingRemoval, workflow.Stage);
        Assert.Equal(2, operations.Calls.Count(call => call == "inspect"));
    }

    [Theory]
    [InlineData(StoreMigrationPreparationState.InnoRunning, StoreMigrationStage.CloseSource)]
    [InlineData(StoreMigrationPreparationState.SourceChanged, StoreMigrationStage.Unsupported)]
    [InlineData(StoreMigrationPreparationState.ValidationFailed, StoreMigrationStage.ValidationFailed)]
    public async Task FailedPreparation_NeverCommits(StoreMigrationPreparationState result, object stage)
    {
        var operations = new Operations { Consent = true, Prepared = result };
        var workflow = Create(operations);
        await workflow.StartAsync(CancellationToken.None);
        Assert.Equal((StoreMigrationStage)stage, workflow.Stage);
        Assert.DoesNotContain("complete", operations.Calls);
    }

    [Theory]
    [InlineData(StoreMigrationCompletionState.NoActiveGateway, StoreMigrationStage.CredentialUnavailable)]
    [InlineData(StoreMigrationCompletionState.CredentialUnavailable, StoreMigrationStage.CredentialUnavailable)]
    [InlineData(StoreMigrationCompletionState.InnoRunning, StoreMigrationStage.CloseSource)]
    [InlineData(StoreMigrationCompletionState.SourceChanged, StoreMigrationStage.Unsupported)]
    public async Task FailedCompletion_DoesNotOfferRemoval(StoreMigrationCompletionState result, object stage)
    {
        var operations = new Operations { Consent = true, Completed = result };
        var workflow = Create(operations);
        await workflow.StartAsync(CancellationToken.None);
        Assert.Equal((StoreMigrationStage)stage, workflow.Stage);
    }

    [Fact]
    public async Task SourceRestartsBeforeCompletion_RetryReinspectsAndPreservesConsent()
    {
        var operations = new Operations { Consent = true, Completed = StoreMigrationCompletionState.InnoRunning };
        var workflow = Create(operations);
        await workflow.StartAsync(CancellationToken.None);
        Assert.Equal(StoreMigrationStage.CloseSource, workflow.Stage);
        operations.Completed = StoreMigrationCompletionState.Completed;
        operations.Calls.Clear();

        await workflow.ContinueAsync(CancellationToken.None);

        Assert.Equal(StoreMigrationStage.AwaitingRemoval, workflow.Stage);
        Assert.Equal(new[] { "inspect", "consent?", "close", "prepare", "complete" }, operations.Calls);
    }

    [Theory]
    [InlineData(StoreMigrationStartupState.UpdateInno, StoreMigrationStage.UpdateRequired)]
    [InlineData(StoreMigrationStartupState.UnsupportedInstallation, StoreMigrationStage.Unsupported)]
    [InlineData(StoreMigrationStartupState.InspectionFailed, StoreMigrationStage.InspectionFailed)]
    [InlineData(StoreMigrationStartupState.RecoveryRequired, StoreMigrationStage.Recovery)]
    [InlineData(StoreMigrationStartupState.AwaitingInnoRemoval, StoreMigrationStage.AwaitingRemoval)]
    public async Task BlockedAdmission_CannotBeOverriddenByConsent(
        StoreMigrationStartupState admission, object stage)
    {
        var operations = new Operations { Admission = new(admission), Consent = true };
        var workflow = Create(operations);
        await workflow.StartAsync(CancellationToken.None);
        Assert.Equal((StoreMigrationStage)stage, workflow.Stage);
        Assert.Equal(new[] { "inspect" }, operations.Calls);
    }

    [Theory]
    [InlineData(StoreMigrationStartupState.UpdateInno, false)]
    [InlineData(StoreMigrationStartupState.UnsupportedInstallation, false)]
    [InlineData(StoreMigrationStartupState.InspectionFailed, false)]
    [InlineData(StoreMigrationStartupState.RecoveryRequired, false)]
    [InlineData(StoreMigrationStartupState.AwaitingInnoRemoval, true)]
    public async Task InformationalAdmission_DoesNotKeepTheAppFromStarting(
        StoreMigrationStartupState admission, bool blocks)
    {
        var operations = new Operations { Admission = new(admission), Consent = true };
        var workflow = Create(operations);

        await workflow.StartAsync(CancellationToken.None);

        Assert.Equal(blocks, workflow.BlocksStartup);
    }

    [Fact]
    public async Task ReceiptWrittenThisSession_BlocksStartupEvenAfterAFailedStage()
    {
        var operations = new Operations
        {
            Admission = new(StoreMigrationStartupState.FinalizationRequired),
            Finalized = StoreMigrationFinalizationState.InspectionFailed
        };
        var workflow = Create(operations);

        await workflow.StartAsync(CancellationToken.None);

        // The stage reports an inspection failure, but the completion receipt still exists.
        Assert.Equal(StoreMigrationStage.InspectionFailed, workflow.Stage);
        Assert.True(workflow.BlocksStartup);
    }

    [Fact]
    public void BeforeAdmissionResolves_ClosingIsTreatedAsBlocking()
    {
        // Dismiss is reachable while the first inspection is still in flight. Nothing is known
        // yet, so closing must not wave the user past a handoff that already moved data.
        var workflow = Create(new Operations());

        Assert.True(workflow.BlocksStartup);
    }

    [Fact]
    public async Task ARetryThatNoLongerSeesTheReceipt_DoesNotReleaseTheBlock()
    {
        var operations = new Operations
        {
            Admission = new(StoreMigrationStartupState.FinalizationRequired),
            Finalized = StoreMigrationFinalizationState.InspectionFailed
        };
        var workflow = Create(operations);
        await workflow.StartAsync(CancellationToken.None);
        Assert.True(workflow.BlocksStartup);

        // Retry re-inspects. A transient failure there must not drop what we already know.
        operations.Admission = new(StoreMigrationStartupState.InspectionFailed);
        await workflow.ContinueAsync(CancellationToken.None);

        Assert.True(workflow.BlocksStartup);
    }

    [Fact]
    public async Task AdmissionCarryingAReceiptUnderAnInformationalState_Blocks()
    {
        var operations = new Operations
        {
            Admission = new(StoreMigrationStartupState.UnsupportedInstallation, null, true)
        };
        var workflow = Create(operations);

        await workflow.StartAsync(CancellationToken.None);

        Assert.Equal(StoreMigrationStage.Unsupported, workflow.Stage);
        Assert.True(workflow.BlocksStartup);
    }

    [Fact]
    public async Task SuccessfulFinalization_ReleasesTheStartupBlock()
    {
        var operations = new Operations
        {
            Admission = new(StoreMigrationStartupState.FinalizationRequired),
            Finalized = StoreMigrationFinalizationState.Finalized
        };
        var workflow = Create(operations);

        await workflow.StartAsync(CancellationToken.None);

        Assert.Equal(StoreMigrationStage.Ready, workflow.Stage);
        Assert.False(workflow.BlocksStartup);
    }

    [Fact]
    public async Task CompletionWrittenDuringMigration_BlocksStartup()
    {
        var operations = new Operations { Consent = true };
        var workflow = Create(operations);

        await workflow.StartAsync(CancellationToken.None);

        Assert.Equal(StoreMigrationStage.AwaitingRemoval, workflow.Stage);
        Assert.True(workflow.BlocksStartup);
    }

    [Fact]
    public async Task AbandonedConsent_LeavesStartupUnblocked()
    {
        var operations = new Operations();
        var workflow = Create(operations);

        await workflow.StartAsync(CancellationToken.None);

        Assert.Equal(StoreMigrationStage.Consent, workflow.Stage);
        Assert.False(workflow.BlocksStartup);
    }

    [Theory]
    [InlineData(StoreMigrationFinalizationState.AwaitingInnoRemoval, StoreMigrationStage.AwaitingRemoval)]
    // A transient startup-preference failure still finalizes the migration, so the app proceeds.
    // A durable refusal is the only one the user must be told about.
    [InlineData(StoreMigrationFinalizationState.StartupPreferenceFailed, StoreMigrationStage.Ready)]
    [InlineData(StoreMigrationFinalizationState.StartupPreferenceRefused, StoreMigrationStage.StartupRefused)]
    [InlineData(StoreMigrationFinalizationState.RecordCleanupFailed, StoreMigrationStage.FinalizationFailed)]
    [InlineData(StoreMigrationFinalizationState.InspectionFailed, StoreMigrationStage.InspectionFailed)]
    public async Task RemovalRetry_ResumesOnlyAfterSuccessfulFinalization(
        StoreMigrationFinalizationState result, object stage)
    {
        var operations = new Operations { Admission = new(StoreMigrationStartupState.AwaitingInnoRemoval), Finalized = result };
        var workflow = Create(operations);
        await workflow.StartAsync(CancellationToken.None);
        operations.Admission = new(StoreMigrationStartupState.FinalizationRequired);
        operations.Calls.Clear();
        await workflow.ContinueAsync(CancellationToken.None);
        Assert.Equal(new[] { "inspect", "finalize" }, operations.Calls);
        Assert.Equal((StoreMigrationStage)stage, workflow.Stage);
    }

    /// <summary>
    /// The refusal notice exists to be read, not to trap the user: Windows alone can re-enable the
    /// startup task, so the window must surface the stage while still letting the app launch.
    /// </summary>
    [Fact]
    public async Task DurableStartupRefusal_SurfacesTheNoticeWithoutBlockingStartup()
    {
        var operations = new Operations
        {
            Admission = new(StoreMigrationStartupState.FinalizationRequired),
            Finalized = StoreMigrationFinalizationState.StartupPreferenceRefused
        };
        var workflow = Create(operations);

        await workflow.StartAsync(CancellationToken.None);

        Assert.Equal(StoreMigrationStage.StartupRefused, workflow.Stage);
        Assert.False(workflow.BlocksStartup);
    }

    [Fact]
    public async Task InspectionFailure_IsVisibleAndNeverStartsNormally()
    {
        var workflow = Create(new Operations { InspectError = new IOException("locked") });
        await workflow.StartAsync(CancellationToken.None);
        Assert.Equal(StoreMigrationStage.InspectionFailed, workflow.Stage);
        Assert.False(workflow.IsBusy);
        Assert.False(workflow.BlocksStartup);
    }

    [Fact]
    public async Task InspectionFailure_KeepsBlockingWhenAReceiptIsOnDisk()
    {
        // Inspection threw before producing a decision, so the receipt was never observed.
        // Closing the window here would resume startup against data that already moved.
        var operations = new Operations { InspectError = new IOException("locked"), Receipt = true };
        var workflow = Create(operations);
        await workflow.StartAsync(CancellationToken.None);
        Assert.Equal(StoreMigrationStage.InspectionFailed, workflow.Stage);
        Assert.Contains("receipt?", operations.Calls);
        Assert.True(workflow.BlocksStartup);
    }

    [Fact]
    public async Task InspectionFailure_BlocksWhenTheReceiptCannotBeConfirmed()
    {
        var workflow = Create(new Operations
        {
            InspectError = new IOException("locked"),
            ReceiptError = new UnauthorizedAccessException("denied")
        });
        await workflow.StartAsync(CancellationToken.None);
        Assert.True(workflow.BlocksStartup);
    }

    [Fact]
    public async Task InspectionFailure_CannotDropAReceiptTheCallerAlreadyObserved()
    {
        // The startup guard inspects first; a failing second pass inside the workflow must not
        // discard what that first pass proved.
        var seed = new StoreMigrationStartupDecision(
            StoreMigrationStartupState.InspectionFailed, null, HoldsCompletionReceipt: true);
        var workflow = new StoreMigrationWorkflow(
            new Operations { InspectError = new IOException("locked") }, NullLogger.Instance, seed);
        await workflow.StartAsync(CancellationToken.None);
        Assert.True(workflow.BlocksStartup);
    }

    [Fact]
    public async Task CorruptConsent_IsPreservedAndCannotLoopThroughConfirmation()
    {
        var operations = new Operations { GrantError = new InvalidDataException("corrupt consent") };
        var workflow = Create(operations);
        await workflow.StartAsync(CancellationToken.None);
        await workflow.ContinueAsync(CancellationToken.None);
        Assert.Equal(StoreMigrationStage.Recovery, workflow.Stage);
        operations.Calls.Clear();
        await workflow.ContinueAsync(CancellationToken.None);
        Assert.Empty(operations.Calls);
        Assert.Equal(StoreMigrationStage.Recovery, workflow.Stage);
    }

    [Fact]
    public async Task ConcurrentClicks_DoNotDuplicatePreparation()
    {
        var pause = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new Operations { Consent = true, CloseTask = pause.Task };
        var workflow = Create(operations);
        var first = workflow.StartAsync(CancellationToken.None);
        await workflow.ContinueAsync(CancellationToken.None);
        Assert.Equal(1, operations.Calls.Count(call => call == "inspect"));
        pause.SetResult(true);
        await first;
        Assert.Equal(1, operations.Calls.Count(call => call == "complete"));
    }

    [Fact]
    public async Task CancelledBeforePreparation_DoesNotCommit()
    {
        using var cancellation = new CancellationTokenSource();
        var pause = new TaskCompletionSource<bool>();
        var operations = new Operations { Consent = true, CloseTask = pause.Task };
        var workflow = Create(operations);
        var work = workflow.StartAsync(cancellation.Token);
        cancellation.Cancel();
        pause.SetResult(true);
        await work;
        Assert.DoesNotContain("prepare", operations.Calls);
        Assert.NotEqual(StoreMigrationStage.Ready, workflow.Stage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9EXAMPLE00000&x=1")]
    [InlineData("https://example.com")]
    [InlineData("9example00000")]
    public void StoreListing_RejectsAbsentOrUntrustedIdentifiers(string? productId) =>
        Assert.False(StoreMigrationListing.TryCreateUri(productId, out _));

    [Fact]
    public void StoreListing_UsesOnlyExplicitProductId()
    {
        Assert.True(StoreMigrationListing.TryCreateUri("9EXAMPLE0000", out var uri));
        Assert.Equal("ms-windows-store://pdp/?ProductId=9EXAMPLE0000", uri!.AbsoluteUri);
    }

    private static StoreMigrationWorkflow Create(Operations operations) => new(operations, NullLogger.Instance);

    private sealed class Operations : IStoreMigrationOperations
    {
        public List<string> Calls { get; } = [];
        public StoreMigrationStartupDecision Admission { get; set; } = new(StoreMigrationStartupState.ConsentRequired,
            new InnoInstallation(@"C:\fixture", @"C:\fixture\app.exe", @"C:\fixture\unins000.exe", "x64", new Version(2026, 9, 5)));
        public bool Consent { get; set; }
        public bool Closed { get; set; } = true;
        public Task<bool>? CloseTask { get; set; }
        public Exception? InspectError { get; set; }
        public bool Receipt { get; set; }
        public Exception? ReceiptError { get; set; }
        public Exception? GrantError { get; set; }
        public StoreMigrationPreparationState Prepared { get; set; } = StoreMigrationPreparationState.Prepared;
        public StoreMigrationCompletionState Completed { get; set; } = StoreMigrationCompletionState.Completed;
        public StoreMigrationFinalizationState Finalized { get; set; } = StoreMigrationFinalizationState.Finalized;
        public StoreMigrationStartupDecision Inspect()
        {
            Calls.Add("inspect");
            if (InspectError is not null) throw InspectError;
            return Admission;
        }
        public bool HasConsent(InnoInstallation installation) { Calls.Add("consent?"); return Consent; }
        public bool HoldsCompletionReceipt()
        {
            Calls.Add("receipt?");
            if (ReceiptError is not null) throw ReceiptError;
            return Receipt;
        }
        public void GrantConsent(InnoInstallation installation)
        {
            Calls.Add("grant");
            if (GrantError is not null) throw GrantError;
            Consent = true;
        }
        public Task<bool> CloseSourceAsync(StoreMigrationStartupDecision admission, CancellationToken cancellationToken)
        { Calls.Add("close"); return CloseTask ?? Task.FromResult(Closed); }
        public Task<StoreMigrationPreparationState> PrepareAsync(InnoInstallation installation)
        { Calls.Add("prepare"); return Task.FromResult(Prepared); }
        public Task<StoreMigrationCompletionState> CompleteAsync(InnoInstallation installation)
        { Calls.Add("complete"); return Task.FromResult(Completed); }
        public Task<StoreMigrationFinalizationDecision> FinalizeAsync()
        { Calls.Add("finalize"); return Task.FromResult(new StoreMigrationFinalizationDecision(Finalized)); }
    }
}
