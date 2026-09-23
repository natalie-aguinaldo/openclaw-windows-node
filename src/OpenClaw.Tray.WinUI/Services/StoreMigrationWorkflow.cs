using OpenClaw.Connection.Migration;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal enum StoreMigrationStage
{
    Inspecting, Consent, ClosingSource, Preparing, Completing, Finalizing,
    CloseSource, AwaitingRemoval, ValidationFailed, CredentialUnavailable,
    UpdateRequired, Unsupported, InspectionFailed, Recovery, FinalizationFailed, Ready
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
}

/// <summary>Serializes UI actions without replacing the admission, adoption, or finalization owners.</summary>
internal sealed class StoreMigrationWorkflow(IStoreMigrationOperations operations, IOpenClawLogger logger)
{
    public StoreMigrationStage Stage { get; private set; } = StoreMigrationStage.Inspecting;
    public bool IsBusy { get; private set; }

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
    /// </summary>
    public bool BlocksStartup =>
        (!_admissionResolved || _holdsCompletedHandoff) && Stage != StoreMigrationStage.Ready;
    public event Action? Changed;
    private InnoInstallation? _promptInstallation;
    private bool _holdsCompletedHandoff;
    private bool _admissionResolved;

    public Task StartAsync(CancellationToken cancellationToken) => RunAsync(false, cancellationToken);
    public Task ContinueAsync(CancellationToken cancellationToken) =>
        RunAsync(Stage == StoreMigrationStage.Consent, cancellationToken);

    private async Task RunAsync(bool confirmed, CancellationToken cancellationToken)
    {
        if (IsBusy || Stage is StoreMigrationStage.Ready or StoreMigrationStage.Recovery)
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
                StoreMigrationCompletionState.NoActiveGateway or StoreMigrationCompletionState.CredentialUnavailable
                    => StoreMigrationStage.CredentialUnavailable,
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
            // whether it also blocks startup is decided by the receipt, not by the failure.
            logger.Error($"Migration workflow failed: {exception}");
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

    private void SetStage(StoreMigrationStage stage)
    {
        Stage = stage;
        Changed?.Invoke();
    }
}
