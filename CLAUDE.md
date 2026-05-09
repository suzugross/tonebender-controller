# ToneBender Controller - WinPE Builder & USB Deploy Tool

## Project Overview

End-to-end Windows tool that automates: **USB partitioning → WinPE ISO build → WinPE USB deployment**, plus Windows install-image (WIM) preparation and ToneBender autopilot configuration.

The built WinPE bootstraps **ToneBender** (`c:\Users\szk-WIN01\Desktop\tonebender`), a C++ Win32 disk imaging tool — this project produces the bootable environment ToneBender runs inside, plus the WIM/FFU images and `autopilot.json` config it consumes.

## Architecture

Hybrid two-layer design:

```
[WPF GUI: src/ToneBenderController/]   ──spawn──>   [tonebender.ps1]
        │                                                │
   3 tabs / 3 ViewModels                         8-step DISM pipeline
   + DiskService (WMI + diskpart)               + Modules/*.psm1
   + WindowsImageService (DISM/Mount-DiskImage)
   + ProfileService / AutopilotService
```

The GUI is the primary entry point. `tonebender.ps1` is invoked as a child process (PowerShell 5.1, runs as Admin) and streams JSON progress on stdout for the GUI to parse.

### GUI (.NET 8 WPF, `src/ToneBenderController/`)

DI-wired with `Microsoft.Extensions.DependencyInjection`. Three tabs, each with its own ViewModel:

| Tab | ViewModel | Pipeline |
|---|---|---|
| **WinPE USB Builder** (blue) | `WinPeBuildViewModel` | Mode-driven (see below) |
| **ToneBender Config** (orange) | `ToneBenderConfigViewModel` | Edit `autopilot.json` (displayTitle, imageFile, postAction, targetDisk, wimIndex, dataPartitionMB) |
| **Image Prep** (purple) | `ImagePrepViewModel` | Mount Windows ISO → enumerate WIM editions → `DISM /Export-Image` single edition → optional driver / unattend.xml / SetupComplete.cmd injection |

### WinPE USB Builder Modes (`BuildMode` enum)

Single tab with a Mode ComboBox at top. `MainViewModel.DispatchWinPeAsync` dispatches by `WinPeBuildViewModel.Mode`:

| Mode | Action | Required input |
|---|---|---|
| `Full` (default) | Partition USB → Build PE → Deploy to USB (the original unified flow) | USB drive + profile + (optional) drivers |
| `PartitionOnly` | diskpart only — produces WINPE+DATA layout with `[IMAGE]\` and `capture-config.json` template on DATA | USB drive |
| `BuildOnly` | Build PE workspace into a user-specified folder. ISO generation is opt-in (default OFF). Profile overrides applied via temp JSON in `%TEMP%`. | Profile + output folder |
| `DriverOnly` | DISM-mount existing `<workspace>\media\sources\boot.wim`, inject drivers, optionally regenerate ISO via `regenerate-pe-iso.ps1`. ISO regen default OFF. **Workspace input only — USB-only PEs are out of scope.** | Workspace folder + driver folder |

Drive-root paths are refused for `BuildOnly` (the engine clears the target dir via `Remove-Item -Recurse -Force`).

### Services (`src/ToneBenderController/Services/`)

| Service | Responsibility |
|---|---|
| `DiskService` | USB detection (WMI), diskpart partitioning, **Disk 0 hard refusal**, removable vs fixed-disk format paths (Portable SSD / 4Kn handling), robocopy deploy |
| `WindowsImageService` | `Mount-DiskImage`, `DISM /Get-WimInfo` (en+ja parser), `/Export-Image` with progress regex, single-mount WIM customization (drivers + unattend.xml + SetupComplete.cmd) |
| `PowerShellService` | Spawn `tonebender.ps1`, parse stdout JSON lines into `BuildProgress`, kill process tree on cancel |
| `ProfileService` / `AutopilotService` | Read/write `Profiles/*.json` and `autopilot.json` (camelCase) |

### PowerShell Engine

| Script | Role |
|---|---|
| `tonebender.ps1` | Full 8-step build pipeline (used by Full and BuildOnly modes) |
| `regenerate-pe-iso.ps1` | ISO-only regeneration from existing workspace (used by DriverOnly mode when ISO regen is enabled) |

| Module | Responsibility |
|---|---|
| `Environment.psm1` | ADK registry detection, PATH/`WADROOT`/`WinPERoot` setup, admin check, `Write-BuildLog` (JSON progress emitter) |
| `WorkSpace.psm1` | `copype.cmd` workspace creation, `Mount-PEImage` / `Dismount-PEImage`, `Copy-ToWIM` |
| `Packages.psm1` | `DISM /Add-Package` (auto-detects matching `<pkg>_<lang>.cab` language packs), `Set-AllIntl` / `Set-InputLocale` / `Set-LayeredDriver` / `Set-TimeZone`, **offline registry hive load** to set ExecutionPolicy=Bypass, `Add-PEDrivers` |
| `Image.psm1` | `MakeWinPEMedia.cmd /ISO` (sets `ErrorActionPreference=Continue` since oscdimg writes progress to stderr) |

### Profiles (`Profiles/`)

- `Profiles/default.json` — amd64, ja-jp, standard WinPE packages, injects `Inject\CaptureTools\` and `Inject\startnet.cmd`
- `Profiles/SetupCommands/*.json` — multiple presets for SetupComplete.cmd command lists (Image Prep tab)

## Build Pipeline (8 Steps in `tonebender.ps1`)

1. Admin privilege check
2. Load and validate JSON profile
3. Detect ADK and initialize environment (PATH, env vars)
4. Create workspace via `copype.cmd`
5. Mount `boot.wim` with DISM
6. Add WinPE packages (base + language packs)
7. Apply locale, set ExecutionPolicy=Bypass in offline registry, inject OEM drivers, inject files into WIM
8. Unmount WIM (commit) and generate ISO

`try/finally` guarantees the WIM is always unmounted (discard on error).

## Technology Stack

- C# / .NET 8 WPF (CommunityToolkit.Mvvm, Microsoft.PowerShell.SDK, System.Management)
- PowerShell 5.1 (Windows built-in, no external modules)
- Windows ADK + WinPE add-on (DISM, copype.cmd, MakeWinPEMedia.cmd, oscdimg)

## Implementation Gotchas (already handled — preserve when editing)

- **Disk 0 is unconditionally refused** in `DiskService.PartitionDriveAsync` — system disk protection.
- **Fixed disks (Portable SSD) need split partition+format**: diskpart partitions, but PowerShell `Format-Volume` formats (4Kn / 512e sector handling).
- **DISM mount dir must be local NTFS** — `WindowsImageService` mounts under `%TEMP%\_drvmount_*`, never on USB.
- **robocopy destination must NOT be quoted**: `"P:\"` parses `\"` as escaped quote and breaks args. Use `P:\` bare.
- **`SetupComplete.cmd` is written as `Encoding.Default`** (Shift-JIS on JP Windows) — not UTF-8.
- **PowerShell 5.1 unwraps single-element JSON arrays** — `tonebender.ps1` wraps `$profile.inject` in `@()` to force array shape.
- **Don't use `StandardOutput.EndOfStream`** when reading DISM progress — synchronous blocking property freezes the UI thread on slow USB writes. Use `ReadLineAsync()` (see `WindowsImageService.ExportEditionAsync`).
- **WIM customization uses a single mount**: unattend.xml + SetupComplete.cmd are injected together in one `Mount-Wim` / `Unmount-Wim` cycle.
- **WinPE registry tweaks via offline hive load**: `Set-PEExecutionPolicy` calls `reg load` against `$MountDir\Windows\System32\config\SOFTWARE`; `[gc]::Collect()` + sleep before `reg unload` to release file handles.

## Key Design Patterns

- **Profile-driven**: build config externalized to JSON, loaded by both GUI (`BuildProfile.cs`) and PS1.
- **Structured logging across the boundary**: `Write-BuildLog` emits compact JSON; `PowerShellService` deserializes stream into `BuildProgress` events bound to the UI.
- **Safe WIM handling**: every mount sits inside `try/finally` (PS1) or try/catch (C#) with discard-unmount on error and `dism /Cleanup-Wim` before remount.
- **DI singletons**: all Services and ViewModels are singletons — single source of truth for USB drive list, mounted ISO state, etc.

## Language Convention

- **Code** (C#, PowerShell, comments, identifiers, log messages): English only.
- **Plan documents / discussion**: Japanese.

## Running

```powershell
# GUI (preferred — Visual Studio or `dotnet run`):
dotnet run --project src/ToneBenderController/ToneBenderController.csproj

# PowerShell engine direct (must run as Administrator):
.\tonebender.ps1 -ProfilePath ".\Profiles\default.json"
.\tonebender.ps1 -ProfilePath ".\Profiles\default.json" -DriverPath "C:\Drivers" -Verbose
```

## Profile JSON Structure

```json
{
  "profile": "default",
  "architecture": "amd64",
  "workDir": "Output\\Work",
  "output": { "iso": true, "isoPath": "Output\\WinPE.iso" },
  "packages": ["WinPE-WMI", "WinPE-PowerShell", "..."],
  "locale": {
    "language": "ja-jp",
    "inputLocale": "0411:00000411",
    "layeredDriver": 6,
    "timezone": "Tokyo Standard Time"
  },
  "inject": [
    { "source": "Inject\\CaptureTools", "destination": "CaptureTools" },
    { "source": "Inject\\startnet.cmd", "destination": "Windows\\System32\\startnet.cmd" }
  ]
}
```

## Bash Commands

All bash commands are pre-approved for this project.
