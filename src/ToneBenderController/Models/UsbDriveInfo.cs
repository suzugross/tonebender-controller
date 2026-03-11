namespace ToneBenderController.Models;

/// <summary>
/// Represents a physical USB drive detected by the system.
/// </summary>
public class UsbDriveInfo
{
    public int DiskNumber { get; set; }
    public string FriendlyName { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool IsFixedDisk { get; set; }

    public string DisplaySize => SizeBytes switch
    {
        >= 1L * 1024 * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1L * 1024 * 1024        => $"{SizeBytes / (1024.0 * 1024):F1} MB",
        _                          => $"{SizeBytes} bytes"
    };

    public override string ToString() => $"Disk {DiskNumber}: {FriendlyName} ({DisplaySize})";
}
