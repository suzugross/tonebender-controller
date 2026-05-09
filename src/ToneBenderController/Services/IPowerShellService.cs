using ToneBenderController.Models;

namespace ToneBenderController.Services;

/// <summary>
/// Executes PowerShell scripts and parses structured output.
/// </summary>
public interface IPowerShellService
{
    /// <summary>Root directory containing tonebender.ps1 and Profiles/.</summary>
    string ScriptDir { get; }

    /// <summary>Run the WinPE build script with the specified profile.</summary>
    Task RunBuildAsync(string profilePath, string? driverPath = null,
        IProgress<BuildProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Regenerate a WinPE ISO from an existing workspace (Driver-only mode).</summary>
    Task RunRegenerateIsoAsync(string workspaceDir, string isoPath,
        IProgress<BuildProgress>? progress = null, CancellationToken ct = default);
}
