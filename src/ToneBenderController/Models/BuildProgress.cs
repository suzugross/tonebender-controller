using System.Text.Json.Serialization;

namespace ToneBenderController.Models;

/// <summary>
/// Parsed from Write-BuildLog JSON output during WinPE build.
/// </summary>
public class BuildProgress
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("step")]
    public int Step { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("time")]
    public string Time { get; set; } = "";
}
