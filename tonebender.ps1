#Requires -Version 5.1
#Requires -RunAsAdministrator

<#
.SYNOPSIS
    tonebender - WinPE Builder Framework
.DESCRIPTION
    Reads a JSON profile and automates WinPE build through ISO generation.
.PARAMETER ProfilePath
    Path to JSON profile
.EXAMPLE
    .\tonebender.ps1 -ProfilePath ".\Profiles\default.json"
    .\tonebender.ps1 -ProfilePath ".\Profiles\default.json" -Verbose
#>

param(
    [Parameter(Mandatory)]
    [string]$ProfilePath
)

$ErrorActionPreference = "Stop"

# --- Load modules ---
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$modulesDir = "$scriptDir\Modules"

Import-Module "$modulesDir\Environment.psm1" -Force
Import-Module "$modulesDir\WorkSpace.psm1" -Force
Import-Module "$modulesDir\Packages.psm1" -Force
Import-Module "$modulesDir\Image.psm1" -Force

# --- Constants ---
$TOTAL_STEPS = 8

# =============================================================================
# Step 1: Admin privilege check
# =============================================================================
Write-BuildLog -Step 1 -Total $TOTAL_STEPS -Message "Checking admin privileges..." -Status "info"
Assert-AdminPrivilege
Write-BuildLog -Step 1 -Total $TOTAL_STEPS -Message "Admin privileges confirmed" -Status "success"

# =============================================================================
# Step 2: Load and validate JSON profile
# =============================================================================
Write-BuildLog -Step 2 -Total $TOTAL_STEPS -Message "Loading profile: $ProfilePath" -Status "info"

# Resolve path
if (-not [System.IO.Path]::IsPathRooted($ProfilePath)) {
    $ProfilePath = Join-Path (Get-Location).Path $ProfilePath
}

if (-not (Test-Path $ProfilePath)) {
    $msg = "Profile not found: $ProfilePath"
    Write-BuildLog -Step 2 -Total $TOTAL_STEPS -Message $msg -Status "error"
    throw $msg
}

try {
    $profile = Get-Content -Path $ProfilePath -Raw -Encoding UTF8 | ConvertFrom-Json
} catch {
    $msg = "Failed to parse profile JSON: $_"
    Write-BuildLog -Step 2 -Total $TOTAL_STEPS -Message $msg -Status "error"
    throw $msg
}

# Validation
$requiredFields = @(
    @{ Name = "profile";       Value = $profile.profile },
    @{ Name = "architecture";  Value = $profile.architecture },
    @{ Name = "workDir";       Value = $profile.workDir },
    @{ Name = "output.iso";    Value = $profile.output.iso },
    @{ Name = "output.isoPath"; Value = $profile.output.isoPath },
    @{ Name = "packages";      Value = $profile.packages }
)

foreach ($field in $requiredFields) {
    if ($null -eq $field.Value) {
        $msg = "Required field missing in profile: $($field.Name)"
        Write-BuildLog -Step 2 -Total $TOTAL_STEPS -Message $msg -Status "error"
        throw $msg
    }
}

if ($profile.architecture -notin @("amd64", "x86")) {
    $msg = "architecture must be 'amd64' or 'x86' (current: $($profile.architecture))"
    Write-BuildLog -Step 2 -Total $TOTAL_STEPS -Message $msg -Status "error"
    throw $msg
}

# Resolve relative paths in profile (relative to script directory)
if (-not [System.IO.Path]::IsPathRooted($profile.workDir)) {
    $profile.workDir = [System.IO.Path]::GetFullPath((Join-Path $scriptDir $profile.workDir))
}
if (-not [System.IO.Path]::IsPathRooted($profile.output.isoPath)) {
    $profile.output.isoPath = [System.IO.Path]::GetFullPath((Join-Path $scriptDir $profile.output.isoPath))
}

Write-BuildLog -Step 2 -Total $TOTAL_STEPS -Message "Profile loaded: $($profile.profile) ($($profile.architecture))" -Status "success"

# =============================================================================
# Step 3: Detect ADK and initialize environment
# =============================================================================
Write-BuildLog -Step 3 -Total $TOTAL_STEPS -Message "Detecting ADK..." -Status "info"

$adkPath = Get-ADKPath
Initialize-ADKEnvironment -ADKPath $adkPath
$ocsPath = Get-WinPEOCsPath -ADKPath $adkPath -Architecture $profile.architecture

Write-BuildLog -Step 3 -Total $TOTAL_STEPS -Message "ADK detected: $adkPath" -Status "success"

# =============================================================================
# Step 4: Create workspace
# =============================================================================
Write-BuildLog -Step 4 -Total $TOTAL_STEPS -Message "Creating workspace: $($profile.workDir)" -Status "info"

New-PEWorkSpace -Architecture $profile.architecture -WorkDir $profile.workDir -Force -Verbose:$VerbosePreference

Write-BuildLog -Step 4 -Total $TOTAL_STEPS -Message "Workspace created" -Status "success"

# =============================================================================
# Step 5-7: Mount WIM -> Add packages -> Unmount -> Generate ISO
#   try/finally ensures WIM is unmounted even on error
# =============================================================================
$mountDir = "$($profile.workDir)\mount"
$wimMounted = $false

try {
    # --- Step 5: Mount WIM ---
    Write-BuildLog -Step 5 -Total $TOTAL_STEPS -Message "Mounting WIM..." -Status "info"

    Mount-PEImage -WorkDir $profile.workDir -MountDir $mountDir -Verbose:$VerbosePreference
    $wimMounted = $true

    Write-BuildLog -Step 5 -Total $TOTAL_STEPS -Message "WIM mounted" -Status "success"

    # --- Step 6: Add packages ---
    Write-BuildLog -Step 6 -Total $TOTAL_STEPS -Message "Adding packages ($($profile.packages.Count))..." -Status "info"

    $pkgParams = @{
        MountDir = $mountDir
        Packages = $profile.packages
        OCsPath  = $ocsPath
    }
    if ($profile.locale -and $profile.locale.language) {
        $pkgParams.Language = $profile.locale.language
    }
    Add-PEPackages @pkgParams -Verbose:$VerbosePreference

    Write-BuildLog -Step 6 -Total $TOTAL_STEPS -Message "Packages added" -Status "success"

    # --- Step 7: Apply locale settings ---
    if ($profile.locale) {
        Write-BuildLog -Step 7 -Total $TOTAL_STEPS -Message "Applying locale settings..." -Status "info"

        Set-PELocale -MountDir $mountDir -Locale $profile.locale -Verbose:$VerbosePreference

        Write-BuildLog -Step 7 -Total $TOTAL_STEPS -Message "Locale settings applied" -Status "success"
    }

    # --- Set PowerShell ExecutionPolicy to Bypass in WIM registry ---
    Write-BuildLog -Step 7 -Total $TOTAL_STEPS -Message "Setting ExecutionPolicy to Bypass..." -Status "info"
    Set-PEExecutionPolicy -MountDir $mountDir -Policy "Bypass" -Verbose:$VerbosePreference
    Write-BuildLog -Step 7 -Total $TOTAL_STEPS -Message "ExecutionPolicy set to Bypass" -Status "success"

    # --- Inject files into WIM ---
    # Wrap in @() to ensure array even with single JSON element (PS 5.1 compat)
    $injectEntries = @($profile.inject | Where-Object { $_ })
    if ($injectEntries.Count -gt 0) {
        Write-BuildLog -Step 7 -Total $TOTAL_STEPS -Message "Injecting files into WIM ($($injectEntries.Count) entries)..." -Status "info"

        foreach ($entry in $injectEntries) {
            $srcPath = $entry.source
            # Resolve relative paths (relative to script directory)
            if (-not [System.IO.Path]::IsPathRooted($srcPath)) {
                $srcPath = [System.IO.Path]::GetFullPath((Join-Path $scriptDir $srcPath))
            }
            Write-Verbose "Inject: $srcPath -> $($entry.destination)"
            Copy-ToWIM -MountDir $mountDir -Source $srcPath -Destination $entry.destination -Verbose:$VerbosePreference
        }

        Write-BuildLog -Step 7 -Total $TOTAL_STEPS -Message "Files injected into WIM" -Status "success"
    }

    # --- Step 8: Unmount WIM (commit) ---
    Write-BuildLog -Step 8 -Total $TOTAL_STEPS -Message "Unmounting WIM (commit)..." -Status "info"

    Dismount-PEImage -MountDir $mountDir -Commit $true -Verbose:$VerbosePreference
    $wimMounted = $false

    Write-BuildLog -Step 8 -Total $TOTAL_STEPS -Message "WIM unmounted" -Status "success"

} finally {
    # Force unmount (discard) if WIM is still mounted after an error
    if ($wimMounted -and (Test-Path $mountDir)) {
        Write-BuildLog -Step 0 -Total $TOTAL_STEPS -Message "Error: Force unmounting WIM (discard)..." -Status "warn"
        Dismount-PEImage -MountDir $mountDir -Commit $false -Verbose:$VerbosePreference
    }
}

# =============================================================================
# Generate ISO
# =============================================================================
if ($profile.output.iso) {
    Write-BuildLog -Step 8 -Total $TOTAL_STEPS -Message "Generating ISO: $($profile.output.isoPath)" -Status "info"

    New-PEImage -WorkDir $profile.workDir -IsoPath $profile.output.isoPath -Verbose:$VerbosePreference

    Write-BuildLog -Step 8 -Total $TOTAL_STEPS -Message "ISO generated: $($profile.output.isoPath)" -Status "success"
}

# =============================================================================
# Done
# =============================================================================
Write-BuildLog -Step $TOTAL_STEPS -Total $TOTAL_STEPS -Message "Build complete!" -Status "success"
