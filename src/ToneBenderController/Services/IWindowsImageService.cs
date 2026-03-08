using ToneBenderController.Models;

namespace ToneBenderController.Services;

/// <summary>
/// ISO mounting and WIM edition extraction operations.
/// </summary>
public interface IWindowsImageService
{
    /// <summary>Mount a Windows ISO and return its drive letter.</summary>
    Task<char> MountIsoAsync(string isoPath);

    /// <summary>Unmount a previously mounted ISO (best-effort, no exceptions).</summary>
    Task UnmountIsoAsync(string isoPath);

    /// <summary>Enumerate editions in a WIM or ESD file.</summary>
    Task<List<WimEdition>> GetWimEditionsAsync(string wimFilePath);

    /// <summary>Export a single edition to a new WIM file.</summary>
    Task ExportEditionAsync(string sourceWimPath, int sourceIndex,
        string destinationWimPath, IProgress<int>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Mount a WIM, inject OEM drivers via DISM /Add-Driver /Recurse, then unmount with commit.
    /// </summary>
    Task InjectDriversIntoWimAsync(string wimPath, string driverPath,
        IProgress<string>? progress = null, CancellationToken ct = default);
}
