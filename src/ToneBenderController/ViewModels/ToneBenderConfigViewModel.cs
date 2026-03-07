using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ToneBenderController.Models;
using ToneBenderController.Services;

namespace ToneBenderController.ViewModels;

/// <summary>
/// ToneBender autopilot.json configuration editor.
/// </summary>
public partial class ToneBenderConfigViewModel : ObservableObject
{
    private readonly IAutopilotService _autopilotService;

    [ObservableProperty]
    private string _imageFilePath = "";

    [ObservableProperty]
    private string _configFilePath = "";

    [ObservableProperty]
    private bool _isFFU;

    [ObservableProperty]
    private string _formatDisplay = "";

    [ObservableProperty]
    private string _displayTitle = "";

    [ObservableProperty]
    private string _imageFile = "";

    [ObservableProperty]
    private string _postAction = "shutdown";

    [ObservableProperty]
    private int _targetDisk;

    [ObservableProperty]
    private int _wimIndex = 1;

    [ObservableProperty]
    private int _dataPartitionMB;

    public List<string> PostActionOptions { get; } = ["shutdown", "restart", "none"];

    public ToneBenderConfigViewModel(IAutopilotService autopilotService)
    {
        _autopilotService = autopilotService;
    }

    [RelayCommand]
    private async Task BrowseImageAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Image File",
            Filter = "Disk Images (*.ffu;*.wim)|*.ffu;*.wim|All Files (*.*)|*.*",
            InitialDirectory = FindImageDirectory()
        };

        if (dialog.ShowDialog() != true) return;

        ImageFilePath = dialog.FileName;
        ImageFile = Path.GetFileName(dialog.FileName);
        UpdateFormat();

        // Check for existing autopilot.json in the same directory
        var dir = Path.GetDirectoryName(dialog.FileName)!;
        ConfigFilePath = Path.Combine(dir, "autopilot.json");

        if (File.Exists(ConfigFilePath))
        {
            await LoadFromFileAsync(ConfigFilePath);
        }
        else
        {
            // Defaults for a new config
            DisplayTitle = Path.GetFileNameWithoutExtension(dialog.FileName);
            PostAction = "shutdown";
            TargetDisk = 0;
            WimIndex = 1;
            DataPartitionMB = 0;
        }
    }

    public async Task SaveConfigAsync()
    {
        var config = new AutopilotConfig
        {
            DisplayTitle = DisplayTitle,
            ImageFile = ImageFile,
            PostAction = PostAction,
            TargetDisk = TargetDisk,
            WimIndex = WimIndex,
            DataPartitionMB = DataPartitionMB
        };
        await _autopilotService.SaveAsync(ConfigFilePath, config);
    }

    partial void OnImageFileChanged(string value)
    {
        UpdateFormat();
    }

    private void UpdateFormat()
    {
        var ext = Path.GetExtension(ImageFile).ToLowerInvariant();
        IsFFU = ext == ".ffu";
        FormatDisplay = IsFFU
            ? "FFU (Full Flash Update)"
            : "WIM (Windows Imaging)";
    }

    private async Task LoadFromFileAsync(string path)
    {
        try
        {
            var config = await _autopilotService.LoadAsync(path);
            DisplayTitle = config.DisplayTitle;
            ImageFile = config.ImageFile;
            PostAction = config.PostAction;
            TargetDisk = config.TargetDisk;
            WimIndex = config.WimIndex;
            DataPartitionMB = config.DataPartitionMB;
        }
        catch
        {
            // If the file is corrupt, keep defaults
        }
    }

    private static string FindImageDirectory()
    {
        // Try to find [IMAGE] folder on connected drives
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            var imageDir = Path.Combine(drive.RootDirectory.FullName, "[IMAGE]");
            if (Directory.Exists(imageDir))
                return imageDir;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }
}
