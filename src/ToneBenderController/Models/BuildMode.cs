namespace ToneBenderController.Models;

/// <summary>
/// Selects which sub-set of the WinPE pipeline to execute.
/// </summary>
public enum BuildMode
{
    /// <summary>USB partition + WinPE build + USB deploy (the unified original flow).</summary>
    Full,

    /// <summary>Partition the selected USB drive into WINPE+DATA only.</summary>
    PartitionOnly,

    /// <summary>Build a WinPE workspace into a user-specified directory only.</summary>
    BuildOnly,

    /// <summary>Apply OEM drivers to an existing workspace's boot.wim only.</summary>
    DriverOnly
}

public record BuildModeOption(string Display, BuildMode Mode);

/// <summary>
/// Optional overrides applied on top of a profile JSON for Build-only mode.
/// Null fields mean "use the profile value as-is".
/// </summary>
public record BuildOverrides
{
    public string? WorkDir { get; init; }
    public string? IsoPath { get; init; }
    public bool? GenerateIso { get; init; }
}
