namespace OpenClaw.Connection.Migration;

public enum InnoInstallationStatus
{
    NotInstalled,
    Detected,
    Unsupported,
    InspectionFailed
}

/// <summary>
/// Read-only installation evidence, not authorization to migrate or uninstall.
/// </summary>
public sealed record InnoInstallation(
    string InstallDirectory,
    string ExecutablePath,
    string UninstallerPath,
    string Architecture,
    Version Version);

/// <param name="Status">What the inspection concluded.</param>
/// <param name="Installation">Verified installation evidence, present only for <see cref="InnoInstallationStatus.Detected"/>.</param>
/// <param name="Reason">Why an unsupported or failed inspection reached that conclusion.</param>
/// <param name="RegisteredVersion">The registered release version, when one could be parsed.</param>
/// <param name="RegisteredVersionUnsupported">
/// Whether the registration was refused because its version is not a stable numeric release,
/// which is the case for a prerelease build. Such a version never parses, so it reaches startup
/// policy with no <see cref="RegisteredVersion"/> to compare against the minimum. Without this
/// flag the refusal is indistinguishable from a bad location, user, or architecture, and the
/// user is told to check things that are already correct.
/// </param>
/// <param name="SourcePayloadPresent">
/// Whether the source tray executable is actually on disk. A registration alone does not prove an
/// installed app: a botched or interrupted uninstall can leave the registry entry behind with the
/// payload gone. Startup policy uses this to tell a real installed source, which must keep the
/// Store app inactive, from an orphan registration, where blocking would leave no working app.
/// </param>
public sealed record InnoInstallationDetection(
    InnoInstallationStatus Status,
    InnoInstallation? Installation = null,
    string? Reason = null,
    Version? RegisteredVersion = null,
    bool SourcePayloadPresent = false,
    bool RegisteredVersionUnsupported = false);

public interface IInnoInstallationDetector
{
    InnoInstallationDetection Detect();
}
