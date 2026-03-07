#Requires -Version 5.1

<#
.SYNOPSIS
    DISM wrapper module for adding WinPE packages and configuring locale
#>

function Add-PEPackages {
    <#
    .SYNOPSIS
        Add specified packages to a mounted WIM image.
        Also installs matching language packs if locale.language is specified.
    .PARAMETER MountDir
        WIM mount directory
    .PARAMETER Packages
        Array of package names (e.g. "WinPE-WMI", "WinPE-Scripting")
    .PARAMETER OCsPath
        WinPE OCs folder path
    .PARAMETER Language
        Optional language code (e.g. "ja-jp"). If set, installs language packs
        from $OCsPath\<language>\<pkg>_<language>.cab when available.
    #>
    param(
        [Parameter(Mandatory)][string]$MountDir,
        [Parameter(Mandatory)][string[]]$Packages,
        [Parameter(Mandatory)][string]$OCsPath,
        [string]$Language
    )

    $dism = "$env:SystemRoot\System32\dism.exe"
    $totalPackages = $Packages.Count
    $current = 0

    foreach ($pkg in $Packages) {
        $current++
        $cabPath = "$OCsPath\$pkg.cab"

        if (-not (Test-Path $cabPath)) {
            Write-Warning "Package not found (skipping): $cabPath"
            continue
        }

        # Build DISM arguments: base package + language pack if available
        $dismArgs = @("/Image:`"$MountDir`"", "/Add-Package", "/PackagePath:`"$cabPath`"")

        if ($Language) {
            $langCab = "$OCsPath\$Language\$($pkg)_$($Language).cab"
            if (Test-Path $langCab) {
                $dismArgs += "/PackagePath:`"$langCab`""
            }
        }

        Write-Verbose "[$current/$totalPackages] Adding package: $pkg"
        $dismCmd = "$dismArgs" -join " "
        cmd.exe /c "`"$dism`" $dismCmd" 2>&1 | Write-Verbose

        if ($LASTEXITCODE -ne 0) {
            throw "Failed to add package: $pkg (ExitCode: $LASTEXITCODE)"
        }

        Write-Verbose "[$current/$totalPackages] Package added: $pkg"
    }

    # Install base language pack (lp.cab) if language is specified
    if ($Language) {
        $lpCab = "$OCsPath\$Language\lp.cab"
        if (Test-Path $lpCab) {
            Write-Verbose "Adding language pack: $lpCab"
            & $dism /Image:"$MountDir" /Add-Package /PackagePath:"$lpCab"
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to add language pack: $lpCab (ExitCode: $LASTEXITCODE)"
            }
        }
    }
}

function Set-PELocale {
    <#
    .SYNOPSIS
        Apply locale, keyboard, and timezone settings to a mounted WIM image
    .PARAMETER MountDir
        WIM mount directory
    .PARAMETER Locale
        Locale settings object from JSON profile
    #>
    param(
        [Parameter(Mandatory)][string]$MountDir,
        [Parameter(Mandatory)]$Locale
    )

    $dism = "$env:SystemRoot\System32\dism.exe"

    if ($Locale.language) {
        Write-Verbose "Setting language: $($Locale.language)"
        & $dism /Image:"$MountDir" /Set-AllIntl:$($Locale.language)
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to set language (ExitCode: $LASTEXITCODE)"
        }
    }

    if ($Locale.inputLocale) {
        Write-Verbose "Setting input locale: $($Locale.inputLocale)"
        & $dism /Image:"$MountDir" /Set-InputLocale:$($Locale.inputLocale)
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to set input locale (ExitCode: $LASTEXITCODE)"
        }
    }

    if ($null -ne $Locale.layeredDriver) {
        Write-Verbose "Setting layered driver: $($Locale.layeredDriver)"
        & $dism /Image:"$MountDir" /Set-LayeredDriver:$($Locale.layeredDriver)
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to set layered driver (ExitCode: $LASTEXITCODE)"
        }
    }

    if ($Locale.timezone) {
        Write-Verbose "Setting timezone: $($Locale.timezone)"
        & $dism /Image:"$MountDir" /Set-TimeZone:"$($Locale.timezone)"
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to set timezone (ExitCode: $LASTEXITCODE)"
        }
    }
}

function Set-PEExecutionPolicy {
    <#
    .SYNOPSIS
        Set PowerShell ExecutionPolicy in the mounted WIM registry.
        This allows PS1 scripts to run without -ExecutionPolicy Bypass at launch.
    .PARAMETER MountDir
        WIM mount directory
    .PARAMETER Policy
        ExecutionPolicy value (default: Bypass)
    #>
    param(
        [Parameter(Mandatory)][string]$MountDir,
        [ValidateSet("Bypass","Unrestricted","RemoteSigned")]
        [string]$Policy = "Bypass"
    )

    $hivePath = "$MountDir\Windows\System32\config\SOFTWARE"
    if (-not (Test-Path $hivePath)) {
        throw "SOFTWARE registry hive not found: $hivePath"
    }

    $tempKey = "HKLM\PE_SOFTWARE"

    try {
        # Load the offline registry hive
        Write-Verbose "Loading registry hive: $hivePath"
        & reg load $tempKey "$hivePath" 2>&1 | Write-Verbose
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to load registry hive (ExitCode: $LASTEXITCODE)"
        }

        # Set ExecutionPolicy
        $regPath = "$tempKey\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell"
        & reg add "$regPath" /v ExecutionPolicy /t REG_SZ /d $Policy /f 2>&1 | Write-Verbose
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to set ExecutionPolicy in registry (ExitCode: $LASTEXITCODE)"
        }

        Write-Verbose "ExecutionPolicy set to '$Policy' in WIM registry"
    } finally {
        # Unload the hive
        [gc]::Collect()
        Start-Sleep -Milliseconds 500
        & reg unload $tempKey 2>&1 | Write-Verbose
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Failed to unload registry hive. A reboot may be required to release the lock."
        }
    }
}

Export-ModuleMember -Function Add-PEPackages, Set-PELocale, Set-PEExecutionPolicy
