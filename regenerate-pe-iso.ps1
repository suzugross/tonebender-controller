#Requires -Version 5.1
#Requires -RunAsAdministrator

<#
.SYNOPSIS
    Regenerate a WinPE ISO from an existing copype workspace.
.DESCRIPTION
    Used by Driver-only mode after boot.wim has been modified, to refresh the ISO
    without rebuilding the workspace from scratch. Skips workspace creation,
    package install, and locale configuration — only invokes MakeWinPEMedia.
.PARAMETER WorkDir
    Existing copype workspace directory (containing media\, fwfiles\, mount\).
.PARAMETER IsoPath
    Output ISO file path.
.EXAMPLE
    .\regenerate-pe-iso.ps1 -WorkDir "D:\my-pe" -IsoPath "D:\my-pe.iso"
#>

param(
    [Parameter(Mandatory)][string]$WorkDir,
    [Parameter(Mandatory)][string]$IsoPath
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$modulesDir = "$scriptDir\Modules"

Import-Module "$modulesDir\Environment.psm1" -Force
Import-Module "$modulesDir\Image.psm1" -Force

# Resolve relative paths
if (-not [System.IO.Path]::IsPathRooted($WorkDir)) {
    $WorkDir = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $WorkDir))
}
if (-not [System.IO.Path]::IsPathRooted($IsoPath)) {
    $IsoPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $IsoPath))
}

if (-not (Test-Path $WorkDir)) {
    throw "Workspace not found: $WorkDir"
}
if (-not (Test-Path "$WorkDir\media")) {
    throw "Workspace is missing media\ subdirectory: $WorkDir"
}

Write-BuildLog -Step 1 -Total 2 -Message "Detecting ADK..." -Status "info"
$adkPath = Get-ADKPath
Initialize-ADKEnvironment -ADKPath $adkPath
Write-BuildLog -Step 1 -Total 2 -Message "ADK detected: $adkPath" -Status "success"

Write-BuildLog -Step 2 -Total 2 -Message "Generating ISO: $IsoPath" -Status "info"
New-PEImage -WorkDir $WorkDir -IsoPath $IsoPath -Verbose:$VerbosePreference
Write-BuildLog -Step 2 -Total 2 -Message "ISO generated: $IsoPath" -Status "success"
