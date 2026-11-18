using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

public sealed partial record ScopeOverlays
{
    /// <summary>The heading vector being dragged, or null while none is.</summary>
    public ScopeHeadingVectorOverlay? HeadingVector { get; init; }
}

/// <summary>
/// One frame of the elastic heading vector: the aircraft the vector is drawn for, where it is and how it is flying, and the
/// pointer it runs toward.
/// </summary>
/// <param name="Callsign">The aircraft's callsign.</param>
/// <param name="Aircraft">The aircraft's position, where the turn arc and the vector start.</param>
/// <param name="HeadingTrue">The aircraft's present heading in true degrees, the arc's start.</param>
/// <param name="GroundSpeed">The aircraft's ground speed in knots, which sets the turn radius and the ETA.</param>
/// <param name="Cursor">The pointer's position, which names the heading.</param>
/// <param name="TurnRateDegPerSec">The type's turn rate in degrees per second, which sets the turn arc's radius.</param>
public sealed record ScopeHeadingVectorOverlay(
    string Callsign,
    LatLon Aircraft,
    double HeadingTrue,
    double GroundSpeed,
    LatLon Cursor,
    double TurnRateDegPerSec
);
