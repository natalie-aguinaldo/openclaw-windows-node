using System.Text.RegularExpressions;
using Xunit.Sdk;

namespace OpenClaw.Tray.Tests;

public sealed class InnoMigrationContractTests
{
    [Fact]
    public void StorePreviewGuard_PrecedesInstanceForwardingAndNormalServices()
    {
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        var launch = app[app.IndexOf("private async Task OnLaunchedAsync", StringComparison.Ordinal)..];
        var guard = launch.IndexOf("await StoreMigrationStartupGuard.ShouldStopLaunchAsync(DeepLinkPipeName)", StringComparison.Ordinal);
        Assert.True(guard > launch.IndexOf("await CliUninstallHandler.RunAsync", StringComparison.Ordinal));
        Assert.True(guard < launch.IndexOf("GetProtocolActivationUri()", StringComparison.Ordinal));
        Assert.True(guard < launch.IndexOf("_mutex = new Mutex(", StringComparison.Ordinal));
        Assert.True(guard < launch.IndexOf("new ActivationRouter(", StringComparison.Ordinal));
        Assert.True(guard < launch.IndexOf("new SettingsManager()", StringComparison.Ordinal));
        Assert.DoesNotContain("new InnoInstallationDetector(", app);
        Assert.DoesNotContain("new MigrationStartupRecordReader(", app);
        Assert.DoesNotContain("new StoreMigrationStartupCoordinator(", app);

        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "StoreMigrationStartupGuard.cs");
        Assert.Matches(@"#if !STORE_MIGRATION_PREVIEW && !STORE_MIGRATION_RELEASE\s+return false;\s+#else", helper);
        Assert.Contains("new StoreMigrationWorkflow(", helper);
        Assert.Contains("new StoreMigrationWindow(", helper);
        Assert.Contains("return !await window.ShowAsync()", helper);
        Assert.DoesNotContain("MessageBoxW", helper);
        helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "StoreMigrationOperations.cs");
        Assert.Contains("new StoreMigrationStartupCoordinator(", helper);
        Assert.Contains("new StoreMigrationConsentCoordinator(", helper);
        Assert.Contains("MigrationRecordCodec.PackageName", helper);
        Assert.Contains("MigrationRecordCodec.PackagePublisher", helper);
        Assert.Contains(".Evaluate(true, MigrationEnvironment.MinimumSourceVersion, _binding.Architecture)", helper);
        Assert.DoesNotContain("File.Write", helper);
        Assert.DoesNotContain("SetPackagedAutoStartAsync", helper);
        Assert.Contains("new InnoMutexLeaseProvider()", helper);
        Assert.Contains("new StoreMigrationAdoptionPreparationCoordinator(", helper);
        Assert.Contains("new MigrationPreparation(_binding!)", helper);
        Assert.Contains("new StoreMigrationCompletionCoordinator(", helper);
        Assert.Contains("new StoreMigrationFinalizationCoordinator(", helper);
        Assert.Contains("new MigrationFinalizationRecordCleaner(", helper);
        Assert.Contains("new InnoSourceRemovalVerifier(_binding!, AppIdentity.MutexBaseName)", helper);
        Assert.Contains("new MigrationInventoryCapture(_binding!)", helper);
        Assert.Contains("AutoStartManager.SetAutoStartAsync(enabled)", helper);
        Assert.DoesNotContain(".GetAwaiter().GetResult()", helper);
        Assert.Contains("new CredentialResolver(DeviceIdentityFileReader.Instance)", helper);
        Assert.DoesNotContain("TaskDialogIndirect", helper);
        Assert.Contains("RequestMigrationShutdownAsync", helper);
        Assert.Contains("coordinator.Retry()", helper);
        Assert.DoesNotContain("Process.Kill", helper);

        var finalizer = Read("src", "OpenClaw.Connection", "Migration",
            "StoreMigrationFinalizationCoordinator.cs");
        Assert.Contains("IStoreMigrationAutoStartApplier", finalizer);
        Assert.Contains("IInnoSourceRemovalVerifier", finalizer);
        Assert.Contains("MigrationInventory.Capture", finalizer);
        Assert.Contains("new Mutex(false, _mutexName, out var createdNew)", finalizer);
        Assert.DoesNotContain("IMigrationSourceLeaseProvider", finalizer);
        Assert.DoesNotContain("InnoMutex", finalizer);
        Assert.DoesNotContain("WaitOne", finalizer);
        Assert.DoesNotContain("ReleaseMutex", finalizer);
        Assert.DoesNotContain("SettingsManager", finalizer);
        Assert.DoesNotContain("CliUninstall", finalizer);
    }

    [Fact]
    public void InnoPreview_GatesHandoffAndUsesCanonicalShutdown()
    {
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "InnoMigrationHandoff.cs");
        Assert.Contains("#if INNO_MIGRATION_PREVIEW || INNO_MIGRATION_RELEASE", helper);
        Assert.Contains("!AppIdentity.IsDev && !PackageHelper.IsPackaged", helper);
        Assert.Contains("!GatewayFixtureIsolation.IsEnabled", helper);
        Assert.Contains(".HasValidConsent(installation.Version.ToString())", helper);
        Assert.Contains("Environment.ProcessPath", helper);
        var settings = Read("src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml.cs");
        Assert.Contains("DefaultButton = ContentDialogButton.Close", settings);
        Assert.True(settings.IndexOf("await confirmation.ShowAsync()", StringComparison.Ordinal) <
            settings.IndexOf("await InnoMigrationHandoff.GrantAndLaunchAsync()", StringComparison.Ordinal));
        Assert.True(helper.IndexOf(".Grant(", StringComparison.Ordinal) <
            helper.IndexOf("Launcher.LaunchUriAsync", StringComparison.Ordinal));
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        Assert.Contains("InnoMigrationHandoff.CreateShutdownHandler(_dispatcherQueue!, ExitApplication)", app);
        Assert.DoesNotContain("Process.Kill", helper);
        Assert.Contains("MigrationEnvironment.StoreProductId", helper);
        Assert.Contains("#if INNO_MIGRATION_RELEASE", helper);
        Assert.Contains("MigrationVersionPolicy.TryParseReleaseVersion(MigrationEnvironment.MinimumSourceVersion", helper);
        Assert.Contains("installation.Version < minimum", helper);

        var project = System.Xml.Linq.XDocument.Parse(Read("src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj"));
        var target = project.Descendants("Target").Single(element =>
            (string?)element.Attribute("Name") == "ValidateInnoMigrationPreview");
        Assert.Contains("'$(Configuration)' != 'Debug'", target.ToString());
        Assert.Contains("'$(PackageMsix)' == 'true'", target.ToString());
        Assert.DoesNotContain(project.Descendants("InnoMigrationPreview"), element => element.Value == "true");
        Assert.Empty(project.Descendants("MigrationPreviewStoreProductId"));
    }

    [Fact]
    public void ReleaseMetadata_DoesNotFallBackToPreviewValues()
    {
        var environment = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "MigrationEnvironment.cs");
        Assert.Matches(
            """#if STORE_MIGRATION_RELEASE \|\| INNO_MIGRATION_RELEASE\s+public static string\? MinimumSourceVersion => Metadata\("MigrationMinimumSourceVersion"\);\s+public static string\? StoreProductId => Metadata\("MigrationStoreProductId"\);\s+#else""",
            environment);
        Assert.Contains("Metadata(\"StoreMigrationPreviewMinimumSourceVersion\")", environment);
        Assert.Contains("Metadata(\"MigrationPreviewStoreProductId\")", environment);
    }

    [Fact]
    public void MigrationWindow_HasAccessibleNamedActionsAndNoInventedProgress()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Windows", "StoreMigrationWindow.xaml");
        Assert.Contains("AutomationProperties.AutomationId=\"MigrationPrimary\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"MigrationDismiss\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"MigrationInstalledApps\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml);
        Assert.DoesNotContain("ProgressBar", xaml);
        var code = Read("src", "OpenClaw.Tray.WinUI", "Windows", "StoreMigrationWindow.xaml.cs");
        Assert.Contains("ms-settings:appsfeatures", code);
        Assert.Contains("StoreMigrationStage.AwaitingRemoval", code);
        Assert.Contains("StoreMigrationStage.Consent or StoreMigrationStage.Recovery ? Dismiss : Primary", code);
        Assert.Contains("args.Cancel = true", code);
    }

    [Fact]
    public void StorePreviewBuildGate_RequiresExplicitNonShippingConfiguration()
    {
        var project = System.Xml.Linq.XDocument.Parse(Read("src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj"));
        var define = project.Descendants("DefineConstants")
            .Single(element => element.Value.Contains("STORE_MIGRATION_PREVIEW", StringComparison.Ordinal));
        Assert.Equal("'$(StoreMigrationPreview)' == 'true'", define.Parent!.Attribute("Condition")!.Value);
        var guard = project.Descendants("Target").Single(element => (string?)element.Attribute("Name") == "ValidateStoreMigrationPreview");
        Assert.Equal("'$(StoreMigrationPreview)' == 'true'", (string?)guard.Attribute("Condition"));
        var configurationError = guard.Elements("Error").First();
        Assert.Equal("'$(Configuration)' != 'Debug' or '$(PackageMsix)' != 'true' or '$(DevBuild)' == 'true'",
            (string?)configurationError.Attribute("Condition"));
        Assert.Contains("StoreMigrationPreviewMinimumSourceVersion", guard.ToString());
        Assert.DoesNotContain(project.Descendants("StoreMigrationPreview"), element => element.Value == "true");
        Assert.Empty(project.Descendants("StoreMigrationPreviewMinimumSourceVersion"));
    }

    [Fact]
    public void CompletedMigrationGuard_PrecedesSettingsAndActivation()
    {
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        var launch = app[app.IndexOf("private async Task OnLaunchedAsync", StringComparison.Ordinal)..];
        var guard = launch.IndexOf("InnoMigrationStartupGuard.ShouldStopLaunch()", StringComparison.Ordinal);
        Assert.True(guard > launch.IndexOf("await CliUninstallHandler.RunAsync", StringComparison.Ordinal));
        Assert.True(guard > launch.IndexOf("_mutex = new Mutex(true, mutexName", StringComparison.Ordinal));
        Assert.Contains("if (ownsMutex && InnoMigrationStartupGuard.ShouldStopLaunch())", launch);
        Assert.True(guard < launch.IndexOf("new ActivationRouter(", StringComparison.Ordinal));
        Assert.True(guard < launch.IndexOf("new SettingsManager()", StringComparison.Ordinal));
        Assert.DoesNotContain("MigrationRecordCodec", app);
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "InnoMigrationStartupGuard.cs");
        Assert.Contains("AppIdentity.IsDev || PackageHelper.IsPackaged", helper);
        Assert.Contains("MigrationRecordCodec.ReadCompletion", helper);
        Assert.Contains("Migration_InnoCompleted", helper);
    }

    [Fact]
    public void Installer_ChecksCompletionBeforeChoiceAndNeverDeletesPreservedState()
    {
        var installer = Read("installer.iss");
        Assert.Contains("Source: \"scripts\\Test-InnoMigration.ps1\"", installer);
        Assert.Contains("Source: \"src\\OpenClaw.Connection\\Migration\\MigrationRecordCodec.cs\"", installer);
        AssertPreservationGuards(installer);
    }

    [Theory]
    [InlineData("if MigrationResult <> 0 then", "if MigrationResult = 10 then")]
    [InlineData("LocalGatewayCleanupRequested := False;",
        "LocalGatewayCleanupRequested := False;\n    LocalGatewayCleanupSucceeded := True;")]
    [InlineData("    Exit;\n  end;\n\n  if UninstallSilent()", "  end;\n\n  if UninstallSilent()")]
    [InlineData("  if not LocalGatewayCleanupRequested then\n    Exit;", "")]
    [InlineData("if Started and (ResultCode = 10) then", "if Started and (ResultCode = 11) then")]
    [InlineData("    end;\n\n    if Started and (ResultCode = 0) then",
        "      LocalGatewayCleanupSucceeded := True;\n    end;\n\n    if Started and (ResultCode = 0) then")]
    [InlineData("if Started and (ResultCode = 0) then", "if ResultCode = 0 then")]
    [InlineData("  if not LocalGatewayCleanupSucceeded then\n    Exit;", "")]
    public void Installer_PreservationContractsRejectUnsafeMutations(string original, string replacement)
    {
        var installer = Read("installer.iss").ReplaceLineEndings("\n");
        Assert.Contains(original, installer);
        var mutated = installer.Replace(original, replacement, StringComparison.Ordinal);
        Assert.ThrowsAny<XunitException>(() => AssertPreservationGuards(mutated));
    }

    private static void AssertPreservationGuards(string installer)
    {
        // These source contracts intentionally pin the small Pascal safety branches.
        // Runtime installer proof is still required; matching keywords alone is not enough.
        Assert.Matches(
            @"MigrationResult := CheckCompletedStoreMigration;\s+" +
            @"if MigrationResult <> 0 then\s+begin\s+" +
            @"LocalGatewayCleanupRequested := False;\s+" +
            @"if MigrationResult = 10 then\s+Log\('[^']*'\)\s+" +
            @"else\s+Log\('[^']*'\);\s+Exit;\s+end;\s+if UninstallSilent\(\)",
            installer);
        Assert.Matches(
            @"begin\s+if not LocalGatewayCleanupRequested then\s+Exit;\s+" +
            @"LocalGatewayCleanupSucceeded := False;\s+repeat",
            installer);
        Assert.Matches(
            @"if Started and \(ResultCode = 10\) then\s+begin\s+" +
            @"Log\('[^']*'\);\s+Exit;\s+end;\s+" +
            @"if Started and \(ResultCode = 0\) then\s+begin\s+" +
            @"LocalGatewayCleanupSucceeded := True;\s+Log\('[^']*'\);\s+Exit;\s+end;",
            installer);
        Assert.Single(Regex.Matches(installer, @"LocalGatewayCleanupSucceeded\s*:=\s*True;"));
        Assert.Matches(
            @"procedure DeleteGeneratedAppState;\s+begin\s+" +
            @"if not LocalGatewayCleanupSucceeded then\s+Exit;\s+" +
            @"if DelTree\(ExpandConstant\('\{app\}'\), True, True, True\) then",
            installer);
    }

    [Fact]
    public void CleanupScript_RechecksReceiptBeforeAnyDestructiveWork()
    {
        var script = Read("scripts", "Uninstall-LocalGateway.ps1");
        var main = script[script.LastIndexOf("\ntry {", StringComparison.Ordinal)..];
        Assert.True(main.IndexOf("Test-InnoMigration.ps1", StringComparison.Ordinal) <
                    main.IndexOf("$script:WslPath = Get-WslExePath", StringComparison.Ordinal));
        Assert.Contains("if ($migrationResult -eq 10)", main);
        Assert.Contains("exit 10", main);
        Assert.Contains("if ($migrationResult -ne 0)", main);
        Assert.True(main.IndexOf("$migrationOperationLock = [IO.FileStream]::new(", StringComparison.Ordinal) <
                    main.IndexOf("$checker =", StringComparison.Ordinal));
        Assert.Contains("[IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Read, [IO.FileShare]::Read", main);
        Assert.Matches(@"finally\s*\{\s*if \(\$null -ne \$migrationOperationLock\) \{\s*" +
                       @"\$migrationOperationLock.Dispose\(\)", main);
        var logger = script[script.IndexOf("function Write-GatewayLog", StringComparison.Ordinal)..
            script.IndexOf("function Add-CleanupWarning", StringComparison.Ordinal)];
        Assert.DoesNotContain("Test-InnoMigration", logger);
    }

    [Fact]
    public void Installer_HoldsMigrationLockThroughEntireUninstall()
    {
        var installer = Read("installer.iss");
        var initialize = installer[installer.IndexOf("function InitializeUninstall:", StringComparison.Ordinal)..
            installer.IndexOf("procedure DeinitializeUninstall;", StringComparison.Ordinal)];
        Assert.Contains("#ifndef DevBuild", initialize);
        Assert.Contains(@"{userappdata}\{#MyInstallDir}\store-migration", initialize);
        Assert.Contains(@"'\prepare.lock'", initialize);
        Assert.Contains("Result := MigrationPathIsOrdinary(LockPath);", initialize);
        Assert.Contains("Result := ForceDirectories(Directory);", initialize);
        Assert.Matches(@"OpenMigrationOperationFile\(\s*LockPath, \$80000000, 1, 0, 4, \$80, 0\)", initialize);
        Assert.Contains("Result := MigrationOperationHandle <> THandle(-1);", initialize);
        Assert.Contains("MigrationOperationLocked := Result;", initialize);
        Assert.Contains("if not Result then", initialize);
        Assert.Matches(@"procedure DeinitializeUninstall;\s*begin\s*if MigrationOperationLocked then\s*begin\s*" +
                       @"CloseMigrationOperationFile\(MigrationOperationHandle\);", installer);
        Assert.Single(Regex.Matches(installer, @"CloseMigrationOperationFile\(MigrationOperationHandle\)"));
    }

    [Fact]
    public void RecordPackageIdentity_MatchesStoreManifest()
    {
        var manifest = System.Xml.Linq.XDocument.Parse(Read("src", "OpenClaw.Tray.WinUI", "Package.appxmanifest"));
        var identity = manifest.Root!.Elements().Single(element => element.Name.LocalName == "Identity");
        Assert.Equal(OpenClaw.Connection.Migration.MigrationRecordCodec.PackageName, identity.Attribute("Name")!.Value);
        Assert.Equal(OpenClaw.Connection.Migration.MigrationRecordCodec.PackagePublisher, identity.Attribute("Publisher")!.Value);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(segments).ToArray()));
}
