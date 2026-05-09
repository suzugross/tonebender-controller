using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ToneBenderController.Models;
using ToneBenderController.Services;

namespace ToneBenderController.ViewModels;

/// <summary>
/// Tab navigation and build execution management.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly WinPeBuildViewModel _winPeVm;
    private readonly ToneBenderConfigViewModel _configVm;
    private readonly ImagePrepViewModel _imagePrepVm;
    private readonly IDiskService _diskService;
    private readonly IPowerShellService _psService;
    private readonly IWindowsImageService _imageService;

    [ObservableProperty]
    private object _currentPage;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _executeButtonText = "Execute";

    public MainViewModel(
        WinPeBuildViewModel winPeVm,
        ToneBenderConfigViewModel configVm,
        ImagePrepViewModel imagePrepVm,
        IDiskService diskService,
        IPowerShellService psService,
        IWindowsImageService imageService)
    {
        _winPeVm = winPeVm;
        _configVm = configVm;
        _imagePrepVm = imagePrepVm;
        _diskService = diskService;
        _psService = psService;
        _imageService = imageService;

        _currentPage = _winPeVm;

        _winPeVm.PropertyChanged += OnChildVmPropertyChanged;
        _imagePrepVm.PropertyChanged += OnChildVmPropertyChanged;
    }

    private void OnChildVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WinPeBuildViewModel.IsBuilding)
                           or nameof(WinPeBuildViewModel.Mode)
                           or nameof(ImagePrepViewModel.IsExporting))
            UpdateExecuteButtonText();
    }

    partial void OnCurrentPageChanged(object? oldValue, object newValue)
    {
        UpdateExecuteButtonText();

        // Unmount ISO when navigating away from Image Prep
        if (oldValue is ImagePrepViewModel && _imagePrepVm.IsMounted)
            _ = _imagePrepVm.UnmountAsync();
    }

    private void UpdateExecuteButtonText()
    {
        ExecuteButtonText = CurrentPage switch
        {
            WinPeBuildViewModel { IsBuilding: true } => "Cancel",
            WinPeBuildViewModel vm => vm.Mode switch
            {
                BuildMode.Full          => "Execute Pipeline",
                BuildMode.PartitionOnly => "Partition USB",
                BuildMode.BuildOnly     => "Build PE",
                BuildMode.DriverOnly    => "Apply Drivers",
                _                       => "Execute"
            },
            ToneBenderConfigViewModel => "Save Config",
            ImagePrepViewModel { IsExporting: true } => "Cancel",
            ImagePrepViewModel { IsExporting: false } => "Export WIM",
            _ => "Execute"
        };
    }

    [RelayCommand]
    private void Navigate(string? page)
    {
        CurrentPage = page switch
        {
            "WinPeBuild"       => _winPeVm,
            "ToneBenderConfig" => _configVm,
            "ImagePrep"        => _imagePrepVm,
            _                  => CurrentPage
        };
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        switch (CurrentPage)
        {
            case WinPeBuildViewModel winPeVm:
                await DispatchWinPeAsync(winPeVm);
                break;
            case ToneBenderConfigViewModel configVm:
                await ExecuteConfigSaveAsync(configVm);
                break;
            case ImagePrepViewModel imagePrepVm:
                await ExecuteImagePrepAsync(imagePrepVm);
                break;
        }
    }

    private async Task DispatchWinPeAsync(WinPeBuildViewModel vm)
    {
        if (vm.IsBuilding)
        {
            vm.CancelBuild();
            StatusText = "Cancelling...";
            return;
        }

        switch (vm.Mode)
        {
            case BuildMode.Full:
                await RunFullPipelineAsync(vm);
                break;
            case BuildMode.PartitionOnly:
                await RunPartitionOnlyAsync(vm);
                break;
            case BuildMode.BuildOnly:
                await RunBuildOnlyAsync(vm);
                break;
            case BuildMode.DriverOnly:
                await RunDriverOnlyAsync(vm);
                break;
        }
    }

    private async Task ExecuteConfigSaveAsync(ToneBenderConfigViewModel configVm)
    {
        if (string.IsNullOrEmpty(configVm.ConfigFilePath))
        {
            StatusText = "No image selected. Use Browse to select an image file.";
            return;
        }

        try
        {
            await configVm.SaveConfigAsync();
            StatusText = $"Config saved: {configVm.ConfigFilePath}";
            MessageBox.Show(
                $"Config saved successfully.\n\n{configVm.ConfigFilePath}",
                "Save Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = "Failed to save config.";
            MessageBox.Show(
                ex.Message,
                "Save Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task ExecuteImagePrepAsync(ImagePrepViewModel imagePrepVm)
    {
        if (imagePrepVm.IsExporting)
        {
            imagePrepVm.CancelExport();
            StatusText = "Cancelling export...";
            return;
        }

        if (imagePrepVm.SelectedEdition is null)
        {
            StatusText = "No edition selected. Load editions from an ISO first.";
            return;
        }

        if (string.IsNullOrEmpty(imagePrepVm.OutputDirectory))
        {
            StatusText = "No output directory selected.";
            return;
        }

        StatusText = "Exporting WIM...";
        bool ok = await imagePrepVm.ExportAsync();
        StatusText = imagePrepVm.StatusText;

        if (ok)
            MessageBox.Show(
                $"WIM export completed successfully.\n\n{imagePrepVm.StatusText}",
                "Export Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
    }

    public async Task CleanupAsync()
    {
        if (_imagePrepVm.IsMounted)
            await _imagePrepVm.UnmountAsync();
    }

    // ─────────────────────────────────────────────────────────────────
    // Mode handlers
    // ─────────────────────────────────────────────────────────────────

    private async Task RunFullPipelineAsync(WinPeBuildViewModel vm)
    {
        // ── Validation ──
        if (vm.SelectedDrive == null)
        {
            StatusText = "No USB drive selected.";
            return;
        }

        if (vm.SelectedProfile is null)
        {
            StatusText = "No build profile selected.";
            return;
        }

        var currentDrives = await _diskService.GetUsbDrivesAsync();
        if (!currentDrives.Any(d => d.DiskNumber == vm.SelectedDrive.DiskNumber))
        {
            StatusText = "Selected drive is no longer connected. Please refresh.";
            return;
        }

        long requiredBytes = ((long)vm.WinPeSizeMB + 1024) * 1024 * 1024;
        if (vm.SelectedDrive.SizeBytes < requiredBytes)
        {
            StatusText = $"Drive too small. Need at least {requiredBytes / (1024 * 1024 * 1024)} GB.";
            return;
        }

        string profileName = vm.SelectedProfile;
        var profile = vm.CurrentProfile;
        if (profile == null)
        {
            StatusText = "No WinPE build profile loaded. Check Profiles directory.";
            return;
        }

        string workDir = Path.IsPathRooted(profile.WorkDir)
            ? profile.WorkDir
            : Path.Combine(_psService.ScriptDir, profile.WorkDir);
        string mediaDir = Path.Combine(workDir, "media");

        var result = MessageBox.Show(
            $"WARNING: All data on Disk {vm.SelectedDrive.DiskNumber} " +
            $"({vm.SelectedDrive.FriendlyName}, {vm.SelectedDrive.DisplaySize}) " +
            $"will be permanently erased.\n\n" +
            $"Pipeline:\n" +
            $"  1. Partition USB drive\n" +
            $"  2. Build WinPE (profile: {profileName})\n" +
            $"  3. Deploy WinPE to USB\n\n" +
            $"Partition layout:\n" +
            $"  WINPE (FAT32): {vm.WinPeSizeMB} MB\n" +
            $"  DATA (NTFS): Remaining space\n\n" +
            $"Continue?",
            "Confirm USB Pipeline",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var config = new UsbPartitionConfig
        {
            WinPeSizeMB = vm.WinPeSizeMB,
            DataUsesRemainingSpace = true
        };

        var progress = new Progress<string>(msg => StatusText = msg);

        try
        {
            // ── Phase 1: Partition USB ──
            StatusText = "[1/3] Partitioning USB drive...";
            var partResult = await _diskService.PartitionDriveAsync(
                vm.SelectedDrive.DiskNumber, config, vm.SelectedDrive.IsFixedDisk, progress);

            if (!partResult.Success)
            {
                StatusText = "[1/3] Partitioning failed.";
                MessageBox.Show(
                    partResult.ErrorMessage ?? "Unknown error",
                    "Partitioning Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // ── Phase 2: Build WinPE ──
            StatusText = "[2/3] Building WinPE...";
            bool buildOk = await vm.RunBuildAsync();

            if (!buildOk)
            {
                StatusText = "[2/3] WinPE build failed.";
                MessageBox.Show(
                    vm.BuildStatus,
                    "WinPE Build Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (!Directory.Exists(mediaDir))
            {
                StatusText = "[2/3] Build completed but media directory not found.";
                MessageBox.Show(
                    $"Expected media directory not found:\n{mediaDir}",
                    "Deploy Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // ── Phase 3: Deploy WinPE to USB ──
            StatusText = "[3/3] Deploying WinPE to USB...";
            await _diskService.DeployWinPeAsync(
                mediaDir, partResult.WinPeLetter, progress);

            string completeMsg = $"WINPE={partResult.WinPeLetter}:, DATA={partResult.DataLetter}:";
            StatusText = $"Pipeline complete! {completeMsg}";
            MessageBox.Show(
                $"USB pipeline completed successfully.\n\n{completeMsg}",
                "Pipeline Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = "Pipeline error.";
            MessageBox.Show(
                ex.Message,
                "Pipeline Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task RunPartitionOnlyAsync(WinPeBuildViewModel vm)
    {
        if (vm.SelectedDrive == null)
        {
            StatusText = "No USB drive selected.";
            return;
        }

        var currentDrives = await _diskService.GetUsbDrivesAsync();
        if (!currentDrives.Any(d => d.DiskNumber == vm.SelectedDrive.DiskNumber))
        {
            StatusText = "Selected drive is no longer connected. Please refresh.";
            return;
        }

        long requiredBytes = ((long)vm.WinPeSizeMB + 1024) * 1024 * 1024;
        if (vm.SelectedDrive.SizeBytes < requiredBytes)
        {
            StatusText = $"Drive too small. Need at least {requiredBytes / (1024 * 1024 * 1024)} GB.";
            return;
        }

        var result = MessageBox.Show(
            $"WARNING: All data on Disk {vm.SelectedDrive.DiskNumber} " +
            $"({vm.SelectedDrive.FriendlyName}, {vm.SelectedDrive.DisplaySize}) " +
            $"will be permanently erased.\n\n" +
            $"Partition layout:\n" +
            $"  WINPE (FAT32): {vm.WinPeSizeMB} MB\n" +
            $"  DATA (NTFS): Remaining space\n\n" +
            $"DATA will be initialized with [IMAGE]\\ and capture-config.json.\n\n" +
            $"Continue?",
            "Confirm USB Partition",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var config = new UsbPartitionConfig
        {
            WinPeSizeMB = vm.WinPeSizeMB,
            DataUsesRemainingSpace = true
        };

        var progress = new Progress<string>(msg => StatusText = msg);

        try
        {
            StatusText = "Partitioning USB drive...";
            var partResult = await _diskService.PartitionDriveAsync(
                vm.SelectedDrive.DiskNumber, config, vm.SelectedDrive.IsFixedDisk, progress);

            if (!partResult.Success)
            {
                StatusText = "Partitioning failed.";
                MessageBox.Show(
                    partResult.ErrorMessage ?? "Unknown error",
                    "Partitioning Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            string completeMsg = $"WINPE={partResult.WinPeLetter}:, DATA={partResult.DataLetter}:";
            StatusText = $"Partitioning complete. {completeMsg}";
            MessageBox.Show(
                $"USB partitioning completed successfully.\n\n{completeMsg}",
                "Partition Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = "Partitioning error.";
            MessageBox.Show(
                ex.Message,
                "Partitioning Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task RunBuildOnlyAsync(WinPeBuildViewModel vm)
    {
        if (vm.SelectedProfile is null)
        {
            StatusText = "No build profile selected.";
            return;
        }

        if (string.IsNullOrEmpty(vm.BuildOutputDirectory))
        {
            StatusText = "No build output directory selected.";
            return;
        }

        string outDir = Path.GetFullPath(vm.BuildOutputDirectory);

        // Safety: refuse drive roots — the engine recreates the workspace via
        // Remove-Item -Recurse -Force which would wipe an entire drive.
        if (string.Equals(Path.GetPathRoot(outDir), outDir, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "Drive root cannot be used as build output. Pick a subfolder.";
            MessageBox.Show(
                $"The build output directory cannot be a drive root ({outDir}).\n\n" +
                $"Pick a subfolder — the engine clears this directory before building.",
                "Invalid Output Directory",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        string outDirName = new DirectoryInfo(outDir).Name;
        string parentDir = Directory.GetParent(outDir)?.FullName ?? outDir;
        string isoPath = Path.Combine(parentDir, $"{outDirName}.iso");

        string isoLine = vm.BuildGenerateIso ? $"  ISO: {isoPath}\n" : "  ISO: (skipped)\n";
        string driverLine = string.IsNullOrEmpty(vm.DriverDirectory)
            ? "  Drivers: (none)\n"
            : $"  Drivers: {vm.DriverDirectory}\n";

        var result = MessageBox.Show(
            $"Build PE workspace into:\n  {outDir}\n\n" +
            $"WARNING: Existing contents of this directory will be DELETED.\n\n" +
            $"Profile: {vm.SelectedProfile}\n" +
            driverLine +
            isoLine +
            $"\nContinue?",
            "Confirm Build PE",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var overrides = new BuildOverrides
        {
            WorkDir = outDir,
            IsoPath = isoPath,
            GenerateIso = vm.BuildGenerateIso
        };

        StatusText = "Building PE...";
        bool ok = await vm.RunBuildAsync(overrides);

        if (!ok)
        {
            StatusText = "Build failed.";
            MessageBox.Show(
                vm.BuildStatus,
                "Build Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        StatusText = $"Build complete. Workspace at {outDir}";
        string isoMsg = vm.BuildGenerateIso ? $"\n\nISO: {isoPath}" : "";
        MessageBox.Show(
            $"PE build completed successfully.\n\nWorkspace: {outDir}{isoMsg}",
            "Build Complete",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async Task RunDriverOnlyAsync(WinPeBuildViewModel vm)
    {
        if (string.IsNullOrEmpty(vm.WorkspacePath))
        {
            StatusText = "No workspace selected.";
            return;
        }

        if (string.IsNullOrEmpty(vm.DriverDirectory))
        {
            StatusText = "No driver directory selected.";
            return;
        }

        string bootWim = Path.Combine(vm.WorkspacePath, "media", "sources", "boot.wim");
        if (!File.Exists(bootWim))
        {
            StatusText = "boot.wim not found in workspace.";
            MessageBox.Show(
                $"Expected file not found:\n{bootWim}\n\n" +
                $"The selected directory must be a copype workspace " +
                $"(produced by Build-only mode or the full pipeline).",
                "Invalid Workspace",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        string workspaceName = new DirectoryInfo(vm.WorkspacePath).Name;
        string parentDir = Directory.GetParent(vm.WorkspacePath)?.FullName ?? vm.WorkspacePath;
        string isoPath = Path.Combine(parentDir, $"{workspaceName}.iso");
        string isoLine = vm.DriverRegenerateIso
            ? $"  ISO will be regenerated at: {isoPath}\n"
            : "  ISO: (not regenerated)\n";

        var result = MessageBox.Show(
            $"Apply OEM drivers to existing PE:\n" +
            $"  Workspace: {vm.WorkspacePath}\n" +
            $"  boot.wim:  {bootWim}\n" +
            $"  Drivers:   {vm.DriverDirectory}\n" +
            isoLine +
            $"\nContinue?",
            "Confirm Driver Injection",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var ct = vm.BeginExternalOperation();
        try
        {
            vm.ReportExternalProgress("Injecting OEM drivers into boot.wim...", "info", 10);
            StatusText = "Injecting OEM drivers...";

            var driverProgress = new Progress<string>(msg =>
            {
                StatusText = msg;
                vm.ReportExternalProgress(msg, "info");
            });

            await _imageService.InjectDriversIntoWimAsync(
                bootWim, vm.DriverDirectory, driverProgress, ct);

            vm.ReportExternalProgress("Driver injection complete.", "success", 60);

            if (vm.DriverRegenerateIso)
            {
                vm.ReportExternalProgress("Regenerating ISO...", "info", 70);
                StatusText = "Regenerating ISO...";

                var psProgress = new Progress<BuildProgress>(bp =>
                {
                    StatusText = $"[{bp.Step}/{bp.Total}] {bp.Message}";
                    vm.ReportExternalProgress(bp.Message, bp.Status);
                });

                await _psService.RunRegenerateIsoAsync(
                    vm.WorkspacePath, isoPath, psProgress, ct);

                vm.ReportExternalProgress($"ISO regenerated: {isoPath}", "success", 100);
            }
            else
            {
                vm.ReportExternalProgress("Done.", "success", 100);
            }

            StatusText = vm.DriverRegenerateIso
                ? $"Drivers applied. ISO: {isoPath}"
                : "Drivers applied.";

            string isoMsg = vm.DriverRegenerateIso ? $"\n\nISO: {isoPath}" : "";
            MessageBox.Show(
                $"OEM drivers applied to boot.wim successfully.{isoMsg}",
                "Driver Injection Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Driver injection cancelled.";
            vm.ReportExternalProgress("Cancelled.", "warn");
        }
        catch (Exception ex)
        {
            StatusText = "Driver injection error.";
            vm.ReportExternalProgress($"Error: {ex.Message}", "error");
            MessageBox.Show(
                ex.Message,
                "Driver Injection Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            vm.EndExternalOperation();
        }
    }
}
