namespace ToneBenderController.Models;

/// <summary>
/// Single log line displayed in the WinPE build log area.
/// </summary>
public record BuildLogEntry(string Time, string Message, string Status);
