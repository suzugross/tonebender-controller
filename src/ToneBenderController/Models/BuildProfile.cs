using System.Text.Json.Serialization;

namespace ToneBenderController.Models;

/// <summary>
/// Maps to Profiles/*.json — WinPE build configuration.
/// </summary>
public class BuildProfile
{
    [JsonPropertyName("profile")]
    public string Profile { get; set; } = "";

    [JsonPropertyName("architecture")]
    public string Architecture { get; set; } = "amd64";

    [JsonPropertyName("workDir")]
    public string WorkDir { get; set; } = @"Output\Work";

    [JsonPropertyName("output")]
    public BuildOutput Output { get; set; } = new();

    [JsonPropertyName("packages")]
    public List<string> Packages { get; set; } = [];

    [JsonPropertyName("locale")]
    public LocaleSettings? Locale { get; set; }

    [JsonPropertyName("inject")]
    public List<InjectEntry> Inject { get; set; } = [];
}

public class BuildOutput
{
    [JsonPropertyName("iso")]
    public bool Iso { get; set; } = true;

    [JsonPropertyName("isoPath")]
    public string IsoPath { get; set; } = @"Output\WinPE.iso";
}

public class LocaleSettings
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("inputLocale")]
    public string? InputLocale { get; set; }

    [JsonPropertyName("layeredDriver")]
    public int? LayeredDriver { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }
}

public class InjectEntry
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("destination")]
    public string Destination { get; set; } = "";
}
