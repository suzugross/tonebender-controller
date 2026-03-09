using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        IPowerShellService psService)
    {
        _winPeVm = winPeVm;
        _configVm = configVm;
        _imagePrepVm = imagePrepVm;
        _diskService = diskService;
        _psService = psService;

        _currentPage = _winPeVm;

        _winPeVm.PropertyChanged += OnChildVmPropertyChanged;
        _imagePrepVm.PropertyChanged += OnChildVmPropertyChanged;
    }

    private void OnChildVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WinPeBuildViewModel.IsBuilding)
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
            WinPeBuildViewModel { IsBuilding: false } => "Execute",
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
                await ExecutePipelineAsync(winPeVm);
                break;
            case ToneBenderConfigViewModel configVm:
                await ExecuteConfigSaveAsync(configVm);
                break;
            case ImagePrepViewModel imagePrepVm:
                await ExecuteImagePrepAsync(imagePrepVm);
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

    private async Task ExecutePipelineAsync(WinPeBuildViewModel vm)
    {
        if (vm.IsBuilding)
        {
            vm.CancelBuild();
            StatusText = "Cancelling build...";
            return;
        }

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

        // Resolve build profile and workspace media path
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

        // ── Confirmation dialog ──
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
                vm.SelectedDrive.DiskNumber, config, progress);

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

            // ── Pipeline complete ──
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
}
