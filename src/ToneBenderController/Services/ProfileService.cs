using System.IO;
using System.Text.Json;
using ToneBenderController.Models;

namespace ToneBenderController.Services;

public class ProfileService : IProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<BuildProfile> LoadAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<BuildProfile>(json, JsonOptions)
               ?? throw new InvalidOperationException($"Failed to parse profile: {path}");
    }

    public async Task SaveAsync(string path, BuildProfile profile)
    {
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    public List<string> GetAvailableProfiles(string profilesDir)
    {
        if (!Directory.Exists(profilesDir))
            return [];

        return Directory.GetFiles(profilesDir, "*.json")
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(n => n is not null)
                        .Cast<string>()
                        .ToList();
    }
}
