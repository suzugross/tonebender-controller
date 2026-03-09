namespace ToneBenderController.Models;

/// <summary>
/// Result of a USB drive partitioning operation.
/// </summary>
public class UsbPartitionResult
{
    public bool Success { get; set; }
    public char WinPeLetter { get; set; }
    public char DataLetter { get; set; }
    public string? ErrorMessage { get; set; }
}
