<#
.SYNOPSIS
    Read-only check for a completed, same-user Inno-to-Store migration.
.DESCRIPTION
    Exit 10 means preservation is required. Exit 0 means no valid receipt.
    Exit 2 means the checker could not run; callers must not start cleanup.
    The record codec is the exact source compiled into OpenClaw.Connection.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppRoot,
    [string]$DataDirectoryName = 'OpenClawTray',
    [Parameter(Mandatory = $true)][ValidateSet('x64', 'arm64')][string]$Architecture,
    [string]$RoamingDirectory,
    [string]$LocalDirectory
)

$ErrorActionPreference = 'Stop'
if ($DataDirectoryName -ne 'OpenClawTray') {
    Write-Verbose 'Store migration does not apply to development installs.'
    exit 0
}

try {
    $codecPath = Join-Path $AppRoot 'MigrationRecordCodec.cs'
    Add-Type -Path $codecPath -ReferencedAssemblies 'System.Security.dll' -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace($RoamingDirectory)) {
        $RoamingDirectory = Join-Path ([Environment]::GetFolderPath('ApplicationData')) $DataDirectoryName
    }
    if ([string]::IsNullOrWhiteSpace($LocalDirectory)) {
        $LocalDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $DataDirectoryName
    }
    $binding = New-Object OpenClaw.Connection.Migration.MigrationBinding
    $binding.InstallDirectory = [IO.Path]::GetFullPath($AppRoot)
    $binding.RoamingDirectory = [IO.Path]::GetFullPath($RoamingDirectory)
    $binding.LocalDirectory = [IO.Path]::GetFullPath($LocalDirectory)
    $binding.Architecture = $Architecture
    $binding.UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $receiptPath = Join-Path (Join-Path $RoamingDirectory 'store-migration') 'completed.dpapi'
} catch {
    Write-Warning "Migration preservation checker could not initialize: $($_.Exception.GetType().Name)."
    exit 2
}

try {
    $null = [OpenClaw.Connection.Migration.MigrationRecordCodec]::ReadCompletion(
        $receiptPath, $binding, [DateTime]::UtcNow)
    Write-Output 'Validated completed Store migration. Preserve generated state and gateway.'
    exit 10
} catch {
    $cause = $_.Exception.GetBaseException()
    if ($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) {
        Write-Verbose 'No completed migration receipt.'
        exit 0
    }
    if ($cause -is [IO.InvalidDataException] -or
        $cause -is [Security.Cryptography.CryptographicException] -or
        $cause -is [IO.EndOfStreamException] -or
        $cause -is [ArgumentException] -or
        $cause -is [Text.DecoderFallbackException]) {
        Write-Warning "Completed migration receipt rejected: $($cause.GetType().Name). Existing uninstall policy applies."
        exit 0
    }
    Write-Warning "Migration preservation check could not finish: $($cause.GetType().Name)."
    exit 2
}
