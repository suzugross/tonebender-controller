#Requires -Version 5.1

<#
.SYNOPSIS
    ISO image generation module
#>

function New-PEImage {
    <#
    .SYNOPSIS
        Generate an ISO file by calling MakeWinPEMedia.cmd
    .PARAMETER WorkDir
        Working directory (created by copype)
    .PARAMETER IsoPath
        Output ISO file path
    #>
    param(
        [Parameter(Mandatory)][string]$WorkDir,
        [Parameter(Mandatory)][string]$IsoPath
    )

    # Ensure output directory exists
    $isoDir = Split-Path -Path $IsoPath -Parent
    if (-not (Test-Path $isoDir)) {
        New-Item -Path $isoDir -ItemType Directory -Force | Out-Null
    }

    # Remove existing ISO file
    if (Test-Path $IsoPath) {
        Write-Verbose "Removing existing ISO file: $IsoPath"
        Remove-Item -Path $IsoPath -Force
    }

    # Locate MakeWinPEMedia.cmd
    $adkPath = $env:WADROOT
    if ([string]::IsNullOrWhiteSpace($adkPath)) {
        throw "WADROOT environment variable is not set. Run Initialize-ADKEnvironment first."
    }

    $makeMedia = "$adkPath\Windows Preinstallation Environment\MakeWinPEMedia.cmd"
    if (-not (Test-Path $makeMedia)) {
        throw "MakeWinPEMedia.cmd not found: $makeMedia"
    }

    # Run MakeWinPEMedia.cmd via cmd.exe (ISO mode)
    # Temporarily set ErrorActionPreference to Continue because oscdimg.exe
    # writes progress to stderr, and PS 5.1 treats stderr + "Stop" as
    # a terminating error even with 2>&1 redirection.
    Write-Verbose "Generating ISO: $IsoPath"
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $result = cmd.exe /c "`"$makeMedia`" /ISO `"$WorkDir`" `"$IsoPath`"" 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prevEAP
    }
    if ($exitCode -ne 0) {
        throw "ISO generation failed (ExitCode: $exitCode)`n$result"
    }

    # Verify output
    if (-not (Test-Path $IsoPath)) {
        throw "ISO generation command succeeded but file not found: $IsoPath"
    }

    $isoSize = (Get-Item $IsoPath).Length / 1MB
    Write-Verbose ("ISO generated: {0} ({1:N1} MB)" -f $IsoPath, $isoSize)
}

Export-ModuleMember -Function New-PEImage
