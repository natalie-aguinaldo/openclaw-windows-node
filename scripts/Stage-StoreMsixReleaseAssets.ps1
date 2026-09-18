<#
.SYNOPSIS
    Stages validated unsigned Store MSIX packages for an alpha GitHub release.
.DESCRIPTION
    Checks both architectures' provenance and hashes before copying any files.
    Build-StoreMsix.ps1 owns package-content validation; this script preserves
    those exact bytes and never signs packages or submits them to Partner Center.
    Returns Files and Notes for the existing release publisher.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)]
    [ValidatePattern('^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)-alpha\.(?:0|[1-9]\d*)$')]
    [string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedSourceCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$ArtifactDirectory = [IO.Path]::GetFullPath([IO.Path]::Combine($repositoryRoot, $ArtifactDirectory))
$OutputDirectory = [IO.Path]::GetFullPath([IO.Path]::Combine($repositoryRoot, $OutputDirectory))
if ((Test-Path -LiteralPath $OutputDirectory) -and
    (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container) -or
     @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0)) {
    throw "The alpha release output directory must be absent or empty: $OutputDirectory"
}

[xml]$manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\OpenClaw.Tray.WinUI\Package.appxmanifest') -Raw
$expectedVersion = ($Version -replace '-alpha\.\d+$', '') + '.0'
$packages = foreach ($architecture in @('x64', 'arm64')) {
    $directory = Join-Path $ArtifactDirectory "openclaw-msix-store-unsigned-$architecture"
    $packageName = "OpenClaw-$architecture.msix"
    $expectedFiles = @($packageName, 'msix-metadata.json') | Sort-Object
    $entries = @(Get-ChildItem -LiteralPath $directory -Force)
    if ($entries.Count -ne 2 -or
        @($entries | Where-Object { $_.PSIsContainer -or $_.LinkType }).Count -gt 0 -or
        @(Compare-Object $expectedFiles @($entries.Name | Sort-Object)).Count -gt 0) {
        throw "Expected exactly the unsigned Store package and metadata for $architecture."
    }

    $metadataPath = Join-Path $directory 'msix-metadata.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ($metadata.sourceTreeDirty -isnot [bool] -or $metadata.sourceTreeDirty -or
        $metadata.sourceCommit -ne $ExpectedSourceCommit) {
        throw "The $architecture Store artifact does not belong to the expected clean source commit."
    }
    if ($metadata.signed -isnot [bool] -or $metadata.signed -or
        $metadata.configuration -ne 'Release' -or
        $metadata.identityName -ne [string]$manifest.Package.Identity.Name -or
        $metadata.publisher -ne [string]$manifest.Package.Identity.Publisher -or
        $metadata.architecture -ne $architecture -or
        $metadata.packageVersion -ne $expectedVersion -or
        $metadata.archive -ne $packageName) {
        throw "The $architecture Store artifact identity, version, or unsigned metadata is invalid."
    }
    $packagePath = Join-Path $directory $packageName
    if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne $metadata.sha256) {
        throw "The $architecture Store package hash does not match its metadata."
    }

    [pscustomobject]@{ Path = $packagePath; Name = $packageName; Metadata = $metadataPath }
}

# A bad second architecture must not leave a publishable partial set.
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$files = foreach ($package in $packages) {
    $packageDestination = Join-Path $OutputDirectory $package.Name
    $metadataDestination = Join-Path $OutputDirectory "$($package.Name)-metadata.json"
    Copy-Item -LiteralPath $package.Path -Destination $packageDestination
    Copy-Item -LiteralPath $package.Metadata -Destination $metadataDestination
    $packageDestination
    $metadataDestination
}

[pscustomobject]@{
    Files = @($files)
    Notes = @"
### Unsigned Store submission packages (alpha only)

OpenClaw-x64.msix and OpenClaw-arm64.msix are unsigned
Partner Center submission inputs, not installers. Their architecture-specific
metadata files record the source commit, package version, and SHA-256.
Upload the MSIX files manually to Partner Center; Microsoft signs accepted
Store submissions. This workflow does not submit or retrieve Store packages.

The Windows package version is $expectedVersion. Different alpha tags with
the same base version produce the same Store version, so verify it against
previous submissions before uploading. Stable releases do not include these
experimental submission assets. Dev-signed tester downloads remain in Actions.
"@
}
