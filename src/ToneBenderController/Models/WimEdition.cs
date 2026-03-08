namespace ToneBenderController.Models;

/// <summary>
/// Represents a single edition (index) inside a WIM/ESD file.
/// </summary>
public class WimEdition
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public long SizeBytes { get; set; }

    public string DisplaySize => SizeBytes switch
    {
        >= 1L * 1024 * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1L * 1024 * 1024        => $"{SizeBytes / (1024.0 * 1024):F1} MB",
        _                          => $"{SizeBytes} bytes"
    };
}
