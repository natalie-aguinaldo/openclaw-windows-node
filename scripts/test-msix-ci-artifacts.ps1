<#
.SYNOPSIS
    Exercises Dev CI artifact validation and build argument contracts.
.DESCRIPTION
    Uses synthetic ZIPs and an in-memory signer. Authenticode is stubbed here;
    the packaging job separately verifies the actual Windows signature. No
    certificate is installed or trusted and no product build is launched.
#>
[CmdletBinding()]
param([string]$RepoRoot = (Split-Path $PSScriptRoot -Parent))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$exporter = Join-Path $RepoRoot 'scripts\Export-DevMsixArtifact.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-msix-ci-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$rsa = [Security.Cryptography.RSA]::Create(2048)
$request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
    'CN=OpenClaw Local Development', $rsa,
    [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1), [DateTimeOffset]::UtcNow.AddDays(1))
$signatureStatus = 'Valid'
$scenarioNumber = 0

function Get-AuthenticodeSignature {
    param([string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath)) { throw 'Signature probe received a missing package.' }
    [pscustomobject]@{ Status = $signatureStatus; SignerCertificate = $certificate }
}

function Assert-Fails {
    param([scriptblock]$Action, [string]$Expected)
    try {
        & $Action
        throw 'The operation unexpectedly succeeded.'
    }
    catch {
        if (-not $_.Exception.Message.Contains($Expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Expected failure containing '$Expected', received: $($_.Exception.Message)"
        }
    }
}

function New-Package {
    param(
        [string]$Directory,
        [string]$Name = 'Dev.msix',
        [string]$Identity = 'OpenClawFoundation.OpenClaw.Dev',
        [string]$Publisher = 'CN=OpenClaw Local Development',
        [string]$Architecture = 'x64',
        [string]$Version = '2026.7.2.123',
        [string]$Omit = ''
    )
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $Directory $Name), [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @(
            'AppxManifest.xml', 'AppxSignature.p7x', 'OpenClaw.Tray.WinUI.exe', 'OpenClaw.Tray.WinUI.dll',
            'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'Microsoft.ui.xaml.dll',
            'OpenClaw.SetupEngine.dll', 'OpenClaw.SetupEngine.UI.dll', "tools/mxc/$Architecture/wxc-exec.exe"
        )) {
            if ($name -eq $Omit) { continue }
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open())
            try {
                $content = if ($name -eq 'AppxManifest.xml') {
                    "<Package><Identity Name=`"$Identity`" Publisher=`"$Publisher`" ProcessorArchitecture=`"$Architecture`" Version=`"$Version`" /></Package>"
                } else { 'Synthetic contract-test content, not executable.' }
                $writer.Write($content)
            }
            finally { $writer.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

function New-Arguments {
    $script:scenarioNumber++
    @{
        Architecture = 'x64'
        PackageDirectory = Join-Path $temporaryRoot "input-$scenarioNumber"
        ExpectedRevision = 123
        ExpectedVersion = '2026.7.2-alpha.4'
        CertificateThumbprint = $certificate.Thumbprint
        OutputDirectory = Join-Path $temporaryRoot "output-$scenarioNumber"
    }
}

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($architecture in @('x64', 'arm64')) {
        $arguments = New-Arguments
        $arguments.Architecture = $architecture
        New-Package -Directory $arguments.PackageDirectory -Architecture $architecture
        # Unrelated build output must not be uploaded, even if it contains a key.
        Set-Content (Join-Path $arguments.PackageDirectory 'do-not-publish.pfx') 'not a real key'
        & $exporter @arguments
        $packageName = "OpenClaw-Dev-$architecture.msix"
        $expectedFiles = @('INSTALL.txt', 'msix-metadata.json', 'OpenClaw-Dev.cer', $packageName)
        $files = @(Get-ChildItem -LiteralPath $arguments.OutputDirectory -File | Select-Object -ExpandProperty Name)
        if (@(Compare-Object $expectedFiles $files).Count -gt 0) {
            throw "Unexpected Dev artifact contents: $($files -join ',')"
        }
        $metadata = Get-Content (Join-Path $arguments.OutputDirectory 'msix-metadata.json') -Raw | ConvertFrom-Json
        $publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            (Join-Path $arguments.OutputDirectory 'OpenClaw-Dev.cer'))
        try {
            if ($publicCertificate.HasPrivateKey -or $publicCertificate.Thumbprint -ne $certificate.Thumbprint) {
                throw 'The exported certificate is not the expected public-only signer.'
            }
        }
        finally { $publicCertificate.Dispose() }
        $actualHash = (Get-FileHash (Join-Path $arguments.OutputDirectory $metadata.archive) -Algorithm SHA256).Hash
        if (-not $metadata.signed -or $metadata.signing -ne 'development-only' -or
            $metadata.archive -ne $packageName -or $metadata.architecture -ne $architecture -or
            $metadata.identityName -ne 'OpenClawFoundation.OpenClaw.Dev' -or
            $metadata.packageVersion -ne '2026.7.2.123' -or $metadata.sha256 -ne $actualHash -or
            $metadata.certificateThumbprint -ne $certificate.Thumbprint -or $metadata.sourceCommit -notmatch '^[0-9a-f]{40}$') {
            throw 'Dev package provenance did not match its inputs.'
        }
        $instructions = Get-Content (Join-Path $arguments.OutputDirectory 'INSTALL.txt') -Raw
        if (-not $instructions.Contains("Add-AppxPackage -Path .\$packageName")) {
            throw 'Dev installation instructions did not name the exported package.'
        }
        Assert-Fails { & $exporter @arguments } 'must be absent or empty'

        $arguments = New-Arguments
        $arguments.Architecture = $architecture
        $arguments.ExpectedVersion = '2026.9.4'
        New-Package -Directory $arguments.PackageDirectory -Architecture $architecture -Version '2026.9.4.123'
        & $exporter @arguments
        $metadata = Get-Content (Join-Path $arguments.OutputDirectory 'msix-metadata.json') -Raw | ConvertFrom-Json
        if ($metadata.packageVersion -ne '2026.9.4.123') { throw 'Dev export ignored the overridden base.' }
    }

    $arguments = New-Arguments
    New-Item -ItemType Directory -Path $arguments.PackageDirectory | Out-Null
    Assert-Fails { & $exporter @arguments } 'found 0'
    New-Package -Directory $arguments.PackageDirectory
    New-Package -Directory $arguments.PackageDirectory -Name Other.msix
    Assert-Fails { & $exporter @arguments } 'found 2'

    foreach ($status in @('NotSigned', 'HashMismatch', 'NotTrusted')) {
        $signatureStatus = $status
        $arguments = New-Arguments
        New-Package -Directory $arguments.PackageDirectory
        Assert-Fails { & $exporter @arguments } 'signature is not trusted and valid'
        if (Test-Path $arguments.OutputDirectory) { throw 'Failed verification published output.' }
    }
    $signatureStatus = 'Valid'
    $arguments = New-Arguments
    New-Package -Directory $arguments.PackageDirectory
    $arguments.CertificateThumbprint = '0' * 40
    Assert-Fails { & $exporter @arguments } 'signer does not match'

    foreach ($mismatch in @(
        @{ Identity = 'OpenClawFoundation.OpenClaw'; Error = 'side-by-side Dev identity' },
        @{ Publisher = 'CN=Wrong'; Error = 'side-by-side Dev identity' },
        @{ Architecture = 'arm64'; Error = 'Expected Dev package' },
        @{ Version = '2026.7.2.122'; Error = 'Expected Dev package' },
        @{ Version = '2026.7.1.123'; Error = 'Expected Dev package' },
        @{ Omit = 'AppxSignature.p7x'; Error = 'missing AppxSignature.p7x' },
        @{ Omit = 'coreclr.dll'; Error = 'missing coreclr.dll' }
    )) {
        $arguments = New-Arguments
        $packageArguments = @{ Directory = $arguments.PackageDirectory }
        foreach ($key in $mismatch.Keys) { if ($key -ne 'Error') { $packageArguments[$key] = $mismatch[$key] } }
        New-Package @packageArguments
        Assert-Fails { & $exporter @arguments } $mismatch.Error
    }

    # Exercise the real Store validator and metadata writer, stubbing only the costly publish.
    & {
        $storeBuilder = Join-Path $RepoRoot 'scripts\Build-StoreMsix.ps1'
        [xml]$storeManifest = Get-Content (Join-Path $RepoRoot 'src\OpenClaw.Tray.WinUI\Package.appxmanifest') -Raw
        $storeProbe = @{ Arguments = @(); ProducedVersion = $null; Calls = 0 }
        function dotnet {
            $storeProbe.Arguments = @($args)
            $storeProbe.Calls++
            $output = ($args | Where-Object { $_ -like '-p:AppxPackageDir=*' }) -replace '^-p:AppxPackageDir=', ''
            $architecture = if ($args -contains 'win-arm64') { 'arm64' } else { 'x64' }
            $versionArgument = @($args | Where-Object { $_ -like '-p:Version=*' })
            $version = if ($storeProbe.ProducedVersion) { $storeProbe.ProducedVersion }
                elseif ($versionArgument.Count) { $versionArgument[0] -replace '^-p:Version=', '' }
                else { '2026.9.5.0' }
            New-Package -Directory $output -Name 'Store.msix' -Architecture $architecture `
                -Version $version -Identity $storeManifest.Package.Identity.Name `
                -Publisher $storeManifest.Package.Identity.Publisher -Omit 'AppxSignature.p7x'
            $global:LASTEXITCODE = 0
        }
        foreach ($case in @(
            @{ Architecture = 'x64'; Override = $null; Expected = '2026.9.5.0' },
            @{ Architecture = 'arm64'; Override = $null; Expected = '2026.9.5.0' },
            @{ Architecture = 'x64'; Override = '2026.9.4.0'; Expected = '2026.9.4.0' },
            @{ Architecture = 'arm64'; Override = '2026.9.4.0'; Expected = '2026.9.4.0' }
        )) {
            $output = Join-Path $temporaryRoot "store-$($storeProbe.Calls)"
            $arguments = @{ Architecture = $case.Architecture; OutputDirectory = $output }
            if ($case.Override) { $arguments.StorePackageVersion = $case.Override }
            & $storeBuilder @arguments
            if ($case.Override) {
                foreach ($property in @(
                    '-p:Version=2026.9.4.0', '-p:UpdateVersionProperties=false', '-p:UpdateAssemblyInfo=false',
                    '-p:AssemblyVersion=2026.9.4.0', '-p:FileVersion=2026.9.4.0', '-p:InformationalVersion=2026.9.4.0'
                )) {
                    if ($storeProbe.Arguments -notcontains $property) { throw "Missing Store override: $property" }
                }
            } elseif (@($storeProbe.Arguments | Where-Object {
                $_ -match '^-p:(Version|UpdateVersionProperties|UpdateAssemblyInfo|AssemblyVersion|FileVersion|InformationalVersion)='
            }).Count) {
                throw 'Default Store builds must retain normal GitVersion behavior.'
            }
            $metadata = Get-Content (Join-Path $output 'msix-metadata.json') -Raw | ConvertFrom-Json
            if ($metadata.packageVersion -ne $case.Expected -or $metadata.signed -or
                $metadata.archive -ne "OpenClaw-$($case.Architecture).msix" -or
                $metadata.identityName -ne $storeManifest.Package.Identity.Name) {
                throw 'Store metadata did not describe the actual overridden package.'
            }
        }
        $calls = $storeProbe.Calls
        foreach ($version in @('v2026.9.4.0', '2026.9.4', '2026.9.4.1', '2026.9.4.0-alpha.1', '02026.9.4.0', '0.9.4.0', '')) {
            Assert-Fails { & $storeBuilder -StorePackageVersion $version } 'cannot validate argument'
        }
        foreach ($version in @('65536.9.4.0', '2026.65536.4.0', '2026.9.65536.0')) {
            Assert-Fails { & $storeBuilder -StorePackageVersion $version } 'Invalid MSIX package version component'
        }
        if ($storeProbe.Calls -ne $calls) { throw 'Invalid Store versions must fail before publishing.' }
        $storeProbe.ProducedVersion = '2026.9.5.0'
        $output = Join-Path $temporaryRoot 'store-mismatch'
        Assert-Fails {
            & $storeBuilder -Architecture x64 -OutputDirectory $output -StorePackageVersion '2026.9.4.0'
        } 'Expected Store package version 2026.9.4.0, found 2026.9.5.0'
        if (Test-Path (Join-Path $output 'msix-metadata.json')) {
            throw 'A mismatched Store version must not receive validated metadata.'
        }
    }

    $workflow = Get-Content (Join-Path $RepoRoot '.github\workflows\ci.yml') -Raw
    function Get-WorkflowRun([string]$Name, [string]$ExpectedEnvironment = '') {
        $step = [regex]::Match($workflow, "(?ms)^    - name: $([regex]::Escape($Name))\r?\n(?<body>.*?)(?=^    - |\z)")
        $body = $step.Groups['body'].Value
        $run = [regex]::Match($body, '(?s)      run: \|\r?\n(?<script>.*)')
        if (-not $run.Success -or -not $body.Contains($ExpectedEnvironment)) { throw "Invalid workflow step: $Name" }
        $run.Groups['script'].Value -replace '(?m)^        ', ''
    }
    $selector = [scriptblock]::Create((Get-WorkflowRun 'Select MSIX artifact version'))
    $overrideBinding = 'MSIX_BASE_VERSION_OVERRIDE: ${{ steps.msix_version.outputs.baseVersionOverride }}'
    $storeRun = Get-WorkflowRun 'Build and validate unsigned Store MSIX' $overrideBinding
    $storeBuild = [scriptblock]::Create($storeRun.Replace(
        '.\scripts\Build-StoreMsix.ps1 @buildArguments', '[pscustomobject]$buildArguments'))
    $devRun = Get-WorkflowRun 'Build signed Dev MSIX' $overrideBinding
    $devBuild = [scriptblock]::Create($devRun.Replace('.\build.ps1 ', 'Invoke-WorkflowDevBuild '))
    function Invoke-WorkflowDevBuild {
        param($Project, $Configuration, $Msix, $MsixRevision, $MsixOutputDirectory, $MsixBaseVersion)
        [pscustomobject](@{} + $PSBoundParameters)
    }
    $exportRun = Get-WorkflowRun 'Validate and stage Dev tester artifact' `
        'EXPECTED_DEV_VERSION: ${{ steps.msix_version.outputs.expectedDevVersion }}'
    if (-not $exportRun.Contains('-ExpectedVersion $env:EXPECTED_DEV_VERSION')) {
        throw 'Dev export must validate the selected version.'
    }

    $savedEnvironment = @{}
    foreach ($name in @(
        'BUILD_ARCHITECTURE', 'BUILD_EVENT', 'PR_HEAD_REPOSITORY', 'PR_HEAD_BRANCH',
        'GITHUB_OUTPUT', 'OPENCLAW_BUILD_VERSION', 'MSIX_BASE_VERSION_OVERRIDE', 'DEV_MSIX_REVISION', 'RUNNER_TEMP'
    )) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    try {
        $env:GITHUB_OUTPUT = Join-Path $temporaryRoot 'version-output'
        $env:OPENCLAW_BUILD_VERSION = '2026.9.5-PullRequest1403.3'
        $env:DEV_MSIX_REVISION = '123'
        $env:RUNNER_TEMP = $temporaryRoot
        foreach ($architecture in @('x64', 'arm64')) {
            foreach ($case in @(
                @{ Event = 'pull_request'; Repo = 'natalie-aguinaldo/openclaw-windows-node'; Branch = 'user/natalie-aguinaldo/msix-ci-artifacts-versioning'; Override = $true },
                @{ Event = 'pull_request'; Repo = 'natalie-aguinaldo/openclaw-windows-node'; Branch = 'user/natalie-aguinaldo/msix-ci-artifacts'; Override = $false },
                @{ Event = 'pull_request'; Repo = 'natalie-aguinaldo/openclaw-windows-node'; Branch = 'other-pr'; Override = $false },
                @{ Event = 'pull_request'; Repo = 'openclaw/openclaw-windows-node'; Branch = 'user/natalie-aguinaldo/msix-ci-artifacts-versioning'; Override = $false },
                @{ Event = 'push'; Repo = ''; Branch = ''; Override = $false },
                @{ Event = 'workflow_dispatch'; Repo = ''; Branch = ''; Override = $false },
                @{ Event = 'pull_request_target'; Repo = 'natalie-aguinaldo/openclaw-windows-node'; Branch = 'user/natalie-aguinaldo/msix-ci-artifacts-versioning'; Override = $false }
            )) {
                $env:BUILD_ARCHITECTURE = $architecture
                $env:BUILD_EVENT = $case.Event
                $env:PR_HEAD_REPOSITORY = $case.Repo
                $env:PR_HEAD_BRANCH = $case.Branch
                Set-Content -LiteralPath $env:GITHUB_OUTPUT -Value '' -NoNewline
                & $selector
                $outputs = ConvertFrom-StringData (Get-Content -LiteralPath $env:GITHUB_OUTPUT -Raw)
                $expectedBase = if ($case.Override) { '2026.9.4' } else { '' }
                $expectedDevVersion = if ($case.Override) { '2026.9.4' } else { $env:OPENCLAW_BUILD_VERSION }
                if ($outputs.baseVersionOverride -ne $expectedBase -or $outputs.expectedDevVersion -ne $expectedDevVersion) {
                    throw "Unexpected MSIX version selection for $($case.Event), $($case.Repo), $($case.Branch)."
                }
                if ($env:OPENCLAW_BUILD_VERSION -ne '2026.9.5-PullRequest1403.3') {
                    throw 'MSIX selection must not change the normal release version.'
                }
                $env:MSIX_BASE_VERSION_OVERRIDE = $outputs.baseVersionOverride
                $selected = & $storeBuild
                if ($selected.Architecture -ne $architecture) { throw 'Store selector changed the requested architecture.' }
                $hasOverride = $null -ne $selected.PSObject.Properties['StorePackageVersion']
                if ($hasOverride -ne $case.Override -or ($hasOverride -and $selected.StorePackageVersion -ne '2026.9.4.0')) {
                    throw "Unexpected Store override for $($case.Event), $($case.Repo), $($case.Branch)."
                }
                $selected = & $devBuild
                $hasOverride = $null -ne $selected.PSObject.Properties['MsixBaseVersion']
                if ($hasOverride -ne $case.Override -or ($hasOverride -and $selected.MsixBaseVersion -ne '2026.9.4') -or
                    $selected.MsixRevision -ne 123 -or $selected.Msix -ne 'Dev' -or
                    $selected.Project -ne 'WinUI' -or $selected.Configuration -ne 'Release' -or
                    $selected.MsixOutputDirectory -ne (Join-Path $temporaryRoot 'openclaw-dev-appx')) {
                    throw "Unexpected Dev build arguments for $($case.Event), $($case.Repo), $($case.Branch)."
                }
            }
        }
    }
    finally {
        foreach ($name in $savedEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
        }
    }

    # Exercise the real parameter binder without executing build.ps1's body.
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'build.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'build.ps1 has syntax errors.' }
    $attributes = ($ast.ParamBlock.Attributes | ForEach-Object { $_.Extent.Text }) -join "`n"
    $bind = [scriptblock]::Create($attributes + "`n" + $ast.ParamBlock.Extent.Text + "`n`$MsixRevision")
    foreach ($revision in @(1, 65535)) {
        if ((& $bind -Msix Dev -MsixRevision $revision) -ne $revision) { throw 'A valid CI revision was rejected.' }
    }
    foreach ($revision in @(0, -1, 65536)) {
        Assert-Fails { & $bind -Msix Dev -MsixRevision $revision } 'cannot validate argument'
        $arguments.ExpectedRevision = $revision
        Assert-Fails { & $exporter @arguments } 'cannot validate argument'
    }
    Assert-Fails { & $bind -PackageMsix } 'parameter cannot be found'

    $bindBase = [scriptblock]::Create($attributes + "`n" + $ast.ParamBlock.Extent.Text + "`n`$MsixBaseVersion")
    foreach ($version in @('2026.9.4', '1.0.0', '65535.65535.65535')) {
        if ((& $bindBase -Msix Dev -MsixBaseVersion $version) -ne $version) { throw 'Valid Dev base was rejected.' }
    }
    foreach ($version in @(
        '', 'v2026.9.4', '2026.9', '2026.9.4.0', '2026.9.4-alpha.1', '02026.9.4',
        '0.9.4', '2026.09.4', '65536.9.4', '2026.65536.4', '2026.9.65536'
    )) {
        Assert-Fails { & $bindBase -Msix Dev -MsixBaseVersion $version } 'cannot validate argument'
    }
    foreach ($mode in @(@{}, @{ Msix = 'Store' })) {
        Assert-Fails { & (Join-Path $RepoRoot 'build.ps1') @mode -MsixBaseVersion '2026.9.4' } '-MsixBaseVersion requires -Msix Dev.'
    }

    # Run the real Dev build argument construction without compiling or touching certificates.
    & {
        $buildFunction = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Build-Project'
        }, $true)
        . ([scriptblock]::Create($buildFunction.Extent.Text))
        $probe = @{ Arguments = @() }
        function Invoke-DotNetCaptured($arguments) {
            $probe.Arguments = @($arguments)
            $global:LASTEXITCODE = 0
        }
        function Write-Success($message) {}
        $explicitMsixRevision = $true
        $MsixRevision = 123
        $MsixOutputDirectory = Join-Path $temporaryRoot 'dev-build-output'
        $DevBuild = $true
        $Configuration = 'Release'
        foreach ($rid in @('win-x64', 'win-arm64')) {
            foreach ($MsixBaseVersion in @('', '2026.9.4')) {
                foreach ($packageMsix in @($false, $true)) {
                    if (-not (Build-Project 'WinUI' (Join-Path $RepoRoot 'build.ps1') $true $packageMsix)) {
                        throw 'Dev argument probe failed.'
                    }
                    if ($MsixBaseVersion) {
                        $revision = if ($packageMsix) { 123 } else { 0 }
                        foreach ($property in @(
                            '-p:Version=2026.9.4', '-p:UpdateVersionProperties=false', '-p:UpdateAssemblyInfo=false',
                            "-p:AssemblyVersion=2026.9.4.$revision", "-p:FileVersion=2026.9.4.$revision",
                            "-p:InformationalVersion=2026.9.4.$revision"
                        )) {
                            if ($probe.Arguments -notcontains $property) { throw "Missing Dev override: $property" }
                        }
                    } elseif (@($probe.Arguments | Where-Object {
                        $_ -match '^-p:(Version|UpdateVersionProperties|UpdateAssemblyInfo|AssemblyVersion|FileVersion|InformationalVersion)='
                    }).Count) {
                        throw 'Default Dev builds must retain normal GitVersion behavior.'
                    }
                    if ($packageMsix -and $probe.Arguments -notcontains '-p:MsixRevision=123') {
                        throw 'The base override must preserve the Dev revision.'
                    }
                }
            }
        }
    }
    Write-Host 'MSIX CI artifact contracts passed: version bounds, matched Store/Dev PR-only bases, build arguments, identity, architecture, signature rejection, exact package selection, provenance, and public-only exports.'
}
finally {
    $certificate.Dispose()
    $rsa.Dispose()
    [IO.Directory]::Delete($temporaryRoot, $true)
}
