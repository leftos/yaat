namespace Yaat.Sim;

/// <summary>
/// A point-in-time snapshot of the modeled surface weather for one station, used to
/// reconstruct (and decide when to re-issue) a reported METAR. Wind values are the
/// observed 2-minute means in degrees TRUE (METAR convention), never the instantaneous
/// field. Calm winds set <see cref="Calm"/> and leave direction unused. A VRB wind sets
/// <see cref="WindVariable"/> (direction unused, no variability group); a ≥60° spread at
/// more than 6 kt carries the dddVddd extremes in <see cref="WindVarFromTrueDeg"/> /
/// <see cref="WindVarToTrueDeg"/>, coded clockwise. <see cref="PeakWindKt"/> and friends
/// carry the trailing 10-minute peak for the PK WND remark when it exceeded 25 kt.
/// </summary>
public sealed record ReportedConditions(
    bool Calm,
    int WindDirTrueDeg,
    int WindSpeedKt,
    int? WindGustKt,
    bool WindVariable,
    int? WindVarFromTrueDeg,
    int? WindVarToTrueDeg,
    int? PeakWindKt,
    int PeakWindDirTrueDeg,
    DateTime? PeakWindUtc,
    double? VisibilityStatuteMiles,
    IReadOnlyList<MetarParser.CloudLayer> Layers,
    int? CeilingFeetAgl,
    double? AltimeterInHg,
    bool Precipitation
);
