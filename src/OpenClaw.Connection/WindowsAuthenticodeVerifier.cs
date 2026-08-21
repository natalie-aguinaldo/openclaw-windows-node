using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using Microsoft.Security.Extensions;

namespace OpenClaw.Connection;

internal readonly record struct AuthenticodeTrustResult(bool IsTrusted, string? Detail)
{
    public static AuthenticodeTrustResult Trusted() => new(true, null);

    public static AuthenticodeTrustResult Rejected(string detail) => new(false, detail);
}

/// <summary>
/// Minimal projection of a Windows AppX/MSIX package as reported by
/// <c>Get-AppxPackage</c>, used to corroborate genuine Microsoft WSL binaries that
/// ship without a per-file Authenticode/catalog signature (MSIX packages are
/// trusted at the package level, not per-file).
/// </summary>
internal readonly record struct AppxPackageInfo(
    string? PackageFamilyName,
    string? Publisher,
    string? SignatureKind,
    string? InstallLocation,
    string? Version);

internal readonly record struct WslRelayPathInspection(
    bool IsSecure,
    string? FileVersion,
    string? Detail)
{
    public static WslRelayPathInspection Secure(string? fileVersion) =>
        new(true, fileVersion, null);

    public static WslRelayPathInspection Rejected(string detail) =>
        new(false, null, detail);
}

internal static class WindowsAuthenticodeVerifier
{
    // Modern WSL ships as an MSIX package. The trailing suffix is a hash derived
    // from the publisher's signing certificate/public key, so an impostor package
    // re-signed with a different certificate would get a different family name.
    private const string WslPackageFamilyName =
        "MicrosoftCorporationII.WindowsSubsystemForLinux_8wekyb3d8bbwe";
    private const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    public static AuthenticodeTrustResult VerifyMicrosoftSignedFile(string path) =>
        VerifyMicrosoftSignedFile(
            path,
            VerifyAuthenticodeSignature,
            LookupWslAppxPackageViaPowerShell,
            InspectWslRelayPath);

    /// <summary>
    /// Test seam for the primary signature, WSL package, and protected-path
    /// evidence used by the package fallback.
    /// </summary>
    internal static AuthenticodeTrustResult VerifyMicrosoftSignedFile(
        string path,
        Func<string, AuthenticodeTrustResult> verifyAuthenticode,
        Func<AppxPackageInfo?> lookupWslPackage,
        Func<string, string, WslRelayPathInspection> inspectRelayPath)
    {
        AuthenticodeTrustResult primary;
        try
        {
            primary = verifyAuthenticode(path);
        }
        catch
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL relay Authenticode verification could not complete.");
        }

        if (primary.IsTrusted)
            return primary;

        // MSIX packages (modern WSL) don't carry a per-file Authenticode/catalog
        // signature, so a failed classic check isn't conclusive for wslrelay.exe.
        // Only consult the package-level fallback for the WSL binaries this
        // codebase actually cares about; never widen trust for arbitrary files.
        if (!string.Equals(Path.GetFileName(path), "wslrelay.exe", StringComparison.OrdinalIgnoreCase))
            return primary;

        return VerifyWslPackageFallback(path, primary, lookupWslPackage, inspectRelayPath);
    }

    private static AuthenticodeTrustResult VerifyAuthenticodeSignature(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var signature = FileSignatureInfo.GetFromFileStream(stream);
            using var signingCertificate = signature.SigningCertificate;
            using var timestampCertificate = signature.TimestampCertificate;

            if (signature.State != SignatureState.SignedAndTrusted)
            {
                return AuthenticodeTrustResult.Rejected(
                    $"WSL relay Authenticode verification failed ({signature.State}).");
            }
            if (signingCertificate is null)
            {
                return AuthenticodeTrustResult.Rejected(
                    "WSL relay Authenticode signer could not be read.");
            }

            return HasMicrosoftPublisherIdentity(signingCertificate.Subject)
                ? AuthenticodeTrustResult.Trusted()
                : AuthenticodeTrustResult.Rejected(
                    "WSL relay Authenticode signer is not Microsoft Corporation.");
        }
        catch
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL relay Authenticode verification could not complete.");
        }
    }

    private static AuthenticodeTrustResult VerifyWslPackageFallback(
        string path,
        AuthenticodeTrustResult primaryFailure,
        Func<AppxPackageInfo?> lookupWslPackage,
        Func<string, string, WslRelayPathInspection> inspectRelayPath)
    {
        AppxPackageInfo? package;
        try
        {
            package = lookupWslPackage();
        }
        catch
        {
            package = null;
        }

        if (package is not { } info)
        {
            // No genuine WSL AppX package installed to corroborate; surface the
            // original Authenticode diagnostic rather than a new, less useful one.
            return primaryFailure;
        }

        if (!string.Equals(info.PackageFamilyName, WslPackageFamilyName, StringComparison.Ordinal))
        {
            // Doesn't match the well-known family name (a different/impostor
            // package can't corroborate this binary); surface the original detail.
            return primaryFailure;
        }

        if (string.IsNullOrEmpty(info.SignatureKind) ||
            string.Equals(info.SignatureKind, "None", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL package signature verification failed: the installed WSL package is unsigned.");
        }

        if (info.Publisher is null || !HasMicrosoftPublisherIdentity(info.Publisher))
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL package signature verification failed: the installed WSL package's publisher is not Microsoft Corporation.");
        }

        if (string.IsNullOrWhiteSpace(info.InstallLocation) ||
            !Path.IsPathFullyQualified(info.InstallLocation))
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL package provenance verification failed: package location data is unavailable.");
        }

        string fullPath;
        string installLocation;
        try
        {
            fullPath = Path.GetFullPath(path);
            installLocation = Path.GetFullPath(info.InstallLocation);
        }
        catch
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL package provenance verification failed: package location data is invalid.");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var windowsAppsRoot = Path.Combine(programFiles, "WindowsApps");
        if (IsPathWithin(installLocation, windowsAppsRoot) &&
            IsPathWithin(fullPath, installLocation))
        {
            return ToTrustResult(
                InspectProtectedRelayPath(fullPath, programFiles, inspectRelayPath));
        }

        var externalRelayPath = Path.Combine(programFiles, "WSL", "wslrelay.exe");
        if (!string.Equals(fullPath, externalRelayPath, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL package provenance verification failed: the relay is not owned by the installed WSL package.");
        }

        var inspection = InspectProtectedRelayPath(
            fullPath,
            programFiles,
            inspectRelayPath);
        if (!inspection.IsSecure)
            return ToTrustResult(inspection);

        // The external WSL layout stamps wslrelay.exe with the package version.
        // Exact equality binds the protected external bytes to the installed package.
        if (!VersionsMatch(info.Version, inspection.FileVersion))
        {
            return AuthenticodeTrustResult.Rejected(
                "WSL package provenance verification failed: the external relay version does not match the installed WSL package.");
        }

        return AuthenticodeTrustResult.Trusted();
    }

    private static WslRelayPathInspection InspectProtectedRelayPath(
        string fullPath,
        string trustedRoot,
        Func<string, string, WslRelayPathInspection> inspectRelayPath)
    {
        try
        {
            return inspectRelayPath(fullPath, trustedRoot);
        }
        catch
        {
            return WslRelayPathInspection.Rejected(
                "WSL package provenance verification failed: the relay path could not be inspected.");
        }
    }

    private static AuthenticodeTrustResult ToTrustResult(WslRelayPathInspection inspection) =>
        inspection.IsSecure
            ? AuthenticodeTrustResult.Trusted()
            : AuthenticodeTrustResult.Rejected(
                inspection.Detail ??
                "WSL package provenance verification failed: the relay path is not protected.");

    private static bool VersionsMatch(string? packageVersion, string? fileVersion) =>
        Version.TryParse(packageVersion, out var parsedPackageVersion) &&
        Version.TryParse(fileVersion, out var parsedFileVersion) &&
        parsedPackageVersion.Equals(parsedFileVersion);

    private static bool IsPathWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal);
    }

    internal static bool HasMicrosoftPublisherIdentity(string subject) =>
        subject.Split(',')
            .Select(part => part.Trim())
            .Any(part =>
                string.Equals(
                    part,
                    "O=Microsoft Corporation",
                    StringComparison.OrdinalIgnoreCase));

    private static AppxPackageInfo? LookupWslAppxPackageViaPowerShell()
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolvePowerShellPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                "Get-AppxPackage -Name 'MicrosoftCorporationII.WindowsSubsystemForLinux' | " +
                "Sort-Object Version -Descending | " +
                "Select-Object -First 1 PackageFamilyName, Publisher, InstallLocation, " +
                "@{Name='SignatureKind'; Expression={$_.SignatureKind.ToString()}}, " +
                "@{Name='Version'; Expression={$_.Version.ToString()}} | " +
                "ConvertTo-Json -Compress");

            process = Process.Start(psi);
            if (process is null)
                return null;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            if (!process.WaitForExit(8_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            string output;
            try
            {
                output = stdoutTask.GetAwaiter().GetResult();
                _ = stderrTask.GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            return ParseAppxPackageJson(output);
        }
        catch
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string ResolvePowerShellPath()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(systemRoot))
            systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot))
            systemRoot = @"C:\Windows";
        return Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static AppxPackageInfo? ParseAppxPackageJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                    return null;
                root = root[0];
            }
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var familyName = GetStringProperty(root, "PackageFamilyName");
            if (familyName is null)
                return null;

            return new AppxPackageInfo(
                familyName,
                GetStringProperty(root, "Publisher"),
                GetStringProperty(root, "SignatureKind"),
                GetStringProperty(root, "InstallLocation"),
                GetStringProperty(root, "Version"));
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static WslRelayPathInspection InspectWslRelayPath(
        string path,
        string trustedRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return WslRelayPathInspection.Rejected(
                "WSL package provenance verification is only available on Windows.");
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(trustedRoot);
            if (!File.Exists(fullPath) || !IsPathWithin(fullPath, fullRoot))
            {
                return WslRelayPathInspection.Rejected(
                    "WSL package provenance verification failed: the relay path is unavailable or outside its trusted root.");
            }

            for (var current = fullPath; ; current = Path.GetDirectoryName(current)!)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return WslRelayPathInspection.Rejected(
                        "WSL package provenance verification failed: the relay path contains a reparse point.");
                }

                FileSystemSecurity security =
                    string.Equals(current, fullPath, StringComparison.OrdinalIgnoreCase)
                        ? new FileInfo(current).GetAccessControl(
                            AccessControlSections.Owner | AccessControlSections.Access)
                        : new DirectoryInfo(current).GetAccessControl(
                            AccessControlSections.Owner | AccessControlSections.Access);
                if (!HasProtectedOwnershipAndWriteAcl(security))
                {
                    return WslRelayPathInspection.Rejected(
                        "WSL package provenance verification failed: the relay path permits untrusted modification.");
                }

                if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
                    break;

                if (string.IsNullOrWhiteSpace(Path.GetDirectoryName(current)))
                {
                    return WslRelayPathInspection.Rejected(
                        "WSL package provenance verification failed: the relay path escaped its trusted root.");
                }
            }

            return WslRelayPathInspection.Secure(
                FileVersionInfo.GetVersionInfo(fullPath).FileVersion);
        }
        catch
        {
            return WslRelayPathInspection.Rejected(
                "WSL package provenance verification failed: the relay path could not be inspected.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasProtectedOwnershipAndWriteAcl(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>();
        var descriptor = new RawSecurityDescriptor(
            security.GetSecurityDescriptorBinaryForm(),
            offset: 0);
        var hasDiscretionaryAcl =
            (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0 &&
            descriptor.DiscretionaryAcl is not null;
        return HasProtectedOwnershipAndWriteAcl(
            owner,
            rules,
            hasDiscretionaryAcl);
    }

    [SupportedOSPlatform("windows")]
    internal static bool HasProtectedOwnershipAndWriteAcl(
        SecurityIdentifier? owner,
        IEnumerable<FileSystemAccessRule> rules,
        bool hasDiscretionaryAcl)
    {
        if (!hasDiscretionaryAcl)
            return false;

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var trustedInstaller = new SecurityIdentifier(TrustedInstallerSid);
        if (owner is null ||
            (!owner.Equals(system) &&
             !owner.Equals(administrators) &&
             !owner.Equals(trustedInstaller)))
        {
            return false;
        }

        const FileSystemRights writeRights =
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.WriteAttributes |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.Delete |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;

        foreach (var rule in rules)
        {
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 ||
                rule.AccessControlType != AccessControlType.Allow ||
                (rule.FileSystemRights & writeRights) == 0)
            {
                continue;
            }

            if (rule.IdentityReference is not SecurityIdentifier sid ||
                (!sid.Equals(system) &&
                 !sid.Equals(administrators) &&
                 !sid.Equals(trustedInstaller)))
            {
                return false;
            }
        }

        return true;
    }
}
