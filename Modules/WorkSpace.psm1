#Requires -Version 5.1

<#
.SYNOPSIS
    WinPE workspace creation and management module
#>

function New-PEWorkSpace {
    <#
    .SYNOPSIS
        Create a WinPE workspace by calling copype.cmd
    .PARAMETER Architecture
        Target architecture ("amd64" or "x86")
    .PARAMETER WorkDir
        Working directory path
    .PARAMETER Force
        Remove existing directory and recreate
    #>
    param(
        [Parameter(Mandatory)][ValidateSet("amd64","x86")][string]$Architecture,
        [Parameter(Mandatory)][string]$WorkDir,
        [switch]$Force
    )

    # Handle existing directory
    if (Test-Path $WorkDir) {
        if ($Force) {
            Write-Verbose "Removing existing workspace: $WorkDir"
            Remove-Item -Path $WorkDir -Recurse -Force -ErrorAction Stop
        } else {
            throw "Workspace already exists: $WorkDir`nUse -Force to recreate."
        }
    }

    # Locate copype.cmd
    $adkPath = $env:WADROOT
    if ([string]::IsNullOrWhiteSpace($adkPath)) {
        throw "WADROOT environment variable is not set. Run Initialize-ADKEnvironment first."
    }

    $copype = "$adkPath\Windows Preinstallation Environment\copype.cmd"
    if (-not (Test-Path $copype)) {
        throw "copype.cmd not found: $copype"
    }

    # Run copype.cmd via cmd.exe
    Write-Verbose "Running copype: $Architecture -> $WorkDir"
    $result = cmd.exe /c "`"$copype`" $Architecture `"$WorkDir`"" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "copype failed (ExitCode: $LASTEXITCODE)`n$result"
    }

    # Verify workspace was created
    if (-not (Test-Path $WorkDir)) {
        throw "copype succeeded but workspace not found: $WorkDir"
    }

    Write-Verbose "Workspace created: $WorkDir"
}

function Mount-PEImage {
    <#
    .SYNOPSIS
        Mount a WIM file
    .PARAMETER WorkDir
        Working directory path (created by copype)
    .PARAMETER MountDir
        Mount destination directory (default: $WorkDir\mount)
    #>
    param(
        [Parameter(Mandatory)][string]$WorkDir,
        [string]$MountDir
    )

    if ([string]::IsNullOrWhiteSpace($MountDir)) {
        $MountDir = "$WorkDir\mount"
    }

    $wimFile = "$WorkDir\media\sources\boot.wim"
    if (-not (Test-Path $wimFile)) {
        throw "WIM file not found: $wimFile"
    }

    # Create mount directory if it does not exist
    if (-not (Test-Path $MountDir)) {
        New-Item -Path $MountDir -ItemType Directory -Force | Out-Null
    }

    # Mount with DISM
    $dism = "$env:SystemRoot\System32\dism.exe"
    Write-Verbose "Mounting WIM: $wimFile -> $MountDir"
    & $dism /Mount-Wim /WimFile:"$wimFile" /Index:1 /MountDir:"$MountDir"
    if ($LASTEXITCODE -ne 0) {
        throw "WIM mount failed (ExitCode: $LASTEXITCODE)"
    }

    Write-Verbose "WIM mounted: $MountDir"
}

function Dismount-PEImage {
    <#
    .SYNOPSIS
        Unmount a WIM file
    .PARAMETER MountDir
        Mount directory
    .PARAMETER Commit
        If $true, commit changes; if $false, discard changes
    #>
    param(
        [Parameter(Mandatory)][string]$MountDir,
        [bool]$Commit = $true
    )

    $dism = "$env:SystemRoot\System32\dism.exe"

    if ($Commit) {
        Write-Verbose "Unmounting WIM with commit: $MountDir"
        & $dism /Unmount-Wim /MountDir:"$MountDir" /Commit
    } else {
        Write-Verbose "Unmounting WIM with discard: $MountDir"
        & $dism /Unmount-Wim /MountDir:"$MountDir" /Discard
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "WIM unmount failed (ExitCode: $LASTEXITCODE)"
        # Attempt cleanup
        Write-Verbose "Running DISM /Cleanup-Wim..."
        & $dism /Cleanup-Wim
    }
}

function Copy-ToWIM {
    <#
    .SYNOPSIS
        Copy files or directories into a mounted WIM image.
    .PARAMETER MountDir
        WIM mount directory
    .PARAMETER Source
        Source file or directory path on the host
    .PARAMETER Destination
        Destination path relative to WIM root (e.g., "CaptureTools")
    #>
    param(
        [Parameter(Mandatory)][string]$MountDir,
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path $Source)) {
        throw "Inject source not found: $Source"
    }

    $destPath = Join-Path $MountDir $Destination

    if (Test-Path $Source -PathType Container) {
        # Copy directory contents recursively
        Write-Verbose "Injecting directory: $Source -> $destPath"
        Copy-Item -Path $Source -Destination $destPath -Recurse -Force
    } else {
        # Copy single file
        $destDir = Split-Path -Parent $destPath
        if (-not (Test-Path $destDir)) {
            New-Item -Path $destDir -ItemType Directory -Force | Out-Null
        }
        Write-Verbose "Injecting file: $Source -> $destPath"
        Copy-Item -Path $Source -Destination $destPath -Force
    }
}

Export-ModuleMember -Function New-PEWorkSpace, Mount-PEImage, Dismount-PEImage, Copy-ToWIM
