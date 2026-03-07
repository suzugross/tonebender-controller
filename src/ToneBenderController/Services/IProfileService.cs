using ToneBenderController.Models;

namespace ToneBenderController.Services;

/// <summary>
/// Reads and writes WinPE build profiles (Profiles/*.json).
/// </summary>
public interface IProfileService
{
    /// <summary>Load a build profile from a JSON file.</summary>
    Task<BuildProfile> LoadAsync(string path);

    /// <summary>Save a build profile to a JSON file.</summary>
    Task SaveAsync(string path, BuildProfile profile);

    /// <summary>List available profile files in the Profiles directory.</summary>
    List<string> GetAvailableProfiles(string profilesDir);
}
