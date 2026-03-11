using System.Collections.ObjectModel;
using System.IO;
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

    public ObservableCollection<WimEdition> Editions { get; } = [];

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

    [ObservableProperty]
    private bool _injectRegistry;

    public ImagePrepViewModel(IWindowsImageService imageService)
    {
        _imageService = imageService;
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

            // Customize WIM (unattend + registry) in a single mount
            if (InjectUnattend || InjectRegistry)
            {
                StatusText = "Customizing WIM...";
                var customizeProgress = new Progress<string>(msg => StatusText = msg);
                await _imageService.CustomizeWimAsync(
                    destPath,
                    InjectUnattend ? GenerateUnattendXml() : null,
                    InjectRegistry,
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
                        <InputLocale>ja-JP</InputLocale>
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
}
