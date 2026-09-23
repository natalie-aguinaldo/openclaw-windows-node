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
        Assert.Matches(@"#if !STORE_MIGRATION_PREVIEW && !PRODUCTION_MIGRATION\s+return false;\s+#else", helper);
        Assert.Contains("new StoreMigrationWorkflow(", helper);
        Assert.Contains("new StoreMigrationWindow(", helper);
        Assert.Contains("return !await window.ShowAsync()", helper);
        Assert.DoesNotContain("MessageBoxW", helper);
        helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "StoreMigrationOperations.cs");
        Assert.Contains("new StoreMigrationStartupCoordinator(", helper);
        Assert.Contains("new StoreMigrationConsentCoordinator(", helper);
        Assert.Contains("MigrationRecordCodec.PackageName", helper);
        Assert.Contains("MigrationRecordCodec.PackagePublisher", helper);
        Assert.DoesNotContain("File.Write", helper);
        Assert.DoesNotContain("SetPackagedAutoStartAsync", helper);
        Assert.Contains("new InnoMutexLeaseProvider()", helper);
        Assert.Contains("new StoreMigrationAdoptionPreparationCoordinator(", helper);
        Assert.Contains("new MigrationPreparation(_binding!)", helper);
        Assert.Contains("new StoreMigrationCompletionCoordinator(", helper);
        var workflow = Read("src", "OpenClaw.Tray.WinUI", "Services", "StoreMigrationWorkflow.cs");
        Assert.Contains("StoreMigrationCompletionState.InnoRunning => StoreMigrationStage.CloseSource", workflow);
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
        Assert.Contains("#if INNO_MIGRATION_PREVIEW || PRODUCTION_MIGRATION", helper);
        Assert.Contains("!AppIdentity.IsDev && !PackageHelper.IsPackaged", helper);
        Assert.Contains("!GatewayFixtureIsolation.IsEnabled", helper);
        Assert.Contains(".HasValidConsent(installation.Version.ToString())", helper);
        Assert.Contains("Environment.ProcessPath", helper);
        var settings = Read("src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml.cs");
        var settingsXaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml");
        Assert.Contains("x:Name=\"StoreMigrationCard\"", settingsXaml);
        Assert.Contains("Migration2_InnoRecommendation", settingsXaml);
        Assert.Contains("Migration2_InnoCardTitle", settingsXaml);
        Assert.Contains("Migration2_InnoCardDescription", settingsXaml);
        var card = settingsXaml[
            settingsXaml.IndexOf("x:Name=\"StoreMigrationCard\"", StringComparison.Ordinal)..
            settingsXaml.IndexOf("<InfoBar x:Name=\"StoreMigrationStatus\"", StringComparison.Ordinal)];
        Assert.Contains("x:Name=\"StoreMigrationAction\"", card);
        Assert.Contains("Style=\"{StaticResource AccentButtonStyle}\"", card);
        Assert.Contains("<Button.ContentTemplate>", card);
        // The card carries no decorative icon tile so its text column aligns with
        // every other settings row, and the action shares that single row.
        Assert.DoesNotContain("<FontIcon", card);
        // HubWindow enforces MinWidth=1000, so any AdaptiveTrigger below that is
        // permanently satisfied and its fallback state can never render. Keeping
        // one here would assert behavior no proof run can ever observe.
        Assert.DoesNotContain("<AdaptiveTrigger", settingsXaml);
        Assert.DoesNotContain("<VisualStateManager.VisualStateGroups>", settingsXaml);
        Assert.Contains("HorizontalAlignment=\"Right\"", card);
        Assert.True(card.IndexOf("Migration2_InnoRecommendation", StringComparison.Ordinal) <
            card.IndexOf("Migration2_InnoCardTitle", StringComparison.Ordinal));
        Assert.Contains("StoreMigrationCard.Visibility = InnoMigrationHandoff.IsAvailable", settings);
        Assert.Contains("LocalizationHelper.GetString(\"Migration2_InnoConsentTitle\")", settings);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", settings);
        Assert.True(settings.IndexOf("await confirmation.ShowAsync()", StringComparison.Ordinal) <
            settings.IndexOf("await InnoMigrationHandoff.GrantAndLaunchAsync()", StringComparison.Ordinal));
        Assert.True(helper.IndexOf(".Grant(", StringComparison.Ordinal) <
            helper.IndexOf("Launcher.LaunchUriAsync", StringComparison.Ordinal));
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        Assert.Contains("InnoMigrationHandoff.CreateShutdownHandler(_dispatcherQueue!, ExitApplication)", app);
        Assert.DoesNotContain("Process.Kill", helper);

        var project = System.Xml.Linq.XDocument.Parse(Read("src", "OpenClaw.Tray.WinUI", "Migration.Build.props"));
        var target = project.Descendants("Target").Single(element =>
            (string?)element.Attribute("Name") == "ValidateInnoMigrationPreview");
        Assert.Contains("'$(Configuration)' != 'Debug'", target.ToString());
        Assert.Contains("'$(PackageMsix)' == 'true'", target.ToString());
        Assert.DoesNotContain(project.Descendants("InnoMigrationPreview"), element => element.Value == "true");
        Assert.Empty(project.Descendants("MigrationPreviewStoreProductId"));
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
        var project = System.Xml.Linq.XDocument.Parse(Read("src", "OpenClaw.Tray.WinUI", "Migration.Build.props"));
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

    [Theory]
    [InlineData("en-us", "blocked from starting normally", "uninstall the previous app", "This preview will not change your setup yet.", "This preview has not changed your setup.")]
    [InlineData("fr-fr", "ne pourra plus démarrer normalement", "désinstaller l'application précédente", "Cet aperçu ne modifiera pas encore votre configuration.", "Cet aperçu n'a pas modifié votre configuration.")]
    [InlineData("nl-nl", "niet meer normaal kunnen starten", "de vorige app verwijderen", "Dit voorbeeld wijzigt uw configuratie nog niet.", "Dit voorbeeld heeft uw configuratie niet gewijzigd.")]
    [InlineData("pt-br", "não poderá mais iniciar normalmente", "desinstalar o aplicativo anterior", "Esta prévia ainda não alterará sua configuração.", "Esta prévia não alterou sua configuração.")]
    [InlineData("zh-cn", "旧版应用将无法正常启动", "卸载旧版应用", "此预览尚不会更改您的配置。", "此预览未更改您的配置。")]
    [InlineData("zh-tw", "舊版應用程式將無法正常啟動", "解除安裝舊版應用程式", "此預覽尚不會變更您的設定。", "此預覽未變更您的設定。")]
    public void Consent_DisclosesStartupBlockAndRequiredRemovalInEveryLocale(
        string locale, string startupBlock, string removal, string oldConsent, string oldRetry)
    {
        var resources = System.Xml.Linq.XDocument.Parse(
            Read("src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"));
        string Value(string key) => resources.Root!.Elements("data")
            .Single(element => (string?)element.Attribute("name") == key).Element("value")!.Value;

        var yes = Value("Migration_StoreYes");
        var no = Value("Migration_StoreNo");
        Assert.DoesNotContain(":", yes + no);
        Assert.DoesNotContain("：", yes + no);
        var consent = string.Format(Value("Migration_StoreConsent"), yes, no);
        var retry = string.Format(Value("Migration_StoreCloseInno"), yes, no);
        Assert.Contains(startupBlock, consent);
        Assert.Contains(removal, consent);
        Assert.Contains(yes, consent);
        Assert.Contains(no, consent);
        Assert.Contains(yes, retry);
        Assert.Contains(no, retry);
        Assert.Contains("{0}", Value("Migration_StoreConsent"));
        Assert.Contains("{1}", Value("Migration_StoreConsent"));
        Assert.Contains("{0}", Value("Migration_StoreCloseInno"));
        Assert.Contains("{1}", Value("Migration_StoreCloseInno"));
        Assert.DoesNotContain(oldConsent, consent);
        Assert.DoesNotContain(oldRetry, Value("Migration_StoreCloseInno"));
        if (locale == "en-us")
        {
            Assert.Contains("protected migration records", consent);
            Assert.Contains("If validation succeeds", consent);
            Assert.Contains("reopen the Store app to finish migration", consent);
            Assert.Contains("Your setup and gateway will be preserved", consent);
            Assert.Contains("nothing is uninstalled automatically", consent);
            Assert.Contains("without starting migration", consent);
            Assert.Contains("Uninstall only after", Value("Migration_StoreCloseInno"));
        }
    }

    [Fact]
    public void StoreMigrationChoices_UseDirectButtonLabelsWithoutNativeActionLegends()
    {
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "StoreMigrationStartupGuard.cs");
        Assert.DoesNotContain("ShowChoice(", helper);
        Assert.DoesNotContain("MessageBoxW", helper);
        var window = Read("src", "OpenClaw.Tray.WinUI", "Windows", "StoreMigrationWindow.xaml.cs");
        Assert.Contains("Primary.Content = LocalizationHelper.GetString(", window);
        Assert.Contains("Dismiss.Content = LocalizationHelper.GetString(", window);
        Assert.Contains("\"Migration_StoreMigrate\" : \"Migration_StoreRetry\"", window);
        Assert.Contains("\"Migration_StoreNotNow\" : \"Migration2_Close\"", window);
        Assert.DoesNotContain("Migration_StoreYes", window);
        Assert.DoesNotContain("Migration_StoreNo\"", window);
    }

    [Fact]
    public void CompletedMigrationGuard_PrecedesSettingsAndActivation()
    {
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        var launch = app[app.IndexOf("private async Task OnLaunchedAsync", StringComparison.Ordinal)..];
        var guard = launch.IndexOf("InnoMigrationStartupGuard.ShouldStopLaunch(out _innoMigrationLease)", StringComparison.Ordinal);
        Assert.True(guard > launch.IndexOf("await CliUninstallHandler.RunAsync", StringComparison.Ordinal));
        Assert.True(guard > launch.IndexOf("_mutex = new Mutex(true, mutexName", StringComparison.Ordinal));
        Assert.Contains("if (ownsMutex && InnoMigrationStartupGuard.ShouldStopLaunch(out _innoMigrationLease))", launch);
        Assert.True(guard < launch.IndexOf("new ActivationRouter(", StringComparison.Ordinal));
        Assert.True(guard < launch.IndexOf("new SettingsManager()", StringComparison.Ordinal));
        Assert.DoesNotContain("MigrationRecordCodec", app);
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "InnoMigrationStartupGuard.cs");
        Assert.Contains("#if !INNO_MIGRATION_PREVIEW && !PRODUCTION_MIGRATION", helper);
        Assert.Contains("AppIdentity.IsDev || PackageHelper.IsPackaged", helper);
        Assert.Contains("MigrationRecordCodec.ReadCompletion", helper);
        Assert.Contains("Migration_InnoCompleted", helper);
        Assert.Contains("GatewayFixtureIsolation.IsEnabled", helper);
        var acquire = helper.IndexOf("runtimeLease = MigrationOperationLock.AcquireRuntime(binding)", StringComparison.Ordinal);
        Assert.True(acquire > 0 && acquire < helper.IndexOf("MigrationRecordCodec.ReadCompletion", StringComparison.Ordinal));
        var lockFailure = helper[acquire..helper.IndexOf("var path =", StringComparison.Ordinal)];
        Assert.Contains("InvalidDataException", lockFailure);
        // Only a readable receipt may block startup; an unavailable lease must not.
        Assert.DoesNotContain("ShowGuidance", lockFailure);
        var receiptRejected = helper[helper.IndexOf("Migration completion receipt rejected", StringComparison.Ordinal)..];
        Assert.Contains("return false;", receiptRejected);
        Assert.DoesNotContain("_innoMigrationLease", Read("src", "OpenClaw.Tray.WinUI", "App.AppShutdownCoordinator.cs"));
        Assert.DoesNotContain("_innoMigrationLease?.Dispose", app);
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
    public void CleanupScript_RefusesToDeleteThroughAReparsePoint()
    {
        var script = Read("scripts", "Uninstall-LocalGateway.ps1");
        var cleanup = script[script.IndexOf("$identityDir = Join-Path $gatewaysDir $id", StringComparison.Ordinal)..];
        var guard = cleanup.IndexOf("[System.IO.FileAttributes]::ReparsePoint", StringComparison.Ordinal);
        var delete = cleanup.IndexOf("Remove-Item -LiteralPath $identityDir -Recurse", StringComparison.Ordinal);

        // Remove-Item -Recurse follows junctions on Windows PowerShell 5.1, so the guard has
        // to run first or the recursive delete destroys whatever the junction targets.
        Assert.True(guard > 0, "Identity cleanup must reject reparse points.");
        Assert.True(guard < delete, "The reparse-point guard must precede the recursive delete.");
        Assert.Contains(@"\A[A-Za-z0-9._-]+\z", cleanup);

        // Checking only the leaf is not enough: a junction at 'gateways' redirects the whole
        // subtree while the identity directory itself still looks ordinary.
        var walk = cleanup.IndexOf("$probe = $identityDir", StringComparison.Ordinal);
        Assert.True(walk > 0 && walk < delete, "The guard must walk ancestors, not just the leaf.");
        Assert.Contains("Split-Path -Path $probe -Parent", cleanup);
    }

    [Fact]
    public void Installer_StopsTheReparseWalkAtAUncShareRoot()
    {
        var installer = Read("installer.iss");
        var walk = installer[installer.IndexOf("function MigrationPathIsOrdinary", StringComparison.Ordinal)..
            installer.IndexOf("function InitializeUninstall:", StringComparison.Ordinal)];

        // ExtractFileDir has no fixed point on a UNC path: it yields '\\server', which is not
        // a filesystem object. Probing it fails with a code that is neither 2 nor 3, so the
        // guard would Exit False and refuse the uninstall on redirected AppData.
        Assert.Contains("MigrationPathIsUncRoot", installer);
        var stop = walk.IndexOf("if MigrationPathIsUncRoot(Path) then", StringComparison.Ordinal);
        var ascend = walk.IndexOf("Parent := ExtractFileDir(Path);", StringComparison.Ordinal);
        Assert.True(stop > 0, "The walk must recognise a UNC share root.");
        Assert.True(stop < ascend, "The walk must stop at the share root before ascending past it.");
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
    public void Installer_ReadsAttributeFailureAsSignedSentinel()
    {
        var installer = Read("installer.iss");
        var scan = installer[installer.IndexOf("function MigrationPathIsOrdinary", StringComparison.Ordinal)..
            installer.IndexOf("function InitializeUninstall:", StringComparison.Ordinal)];

        // Pascal Script resolves the type of an unsigned $FFFFFFFF literal
        // inconsistently, so the failure sentinel is compared as a signed -1.
        Assert.Contains("function MigrationPathAttributes(FileName: String): Integer;", installer);
        Assert.Contains("Attributes: Integer;", scan);
        Assert.Contains("if Attributes = -1 then", scan);
        Assert.DoesNotContain("= $FFFFFFFF", scan);
        Assert.Contains("else if (Attributes and $400) <> 0 then", scan);
    }

    [Fact]
    public void RecordPackageIdentity_MatchesStoreManifest()
    {
        var manifest = System.Xml.Linq.XDocument.Parse(Read("src", "OpenClaw.Tray.WinUI", "Package.appxmanifest"));
        var identity = manifest.Root!.Elements().Single(element => element.Name.LocalName == "Identity");
        Assert.Equal(OpenClaw.Connection.Migration.MigrationRecordCodec.PackageName, identity.Attribute("Name")!.Value);
        Assert.Equal(OpenClaw.Connection.Migration.MigrationRecordCodec.PackagePublisher, identity.Attribute("Publisher")!.Value);
    }

    [Theory]
    [InlineData("gateways")]
    [InlineData("identity")]
    public void CleanupScript_DoesNotDeleteThroughAJunction(string junctionAt)
    {
        // The shipped guard text is executed against a real junction. The source-order assertions
        // above passed while the guard still checked only the leaf, so they cannot stand alone.
        var temp = Path.Combine(Path.GetTempPath(), "oc-junction-" + Guid.NewGuid().ToString("N"));
        var sentinel = Path.Combine(temp, "valuable");
        var dataDir = Path.Combine(temp, "data");
        var gateways = Path.Combine(dataDir, "gateways");
        var identity = Path.Combine(gateways, "victim");
        try
        {
            Directory.CreateDirectory(dataDir);
            if (junctionAt == "gateways")
            {
                Directory.CreateDirectory(Path.Combine(sentinel, "victim"));
                File.WriteAllText(Path.Combine(sentinel, "victim", "keep.txt"), "keep");
                Junction(gateways, sentinel);
            }
            else
            {
                Directory.CreateDirectory(sentinel);
                File.WriteAllText(Path.Combine(sentinel, "keep.txt"), "keep");
                Directory.CreateDirectory(gateways);
                Junction(identity, sentinel);
            }

            var output = RunGuard(dataDir, identity);

            Assert.StartsWith("WARNED", output);
            Assert.True(File.Exists(Path.Combine(sentinel, junctionAt == "gateways" ? "victim" : "", "keep.txt")),
                $"The junction target was deleted through '{junctionAt}'.");
        }
        finally
        {
            TryRemove(gateways);
            TryRemove(identity);
            TryRemove(temp);
        }
    }

    [Fact]
    public void CleanupScript_StillDeletesAnOrdinaryIdentityDirectory()
    {
        var temp = Path.Combine(Path.GetTempPath(), "oc-junction-" + Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(temp, "data");
        var identity = Path.Combine(dataDir, "gateways", "victim");
        try
        {
            Directory.CreateDirectory(identity);
            File.WriteAllText(Path.Combine(identity, "device-key.json"), "{}");

            var output = RunGuard(dataDir, identity);

            // The guard must not become so strict that ordinary cleanup stops working.
            Assert.StartsWith("NOWARN", output);
            Assert.False(Directory.Exists(identity));
        }
        finally
        {
            TryRemove(temp);
        }
    }

    /// <summary>
    /// Runs the guard exactly as shipped, lifted out of the surrounding uninstall so the test does
    /// not touch WSL, scheduled tasks, or the registry.
    /// </summary>
    private static string RunGuard(string dataDir, string identityDir)
    {
        var script = Read("scripts", "Uninstall-LocalGateway.ps1");
        const string start = "if (Test-Path -LiteralPath $identityDir -PathType Container) {";
        const string end = "Write-GatewayLog \"Deleted identity directory for local gateway record $id.\"";
        var from = script.IndexOf(start, StringComparison.Ordinal);
        var to = script.IndexOf(end, StringComparison.Ordinal);
        Assert.True(from > 0 && to > from, "The identity cleanup guard could not be located.");
        var guard = script[from..(to + end.Length)] + "\n}";

        var harness = $$"""
            $ErrorActionPreference = 'Stop'
            $DataDir = '{{dataDir}}'
            $identityDir = '{{identityDir}}'
            $id = 'victim'
            $script:warnings = @()
            function Add-CleanupWarning { param($Message) $script:warnings += $Message }
            function Write-GatewayLog { param($Message) }
            foreach ($once in @(1)) {
            {{guard}}
            }
            if ($script:warnings) { "WARNED: $($script:warnings -join '; ')" } else { 'NOWARN' }
            """;

        var file = Path.Combine(Path.GetTempPath(), "oc-guard-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, harness);
        try
        {
            // Windows PowerShell 5.1 specifically: that is what Inno runs during uninstall, and
            // its Remove-Item is the one that follows junctions.
            var info = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{file}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"Guard harness failed: {error}");
            return output;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static void Junction(string link, string target)
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Could not create junction: {error}");
    }

    private static void TryRemove(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(segments).ToArray()));
}
