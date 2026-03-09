using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ToneBenderController.Models;
using ToneBenderController.Services;

namespace ToneBenderController.ViewModels;

/// <summary>
/// USB drive selection, WinPE build, and deploy — unified pipeline.
/// </summary>
public partial class WinPeBuildViewModel : ObservableObject
{
    private readonly IProfileService _profileService;
    private readonly IPowerShellService _psService;
    private readonly IDiskService _diskService;
    private readonly string _profilesDir;
    private CancellationTokenSource? _buildCts;

    // ── USB Drive ──
    public ObservableCollection<UsbDriveInfo> UsbDrives { get; } = [];

    [ObservableProperty]
    private UsbDriveInfo? _selectedDrive;

    [ObservableProperty]
    private int _winPeSizeMB = 4096;

    // ── Build Profile ──
    public ObservableCollection<string> AvailableProfiles { get; } = [];
    public ObservableCollection<BuildLogEntry> BuildLog { get; } = [];

    [ObservableProperty]
    private string? _selectedProfile;

    [ObservableProperty]
    private BuildProfile? _currentProfile;

    [ObservableProperty]
    private string _buildStatus = "";

    [ObservableProperty]
    private int _buildProgress;

    [ObservableProperty]
    private bool _isBuilding;

    [ObservableProperty]
    private string _profileDetail = "";

    [ObservableProperty]
    private string _driverDirectory = "";

    public WinPeBuildViewModel(
        IProfileService profileService,
        IPowerShellService psService,
        IDiskService diskService)
    {
        _profileService = profileService;
        _psService = psService;
        _diskService = diskService;
        _profilesDir = Path.Combine(_psService.ScriptDir, "Profiles");

        try
        {
            var profiles = _profileService.GetAvailableProfiles(_profilesDir);
            foreach (var p in profiles)
                AvailableProfiles.Add(p);
            SelectedProfile = AvailableProfiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            BuildStatus = ex.Message;
        }

        _ = RefreshDrivesAsync();
    }

    // ── USB Drive commands ──

    [RelayCommand]
    private async Task RefreshDrivesAsync()
    {
        UsbDrives.Clear();
        var drives = await _diskService.GetUsbDrivesAsync();
        foreach (var drive in drives)
            UsbDrives.Add(drive);
    }

    // ── Profile ──

    partial void OnSelectedProfileChanged(string? value)
    {
        if (value is null)
        {
            CurrentProfile = null;
            ProfileDetail = "";
            return;
        }

        _ = LoadProfileAsync(Path.Combine(_profilesDir, value + ".json"));
    }

    partial void OnCurrentProfileChanged(BuildProfile? value)
    {
        if (value is null)
        {
            ProfileDetail = "";
            return;
        }

        ProfileDetail = $"{value.Architecture} | {value.Packages.Count} packages | {value.Output.IsoPath}";
    }

    // ── OEM Drivers ──

    [RelayCommand]
    private void BrowseDriverDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select OEM Driver Directory"
        };
        if (dialog.ShowDialog() == true)
            DriverDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void ClearDriverDirectory()
    {
        DriverDirectory = "";
    }

    // ── Build ──

    public async Task<bool> RunBuildAsync()
    {
        if (SelectedProfile is null || IsBuilding) return false;

        IsBuilding = true;
        BuildProgress = 0;
        BuildStatus = "Starting build...";
        BuildLog.Clear();

        _buildCts = new CancellationTokenSource();

        var progress = new Progress<BuildProgress>(bp =>
        {
            BuildProgress = bp.Total > 0
                ? (int)(bp.Step / (double)bp.Total * 100)
                : 0;
            BuildStatus = $"[{bp.Step}/{bp.Total}] {bp.Message}";
            BuildLog.Add(new BuildLogEntry(bp.Time, bp.Message, bp.Status));
        });

        try
        {
            var profilePath = Path.Combine(_profilesDir, SelectedProfile + ".json");
            string? driverPath = string.IsNullOrEmpty(DriverDirectory) ? null : DriverDirectory;
            await _psService.RunBuildAsync(profilePath, driverPath, progress, _buildCts.Token);

            BuildProgress = 100;
            BuildStatus = "Build complete!";
            BuildLog.Add(new BuildLogEntry(
                DateTime.Now.ToString("HH:mm:ss"), "Build complete!", "success"));
            return true;
        }
        catch (OperationCanceledException)
        {
            BuildStatus = "Build cancelled.";
            BuildLog.Add(new BuildLogEntry(
                DateTime.Now.ToString("HH:mm:ss"),
                "Build cancelled. WIM may remain mounted — run 'dism /unmount-wim /mountdir:<path> /discard' if needed.",
                "warn"));
            return false;
        }
        catch (Exception ex)
        {
            BuildStatus = $"Error: {ex.Message}";
            BuildLog.Add(new BuildLogEntry(
                DateTime.Now.ToString("HH:mm:ss"), $"Error: {ex.Message}", "error"));
            return false;
        }
        finally
        {
            IsBuilding = false;
            _buildCts.Dispose();
            _buildCts = null;
        }
    }

    public void CancelBuild()
    {
        _buildCts?.Cancel();
    }

    private async Task LoadProfileAsync(string path)
    {
        try
        {
            CurrentProfile = await _profileService.LoadAsync(path);
        }
        catch (Exception ex)
        {
            BuildStatus = $"Failed to load profile: {ex.Message}";
            CurrentProfile = null;
        }
    }
}
