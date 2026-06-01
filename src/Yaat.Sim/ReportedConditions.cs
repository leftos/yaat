namespace Yaat.Sim;

/// <summary>
/// A point-in-time snapshot of the modeled surface weather for one station, used to
/// reconstruct (and decide when to re-issue) a reported METAR. Wind direction is degrees
/// TRUE (METAR convention); calm winds set <see cref="Calm"/> and leave direction unused.
/// </summary>
public sealed record ReportedConditions(
    bool Calm,
    int WindDirTrueDeg,
    int WindSpeedKt,
    int? WindGustKt,
    double? VisibilityStatuteMiles,
    IReadOnlyList<MetarParser.CloudLayer> Layers,
    int? CeilingFeetAgl,
    double? AltimeterInHg,
    bool Precipitation
);
