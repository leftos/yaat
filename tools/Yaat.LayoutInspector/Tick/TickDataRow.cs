namespace Yaat.LayoutInspector.Tick;

/// <summary>
/// One row from a <c>Yaat.Sim.Tests.Helpers.TickRecorder</c> CSV file. Fields
/// are nullable when the underlying column is optional so both HTML overlays
/// and text-table output can share the same record without forcing defaults.
/// </summary>
public record TickDataRow(
    int Time,
    double Lat,
    double Lon,
    double Hdg,
    double Gs,
    string Phase,
    string Twy,
    int? NavTarget,
    double? NavDist,
    double? NavBrg,
    double? NavTargetSpd,
    double? NavBrakeLimit,
    double? NavArcLimit,
    bool? NavOnArc,
    double? NavNodeReqSpd
);
