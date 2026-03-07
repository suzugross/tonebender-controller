namespace ToneBenderController.Models;

/// <summary>
/// Configuration for USB drive partitioning.
/// Three partitions: WINPE (FAT32), WININST (FAT32), DATA (NTFS).
/// </summary>
public class UsbPartitionConfig
{
    /// <summary>WINPE partition size in MB (FAT32, WinPE boot).</summary>
    public int WinPeSizeMB { get; set; } = 2048;

    /// <summary>WININST partition size in MB (FAT32, Windows install media).</summary>
    public int WinInstSizeMB { get; set; } = 8192;

    /// <summary>DATA partition uses remaining space (NTFS, image/data storage).</summary>
    public bool DataUsesRemainingSpace { get; set; } = true;
}
