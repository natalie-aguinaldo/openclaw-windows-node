namespace OpenClaw.Tray.Tests;

public sealed class MsixDevelopmentSigningTests
{
    [Fact]
    public void DevelopmentMsixSigning_IsLocalOnlyAndStoreBuildsRemainUnsigned()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj"));
        var script = File.ReadAllText(Path.Combine(
            root, "scripts", "setup-dev-msix-cert.ps1"));

        Assert.Contains("CN=OpenClaw Local Development", project);
        Assert.Contains(@"$(LOCALAPPDATA)\OpenClawDevelopment\MSIX", project);
        Assert.Contains("'$(DevBuild)' == 'true'", project);
        Assert.Contains("<PackageCertificateThumbprint", project);
        Assert.DoesNotContain("<PackageCertificateKeyFile", project);
        Assert.DoesNotContain("<PackageCertificatePassword", project);
        Assert.Contains("Partner Center", project);

        Assert.Contains(@"%LOCALAPPDATA%\OpenClawDevelopment\MSIX", script);
        Assert.Contains(@"Cert:\CurrentUser\My", script);
        Assert.Contains(@"Cert:\LocalMachine\TrustedPeople", script);
        Assert.Contains("-KeyExportPolicy NonExportable", script);
        Assert.Contains("[switch]$Remove", script);
        Assert.Contains("Microsoft Store submissions", script);
        // The helper prints the command a developer runs next. A stale switch
        // name here is silently ignored by build.ps1's parameter binder only if
        // CmdletBinding is absent, so keep this invocation exact.
        Assert.Contains(@".\build.ps1 -Project WinUI -Msix Dev", script);
        Assert.DoesNotContain("-PackageMsix", script);
        Assert.DoesNotContain("Export-PfxCertificate", script);
        Assert.DoesNotContain("AppInstaller", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildScript_DevelopmentMsixPathStaysLocallySigned()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var buildScript = File.ReadAllText(Path.Combine(root, "build.ps1"));

        Assert.Contains("[ValidateSet(\"Dev\", \"Store\")]", buildScript);
        // Without CmdletBinding, a removed switch such as -PackageMsix lands in
        // $args and silently produces an unpackaged build.
        Assert.Contains("[CmdletBinding()]", buildScript);
        Assert.Contains("$buildDevMsix = ($Msix -eq \"Dev\")", buildScript);
        Assert.Contains("$DevBuild = $true", buildScript);
        Assert.Contains("-p:PackageMsix=true", buildScript);
        Assert.Contains("\"publish\", $path", buildScript);
        Assert.Contains("\"--self-contained\"", buildScript);
        Assert.Contains("-p:MsixRevision=$msixRevision", buildScript);
        Assert.Contains("setup-dev-msix-cert.ps1", buildScript);
        Assert.DoesNotContain("ReleaseChannel", buildScript);
        Assert.DoesNotContain("AppInstaller", buildScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildScript_StoreMsixPathIsUnsignedReleaseIdentityForBothArchitectures()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var buildScript = File.ReadAllText(Path.Combine(root, "build.ps1"));
        var packagingScript = File.ReadAllText(Path.Combine(root, "scripts", "Build-Msix.ps1"));

        Assert.Contains("$buildStoreMsix = ($Msix -eq \"Store\")", buildScript);
        Assert.Contains("$storeMsixRuntimeIdentifiers = @(\"win-x64\", \"win-arm64\")", buildScript);
        Assert.Contains(@"scripts\Build-Msix.ps1", buildScript);

        // The Store identity and the dev identity must never be produced by the same build.
        Assert.Contains("-Msix Store cannot be combined with -DevBuild", buildScript);
        Assert.Contains("-Msix Store requires -Configuration Release", buildScript);
        // Dev and Store are a single ValidateSet parameter, so requesting both is
        // unrepresentable rather than rejected by a hand-written guard.
        Assert.DoesNotContain("[switch]$PackageMsix", buildScript);
        Assert.DoesNotContain("[switch]$StoreMsix", buildScript);

        // Partner Center signs Store submissions; a locally signed package is rejected.
        Assert.Contains("-p:AppxPackageSigningEnabled=false", packagingScript);
        Assert.Contains("'AppxSignature.p7x',", packagingScript);
        Assert.Contains("The MSIX contains forbidden content:", packagingScript);

        // The tracked manifest, not the build script, owns the release identity.
        Assert.Contains("$sourceManifest.Package.Identity.Name", packagingScript);
        Assert.Contains("$sourceManifest.Package.Identity.Publisher", packagingScript);
        Assert.Contains("-ne $expectedIdentityName", packagingScript);
        Assert.Contains("-ne $expectedPublisher", packagingScript);
        Assert.Contains("-ne $Architecture", packagingScript);
        Assert.Contains("must end in .0 for release packages", packagingScript);

        // Exactly one package per architecture, deterministically named, with provenance.
        Assert.Contains("$builtPackages.Count -ne 1", packagingScript);
        Assert.Contains("\"OpenClawCompanion-$Architecture.msix\"", packagingScript);
        Assert.Contains("msix-metadata.json", packagingScript);
        Assert.Contains("signed = $false", packagingScript);
        Assert.Contains("sourceTreeDirty = $sourceTreeDirty", packagingScript);

        // A caller-supplied output directory must never be recursively deleted,
        // and the script must run under Windows PowerShell 5.1 strict mode.
        Assert.Contains("already exists and is not empty", packagingScript);
        Assert.Contains("Get-Variable -Name IsWindows", packagingScript);
        Assert.DoesNotContain("if (-not $IsWindows)", packagingScript);

        // Loose CRT files belong to the Inno payload; MSIX resolves them via VCLibs.
        Assert.Contains("'vcruntime140.dll',", packagingScript);
        Assert.Contains("'msvcp140.dll',", packagingScript);
        Assert.Contains("The MSIX is missing required content:", packagingScript);
        Assert.Contains("\"tools/mxc/$Architecture/wxc-exec.exe\"", packagingScript);
        Assert.Contains("'OpenClaw.SetupEngine.UI.dll',", packagingScript);
        Assert.Contains("'coreclr.dll',", packagingScript);

        // The Store path must never pass the dev identity or dev signing to MSBuild.
        Assert.DoesNotContain("DevBuild", packagingScript);
        Assert.DoesNotContain("PackageCertificate", packagingScript);
        Assert.DoesNotContain("MsixRevision", packagingScript);
    }

    [Fact]
    public void MsixManifest_RegistersTheToolkitToastActivator()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var manifest = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Package.appxmanifest"));

        Assert.Contains("xmlns:desktop=", manifest);
        Assert.Contains("xmlns:com=", manifest);
        Assert.Contains("windows.toastNotificationActivation", manifest);
        Assert.Contains("windows.comServer", manifest);
        Assert.Contains("EF9297B3-EEEB-4E50-8306-D1D118E04BC7", manifest);
        Assert.Contains("Arguments=\"-ToastActivated\"", manifest);
        Assert.Contains("Microsoft.VCLibs.140.00.UWPDesktop", manifest);
        Assert.Contains("MinVersion=\"14.0.33728.0\"", manifest);
    }

    [Fact]
    public void MsixPayload_UsesStandardPublishItemsForNativeDependencies()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj"));
        var directoryTargets = File.ReadAllText(Path.Combine(root, "src", "Directory.Build.targets"));

        Assert.Contains("AddWxcExecToPublishItems", project);
        Assert.Contains("<ResolvedFileToPublish Include=\"@(_WxcExecPackageFiles)\">", project);
        Assert.Contains(@"<RelativePath>tools\mxc\$(MxcArch)\%(Filename)%(Extension)</RelativePath>", project);

        // The VC runtime deliberately does NOT use publish items. MSIX resolves the CRT
        // through its VCLibs framework dependency, so the loose DLLs are only needed by
        // the unpackaged Inno payload, where the post-publish copy already delivers them.
        Assert.Contains("CopyOpenClawVCRuntimeToPublish", directoryTargets);
        Assert.DoesNotContain("AddOpenClawVCRuntimeToPublishItems", directoryTargets);
    }

    [Fact]
    public void GeneratedDevelopmentManifest_UsesVersionAndIdentityIsolation()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj"));

        Assert.Contains("DependsOnTargets=\"GetVersion\"", project);
        Assert.Contains("<UpdateVersionProperties>true</UpdateVersionProperties>",
            File.ReadAllText(Path.Combine(root, "src", "Directory.Build.props")));
        Assert.Contains("$(GitVersion_CommitsSinceVersionSource)", project);
        Assert.Contains("Publisher=\"$(OpenClawDevMsixPublisher)\"", project);
        Assert.Contains("ToastActivatorClsid=\"C536D4AD-19BE-4F7A-B227-AB97629BF299\"", project);
        Assert.Contains("toastClsidRegex.Replace", project);
        Assert.Contains("comClassRegex.Replace", project);
    }

    [Fact]
    public void PackagedAutoStart_UsesTheManifestStartupTask()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var manifest = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Package.appxmanifest"));
        var manager = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "Services", "AutoStartManager.cs"));

        Assert.Contains("Category=\"windows.startupTask\"", manifest);
        Assert.Contains("TaskId=\"OpenClawStartup\"", manifest);
        Assert.Contains("Enabled=\"false\"", manifest);
        Assert.Contains("EntryPoint=\"Windows.FullTrustApplication\"", manifest);

        Assert.Contains("PackageHelper.IsPackaged", manager);
        Assert.Contains("StartupTask.GetAsync(AppIdentity.PackageStartupTaskId)", manager);
        Assert.Contains("RequestEnableAsync()", manager);
        Assert.Contains("startupTask.Disable()", manager);
        Assert.Contains("IsPackagedAutoStartEnabledAsync()", manager);

        // The scheduled task named AppIdentity.StartupTaskName is created by installer.iss
        // and removed by the Inno uninstaller. A packaged build must never delete it on its
        // own: that would silently disable a legacy install the user has not agreed to
        // replace.
        Assert.DoesNotContain("MigrateLegacyAutoStartAsync", manager);
        Assert.DoesNotContain("PackagedLegacyCleanup", manager);

        var app = File.ReadAllText(Path.Combine(
            root, "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        Assert.DoesNotContain("MigrateLegacyAutoStartAsync", app);
        Assert.Contains("await ApplyAutoStartCore(origin, !_settings.AutoStart);", app);
        Assert.Contains("await AutoStartManager.IsAutoStartEnabledAsync()", app);
    }
}
