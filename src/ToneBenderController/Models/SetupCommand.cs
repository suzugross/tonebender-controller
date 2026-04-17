using System.Text.Json.Serialization;

namespace ToneBenderController.Models;

public class SetupCommand
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";
}
