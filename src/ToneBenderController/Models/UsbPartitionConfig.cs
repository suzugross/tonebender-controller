namespace ToneBenderController.Models;

/// <summary>
/// Configuration for USB drive partitioning.
/// Two partitions: WINPE (FAT32), DATA (NTFS).
/// </summary>
public class UsbPartitionConfig
{
    /// <summary>WINPE partition size in MB (FAT32, WinPE boot).</summary>
    public int WinPeSizeMB { get; set; } = 4096;

    /// <summary>DATA partition uses remaining space (NTFS, image/data storage).</summary>
    public bool DataUsesRemainingSpace { get; set; } = true;
}
