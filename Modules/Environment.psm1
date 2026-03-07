#Requires -Version 5.1

<#
.SYNOPSIS
    ADK detection, environment variable setup, and admin privilege check module
#>

function Write-BuildLog {
    param(
        [int]$Step,
        [int]$Total,
        [string]$Message,
        [ValidateSet("info","success","error","warn")]
        [string]$Status = "info"
    )
    $entry = @{
        type    = "progress"
        step    = $Step
        total   = $Total
        message = $Message
        status  = $Status
        time    = (Get-Date -Format "HH:mm:ss")
    } | ConvertTo-Json -Compress
    Write-Output $entry
}

function Get-ADKPath {
    <#
    .SYNOPSIS
        Retrieve ADK install path from the registry
    .OUTPUTS
        ADK install path string (e.g. C:\Program Files (x86)\Windows Kits\10\)
    #>
    $regPath = "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots"

    if (-not (Test-Path $regPath)) {
        throw "ADK registry key not found: $regPath"
    }

    $kitsRoot = (Get-ItemProperty -Path $regPath -Name "KitsRoot10" -ErrorAction Stop).KitsRoot10

    if ([string]::IsNullOrWhiteSpace($kitsRoot)) {
        throw "KitsRoot10 value is empty. Please verify that ADK is installed correctly."
    }

    # Trim trailing backslash for consistency
    $kitsRoot = $kitsRoot.TrimEnd('\')

    $adkPath = "$kitsRoot\Assessment and Deployment Kit"
    if (-not (Test-Path $adkPath)) {
        throw "ADK directory not found: $adkPath"
    }

    return $adkPath
}

function Get-WinPEOCsPath {
    <#
    .SYNOPSIS
        Get the WinPE Optional Components (OCs) path
    .PARAMETER ADKPath
        ADK install path
    .PARAMETER Architecture
        Target architecture ("amd64" or "x86")
    #>
    param(
        [Parameter(Mandatory)][string]$ADKPath,
        [Parameter(Mandatory)][ValidateSet("amd64","x86")][string]$Architecture
    )

    $ocsPath = "$ADKPath\Windows Preinstallation Environment\$Architecture\WinPE_OCs"

    if (-not (Test-Path $ocsPath)) {
        throw "WinPE OCs directory not found: $ocsPath`nPlease verify that the Windows PE add-on is installed."
    }

    return $ocsPath
}

function Initialize-ADKEnvironment {
    <#
    .SYNOPSIS
        Configure PATH so that ADK commands (copype, MakeWinPEMedia, etc.) are accessible.
        Runs entirely within PowerShell without calling DandISetEnv.bat.
    .PARAMETER ADKPath
        ADK install path
    #>
    param(
        [Parameter(Mandatory)][string]$ADKPath
    )

    # Deployment Tools (contains oscdimg, etc.)
    $deployTools = "$ADKPath\Deployment Tools\x86\Oscdimg"
    $deployToolsDism = "$ADKPath\Deployment Tools\x86\DISM"

    # Windows PE Tools (contains MakeWinPEMedia.cmd, copype.cmd, etc.)
    $winPERoot = "$ADKPath\Windows Preinstallation Environment"

    # Verify copype.cmd exists
    $copypePath = "$winPERoot\copype.cmd"
    if (-not (Test-Path $copypePath)) {
        throw "copype.cmd not found: $copypePath"
    }

    # Add to PATH (avoid duplicates)
    $pathsToAdd = @(
        "$ADKPath\Deployment Tools\x86\Oscdimg"
        "$ADKPath\Deployment Tools\x86\DISM"
        $winPERoot
    )

    foreach ($p in $pathsToAdd) {
        if ($env:PATH -notlike "*$p*") {
            $env:PATH = "$p;$env:PATH"
        }
    }

    # Set environment variables required by copype.cmd and MakeWinPEMedia.cmd
    $env:WADROOT     = $ADKPath
    $env:WinPERoot   = $winPERoot
    $env:OSCDImgRoot = "$ADKPath\Deployment Tools\x86\Oscdimg"
    $env:DISMRoot    = "$ADKPath\Deployment Tools\x86\DISM"
}

function Assert-AdminPrivilege {
    <#
    .SYNOPSIS
        Check for admin privileges. If not running as admin, re-launch with elevation.
    #>
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentUser)
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $isAdmin) {
        Write-Warning "Administrator privileges required. Re-launching with elevation..."

        $scriptPath = $MyInvocation.ScriptName
        if ([string]::IsNullOrWhiteSpace($scriptPath)) {
            # When called from a module, get the caller script path
            $scriptPath = (Get-PSCallStack | Where-Object { $_.ScriptName -and $_.ScriptName -ne $MyInvocation.ScriptName } | Select-Object -First 1).ScriptName
        }

        if ([string]::IsNullOrWhiteSpace($scriptPath)) {
            throw "Please run as Administrator. Could not determine script path for self-elevation."
        }

        $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""

        # Pass through original script arguments
        $boundParams = (Get-PSCallStack)[1].InvocationInfo.BoundParameters
        if ($boundParams) {
            foreach ($key in $boundParams.Keys) {
                $val = $boundParams[$key]
                if ($val -is [switch]) {
                    if ($val.IsPresent) { $arguments += " -$key" }
                } else {
                    $arguments += " -$key `"$val`""
                }
            }
        }

        Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -Verb RunAs
        exit
    }
}

Export-ModuleMember -Function Get-ADKPath, Get-WinPEOCsPath, Initialize-ADKEnvironment, Assert-AdminPrivilege, Write-BuildLog
