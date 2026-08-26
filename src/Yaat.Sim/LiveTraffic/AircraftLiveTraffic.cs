using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.LiveTraffic;

/// <summary>
/// Per-aircraft live-traffic satellite: the last external sample plus the dead-reckoning clock.
/// Present (non-null on <see cref="AircraftState.LiveTraffic"/>) exactly while the aircraft is a
/// shadow driven by <see cref="LiveTrafficKinematics"/>; assuming the aircraft sets it to null.
/// Positions are always re-derived from the sample fields plus <see cref="SecondsSinceSample"/>,
/// never integrated tick-to-tick, so replaying the same samples reproduces the same motion.
/// </summary>
public sealed class AircraftLiveTraffic
{
    public LiveTrafficSource Source { get; set; }

    /// <summary>Sim-clock second the current sample was placed at (out-of-order samples are ignored).</summary>
    public double ObservedAtSimSeconds { get; set; }

    /// <summary>Accumulated from tick dt — the only clock <see cref="LiveTrafficKinematics.Advance"/> reads.</summary>
    public double SecondsSinceSample { get; set; }

    public LatLon SamplePosition { get; set; }
    public double SampleAltitude { get; set; }
    public double SampleGroundSpeed { get; set; }
    public double SampleTrueTrack { get; set; }
    public double SampleVerticalSpeed { get; set; }

    /// <summary>Altitude and time of the sample before the current one; feeds vertical-speed derivation.</summary>
    public double? PreviousSampleAltitude { get; set; }
    public double? PreviousObservedAtSimSeconds { get; set; }

    /// <summary>Two sweeps of the source have passed without a sample: displayed as CST, still dead-reckoned.</summary>
    public bool IsCoasting { get; set; }

    /// <summary>Feed-side identity (e.g. GUFI); opaque to the sim.</summary>
    public string? ExternalId { get; set; }

    public AircraftLiveTrafficDto ToSnapshot() =>
        new()
        {
            Source = (int)Source,
            ObservedAtSimSeconds = ObservedAtSimSeconds,
            SecondsSinceSample = SecondsSinceSample,
            SampleLat = SamplePosition.Lat,
            SampleLon = SamplePosition.Lon,
            SampleAltitude = SampleAltitude,
            SampleGroundSpeed = SampleGroundSpeed,
            SampleTrueTrack = SampleTrueTrack,
            SampleVerticalSpeed = SampleVerticalSpeed,
            PreviousSampleAltitude = PreviousSampleAltitude,
            PreviousObservedAtSimSeconds = PreviousObservedAtSimSeconds,
            IsCoasting = IsCoasting,
            ExternalId = ExternalId,
        };

    public static AircraftLiveTraffic FromSnapshot(AircraftLiveTrafficDto dto) =>
        new()
        {
            Source = (LiveTrafficSource)dto.Source,
            ObservedAtSimSeconds = dto.ObservedAtSimSeconds,
            SecondsSinceSample = dto.SecondsSinceSample,
            SamplePosition = new LatLon(dto.SampleLat, dto.SampleLon),
            SampleAltitude = dto.SampleAltitude,
            SampleGroundSpeed = dto.SampleGroundSpeed,
            SampleTrueTrack = dto.SampleTrueTrack,
            SampleVerticalSpeed = dto.SampleVerticalSpeed,
            PreviousSampleAltitude = dto.PreviousSampleAltitude,
            PreviousObservedAtSimSeconds = dto.PreviousObservedAtSimSeconds,
            IsCoasting = dto.IsCoasting,
            ExternalId = dto.ExternalId,
        };
}
