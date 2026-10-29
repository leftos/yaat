namespace Yaat.Sim.Simulation;

/// <summary>
/// Why a wind-grid entry read (<see cref="RecordingArchive.ReadWindGridEntry"/>) gave no grid, or <see cref="None"/> when it
/// gave one.
/// </summary>
public enum WindGridEntryProblem
{
    None,

    /// <summary>The archive has no entry under the grid's name.</summary>
    Missing,

    /// <summary>The entry decompresses past <see cref="Weather.WindGridRegistry.MaxGridBytes"/>.</summary>
    TooLarge,

    /// <summary>The entry's bytes do not hash to the name it is stored under.</summary>
    WrongHash,
}

/// <summary>
/// One wind-grid entry read from a recording archive or a room checkpoint: its GRIB2 bytes when they are the grid's, else
/// why not (<see cref="Problem"/>), with the entry's name and, for <see cref="WindGridEntryProblem.WrongHash"/>, the hash its
/// bytes have.
/// </summary>
public readonly record struct WindGridEntryRead(byte[]? Grib2, WindGridEntryProblem Problem, string EntryName, string? ActualHash);
