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

function Test-AclAuthority {
    param(
        [Security.AccessControl.FileSystemSecurity]$Acl,
        [string[]]$Trusted
    )

    # Only a principal that can change or delete the receipt affects the decision. Read,
    # list, and traverse grants are common in managed profiles and inherit into this
    # directory harmlessly, so matching those would make every ordinary uninstall
    # unverifiable. The mask names individual mutating bits on purpose: FullControl and
    # Modify are composites that also carry the read bits, so testing against them would
    # match a read-only grant.
    $mutating = [int]([Security.AccessControl.FileSystemRights]::WriteData `
        -bor [Security.AccessControl.FileSystemRights]::AppendData `
        -bor [Security.AccessControl.FileSystemRights]::WriteAttributes `
        -bor [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes `
        -bor [Security.AccessControl.FileSystemRights]::Delete `
        -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles `
        -bor [Security.AccessControl.FileSystemRights]::ChangePermissions `
        -bor [Security.AccessControl.FileSystemRights]::TakeOwnership)

    # An ACE may also carry the generic rights, which FileSystemRights has no names for and
    # which intersect none of the specific bits above. The kernel maps GENERIC_ALL to
    # FILE_ALL_ACCESS (DELETE, FILE_DELETE_CHILD, WRITE_DAC) and GENERIC_WRITE on a
    # directory to FILE_ADD_FILE, so a grant in that form can delete or replace the receipt
    # while every named bit reads as clear. GENERIC_READ and GENERIC_EXECUTE are harmless.
    $genericAll = 0x10000000
    $genericWrite = 0x40000000
    $mutating = $mutating -bor $genericAll -bor $genericWrite

    $owner = $Acl.GetOwner([Security.Principal.SecurityIdentifier])
    if ($null -eq $owner -or $Trusted -notcontains $owner.Value) {
        return $false
    }
    foreach ($rule in $Acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) {
            continue
        }
        if ($Trusted -contains $rule.IdentityReference.Value) {
            continue
        }
        # An inherit-only entry describes children and grants nothing on this object.
        if ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) {
            continue
        }
        if ((([int]$rule.FileSystemRights) -band $mutating) -ne 0) {
            return $false
        }
    }
    return $true
}

function Test-MigrationStateAuthority {
    param([string]$Directory)

    # An absent receipt only means "no migration happened" when nobody outside the
    # trusted principals could have deleted it. Ownership counts as access: an owner
    # keeps implicit WRITE_DAC and can grant itself delete rights at any time.
    # Uninstall creates this directory itself with a plain ForceDirectories call, so an
    # unprotected DACL is ordinary here and is deliberately not treated as tampering.
    # This reads ACLs through .NET types rather than Get-Acl/Get-ChildItem: the
    # uninstaller can launch PowerShell where Microsoft.PowerShell.Security fails to
    # autoload, and a module-load failure here would preserve every gateway.
    try {
        if (-not [IO.Directory]::Exists($Directory)) {
            return $true
        }
        $trusted = @(
            [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
            'S-1-5-18',
            'S-1-5-32-544'
        )
        $acls = @((New-Object IO.DirectoryInfo $Directory).GetAccessControl())
        foreach ($file in [IO.Directory]::GetFiles($Directory)) {
            $acls += (New-Object IO.FileInfo $file).GetAccessControl()
        }
        foreach ($acl in $acls) {
            if (-not (Test-AclAuthority -Acl $acl -Trusted $trusted)) {
                return $false
            }
        }
        return $true
    } catch {
        return $false
    }
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
        # Falling through to the inline check would reintroduce the unbounded
        # Add-Type stall this watchdog exists to prevent, and the caller waits
        # with ewWaitUntilTerminated. Report the uncertain verdict instead; it
        # already fails closed and preserves the gateway.
        Write-Warning "Migration preservation watchdog could not start: $($_.Exception.GetType().Name)."
        exit 2
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
        # An absent receipt only means "no migration happened" when the state directory
        # could not have been tampered with. A foreign owner can delete completed.dpapi,
        # and that deletion is indistinguishable from absence here, so refuse to treat
        # unverifiable state as consent to unregister the gateway.
        if (-not (Test-MigrationStateAuthority -Directory (Split-Path -Parent $receiptPath))) {
            Write-Warning 'Migration state authority could not be verified. Preserving generated state and gateway.'
            exit 2
        }
        Write-Verbose 'No completed migration receipt.'
        exit 0
    }
    # A receipt exists but did not validate. "Present but unverifiable" is not the
    # same as "absent": a future schema, a DPAPI failure, or binding drift must
    # preserve state rather than authorize unregistering the gateway.
    Write-Warning "Completed migration receipt could not be validated: $($cause.GetType().Name)."
    exit 2
}
