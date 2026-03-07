using ToneBenderController.Models;

namespace ToneBenderController.Services;

/// <summary>
/// Reads and writes ToneBender autopilot.json configuration.
/// </summary>
public interface IAutopilotService
{
    /// <summary>Load autopilot config from a JSON file.</summary>
    Task<AutopilotConfig> LoadAsync(string path);

    /// <summary>Save autopilot config to a JSON file.</summary>
    Task SaveAsync(string path, AutopilotConfig config);
}
