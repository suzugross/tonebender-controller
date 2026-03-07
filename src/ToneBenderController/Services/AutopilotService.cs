using System.IO;
using System.Text.Json;
using ToneBenderController.Models;

namespace ToneBenderController.Services;

public class AutopilotService : IAutopilotService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<AutopilotConfig> LoadAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<AutopilotConfig>(json, JsonOptions)
               ?? throw new InvalidOperationException($"Failed to parse autopilot config: {path}");
    }

    public async Task SaveAsync(string path, AutopilotConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }
}
