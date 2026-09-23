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
        var guard = launch.IndexOf("StoreMigrationStartupGuard.ShouldStopLaunch()", StringComparison.Ordinal);
        Assert.True(guard > launch.IndexOf("await CliUninstallHandler.RunAsync", StringComparison.Ordinal));
        Assert.True(guard < launch.IndexOf("GetProtocolActivationUri()", StringComparison.Ordinal));
        Assert.True(guard < launch.IndexOf("_mutex = new Mutex(", StringComparison.Ordinal));
        Assert.True(guard < launch.IndexOf("new ActivationRouter(", StringComparison.Ordinal));
        Assert.True(guard < launch.IndexOf("new SettingsManager()", StringComparison.Ordinal));
        Assert.DoesNotContain("new InnoInstallationDetector(", app);
        Assert.DoesNotContain("new MigrationStartupRecordReader(", app);
        Assert.DoesNotContain("new StoreMigrationStartupCoordinator(", app);

        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "StoreMigrationStartupGuard.cs");
        Assert.Matches(@"#if !STORE_MIGRATION_PREVIEW\s+return false;\s+#else", helper);
        Assert.Contains("new StoreMigrationStartupCoordinator(", helper);
        Assert.Contains("new StoreMigrationConsentCoordinator(", helper);
        Assert.Contains("decision.AllowsNormalStartup", helper);
        Assert.Contains("MigrationRecordCodec.PackageName", helper);
        Assert.Contains("MigrationRecordCodec.PackagePublisher", helper);
        Assert.DoesNotContain("File.Write", helper);
        Assert.DoesNotContain("SetPackagedAutoStartAsync", helper);
        Assert.Contains("Migration_StoreConsent", helper);
        Assert.Contains("Migration_StoreCloseInno", helper);
        Assert.Contains("new InnoMutexLeaseProvider()", helper);
        Assert.Contains("new StoreMigrationAdoptionPreparationCoordinator(", helper);
        Assert.Contains("new MigrationPreparation(binding)", helper);
        Assert.Contains("new StoreMigrationCompletionCoordinator(", helper);
        Assert.Contains("while (result.State == StoreMigrationCompletionState.InnoRunning)", helper);
        Assert.Contains("new StoreMigrationFinalizationCoordinator(", helper);
        Assert.Contains("new MigrationFinalizationRecordCleaner(", helper);
        Assert.Contains("new InnoSourceRemovalVerifier(binding, AppIdentity.MutexBaseName)", helper);
        Assert.Contains("new MigrationInventoryCapture(binding)", helper);
        Assert.Contains("records.Read().Status != MigrationStartupRecordStatus.Completed", helper);
        Assert.Contains("AutoStartManager.SetAutoStartAsync(enabled)", helper);
        Assert.Contains("Task.Run(() => AutoStartManager.SetAutoStartAsync(enabled)).ConfigureAwait(false)", helper);
        // A Windows startup refusal is a durable answer, not a transient failure. The applier must
        // translate it so finalization clears the receipt instead of blocking every future launch.
        Assert.Contains("catch (AutoStartRefusedException exception) when (exception.IsDurable)", helper);
        Assert.Contains(
            "throw new StoreMigrationAutoStartRefusedException(exception.Message, exception)",
            helper);
        Assert.Contains("new CredentialResolver(DeviceIdentityFileReader.Instance)", helper);
        Assert.Contains("0x00000124", helper);
        Assert.DoesNotContain("TaskDialogIndirect", helper);
        Assert.Contains("Migration_StoreValidationFailed", helper);
        Assert.Contains("Migration_StoreAwaitingInnoRemoval", helper);
        Assert.Contains("Migration_StoreFinalizationFailed", helper);
        Assert.Contains("Migration_StoreCredentialUnavailable", helper);
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

    /// <summary>
    /// The migration lock coordinates with a Store migration that most users will never run.
    /// It must never become a reason an ordinary user cannot start or uninstall the app: only
    /// a live contended lock may block, and an unreadable one must degrade instead.
    /// </summary>
    [Fact]
    public void InaccessibleMigrationLock_BlocksNeitherStartupNorUninstall()
    {
        var guard = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "InnoMigrationStartupGuard.cs");
        var acquire = guard.IndexOf("MigrationOperationLock.AcquireRuntime(binding)", StringComparison.Ordinal);
        var busy = guard.IndexOf("catch (IOException ex) when (MigrationOperationLock.IsBusy(ex))",
            StringComparison.Ordinal);
        Assert.True(busy > acquire, "Only a contended lock may block Inno startup.");
        var receipt = guard.IndexOf("MigrationRecordCodec.ReadCompletion", StringComparison.Ordinal);
        var degrade = guard.IndexOf("continuing without it", StringComparison.Ordinal);
        Assert.InRange(degrade, busy, receipt);
        // The degraded path must fall through to the receipt check rather than showing guidance,
        // because ShowGuidance returns true and would stop the launch it is meant to preserve.
        Assert.DoesNotContain("ShowGuidance", guard[degrade..receipt]);

        var installer = Read("installer.iss");
        var uninstall = installer[installer.IndexOf("function InitializeUninstall", StringComparison.Ordinal)..];
        uninstall = uninstall[..uninstall.IndexOf("procedure DeinitializeUninstall", StringComparison.Ordinal)];
        Assert.Matches(@"if \(LastError = 32\) or \(LastError = 33\) then\s+begin\s+Result := False;", uninstall);
        Assert.Single(Regex.Matches(uninstall, @"Result := False;"));
        Assert.Contains("MigrationOperationUnavailable := True;", uninstall);

        // An unavailable lock must still suppress destructive cleanup, since the cleanup child
        // cannot join a lock the uninstaller never obtained.
        var choice = installer[installer.IndexOf("procedure EnsureLocalGatewayCleanupChoice", StringComparison.Ordinal)..];
        var suppression = choice.IndexOf("if MigrationOperationUnavailable then", StringComparison.Ordinal);
        Assert.InRange(suppression, 0, choice.IndexOf("MigrationResult := CheckCompletedStoreMigration",
            StringComparison.Ordinal));
        Assert.Contains("WarnMigrationCheckUnavailable;", choice[suppression..]);
    }

    /// <summary>
    /// Preservation is a dead end unless the notice carries the removal path itself. No document
    /// in this repository is linked from the uninstaller, and the user may have no app left.
    /// </summary>
    [Fact]
    public void PreservationNotice_CarriesItsOwnRemovalInstructions()
    {
        var installer = Read("installer.iss");
        var warn = installer[installer.IndexOf("procedure WarnMigrationCheckUnavailable", StringComparison.Ordinal)..];
        warn = warn[..warn.IndexOf("procedure EnsureLocalGatewayCleanupChoice", StringComparison.Ordinal)];

        Assert.Contains("wsl --unregister {#MyDistroName}", warn);
        Assert.Contains("Settings > Local Gateway > ", warn);
        Assert.DoesNotContain("uninstall documentation", warn);
        // The silent path skips the dialog, so the log line is the only audit trail an
        // enterprise administrator gets. It must name what was left behind.
        var log = warn[..warn.IndexOf("if not UninstallSilent()", StringComparison.Ordinal)];
        Assert.Contains("{#MyDistroName}", log);
        Assert.Contains(@"{localappdata}\{#MyInstallDir}\wsl\{#MyDistroName}", log);

        var doc = Read("docs", "uninstall-portable.md");
        Assert.Contains("Installer Uninstall Preserved the Local Gateway", doc);
        Assert.Contains("wsl --unregister OpenClawGateway", doc);
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

    /// <summary>
    /// A durable startup refusal is invisible otherwise: finalization clears the receipt and the
    /// app launches normally, so the only signal the user ever gets that OpenClaw will never start
    /// at sign-in again would be a log line. The notice must be shown, and it must not block launch.
    /// </summary>
    [Fact]
    public void StartupRefusal_NotifiesTheUserWithoutBlockingLaunch()
    {
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "StoreMigrationStartupGuard.cs");
        var branch = helper[helper.IndexOf("if (finalization.AllowsNormalStartup)", StringComparison.Ordinal)..];
        var notice = branch.IndexOf(@"ShowGuidance(""Migration_StoreStartupRefused"")", StringComparison.Ordinal);
        Assert.True(notice > 0, "The refusal notice must be raised inside the allows-normal-startup branch.");
        Assert.Contains(
            "finalization.State == StoreMigrationFinalizationState.StartupPreferenceRefused",
            branch[..notice]);
        Assert.True(branch.IndexOf("return false;", StringComparison.Ordinal) > notice,
            "The refusal notice must be followed by return false so startup still proceeds.");
        Assert.DoesNotContain("StartupPreferenceFailed", helper);
    }

    [Theory]
    [InlineData("en-us", "Settings > Apps > Startup")]
    [InlineData("fr-fr", "Paramètres > Applications > Démarrage")]
    [InlineData("nl-nl", "Instellingen > Apps > Opstarten")]
    [InlineData("pt-br", "Configurações > Aplicativos > Inicializar")]
    [InlineData("zh-cn", "设置 > 应用 > 启动")]
    [InlineData("zh-tw", "設定 > 應用程式 > 啟動")]
    public void StartupRefusalNotice_PointsAtTheWindowsStartupSettingInEveryLocale(
        string locale, string startupSetting)
    {
        var resources = System.Xml.Linq.XDocument.Parse(
            Read("src", "OpenClaw.Tray.WinUI", "Strings", locale, "Resources.resw"));
        var notice = resources.Root!.Elements("data")
            .Single(element => (string?)element.Attribute("name") == "Migration_StoreStartupRefused")
            .Element("value")!.Value;

        Assert.Contains(startupSetting, notice);
        Assert.Contains("OpenClaw Companion", notice);
        Assert.DoesNotContain("—", notice);
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
    public void StorePreviewChoices_UseNativeYesNoWithoutActionLegends()
    {
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "StoreMigrationStartupGuard.cs");
        var choice = helper[helper.IndexOf("private static bool ShowChoice(", StringComparison.Ordinal)..
            helper.IndexOf("private static void ShowGuidance(", StringComparison.Ordinal)];
        Assert.Contains("ShowChoice(string contentKey)", choice);
        Assert.Contains("LocalizationHelper.Format(contentKey,", choice);
        Assert.DoesNotContain("primaryKey", choice);
        Assert.DoesNotContain("secondaryKey", choice);
        Assert.DoesNotContain("\\r\\n", choice);
        Assert.Contains("0x00000124) == 6", choice);
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
        Assert.Contains("AppIdentity.IsDev || PackageHelper.IsPackaged", helper);
        Assert.Contains("MigrationRecordCodec.ReadCompletion", helper);
        Assert.Contains("Migration_InnoCompleted", helper);
        Assert.Contains("GatewayFixtureIsolation.IsEnabled", helper);
        var acquire = helper.IndexOf("runtimeLease = MigrationOperationLock.AcquireRuntime(binding)", StringComparison.Ordinal);
        Assert.True(acquire > 0 && acquire < helper.IndexOf("MigrationRecordCodec.ReadCompletion", StringComparison.Ordinal));
        var lockFailure = helper[acquire..helper.IndexOf("var path =", StringComparison.Ordinal)];
        Assert.Contains("InvalidDataException", lockFailure);
        Assert.Contains("return ShowGuidance(\"Migration_InnoCheckFailed\")", lockFailure);
        Assert.DoesNotContain("_innoMigrationLease", Read("src", "OpenClaw.Tray.WinUI", "App.AppShutdownCoordinator.cs"));
        Assert.DoesNotContain("_innoMigrationLease?.Dispose", app);
    }

    [Fact]
    public void Installer_ChecksCompletionBeforeChoiceAndNeverDeletesPreservedState()
    {
        var installer = Read("installer.iss");
        Assert.Contains("Source: \"scripts\\Uninstall-LocalGateway.ps1\"", installer);
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
            @"else\s+WarnMigrationCheckUnavailable;\s+Exit;\s+end;\s+if UninstallSilent\(\)",
            installer);
        // An unverifiable migration state must be reported, not silently swallowed.
        Assert.Matches(
            @"procedure WarnMigrationCheckUnavailable;\s+begin\s+" +
            @"Log\([\s\S]*?\);\s+" +
            @"if not UninstallSilent\(\) then\s+MsgBox\(",
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
    public void CleanupScript_BoundsEveryChildProcessWait()
    {
        var script = Read("scripts", "Uninstall-LocalGateway.ps1");
        // A hung wsl.exe or checker must not block uninstall forever, and a
        // timed-out operation is indeterminate rather than successful.
        Assert.DoesNotMatch(@"-Wait\b", script);
        Assert.DoesNotMatch(@"(?m)^\s*(\$\w+ = )?Start-Process\b", script);
        Assert.Contains("$process.WaitForExit($TimeoutMilliseconds)", script);
        Assert.Contains("-TimeoutMilliseconds ($WslTimeoutSeconds * 1000)", script);
        Assert.Contains("-TimeoutMilliseconds ($MigrationCheckTimeoutSeconds * 1000)", script);
        Assert.Contains("did not finish within $WslTimeoutSeconds seconds", script);
        Assert.Contains("did not finish within $MigrationCheckTimeoutSeconds seconds", script);
    }

    [Fact]
    public void CleanupScript_KeepsCheckerDiagnostics()
    {
        var script = Read("scripts", "Uninstall-LocalGateway.ps1");
        // Exit 2 is an irreversible-decision input. Losing the checker's stderr
        // reduces every support case to an opaque exit code.
        Assert.Contains("Migration preservation check output:", script);
        Assert.Contains("Migration preservation check exited $migrationResult.", script);
        // Child arguments must use the shared quoting helper, not hand-rolled quotes.
        Assert.Contains("'-AppRoot', (ConvertTo-ProcessArgument $AppRoot)", script);
        AssertBoundedProcessHelper(script);
    }

    [Fact]
    public void CleanupScript_ReportsPreDestructiveFailuresAsUncertain()
    {
        var script = Read("scripts", "Uninstall-LocalGateway.ps1").ReplaceLineEndings("\n");
        // Read-only discovery stays inside the admission phase; only the two
        // genuinely destructive steps leave it. A deleted flag flip must fail here.
        Assert.Matches(
            @"function Enter-DestructivePhase \{\s+#[^\n]*\n\s+\$script:MigrationAdmissionPhase = \$false\s+\}",
            script);
        Assert.Matches(
            @"function Remove-GatewayDirectory \{\s+Enter-DestructivePhase",
            script);
        Assert.Matches(
            @"Enter-DestructivePhase\s+\$unregisterResult = Invoke-Wsl -Arguments @\('--unregister'",
            script);
        Assert.Equal(2, Regex.Matches(script, @"^\s*Enter-DestructivePhase\s*$",
            RegexOptions.Multiline).Count);
        Assert.Matches(
            @"\$failureExitCode = if \(\$script:MigrationAdmissionPhase\) \{ 2 \} else \{ 1 \}",
            script);
        Assert.Contains("exit $failureExitCode", script);
        // Locating wsl.exe and listing distros destroy nothing, so they must not
        // sit past the admission boundary in the main flow.
        var main = script[script.LastIndexOf("\ntry {", StringComparison.Ordinal)..];
        Assert.DoesNotMatch(
            @"Enter-DestructivePhase[\s\S]*?\$script:WslPath = Get-WslExePath",
            main);
    }

    [Fact]
    public void Installer_RoutesCheckerUncertaintyToPreservationNotRetry()
    {
        var installer = Read("installer.iss");
        // Exit 2 means "unknown", so offering Retry against an unchanging verdict
        // is misleading. It must reach the preservation notice instead.
        Assert.Matches(
            @"if Started and \(ResultCode = 2\) then\s+begin\s+" +
            @"WarnMigrationCheckUnavailable;\s+Exit;\s+end;",
            installer);
        // The installer's own failure codes must not collide with the script contract.
        Assert.DoesNotMatch(@"ResultCode := [0-9];", installer);
        // The preservation notice must not name files uninstall is about to delete.
        Assert.DoesNotContain("run Uninstall-LocalGateway.ps1 from", installer);
        // These helpers run during usUninstall, before Inno removes files, so they
        // need no retention flag. Retaining them stranded all three in {app} on
        // every uninstall that kept the local gateway, including the ordinary
        // non-migration "keep my gateway" choice.
        foreach (var helper in new[]
        {
            "scripts\\Uninstall-LocalGateway.ps1", "scripts\\Test-InnoMigration.ps1",
            "src\\OpenClaw.Connection\\Migration\\MigrationRecordCodec.cs"
        })
            Assert.DoesNotMatch(
                Regex.Escape($"Source: \"{helper}\"") + @"[^\n]*uninsneveruninstall",
                installer);
    }

    [Fact]
    public void Checker_ReportsUncertaintyWhenItsWatchdogCannotStart()
    {
        var script = Read("scripts", "Test-InnoMigration.ps1");
        var start = script.IndexOf("-TimeoutMilliseconds ($TimeoutSeconds * 1000)", StringComparison.Ordinal);
        var handler = script[start..script.IndexOf("if ($null -ne $watchdogResult)", start, StringComparison.Ordinal)];
        // Falling back to the inline Add-Type check would reintroduce the unbounded
        // stall the watchdog exists to prevent, and installer.iss waits for this
        // process with ewWaitUntilTerminated.
        Assert.Contains("exit 2", handler);
        Assert.DoesNotContain("$watchdogResult = $null", handler);
    }

    [Fact]
    public void Checker_TreatsUnverifiableReceiptAsPreserveNotAbsent()
    {
        var script = Read("scripts", "Test-InnoMigration.ps1");
        var handler = script[script.LastIndexOf("} catch {", StringComparison.Ordinal)..];
        // Only a genuinely absent receipt may authorize the existing uninstall
        // policy. Schema, DPAPI, or binding drift must fail closed.
        Assert.Contains("[IO.FileNotFoundException]", handler);
        Assert.Contains("[IO.DirectoryNotFoundException]", handler);
        Assert.DoesNotContain("CryptographicException", handler);
        Assert.DoesNotContain("InvalidDataException", handler);
        Assert.Single(Regex.Matches(handler, @"exit 0"));
        Assert.Contains("exit 2", handler);
        Assert.Contains("-TimeoutMilliseconds ($TimeoutSeconds * 1000)", script);
        // $PSHOME resolves to the PowerShell 7 directory under pwsh, which has no
        // powershell.exe. The watchdog must name Windows PowerShell explicitly.
        Assert.DoesNotContain("Join-Path $PSHOME", script);
        Assert.Contains("System32\\WindowsPowerShell\\v1.0\\powershell.exe", script);
        Assert.Contains("$watchdogResult.Output.Trim()", script);
        AssertBoundedProcessHelper(script);
    }

    private static void AssertBoundedProcessHelper(string script)
    {
        // Start-Process -PassThru returns a null ExitCode once output is redirected,
        // which silently turned every verdict into exit 0. Drive the process directly,
        // and start both reads before waiting so a full pipe cannot deadlock.
        Assert.Contains("$psi.UseShellExecute = $false", script);
        Assert.Contains("[System.Diagnostics.Process]::Start($psi)", script);
        Assert.Matches(
            @"\$stdout = \$process\.StandardOutput\.ReadToEndAsync\(\)\s+" +
            @"\$stderr = \$process\.StandardError\.ReadToEndAsync\(\)\s+\r?\n?\s*" +
            @"if \(-not \$process\.WaitForExit\(\$TimeoutMilliseconds\)\)",
            script);
        Assert.Contains("ExitCode = [int]$process.ExitCode", script);
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
        Assert.Contains("if not MigrationPathIsOrdinary(LockPath) then", initialize);
        Assert.Contains("else if ForceDirectories(Directory) then", initialize);
        // A rejected reparse path must never reach ForceDirectories, so the two checks
        // stay sequential rather than relying on Pascal Script short-circuit evaluation.
        Assert.DoesNotContain("MigrationPathIsOrdinary(LockPath) and", initialize);
        Assert.Matches(@"OpenMigrationOperationFile\(\s*LockPath, \$80000000, 1, 0, 4, \$80, 0\)", initialize);
        Assert.Matches(
            @"if MigrationOperationHandle <> THandle\(-1\) then\s+begin\s+" +
            @"MigrationOperationLocked := True;\s+MigrationOperationUnavailable := False;",
            initialize);
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
