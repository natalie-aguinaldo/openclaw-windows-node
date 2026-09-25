using OpenClaw.Connection.Migration;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal enum StoreMigrationStage
{
    Inspecting, Consent, ClosingSource, Preparing, Completing, Finalizing,
    CloseSource, AwaitingRemoval, ValidationFailed,
    UpdateRequired, Unsupported, InspectionFailed, Recovery, FinalizationFailed,
    StartupRefused, Ready
}

internal interface IStoreMigrationOperations
{
    StoreMigrationStartupDecision Inspect();
    bool HasConsent(InnoInstallation installation);
    void GrantConsent(InnoInstallation installation);
    Task<bool> CloseSourceAsync(StoreMigrationStartupDecision admission, CancellationToken cancellationToken);
    Task<StoreMigrationPreparationState> PrepareAsync(InnoInstallation installation);
    Task<StoreMigrationCompletionState> CompleteAsync(InnoInstallation installation);
    Task<StoreMigrationFinalizationDecision> FinalizeAsync();

    /// <summary>
    /// Whether the records are present but undecodable, which is the only recovery cause the user
    /// can clear from inside the app. Must not throw: it runs while recovery is already showing.
    /// </summary>
    bool RecordsAreUnreadable();

    /// <summary>Removes undecodable records. See <see cref="StoreMigrationRecoveryDiscard"/>.</summary>
    StoreMigrationDiscardState DiscardUnreadableRecords();

    /// <summary>
    /// Receipt presence only, for when <see cref="Inspect"/> itself fails and never produces a
    /// decision. Must answer without requiring a successful inspection.
    /// </summary>
    bool HoldsCompletionReceipt();
}

/// <summary>Serializes UI actions without replacing the admission, adoption, or finalization owners.</summary>
internal sealed class StoreMigrationWorkflow(
    IStoreMigrationOperations operations,
    IOpenClawLogger logger,
    StoreMigrationStartupDecision? initialAdmission = null)
{
    public StoreMigrationStage Stage { get; private set; } = StoreMigrationStage.Inspecting;
    public bool IsBusy { get; private set; }

    /// <summary>
    /// Whether recovery can be cleared from inside the app. Only undecodable records qualify:
    /// every other recovery cause is repaired outside the window, by removing the previous app.
    /// </summary>
    public bool CanDiscardRecords { get; private set; }

    /// <summary>
    /// Closing the window abandons migration, so only a handoff whose completion receipt already
    /// exists may keep the app from starting. This tracks
    /// <see cref="StoreMigrationStartupDecision.BlocksStartup"/> rather than the visible stage,
    /// because a receipt can survive a stage that later reports an inspection failure. Every
    /// other state has nothing to protect, and refusing to launch would leave the user stuck.
    /// <para>
    /// Before the first admission resolves, nothing is known yet, so closing is treated as
    /// blocking. Otherwise a close raced against startup inspection would wave the user through
    /// a handoff that had already moved data.
    /// </para>
    /// <para>
    /// <see cref="StoreMigrationStage.StartupRefused"/> is excluded alongside
    /// <see cref="StoreMigrationStage.Ready"/>: finalization succeeded and cleared the receipt,
    /// so the window is only reporting that Windows refused the startup task. Blocking there
    /// would withhold an app whose migration is complete.
    /// </para>
    /// </summary>
    public bool BlocksStartup =>
        (!_admissionResolved || _holdsCompletedHandoff) &&
        Stage is not (StoreMigrationStage.Ready or StoreMigrationStage.StartupRefused);
    public event Action? Changed;
    private InnoInstallation? _promptInstallation;
    // Seeded from the caller's admission so a receipt observed before this workflow existed is
    // not lost when a later inspection pass fails.
    private bool _holdsCompletedHandoff = initialAdmission?.BlocksStartup ?? false;
    private bool _admissionResolved;

    public Task StartAsync(CancellationToken cancellationToken) => RunAsync(false, cancellationToken);
    public Task ContinueAsync(CancellationToken cancellationToken) =>
        RunAsync(Stage == StoreMigrationStage.Consent, cancellationToken);

    /// <summary>
    /// Deletes records that cannot be decoded, then re-inspects. The receipt hold is released only
    /// on success: the hold exists because an undecodable receipt might mean data moved, and after
    /// the records are gone there is nothing left for a later pass to read it from.
    /// </summary>
    public async Task DiscardRecordsAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || Stage != StoreMigrationStage.Recovery)
            return;

        IsBusy = true;
        var result = StoreMigrationDiscardState.Failed;
        try
        {
            Changed?.Invoke();
            result = await Task.Run(operations.DiscardUnreadableRecords, cancellationToken);
            if (result != StoreMigrationDiscardState.Discarded)
                logger.Error($"Could not discard the migration records ({result}).");
        }
        catch (Exception exception)
        {
            logger.Error($"Discarding the migration records failed: {exception}");
        }
        finally
        {
            IsBusy = false;
            // A contended or failing discard stays offered so the user can retry. Readable
            // records mean the offer was wrong, and repeating it would only mislead.
            if (result == StoreMigrationDiscardState.RecordsAreReadable)
                CanDiscardRecords = false;
            Changed?.Invoke();
        }

        if (result != StoreMigrationDiscardState.Discarded)
            return;

        _holdsCompletedHandoff = false;
        await RunAsync(false, cancellationToken);
    }

    private async Task RunAsync(bool confirmed, CancellationToken cancellationToken)
    {
        // Recovery is deliberately retryable. Its usual cause, a receipt whose source version no
        // longer matches the installed Inno, clears once the user removes that app, and a pass
        // that refused to re-inspect would leave them looking at advice they had already followed.
        if (IsBusy || Stage is StoreMigrationStage.Ready)
            return;

        IsBusy = true;
        try
        {
            SetStage(StoreMigrationStage.Inspecting);
            cancellationToken.ThrowIfCancellationRequested();
            var admission = operations.Inspect();
            // Never cleared by a later pass: Retry re-inspects, and a transient failure there
            // must not drop a receipt this session already observed or wrote.
            _holdsCompletedHandoff |= admission.BlocksStartup;
            if (admission.AllowsNormalStartup)
            {
                SetStage(StoreMigrationStage.Ready);
                return;
            }

            if (admission.State == StoreMigrationStartupState.FinalizationRequired)
            {
                SetStage(StoreMigrationStage.Finalizing);
                var result = await operations.FinalizeAsync();
                // A durable startup refusal still finalizes the migration, so it must not be
                // folded into Ready: the user has to be told that only Windows can re-enable
                // the startup task. Launch still proceeds once they close the window.
                if (result.State == StoreMigrationFinalizationState.StartupPreferenceRefused)
                {
                    SetStage(StoreMigrationStage.StartupRefused);
                    return;
                }
                SetStage(result.AllowsNormalStartup ? StoreMigrationStage.Ready : result.State switch
                {
                    StoreMigrationFinalizationState.AwaitingInnoRemoval => StoreMigrationStage.AwaitingRemoval,
                    StoreMigrationFinalizationState.InspectionFailed => StoreMigrationStage.InspectionFailed,
                    _ => StoreMigrationStage.FinalizationFailed
                });
                return;
            }

            if (admission.State != StoreMigrationStartupState.ConsentRequired || admission.Installation is null)
            {
                SetStage(admission.State switch
                {
                    StoreMigrationStartupState.AwaitingInnoRemoval => StoreMigrationStage.AwaitingRemoval,
                    StoreMigrationStartupState.UpdateInno => StoreMigrationStage.UpdateRequired,
                    StoreMigrationStartupState.UnsupportedInstallation => StoreMigrationStage.Unsupported,
                    StoreMigrationStartupState.InspectionFailed => StoreMigrationStage.InspectionFailed,
                    _ => StoreMigrationStage.Recovery
                });
                return;
            }

            if ((confirmed && _promptInstallation != admission.Installation) ||
                (!confirmed && !operations.HasConsent(admission.Installation)))
            {
                _promptInstallation = admission.Installation;
                SetStage(StoreMigrationStage.Consent);
                return;
            }
            if (confirmed)
            {
                try
                {
                    operations.GrantConsent(admission.Installation);
                }
                catch (Exception exception) when (exception is InvalidDataException or System.Security.Cryptography.CryptographicException)
                {
                    logger.Error($"Migration consent needs recovery; the existing record was preserved: {exception.Message}");
                    SetStage(StoreMigrationStage.Recovery);
                    return;
                }
            }

            SetStage(StoreMigrationStage.ClosingSource);
            if (!await operations.CloseSourceAsync(admission, cancellationToken))
            {
                SetStage(StoreMigrationStage.CloseSource);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            SetStage(StoreMigrationStage.Preparing);
            var prepared = await operations.PrepareAsync(admission.Installation);
            if (prepared != StoreMigrationPreparationState.Prepared)
            {
                SetStage(prepared switch
                {
                    StoreMigrationPreparationState.InnoRunning => StoreMigrationStage.CloseSource,
                    StoreMigrationPreparationState.SourceChanged => StoreMigrationStage.Unsupported,
                    _ => StoreMigrationStage.ValidationFailed
                });
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            SetStage(StoreMigrationStage.Completing);
            var completed = await operations.CompleteAsync(admission.Installation);
            // A written receipt means data has moved; the window may no longer be dismissed
            // into normal startup until finalization succeeds.
            _holdsCompletedHandoff |= completed == StoreMigrationCompletionState.Completed;
            SetStage(completed switch
            {
                StoreMigrationCompletionState.Completed => StoreMigrationStage.AwaitingRemoval,
                StoreMigrationCompletionState.InnoRunning => StoreMigrationStage.CloseSource,
                StoreMigrationCompletionState.SourceChanged => StoreMigrationStage.Unsupported,
                _ => StoreMigrationStage.ValidationFailed
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStage(StoreMigrationStage.CloseSource);
        }
        catch (Exception exception)
        {
            // This is the UI error boundary. A failure here is reported as an inspection problem;
            // whether it also blocks startup is decided by the receipt, not by the failure. The
            // receipt is probed directly because a throwing inspection never observed one, and
            // leaving the flag clear would let a close resume startup against moved data.
            logger.Error($"Migration workflow failed: {exception}");
            _holdsCompletedHandoff |= HoldsReceiptWithoutInspection();
            SetStage(StoreMigrationStage.InspectionFailed);
        }
        finally
        {
            // The pass reached a definite stage, so closing may now be judged on the receipt.
            _admissionResolved = true;
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Fails safe: if the receipt cannot be confirmed after the workflow already failed, nothing
    /// is known about whether data moved, and the app must not resume normal startup.
    /// </summary>
    private bool HoldsReceiptWithoutInspection()
    {
        try
        {
            return operations.HoldsCompletionReceipt();
        }
        catch (Exception exception)
        {
            logger.Error($"Could not confirm the migration receipt after a workflow failure: {exception}");
            return true;
        }
    }

    private void SetStage(StoreMigrationStage stage)
    {
        Stage = stage;
        if (stage == StoreMigrationStage.Recovery)
            CanDiscardRecords = RecordsAreUnreadable();
        Changed?.Invoke();
    }

    /// <summary>
    /// Recovery is already showing when this runs, so a failure here must not replace it with an
    /// error. An unanswerable probe hides the offer rather than promising an escape that is not
    /// there; the guidance text still names the remedies that work from outside the app.
    /// </summary>
    private bool RecordsAreUnreadable()
    {
        try
        {
            return operations.RecordsAreUnreadable();
        }
        catch (Exception exception)
        {
            logger.Error($"Could not classify the migration records during recovery: {exception}");
            return false;
        }
    }
}
