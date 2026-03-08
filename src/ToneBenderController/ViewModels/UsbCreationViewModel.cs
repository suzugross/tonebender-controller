using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ToneBenderController.Models;
using ToneBenderController.Services;

namespace ToneBenderController.ViewModels;

/// <summary>
/// USB drive selection and partition configuration.
/// </summary>
public partial class UsbCreationViewModel : ObservableObject
{
    private readonly IDiskService _diskService;

    public ObservableCollection<UsbDriveInfo> UsbDrives { get; } = [];

    [ObservableProperty]
    private UsbDriveInfo? _selectedDrive;

    [ObservableProperty]
    private int _winPeSizeMB = 2048;

    [ObservableProperty]
    private int _winInstSizeMB = 8192;

    public UsbCreationViewModel(IDiskService diskService)
    {
        _diskService = diskService;
        _ = RefreshDrivesAsync();
    }

    [RelayCommand]
    private async Task RefreshDrivesAsync()
    {
        UsbDrives.Clear();
        var drives = await _diskService.GetUsbDrivesAsync();
        foreach (var drive in drives)
            UsbDrives.Add(drive);
    }
}
