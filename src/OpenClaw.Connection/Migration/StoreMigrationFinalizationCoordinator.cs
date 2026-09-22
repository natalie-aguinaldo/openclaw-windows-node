using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using OpenClaw.Shared;

namespace OpenClaw.Connection.Migration;

public enum StoreMigrationFinalizationState
{
    AwaitingInnoRemoval,
    InspectionFailed,
    StartupPreferenceFailed,
    RecordCleanupFailed,
    Finalized
}

public sealed record StoreMigrationFinalizationDecision(StoreMigrationFinalizationState State)
{
    public bool AllowsNormalStartup => State == StoreMigrationFinalizationState.Finalized;
}

public enum InnoSourceRemovalStatus
{
    Removed,
    SourcePresent,
    InspectionFailed
}

/// <summary>
/// Verifies that source payload and runtime evidence disappeared after its registry entry did.
/// </summary>
public interface IInnoSourceRemovalVerifier
{
    InnoSourceRemovalStatus VerifyRemoved();
}

/// <summary>
/// Applies the completed migration's startup preference through the host platform.
/// </summary>
public interface IStoreMigrationAutoStartApplier
{
    Task ApplyAsync(bool enabled);
}

/// <summary>
/// Captures the bounded migration inventory at finalization time.
/// </summary>
public interface IMigrationInventoryCapture
{
    MigrationInventory Capture();
}

/// <summary>
/// Removes completed migration records only after the Store startup preference is applied.
/// </summary>
public interface IStoreMigrationRecordCleaner
{
    void ClearCompleted(MigrationRecord receipt);
}

/// <summary>
/// Checks only the canonical source executable, uninstaller, process image, and instance mutex.
/// It never acquires or holds the source mutex.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InnoSourceRemovalVerifier(MigrationBinding binding, string mutexName) : IInnoSourceRemovalVerifier
{
    private readonly string _sourceDirectory = CanonicalizeDirectory(binding.InstallDirectory);
    private readonly string _mutexName = string.IsNullOrWhiteSpace(mutexName)
        ? throw new ArgumentException("Source mutex name is required.", nameof(mutexName))
        : mutexName;

    public InnoSourceRemovalStatus VerifyRemoved()
    {
        try
        {
            var executable = Path.Combine(_sourceDirectory, "OpenClaw.Tray.WinUI.exe");
            var uninstaller = Path.Combine(_sourceDirectory, "unins000.exe");
            MigrationRecordCodec.RejectReparsePoints(executable);
            MigrationRecordCodec.RejectReparsePoints(uninstaller);
            if (File.Exists(executable) || File.Exists(uninstaller))
                return InnoSourceRemovalStatus.SourcePresent;

            foreach (var process in Process.GetProcessesByName("OpenClaw.Tray.WinUI"))
            {
                using (process)
                {
                    var imagePath = Path.GetFullPath(process.MainModule?.FileName
                        ?? throw new InvalidOperationException("Source process has no image path."));
                    if (string.Equals(imagePath, executable, StringComparison.OrdinalIgnoreCase))
                        return InnoSourceRemovalStatus.SourcePresent;
                }
            }

            using var mutex = new Mutex(false, _mutexName, out var createdNew);
            return createdNew ? InnoSourceRemovalStatus.Removed : InnoSourceRemovalStatus.SourcePresent;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         SecurityException or Win32Exception or
                                         InvalidOperationException or ArgumentException)
        {
            return InnoSourceRemovalStatus.InspectionFailed;
        }
    }

    private static string CanonicalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Source installation directory must be absolute.", nameof(directory));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }
}

/// <summary>
/// Captures the canonical inventory without giving finalization another owner for its policy.
/// </summary>
public sealed class MigrationInventoryCapture(MigrationBinding binding) : IMigrationInventoryCapture
{
    public MigrationInventory Capture() =>
        MigrationInventory.Capture(binding.RoamingDirectory, binding.LocalDirectory);
}

/// <summary>
/// Reparse-safe final cleanup that keeps the completion receipt until all earlier cleanup succeeds.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MigrationFinalizationRecordCleaner(MigrationBinding binding) : IStoreMigrationRecordCleaner
{
    public void ClearCompleted(MigrationRecord receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        var intentPath = Path.Combine(directory, MigrationRecordCodec.IntentFileName);
        var completionPath = Path.Combine(directory, MigrationRecordCodec.CompletionFileName);

        MigrationRecordCodec.RejectReparsePoints(directory);
        MigrationRecordCodec.RejectReparsePoints(intentPath);
        MigrationRecordCodec.RejectReparsePoints(completionPath);
        var durable = MigrationRecordCodec.ReadCompletion(
            completionPath, binding, DateTime.UtcNow);
        if (!SameReceipt(durable, receipt))
            throw new InvalidDataException("Completion receipt changed before cleanup.");

        // The receipt is the recovery anchor, so it is always deleted last.
        File.Delete(intentPath);
        File.Delete(completionPath);
    }

    private static bool SameReceipt(MigrationRecord left, MigrationRecord right) =>
        left.Kind == right.Kind &&
        left.MigrationId == right.MigrationId &&
        left.SourceVersion == right.SourceVersion &&
        left.TargetVersion == right.TargetVersion &&
        left.Fingerprint == right.Fingerprint &&
        left.AutoStart == right.AutoStart &&
        left.CreatedUtc == right.CreatedUtc &&
        left.ExpiresUtc == right.ExpiresUtc &&
        left.InventoryJson == right.InventoryJson &&
        left.Binding.InstallDirectory == right.Binding.InstallDirectory &&
        left.Binding.RoamingDirectory == right.Binding.RoamingDirectory &&
        left.Binding.LocalDirectory == right.Binding.LocalDirectory &&
        left.Binding.Architecture == right.Binding.Architecture &&
        left.Binding.UserSid == right.Binding.UserSid;
}

/// <summary>
/// Finalizes a previously validated handoff after exact source removal. It intentionally does
/// not acquire the Inno-visible mutex, start services, or invoke uninstallation.
/// </summary>
public sealed class StoreMigrationFinalizationCoordinator(
    MigrationBinding binding,
    IInnoInstallationDetector detector,
    IInnoSourceRemovalVerifier sourceRemoval,
    IMigrationStartupRecordReader records,
    IMigrationInventoryCapture inventory,
    IStoreMigrationAutoStartApplier autoStart,
    IStoreMigrationRecordCleaner cleaner,
    IOpenClawLogger logger)
{
    public async Task<StoreMigrationFinalizationDecision> FinalizeAsync()
    {
        var detected = detector.Detect();
        if (detected.Status == InnoInstallationStatus.Detected)
            return new(StoreMigrationFinalizationState.AwaitingInnoRemoval);
        if (detected.Status is InnoInstallationStatus.Unsupported or InnoInstallationStatus.InspectionFailed)
            return new(StoreMigrationFinalizationState.InspectionFailed);
        if (detected.Status != InnoInstallationStatus.NotInstalled)
            throw new InvalidOperationException("Unknown Inno installation detection result.");

        var sourceRemovalStatus = sourceRemoval.VerifyRemoved();
        if (sourceRemovalStatus == InnoSourceRemovalStatus.SourcePresent)
            return new(StoreMigrationFinalizationState.AwaitingInnoRemoval);
        if (sourceRemovalStatus == InnoSourceRemovalStatus.InspectionFailed)
            return new(StoreMigrationFinalizationState.InspectionFailed);
        if (sourceRemovalStatus != InnoSourceRemovalStatus.Removed)
            throw new InvalidOperationException("Unknown Inno source-removal result.");

        var directory = Path.Combine(binding.RoamingDirectory, MigrationRecordCodec.DirectoryName);
        var lockPath = Path.Combine(directory, "prepare.lock");
        try
        {
            MigrationRecordCodec.RejectReparsePoints(directory);
            MigrationRecordCodec.RejectReparsePoints(lockPath);
            using var finalizationLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var durable = records.Read();
            if (durable.Status != MigrationStartupRecordStatus.Completed || durable.Record is null)
                return new(StoreMigrationFinalizationState.InspectionFailed);

            MigrationInventory current;
            try
            {
                current = inventory.Capture();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             InvalidDataException)
            {
                logger.Error($"Store migration finalization inventory failed: {exception.Message}");
                return new(StoreMigrationFinalizationState.InspectionFailed);
            }

            if (!string.Equals(current.Fingerprint, durable.Record.Fingerprint, StringComparison.Ordinal))
            {
                logger.Warn("Store migration finalization inventory no longer matches the completion receipt.");
                return new(StoreMigrationFinalizationState.InspectionFailed);
            }

            try
            {
                await autoStart.ApplyAsync(durable.Record.AutoStart).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is COMException or IOException or
                                             UnauthorizedAccessException or InvalidOperationException)
            {
                logger.Error($"Store migration startup preference finalization failed: {exception.Message}");
                return new(StoreMigrationFinalizationState.StartupPreferenceFailed);
            }

            try
            {
                cleaner.ClearCompleted(durable.Record);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             InvalidDataException)
            {
                logger.Error($"Store migration record cleanup failed: {exception.Message}");
                return new(StoreMigrationFinalizationState.RecordCleanupFailed);
            }

            logger.Info($"Store migration finalized: {durable.Record.MigrationId}.");
            return new(StoreMigrationFinalizationState.Finalized);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         InvalidDataException)
        {
            logger.Error($"Store migration finalization could not acquire the preparation lock: {exception.Message}");
            return new(StoreMigrationFinalizationState.RecordCleanupFailed);
        }
    }
}
