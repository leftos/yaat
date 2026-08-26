namespace Yaat.Sim.LiveTraffic;

/// <summary>Which surveillance source produced a live-traffic sample. Drives the coast timeout and the ground/airborne split.</summary>
public enum LiveTrafficSource
{
    /// <summary>Terminal radar (STARS / TAIS), ~4.5 s sweep.</summary>
    Stars,

    /// <summary>En-route radar (ERAM / FDPS), ~12 s sweep.</summary>
    Eram,

    /// <summary>Surface surveillance (ASDE-X), ~1 s update; the aircraft is on the ground.</summary>
    Asdex,
}

/// <summary>Why a shadow aircraft left the room.</summary>
public enum LiveTrafficRemovalReason
{
    /// <summary>No sample for longer than the source's removal timeout.</summary>
    Stale,

    /// <summary>The feed dropped the track (STDDS delete, track termination).</summary>
    Dropped,

    /// <summary>The track left the room's geographic or altitude scope.</summary>
    OutOfScope,

    /// <summary>An instructor deleted it.</summary>
    Deleted,
}

/// <summary>
/// One observation of a real aircraft as the simulation sees it. Feed-agnostic: the sim never
/// knows where a sample came from beyond <see cref="Source"/>. Time is the room's sim clock,
/// never wall-clock, so recordings replay by sim time. Serialized inside
/// <see cref="Simulation.RecordedLiveTrafficSample"/>.
/// </summary>
/// <param name="ObservedAtSimSeconds">Sim-clock second the sample was placed at.</param>
/// <param name="Lat">Latitude, degrees.</param>
/// <param name="Lon">Longitude, degrees.</param>
/// <param name="AltitudeFt">Altitude, feet MSL (Mode C, 100-ft quantised for radar sources).</param>
/// <param name="GroundSpeedKts">Ground speed, knots.</param>
/// <param name="TrueTrackDeg">True track, degrees.</param>
/// <param name="VerticalSpeedFpm">Vertical speed when the feed reports one; null → derived from consecutive samples.</param>
/// <param name="Source">Producing surveillance source.</param>
/// <param name="BeaconCode">Reported transponder code, when known.</param>
public sealed record LiveTrafficSample(
    double ObservedAtSimSeconds,
    double Lat,
    double Lon,
    double AltitudeFt,
    double GroundSpeedKts,
    double TrueTrackDeg,
    double? VerticalSpeedFpm,
    LiveTrafficSource Source,
    uint? BeaconCode
)
{
    public LatLon Position => new(Lat, Lon);

    /// <summary>Surface-surveillance samples are on the ground; radar samples are airborne.</summary>
    public bool IsOnGround => Source == LiveTrafficSource.Asdex;
}
