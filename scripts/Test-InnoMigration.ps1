<#
.SYNOPSIS
    Read-only check for a completed, same-user Inno-to-Store migration.
.DESCRIPTION
    Exit 10 means preservation is required. Exit 0 means no receipt is present.
    Exit 2 means the migration state could not be established; callers must not
    start cleanup. A receipt that exists but does not validate is exit 2, never
    exit 0: schema, DPAPI, or binding drift must not authorize destroying state.
    The record codec is the exact source compiled into OpenClaw.Connection.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppRoot,
    [string]$DataDirectoryName = 'OpenClawTray',
    [Parameter(Mandatory = $true)][ValidateSet('x64', 'arm64')][string]$Architecture,
    [string]$RoamingDirectory,
    [string]$LocalDirectory,
    [int]$TimeoutSeconds = 90,
    [switch]$NoWatchdog
)

$ErrorActionPreference = 'Stop'
if ($DataDirectoryName -ne 'OpenClawTray') {
    Write-Verbose 'Store migration does not apply to development installs.'
    exit 0
}

function ConvertTo-ProcessArgument {
    param([string]$Value)

    # Double any trailing backslashes so the closing quote is not escaped.
    $escaped = $Value -replace '(\\+)$', '$1$1'
    return '"' + ($escaped -replace '"', '\"') + '"'
}

function Start-BoundedProcess {
    param([string]$FilePath, [string]$Arguments, [int]$TimeoutMilliseconds)

    # Start-Process -PassThru does not reliably expose ExitCode once output is
    # redirected, so drive the process directly instead.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    # Begin both reads before waiting so a full pipe buffer cannot deadlock us.
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()

    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        try { $process.Kill() } catch {}
        return [pscustomobject]@{ TimedOut = $true; ExitCode = $null; Output = '' }
    }

    # The parameterless wait lets the redirected streams finish flushing.
    $process.WaitForExit()
    $output = (@($stdout.Result, $stderr.Result) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine
    return [pscustomobject]@{ TimedOut = $false; ExitCode = [int]$process.ExitCode; Output = $output }
}

# Add-Type compiles through csc.exe and can stall on a contended %TEMP% or an
# antivirus scan. Callers cannot bound Exec/ewWaitUntilTerminated, so the check
# bounds itself by running the real work in a child process.
if (-not $NoWatchdog -and $TimeoutSeconds -gt 0) {
    $watchdogResult = $null
    try {
        $arguments = @(
            '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', (ConvertTo-ProcessArgument $PSCommandPath),
            '-AppRoot', (ConvertTo-ProcessArgument $AppRoot),
            '-DataDirectoryName', (ConvertTo-ProcessArgument $DataDirectoryName),
            '-Architecture', $Architecture,
            '-NoWatchdog')
        if (-not [string]::IsNullOrWhiteSpace($RoamingDirectory)) {
            $arguments += @('-RoamingDirectory', (ConvertTo-ProcessArgument $RoamingDirectory))
        }
        if (-not [string]::IsNullOrWhiteSpace($LocalDirectory)) {
            $arguments += @('-LocalDirectory', (ConvertTo-ProcessArgument $LocalDirectory))
        }
        $watchdogResult = Start-BoundedProcess `
            -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
            -Arguments ($arguments -join ' ') `
            -TimeoutMilliseconds ($TimeoutSeconds * 1000)
    } catch {
        # A watchdog we cannot start must not become a verdict. Fall through and
        # run the check inline rather than reporting a state we did not observe.
        Write-Verbose "Migration preservation watchdog unavailable: $($_.Exception.GetType().Name)."
        $watchdogResult = $null
    }

    if ($null -ne $watchdogResult) {
        if ($watchdogResult.TimedOut) {
            Write-Warning "Migration preservation check did not finish within $TimeoutSeconds seconds."
            exit 2
        }
        # Re-emit the child's verdict so callers keep the diagnostics that
        # distinguish DPAPI failure, schema drift, and binding mismatch.
        if (-not [string]::IsNullOrWhiteSpace($watchdogResult.Output)) {
            Write-Output $watchdogResult.Output.Trim()
        }
        exit $watchdogResult.ExitCode
    }
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
    # A receipt exists but did not validate. "Present but unverifiable" is not the
    # same as "absent": a future schema, a DPAPI failure, or binding drift must
    # preserve state rather than authorize unregistering the gateway.
    Write-Warning "Completed migration receipt could not be validated: $($cause.GetType().Name)."
    exit 2
}
