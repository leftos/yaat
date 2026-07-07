namespace Yaat.Sim;

/// <summary>
/// Kind of active-procedure geometry projected onto the radar "Show nav route" overlay, used by the
/// client to style the shape.
/// </summary>
public enum NavRouteShapeKind
{
    /// <summary>An open-ended coded departure leg (CA/VA/CD/VD/CR/VR): a heading/track vector from the
    /// leg's entry point, drawn dashed because its endpoint is a condition (altitude/distance/radial),
    /// not a fixed point.</summary>
    CodedLegVector = 0,

    /// <summary>A holding pattern racetrack (inbound/outbound legs + the two 180° turns).</summary>
    HoldRacetrack = 1,

    /// <summary>A procedure-turn course reversal (outbound, 45° barb, 180° turn, inbound).</summary>
    ProcedureTurn = 2,
}

/// <summary>
/// A drawable shape the server computes from an aircraft's active procedure phase (hold, procedure
/// turn, or the coded fix-less legs of a SID) and projects to the client so the "Show nav route"
/// overlay can draw the full path the aircraft is flying — geometry that never appears in the flat
/// <see cref="NavRouteFixDto"/> route. <see cref="Points"/> is a polyline of <c>[lat, lon]</c> pairs
/// (arc turns already densified). <see cref="Labels"/>, anchored at
/// <see cref="LabelLat"/>/<see cref="LabelLon"/>, carry an optional crossing-restriction or text
/// label (null when the shape needs none).
/// </summary>
public record NavRouteShapeDto(NavRouteShapeKind Kind, List<double[]> Points, List<string>? Labels, double? LabelLat, double? LabelLon);
