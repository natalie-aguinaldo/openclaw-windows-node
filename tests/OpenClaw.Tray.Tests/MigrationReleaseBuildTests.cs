using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class MigrationReleaseBuildTests
{
    private const string TestProductId = "9EXAMPLE0000";
    private const string TestSourceVersion = "2026.9.5.0";

    [Theory]
    [InlineData("Debug", false, false)]
    [InlineData("Debug", true, true)]
    [InlineData("Release", false, false)]
    [InlineData("Release", false, true)]
    [InlineData("Release", true, true)]
    public async Task OrdinaryBuilds_RemainDisabledEvenWithReleaseValues(
        string configuration, bool devBuild, bool packaged)
    {
        var result = await EvaluateAsync(new()
        {
            ["Configuration"] = configuration,
            ["DevBuild"] = devBuild.ToString(),
            ["PackageMsix"] = packaged.ToString(),
            ["RuntimeIdentifier"] = "win-x64",
            ["MigrationStoreProductId"] = TestProductId,
            ["MigrationMinimumSourceVersion"] = TestSourceVersion
        });

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("false", json.RootElement.GetProperty("Properties").GetProperty("MigrationReleaseEnabled").GetString());
        Assert.DoesNotContain("_MIGRATION_RELEASE", Constants(json));
        Assert.Empty(MigrationMetadata(json));
    }

    [Theory]
    [InlineData(false, "win-x64", "INNO_MIGRATION_RELEASE", "STORE_MIGRATION_RELEASE")]
    [InlineData(true, "win-x64", "STORE_MIGRATION_RELEASE", "INNO_MIGRATION_RELEASE")]
    [InlineData(false, "win-arm64", "INNO_MIGRATION_RELEASE", "STORE_MIGRATION_RELEASE")]
    [InlineData(true, "win-arm64", "STORE_MIGRATION_RELEASE", "INNO_MIGRATION_RELEASE")]
    public async Task ExplicitRelease_EmitsOnlyMatchingHostAndExactMetadata(
        bool packaged, string runtime, string included, string excluded)
    {
        var properties = ReleaseProperties();
        properties["PackageMsix"] = packaged.ToString();
        properties["RuntimeIdentifier"] = runtime;
        var result = await EvaluateAsync(properties);

        Assert.True(result.ExitCode == 0, result.Output);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Contains(included, Constants(json));
        Assert.DoesNotContain(excluded, Constants(json));
        Assert.DoesNotContain("_MIGRATION_PREVIEW", Constants(json));
        var metadata = MigrationMetadata(json);
        Assert.Equal(2, metadata.Count);
        Assert.Equal(TestProductId, metadata["MigrationStoreProductId"]);
        Assert.Equal(TestSourceVersion, metadata["MigrationMinimumSourceVersion"]);
    }

    [Theory]
    [InlineData("MigrationReleaseEnabled", "yes", "OCMIG001")]
    [InlineData("MigrationReleaseEnabled", "on", "OCMIG001")]
    [InlineData("Configuration", "Debug", "OCMIG002")]
    [InlineData("DevBuild", "true", "OCMIG002")]
    [InlineData("StoreMigrationPreview", "true", "OCMIG003")]
    [InlineData("InnoMigrationPreview", "true", "OCMIG003")]
    [InlineData("RuntimeIdentifier", "win-x86", "OCMIG004")]
    [InlineData("PackageMsix", "yes", "OCMIG005")]
    [InlineData("MigrationStoreProductId", "", "OCMIG006")]
    [InlineData("MigrationStoreProductId", "9example0000", "OCMIG006")]
    [InlineData("MigrationStoreProductId", "9EXAMPLE0000X", "OCMIG006")]
    [InlineData("MigrationStoreProductId", "9EXAMPLE0000\n", "OCMIG006")]
    [InlineData("MigrationMinimumSourceVersion", "", "OCMIG007")]
    [InlineData("MigrationMinimumSourceVersion", "2026.9.5-alpha.10", "OCMIG007")]
    [InlineData("MigrationMinimumSourceVersion", "2026.9", "OCMIG007")]
    [InlineData("MigrationMinimumSourceVersion", "2026.9.5.0.1", "OCMIG007")]
    [InlineData("MigrationMinimumSourceVersion", "0.0.0", "OCMIG007")]
    [InlineData("MigrationMinimumSourceVersion", "2026.9.5\n", "OCMIG007")]
    public async Task IncompleteOrUnsupportedRelease_FailsBeforeCompilation(string key, string value, string code)
    {
        var properties = ReleaseProperties();
        properties[key] = value;
        var result = await EvaluateAsync(properties);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(code, result.Output);
    }

    [Fact]
    public async Task NumericOverflow_CannotBecomeAReleaseFloor()
    {
        var properties = ReleaseProperties();
        properties["MigrationMinimumSourceVersion"] = "2026.9.2147483648";
        var result = await EvaluateAsync(properties);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("MigrationMinimumSourceVersion", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewValues_CannotAuthorizeRelease(bool packaged)
    {
        var properties = ReleaseProperties();
        properties["PackageMsix"] = packaged.ToString();
        properties.Remove("MigrationStoreProductId");
        properties.Remove("MigrationMinimumSourceVersion");
        properties["MigrationPreviewStoreProductId"] = TestProductId;
        properties["StoreMigrationPreviewMinimumSourceVersion"] = TestSourceVersion;
        var result = await EvaluateAsync(properties);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OCMIG006", result.Output);
    }

    [Theory]
    [InlineData(false, "INNO_MIGRATION_PREVIEW")]
    [InlineData(true, "STORE_MIGRATION_PREVIEW")]
    public async Task ExistingDebugPreviews_RemainIndependent(bool packaged, string symbol)
    {
        var result = await EvaluateAsync(new()
        {
            ["Configuration"] = "Debug",
            ["DevBuild"] = "false",
            ["PackageMsix"] = packaged.ToString(),
            ["RuntimeIdentifier"] = "win-x64",
            [packaged ? "StoreMigrationPreview" : "InnoMigrationPreview"] = "true",
            ["MigrationPreviewStoreProductId"] = TestProductId,
            ["StoreMigrationPreviewMinimumSourceVersion"] = TestSourceVersion
        });
        Assert.True(result.ExitCode == 0, result.Output);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Contains(symbol, Constants(json));
        Assert.DoesNotContain("_MIGRATION_RELEASE", Constants(json));
        Assert.DoesNotContain("MigrationStoreProductId", MigrationMetadata(json).Keys);
        Assert.DoesNotContain("MigrationMinimumSourceVersion", MigrationMetadata(json).Keys);
    }

    [Fact]
    public void ReleaseValidation_IsInTheActualCompilePath()
    {
        var project = XDocument.Load(ProjectPath);
        var target = project.Descendants("Target").Single(element =>
            (string?)element.Attribute("Name") == "ValidateMigrationRelease");
        Assert.Equal("CoreCompile", (string?)target.Attribute("BeforeTargets"));
        Assert.DoesNotContain(project.Descendants("MigrationReleaseEnabled"), element => element.Value == "true");
        Assert.Empty(project.Descendants("MigrationStoreProductId"));
        Assert.Empty(project.Descendants("MigrationMinimumSourceVersion"));
    }

    private static string ProjectPath => Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
        "src", "OpenClaw.Tray.WinUI", "OpenClaw.Tray.WinUI.csproj");

    private static Dictionary<string, string> ReleaseProperties() => new()
    {
        ["MigrationReleaseEnabled"] = "true",
        ["Configuration"] = "Release",
        ["DevBuild"] = "false",
        ["PackageMsix"] = "false",
        ["RuntimeIdentifier"] = "win-x64",
        ["MigrationStoreProductId"] = TestProductId,
        ["MigrationMinimumSourceVersion"] = TestSourceVersion
    };

    private static string Constants(JsonDocument json) =>
        json.RootElement.GetProperty("Properties").GetProperty("DefineConstants").GetString()!;

    private static Dictionary<string, string> MigrationMetadata(JsonDocument json) =>
        json.RootElement.GetProperty("Items").GetProperty("AssemblyAttribute").EnumerateArray()
            .Where(item => item.TryGetProperty("_Parameter1", out var key) &&
                key.GetString()!.StartsWith("Migration", StringComparison.Ordinal))
            .ToDictionary(item => item.GetProperty("_Parameter1").GetString()!,
                item => item.GetProperty("_Parameter2").GetString()!);

    private static async Task<(int ExitCode, string Output)> EvaluateAsync(Dictionary<string, string> properties)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = TestRepositoryPaths.GetRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "msbuild", ProjectPath, "-nologo", "-v:q",
            "-t:ValidateMigrationRelease;ValidateInnoMigrationPreview;ValidateStoreMigrationPreview",
            "-getProperty:MigrationReleaseEnabled,DefineConstants", "-getItem:AssemblyAttribute"
        })
            startInfo.ArgumentList.Add(argument);
        foreach (var (key, value) in properties)
            startInfo.ArgumentList.Add($"-p:{key}={value}");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Migration build evaluation timed out.\n{await stdout}\n{await stderr}");
        }
        return (process.ExitCode, (await stdout) + (await stderr));
    }
}
