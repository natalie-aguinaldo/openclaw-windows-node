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
        Assert.Contains("new StoreMigrationFinalizationCoordinator(", helper);
        Assert.Contains("new MigrationFinalizationRecordCleaner(", helper);
        Assert.Contains("new InnoSourceRemovalVerifier(binding, AppIdentity.MutexBaseName)", helper);
        Assert.Contains("new MigrationInventoryCapture(binding)", helper);
        Assert.Contains("records.Read().Status != MigrationStartupRecordStatus.Completed", helper);
        Assert.Contains("AutoStartManager.SetAutoStartAsync(enabled)", helper);
        Assert.Contains("Task.Run(() => AutoStartManager.SetAutoStartAsync(enabled)).ConfigureAwait(false)", helper);
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
