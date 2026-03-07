using System.Text.Json.Serialization;

namespace ToneBenderController.Models;

/// <summary>
/// Maps to autopilot.json — mirrors ToneBender's AutopilotConfig.h.
/// </summary>
public class AutopilotConfig
{
    [JsonPropertyName("displayTitle")]
    public string DisplayTitle { get; set; } = "";

    [JsonPropertyName("imageFile")]
    public string ImageFile { get; set; } = "";

    [JsonPropertyName("postAction")]
    public string PostAction { get; set; } = "shutdown";

    [JsonPropertyName("targetDisk")]
    public int TargetDisk { get; set; }

    [JsonPropertyName("wimIndex")]
    public int WimIndex { get; set; } = 1;

    [JsonPropertyName("dataPartitionMB")]
    public int DataPartitionMB { get; set; }
}
