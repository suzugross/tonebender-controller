using ToneBenderController.Models;

namespace ToneBenderController.Services;

/// <summary>
/// USB drive enumeration and partitioning operations.
/// </summary>
public interface IDiskService
{
    /// <summary>Enumerate connected USB removable drives.</summary>
    Task<List<UsbDriveInfo>> GetUsbDrivesAsync();

    /// <summary>Partition a USB drive with WINPE/DATA layout.</summary>
    Task<UsbPartitionResult> PartitionDriveAsync(int diskNumber, UsbPartitionConfig config, IProgress<string>? progress = null);

    /// <summary>Detect whether the system uses UEFI (true) or BIOS (false) firmware.</summary>
    Task<bool> IsUefiFirmwareAsync();

    /// <summary>Deploy WinPE workspace media files to a USB partition using robocopy.</summary>
    Task<bool> DeployWinPeAsync(string mediaSourceDir, char winPeDriveLetter, IProgress<string>? progress = null);
}
