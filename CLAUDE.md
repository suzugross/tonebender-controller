# ToneBender Controller - WinPE Builder Framework

## Project Overview

PowerShell-based WinPE build automation framework.
Reads a JSON profile and orchestrates the full WinPE ISO build pipeline using Windows ADK.

The resulting WinPE ISO contains **ToneBender** (`c:\Users\szk-WIN01\Desktop\tonebender`), a C++ Win32 disk imaging tool. This project builds the bootable environment; ToneBender is the application that runs inside it.

## Project Vision

End-to-end tool covering: bootable USB creation → WinPE build → ToneBender configuration.
Make WinPE creation simple and accessible.

### Feature Roadmap

#### 1. Bootable USB Creation
- Partition a USB drive into 3 partitions with user-specified sizes:
  - **WINPE** (FAT32) — WinPE boot partition
  - **WININST** (FAT32) — Windows install media
  - **DATA** (NTFS) — Data/image storage
- Auto-create ToneBender directory structure from templates on the USB

#### 2. WinPE Build
- Core WinPE creation (current functionality, skip fine-tuning params for now)
- Deploy `startnet.cmd` that auto-launches ToneBender on boot
- OEM driver injection into WinPE image

#### 3. Windows Clean Install Preparation (deferred)
- Mount Windows ISO and enumerate edition indexes
- Extract the desired edition's WIM
- Optionally inject drivers into the extracted WIM
- Produce a ToneBender-ready image for deployment

#### 4. ToneBender Configuration
- GUI-based autopilot mode configuration editor
- Edit autopilot.json settings (target disk, image file, post-action, data partition, etc.) visually

## Language

- Code: English only (comments, variable names, messages)
- Plan documents: Japanese

## Technology Stack

- PowerShell 5.1 (Windows built-in, no external modules)
- Windows ADK (Assessment and Deployment Kit) + WinPE add-on
- DISM for WIM manipulation and package installation
- copype.cmd / MakeWinPEMedia.cmd from ADK

## Architecture

### Main Script
- `tonebender.ps1` — 8-step build orchestrator (admin check → profile load → ADK detect → workspace → mount → packages → locale/inject → unmount/ISO)

### Modules (`Modules/`)
| Module | Responsibility |
|--------|---------------|
| `Environment.psm1` | ADK registry detection, PATH setup, admin check, `Write-BuildLog` |
| `WorkSpace.psm1` | copype workspace creation, WIM mount/unmount, file injection (`Copy-ToWIM`) |
| `Packages.psm1` | DISM package installation, locale/keyboard/timezone, ExecutionPolicy |
| `Image.psm1` | ISO generation via `MakeWinPEMedia.cmd` |

### Profiles (`Profiles/`)
- JSON configuration files defining architecture, packages, locale, file injection, and output settings
- `default.json` — amd64, Japanese locale, standard WinPE packages

## Build Pipeline (8 Steps)

1. Admin privilege check
2. Load and validate JSON profile
3. Detect ADK and initialize environment (PATH, env vars)
4. Create workspace via `copype.cmd`
5. Mount boot.wim with DISM
6. Add WinPE packages (base + language packs)
7. Apply locale settings, set ExecutionPolicy, inject files into WIM
8. Unmount WIM (commit) and generate ISO

## Key Design Patterns

- **Profile-driven**: All build configuration externalized to JSON
- **Structured logging**: `Write-BuildLog` outputs JSON progress for machine-readable output
- **Safe WIM handling**: try/finally ensures WIM is always unmounted (discard on error)
- **Module isolation**: Each module exports only its public functions via `Export-ModuleMember`

## Running

```powershell
# Must run as Administrator
.\tonebender.ps1 -ProfilePath ".\Profiles\default.json"
.\tonebender.ps1 -ProfilePath ".\Profiles\default.json" -Verbose
```

## Profile JSON Structure

```json
{
  "profile": "name",
  "architecture": "amd64",
  "workDir": "Output\\Work",
  "output": { "iso": true, "isoPath": "Output\\WinPE.iso" },
  "packages": ["WinPE-WMI", "WinPE-PowerShell", ...],
  "locale": {
    "language": "ja-jp",
    "inputLocale": "0411:00000411",
    "layeredDriver": 6,
    "timezone": "Tokyo Standard Time"
  },
  "inject": [
    { "source": "..\\CaptureTools", "destination": "CaptureTools" }
  ]
}
```

## Bash Commands

All bash commands are pre-approved for this project.
