using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ToneBenderController.Models;
using ToneBenderController.Services;

namespace ToneBenderController.ViewModels;

/// <summary>
/// Windows ISO mounting, WIM edition enumeration, and single-edition export.
/// </summary>
public partial class ImagePrepViewModel : ObservableObject
{
    private readonly IWindowsImageService _imageService;
    private CancellationTokenSource? _exportCts;
    private string? _wimFilePath;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ObservableCollection<WimEdition> Editions { get; } = [];
    public ObservableCollection<SetupCommand> SetupCommands { get; } = [];
    public ObservableCollection<string> AvailablePresets { get; } = [];

    [ObservableProperty]
    private string _selectedPreset = "default";

    [ObservableProperty]
    private string _isoFilePath = "";

    [ObservableProperty]
    private bool _isMounted;

    [ObservableProperty]
    private char _mountedDriveLetter;

    [ObservableProperty]
    private WimEdition? _selectedEdition;

    [ObservableProperty]
    private string _outputDirectory = "";

    [ObservableProperty]
    private string _outputFileName = "";

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private int _exportProgress;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _driverDirectory = "";

    [ObservableProperty]
    private bool _injectUnattend;

    public ImagePrepViewModel(IWindowsImageService imageService)
    {
        _imageService = imageService;
        RefreshPresets();
        LoadSetupCommands();
    }

    partial void OnSelectedPresetChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
            LoadSetupCommands();
    }

    partial void OnSelectedEditionChanged(WimEdition? value)
    {
        if (value is null)
        {
            OutputFileName = "";
            return;
        }

        OutputFileName = SanitizeFileName(value.Name) + ".wim";
    }

    // ── Setup Commands management ────────────────────────────────

    private static string GetSetupCommandsDir()
    {
        string appDir = AppContext.BaseDirectory;
        string? dir = appDir;
        while (dir != null)
        {
            string profilesDir = Path.Combine(dir, "Profiles");
            if (Directory.Exists(profilesDir))
                return Path.Combine(profilesDir, "SetupCommands");
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(appDir, "Profiles", "SetupCommands");
    }

    private void RefreshPresets()
    {
        string dir = GetSetupCommandsDir();
        Directory.CreateDirectory(dir);

        // Migrate legacy setup-commands.json if it exists
        string legacyPath = Path.Combine(Directory.GetParent(dir)!.FullName, "setup-commands.json");
        string defaultPath = Path.Combine(dir, "default.json");
        if (File.Exists(legacyPath) && !File.Exists(defaultPath))
        {
            File.Move(legacyPath, defaultPath);
        }

        // Ensure default.json exists
        if (!File.Exists(defaultPath))
        {
            string json = JsonSerializer.Serialize(GetDefaultSetupCommands(), s_jsonOptions);
            File.WriteAllText(defaultPath, json);
        }

        string previous = SelectedPreset;
        AvailablePresets.Clear();
        foreach (string file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
            AvailablePresets.Add(Path.GetFileNameWithoutExtension(file));

        if (AvailablePresets.Contains(previous))
            SelectedPreset = previous;
        else if (AvailablePresets.Count > 0)
            SelectedPreset = AvailablePresets[0];
    }

    private void LoadSetupCommands()
    {
        SetupCommands.Clear();

        string path = Path.Combine(GetSetupCommandsDir(), $"{SelectedPreset}.json");
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                var commands = JsonSerializer.Deserialize<List<SetupCommand>>(json);
                if (commands != null)
                {
                    foreach (var cmd in commands)
                        SetupCommands.Add(cmd);
                    return;
                }
            }
            catch { /* fall through */ }
        }

        foreach (var cmd in GetDefaultSetupCommands())
            SetupCommands.Add(cmd);
    }

    private void SaveSetupCommands()
    {
        try
        {
            string dir = GetSetupCommandsDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{SelectedPreset}.json");
            string json = JsonSerializer.Serialize(SetupCommands.ToList(), s_jsonOptions);
            File.WriteAllText(path, json);
        }
        catch { /* best-effort */ }
    }

    [RelayCommand]
    private void AddSetupCommand()
    {
        SetupCommands.Add(new SetupCommand
        {
            Description = "New command",
            IsEnabled = true,
            Command = ""
        });
        SaveSetupCommands();
    }

    [RelayCommand]
    private void RemoveSetupCommand(SetupCommand? command)
    {
        if (command != null && SetupCommands.Remove(command))
            SaveSetupCommands();
    }

    [RelayCommand]
    private void SaveCommands()
    {
        SaveSetupCommands();
    }

    [RelayCommand]
    private void SaveAsPreset()
    {
        var sfd = new SaveFileDialog
        {
            Title = "Save Preset As",
            Filter = "JSON Files (*.json)|*.json",
            InitialDirectory = GetSetupCommandsDir(),
            FileName = "new-preset.json"
        };

        if (sfd.ShowDialog() == true)
        {
            string name = Path.GetFileNameWithoutExtension(sfd.FileName);
            string dir = GetSetupCommandsDir();
            string path = Path.Combine(dir, $"{name}.json");

            try
            {
                string json = JsonSerializer.Serialize(SetupCommands.ToList(), s_jsonOptions);
                File.WriteAllText(path, json);

                RefreshPresets();
                SelectedPreset = name;
            }
            catch { /* best-effort */ }
        }
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset == "default") return;

        string path = Path.Combine(GetSetupCommandsDir(), $"{SelectedPreset}.json");
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { return; }
        }

        RefreshPresets();
    }

    // ── ISO / Edition browsing ───────────────────────────────────

    [RelayCommand]
    private async Task BrowseIsoAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Windows ISO",
            Filter = "ISO Images (*.iso)|*.iso|All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            IsoFilePath = dialog.FileName;
            await LoadEditionsAsync();
        }
    }

    [RelayCommand]
    private async Task LoadEditionsAsync()
    {
        if (string.IsNullOrEmpty(IsoFilePath))
        {
            StatusText = "No ISO file selected.";
            return;
        }

        if (!File.Exists(IsoFilePath))
        {
            StatusText = "ISO file not found.";
            return;
        }

        try
        {
            // Unmount previous ISO if any
            if (IsMounted)
                await UnmountAsync();

            StatusText = "Mounting ISO...";
            MountedDriveLetter = await _imageService.MountIsoAsync(IsoFilePath);
            IsMounted = true;

            // Find install.wim or install.esd
            string sourcesDir = $"{MountedDriveLetter}:\\sources";
            _wimFilePath = FindInstallImage(sourcesDir);

            if (_wimFilePath == null)
            {
                StatusText = "No install.wim or install.esd found in ISO.";
                return;
            }

            StatusText = $"Reading editions from {Path.GetFileName(_wimFilePath)}...";
            var editions = await _imageService.GetWimEditionsAsync(_wimFilePath);

            Editions.Clear();
            foreach (var ed in editions)
                Editions.Add(ed);

            SelectedEdition = Editions.FirstOrDefault();

            // Auto-detect output directory
            if (string.IsNullOrEmpty(OutputDirectory))
                OutputDirectory = FindImageDirectory();

            StatusText = $"{Editions.Count} edition(s) found.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

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

    [RelayCommand]
    private void BrowseOutputDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Output Directory",
            InitialDirectory = string.IsNullOrEmpty(OutputDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                : OutputDirectory
        };

        if (dialog.ShowDialog() == true)
            OutputDirectory = dialog.FolderName;
    }

    // ── Export ────────────────────────────────────────────────────

    public async Task<bool> ExportAsync()
    {
        if (SelectedEdition is null || _wimFilePath is null || IsExporting)
            return false;

        if (string.IsNullOrEmpty(OutputDirectory))
        {
            StatusText = "No output directory selected.";
            return false;
        }

        if (string.IsNullOrEmpty(OutputFileName))
        {
            StatusText = "No output filename specified.";
            return false;
        }

        // Save latest toggle states before export
        SaveSetupCommands();

        IsExporting = true;
        ExportProgress = 0;
        StatusText = $"Exporting {SelectedEdition.Name}...";

        _exportCts = new CancellationTokenSource();

        var progress = new Progress<int>(pct =>
        {
            ExportProgress = pct;
            StatusText = $"Exporting {SelectedEdition.Name}... {pct}%";
        });

        try
        {
            // Create ToneBender-compatible timestamped subdirectory:
            //   [IMAGE]/YYYY_MM_DD_HHMMSS_ImageName/ImageName.wim
            string imageName = Path.GetFileNameWithoutExtension(OutputFileName);
            string timestamp = DateTime.Now.ToString("yyyy_MM_dd_HHmmss");
            string subDirName = $"{timestamp}_{imageName}";
            string subDir = Path.Combine(OutputDirectory, subDirName);
            Directory.CreateDirectory(subDir);

            string destPath = Path.Combine(subDir, OutputFileName);
            await _imageService.ExportEditionAsync(
                _wimFilePath, SelectedEdition.Index, destPath, progress, _exportCts.Token);

            ExportProgress = 100;

            // Inject OEM drivers if a driver directory is specified
            if (!string.IsNullOrEmpty(DriverDirectory))
            {
                StatusText = "Injecting OEM drivers...";
                var driverProgress = new Progress<string>(msg => StatusText = msg);
                await _imageService.InjectDriversIntoWimAsync(
                    destPath, DriverDirectory, driverProgress, _exportCts.Token);
            }

            // Customize WIM (unattend + SetupComplete) in a single mount
            bool hasSetupCommands = SetupCommands.Any(c => c.IsEnabled);

            if (InjectUnattend || hasSetupCommands)
            {
                StatusText = "Customizing WIM...";
                var customizeProgress = new Progress<string>(msg => StatusText = msg);
                await _imageService.CustomizeWimAsync(
                    destPath,
                    InjectUnattend ? GenerateUnattendXml() : null,
                    hasSetupCommands ? GenerateSetupCompleteCmd() : null,
                    customizeProgress,
                    _exportCts.Token);
            }

            StatusText = $"Export complete: {subDirName}\\{OutputFileName}";
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Export cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            StatusText = $"Export error: {ex.Message}";
            return false;
        }
        finally
        {
            IsExporting = false;
            _exportCts.Dispose();
            _exportCts = null;
        }
    }

    public void CancelExport()
    {
        _exportCts?.Cancel();
    }

    public async Task UnmountAsync()
    {
        if (!IsMounted || string.IsNullOrEmpty(IsoFilePath)) return;

        await _imageService.UnmountIsoAsync(IsoFilePath);
        IsMounted = false;
        MountedDriveLetter = default;
        _wimFilePath = null;
    }

    // ── Private helpers ──────────────────────────────────────────

    private static string? FindInstallImage(string sourcesDir)
    {
        if (!Directory.Exists(sourcesDir)) return null;

        string wimPath = Path.Combine(sourcesDir, "install.wim");
        if (File.Exists(wimPath)) return wimPath;

        string esdPath = Path.Combine(sourcesDir, "install.esd");
        if (File.Exists(esdPath)) return esdPath;

        return null;
    }

    private static string FindImageDirectory()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            var imageDir = Path.Combine(drive.RootDirectory.FullName, "[IMAGE]");
            if (Directory.Exists(imageDir))
                return imageDir;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    private static string SanitizeFileName(string name)
    {
        return Regex.Replace(name, @"[^\w\-.()]", "_");
    }

    private string GenerateSetupCompleteCmd()
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");

        var enabled = SetupCommands.Where(c => c.IsEnabled).ToList();
        int total = enabled.Count;
        int step = 0;

        foreach (var cmd in enabled)
        {
            step++;
            sb.AppendLine($"echo [{step}/{total}] {cmd.Description}...");
            sb.AppendLine(cmd.Command);
            sb.AppendLine();
        }

        sb.AppendLine("echo SetupComplete finished.");
        return sb.ToString();
    }

    private static string GenerateUnattendXml()
    {
        return """
            <?xml version="1.0" encoding="utf-8"?>
            <unattend xmlns="urn:schemas-microsoft-com:unattend">
                <settings pass="generalize">
                    <component name="Microsoft-Windows-PnpSysprep" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                        <DoNotCleanUpNonPresentDevices>true</DoNotCleanUpNonPresentDevices>
                        <PersistAllDeviceInstalls>true</PersistAllDeviceInstalls>
                    </component>
                </settings>
                <settings pass="specialize">
                    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                        <ComputerName>*</ComputerName>
                        <CopyProfile>true</CopyProfile>
                        <TimeZone>Tokyo Standard Time</TimeZone>
                    </component>
                </settings>
                <settings pass="oobeSystem">
                    <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                        <InputLocale>0411:00000411</InputLocale>
                        <SystemLocale>ja-JP</SystemLocale>
                        <UILanguage>ja-JP</UILanguage>
                        <UILanguageFallback>ja-JP</UILanguageFallback>
                        <UserLocale>ja-JP</UserLocale>
                    </component>
                    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                        <OOBE>
                            <HideEULAPage>true</HideEULAPage>
                            <ProtectYourPC>3</ProtectYourPC>
                            <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
                            <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
                            <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
                        </OOBE>
                        <UserAccounts>
                            <LocalAccounts>
                                <LocalAccount wcm:action="add">
                                    <Name>test</Name>
                                    <Group>Administrators</Group>
                                </LocalAccount>
                            </LocalAccounts>
                        </UserAccounts>
                    </component>
                    <component name="Microsoft-Windows-SecureStartup-FilterDriver" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                        <PreventDeviceEncryption>true</PreventDeviceEncryption>
                    </component>
                </settings>
            </unattend>
            """;
    }

    private static List<SetupCommand> GetDefaultSetupCommands()
    {
        return
        [
            new() { Description = "Administrator 有効化", IsEnabled = true, Command = "net user Administrator /active:yes" },
            new() { Description = "自動ログオン (AutoAdminLogon)", IsEnabled = true, Command = "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon\" /v AutoAdminLogon /t REG_SZ /d \"1\" /f" },
            new() { Description = "自動ログオン (DefaultUserName)", IsEnabled = true, Command = "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon\" /v DefaultUserName /t REG_SZ /d \"Administrator\" /f" },
            new() { Description = "自動ログオン (DefaultPassword)", IsEnabled = true, Command = "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon\" /v DefaultPassword /t REG_SZ /d \"\" /f" },
            new() { Description = "test ユーザー削除", IsEnabled = true, Command = "net user test /delete" },
            new() { Description = "タスク無効化 (Pre-staged app cleanup)", IsEnabled = true, Command = "schtasks /change /disable /tn \"\\Microsoft\\Windows\\AppxDeploymentClient\\Pre-staged app cleanup\" >nul 2>&1" },
            new() { Description = "Consumer Features 無効化", IsEnabled = true, Command = "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent\" /v DisableWindowsConsumerFeatures /t REG_DWORD /d 1 /f" },
            new() { Description = "Store 自動更新無効化 (Policy)", IsEnabled = true, Command = "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\WindowsStore\" /v AutoDownload /t REG_DWORD /d 2 /f" },
            new() { Description = "Store 自動ダウンロード無効化", IsEnabled = true, Command = "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsStore\\WindowsUpdate\" /v AutoDownload /t REG_DWORD /d 5 /f" },
            new() { Description = "Store OS アップグレード無効化", IsEnabled = true, Command = "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\WindowsStore\" /v DisableOSUpgrade /t REG_DWORD /d 1 /f" },
            new() { Description = "日本語キーボード (LayerDriver JPN)", IsEnabled = true, Command = "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\i8042prt\\Parameters\" /v \"LayerDriver JPN\" /t REG_SZ /d \"kbd106.dll\" /f" },
            new() { Description = "日本語キーボード (OverrideKeyboardSubtype)", IsEnabled = true, Command = "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\i8042prt\\Parameters\" /v \"OverrideKeyboardSubtype\" /t REG_DWORD /d 2 /f" },
            new() { Description = "再起動", IsEnabled = true, Command = "shutdown /r /t 3" },
        ];
    }
}
