namespace Yaat.LayoutInspector.Tick;

/// <summary>
/// One per-tick aircraft state row, derived from a <see cref="TickEvent"/>
/// in a TickRecorder JSON file. Used by both the HTML overlay and the
/// text tick-table formatter.
/// </summary>
public record TickDataRow(
    int Time,
    string Callsign,
    double Lat,
    double Lon,
    double Hdg,
    double Gs,
    string Phase,
    string Twy,
    int? NavTarget,
    double? NavDist,
    double? NavBrg,
    double? NavAngleDiff,
    double? NavTargetSpd,
    double? NavBrakeLimit,
    double? NavArcLimit,
    bool? NavOnArc,
    double? NavNodeReqSpd
)
{
    public static TickDataRow From(TickEvent ev) =>
        new(
            Time: ev.T,
            Callsign: ev.Callsign,
            Lat: ev.Lat,
            Lon: ev.Lon,
            Hdg: ev.Hdg,
            Gs: ev.Gs,
            Phase: ev.Phase,
            Twy: ev.Twy ?? "",
            NavTarget: ev.Nav?.TargetNodeId,
            NavDist: ev.Nav?.DistNm,
            NavBrg: ev.Nav?.BrgDeg,
            NavAngleDiff: ev.Nav?.AngleDiffDeg,
            NavTargetSpd: ev.Nav?.TargetSpdKts,
            NavBrakeLimit: ev.Nav?.BrakeLimitKts,
            NavArcLimit: ev.Nav?.ArcLimitKts,
            NavOnArc: ev.Nav?.OnArc,
            NavNodeReqSpd: ev.Nav?.NodeReqSpdKts
        );
}
