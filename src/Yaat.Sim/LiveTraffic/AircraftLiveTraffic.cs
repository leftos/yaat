using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.LiveTraffic;

/// <summary>One past observation kept for vertical-speed derivation, level/hold detection at assume.</summary>
public readonly record struct LiveTrafficHistoryPoint(
    double ObservedAtSimSeconds,
    double Lat,
    double Lon,
    double AltitudeFt,
    double TrueTrackDeg,
    double GroundSpeedKts
);

/// <summary>
/// Per-aircraft live-traffic satellite: the last external sample plus the dead-reckoning clock.
/// Present (non-null on <see cref="AircraftState.LiveTraffic"/>) exactly while the aircraft is a
/// shadow driven by <see cref="LiveTrafficKinematics"/>; assuming the aircraft sets it to null.
/// Positions are always re-derived from the sample fields plus <see cref="SecondsSinceSample"/>,
/// never integrated tick-to-tick, so replaying the same samples reproduces the same motion.
/// </summary>
public sealed class AircraftLiveTraffic
{
    /// <summary>Observations kept in <see cref="History"/> (≈ 90 s of STARS sweeps: enough to see a racetrack turn).</summary>
    public const int HistoryCapacity = 24;

    public LiveTrafficSource Source { get; set; }

    /// <summary>Sim-clock second the current sample was placed at (out-of-order samples are ignored).</summary>
    public double ObservedAtSimSeconds { get; set; }

    /// <summary>
    /// Sim-clock second the current sample was *applied* — when the feed delivered it, as opposed to when the source
    /// observed it. The feed carries a delivery latency (SCDS ≈ 10 s terminal, ≈ 50 s en-route), so freshness — coast,
    /// removal — is measured from this clock, while dead reckoning still projects from <see cref="ObservedAtSimSeconds"/>.
    /// <see cref="LiveTrafficKinematics.Apply"/> seeds it with the observation second; the engine's apply paths stamp
    /// the actual second so live and replay age identically.
    /// </summary>
    public double AppliedAtSimSeconds { get; set; }

    /// <summary>Accumulated from tick dt — the dead-reckoning clock <see cref="LiveTrafficKinematics.Advance"/> projects by.</summary>
    public double SecondsSinceSample { get; set; }

    /// <summary>
    /// Seconds since the feed last delivered a new sample, recomputed every <see cref="LiveTrafficKinematics.Advance"/>
    /// (per-tick derived value, not serialized). This is the freshness clock — coast and the ground-conflict ghost rule
    /// read it — as opposed to <see cref="SecondsSinceSample"/>, which ages from the observation and carries the feed's
    /// delivery latency.
    /// </summary>
    public double DeliverySilenceSeconds { get; set; }

    public LatLon SamplePosition { get; set; }
    public double SampleAltitude { get; set; }
    public double SampleGroundSpeed { get; set; }
    public double SampleTrueTrack { get; set; }
    public double SampleVerticalSpeed { get; set; }

    /// <summary>
    /// Field elevation of the nearest airport when the sample was applied (0 with none within range): the floor a
    /// dead-reckoned descent is clamped to, so a coasting arrival never sinks below the field at a high-elevation airport.
    /// </summary>
    public double FloorAltitudeFt { get; set; }

    /// <summary>Recent samples, oldest first, ending with the current one; capped at <see cref="HistoryCapacity"/>.</summary>
    public List<LiveTrafficHistoryPoint> History { get; } = [];

    /// <summary>
    /// Two sweeps of the source have passed without a sample, or the source flagged the sample itself as a coast:
    /// displayed as CST, still dead-reckoned, excluded from conflict alerting.
    /// </summary>
    public bool IsCoasting { get; set; }

    /// <summary>The current sample carried the source's own coast flag (<see cref="LiveTrafficSample.SourceCoasting"/>).</summary>
    public bool SourceCoasting { get; set; }

    /// <summary>Latest clearance fields the feed carried (null when it never did); seed the assume hand-off.</summary>
    public double? AssignedAltitudeFt { get; set; }
    public double? InterimAltitudeFt { get; set; }
    public double? ClearedHeadingDeg { get; set; }
    public double? ClearedSpeedKts { get; set; }
    public string? ClearanceText { get; set; }

    /// <summary>The feed's airborne-hold flag as last stated (null until it says either way) and the fix while holding.</summary>
    public bool? AirborneHold { get; set; }
    public string? HoldFix { get; set; }

    /// <summary>Feed-side identity (e.g. GUFI); opaque to the sim.</summary>
    public string? ExternalId { get; set; }

    /// <summary>Runway use on the room's primary airport at the last observation tick (the landing→surface edge stamps <c>Landed</c>).</summary>
    public RunwayUseKind? LastRunwayUse { get; set; }

    /// <summary>Set by the observer at the landing→surface edge, cleared once airborne again: the rollout is not a takeoff roll.</summary>
    public bool LandedOnRunway { get; set; }

    /// <summary>Set by the observer at the roll→airborne edge, cleared past the departure window: the §3-9-6 / §3-10-3.a.2 clock keeps running after liftoff.</summary>
    public bool DepartedOnRunway { get; set; }

    /// <summary>Airport and designator of the runway the latches refer to (the observer's last non-null runway use).</summary>
    public string? LatchedRunwayAirport { get; set; }
    public string? LatchedRunwayDesignator { get; set; }

    public AircraftLiveTrafficDto ToSnapshot() =>
        new()
        {
            Source = (int)Source,
            ObservedAtSimSeconds = ObservedAtSimSeconds,
            AppliedAtSimSeconds = AppliedAtSimSeconds,
            SecondsSinceSample = SecondsSinceSample,
            SampleLat = SamplePosition.Lat,
            SampleLon = SamplePosition.Lon,
            SampleAltitude = SampleAltitude,
            SampleGroundSpeed = SampleGroundSpeed,
            SampleTrueTrack = SampleTrueTrack,
            SampleVerticalSpeed = SampleVerticalSpeed,
            FloorAltitudeFt = FloorAltitudeFt,
            History = History
                .Select(h => new LiveTrafficHistoryPointDto
                {
                    ObservedAtSimSeconds = h.ObservedAtSimSeconds,
                    Lat = h.Lat,
                    Lon = h.Lon,
                    AltitudeFt = h.AltitudeFt,
                    GroundSpeedKts = h.GroundSpeedKts,
                    TrueTrackDeg = h.TrueTrackDeg,
                })
                .ToList(),
            IsCoasting = IsCoasting,
            SourceCoasting = SourceCoasting,
            AssignedAltitudeFt = AssignedAltitudeFt,
            InterimAltitudeFt = InterimAltitudeFt,
            ClearedHeadingDeg = ClearedHeadingDeg,
            ClearedSpeedKts = ClearedSpeedKts,
            ClearanceText = ClearanceText,
            AirborneHold = AirborneHold,
            HoldFix = HoldFix,
            ExternalId = ExternalId,
            LastRunwayUse = LastRunwayUse is { } use ? (int)use : null,
            LandedOnRunway = LandedOnRunway,
            DepartedOnRunway = DepartedOnRunway,
            LatchedRunwayAirport = LatchedRunwayAirport,
            LatchedRunwayDesignator = LatchedRunwayDesignator,
        };

    public static AircraftLiveTraffic FromSnapshot(AircraftLiveTrafficDto dto)
    {
        var lt = new AircraftLiveTraffic
        {
            Source = (LiveTrafficSource)dto.Source,
            ObservedAtSimSeconds = dto.ObservedAtSimSeconds,
            AppliedAtSimSeconds = dto.AppliedAtSimSeconds ?? dto.ObservedAtSimSeconds,
            SecondsSinceSample = dto.SecondsSinceSample,
            SamplePosition = new LatLon(dto.SampleLat, dto.SampleLon),
            SampleAltitude = dto.SampleAltitude,
            SampleGroundSpeed = dto.SampleGroundSpeed,
            SampleTrueTrack = dto.SampleTrueTrack,
            SampleVerticalSpeed = dto.SampleVerticalSpeed,
            FloorAltitudeFt = dto.FloorAltitudeFt,
            IsCoasting = dto.IsCoasting,
            SourceCoasting = dto.SourceCoasting,
            AssignedAltitudeFt = dto.AssignedAltitudeFt,
            InterimAltitudeFt = dto.InterimAltitudeFt,
            ClearedHeadingDeg = dto.ClearedHeadingDeg,
            ClearedSpeedKts = dto.ClearedSpeedKts,
            ClearanceText = dto.ClearanceText,
            AirborneHold = dto.AirborneHold,
            HoldFix = dto.HoldFix,
            ExternalId = dto.ExternalId,
            LastRunwayUse = dto.LastRunwayUse is { } use ? (RunwayUseKind)use : null,
            LandedOnRunway = dto.LandedOnRunway,
            DepartedOnRunway = dto.DepartedOnRunway,
            LatchedRunwayAirport = dto.LatchedRunwayAirport,
            LatchedRunwayDesignator = dto.LatchedRunwayDesignator,
        };
        foreach (var h in dto.History ?? [])
        {
            lt.History.Add(new LiveTrafficHistoryPoint(h.ObservedAtSimSeconds, h.Lat, h.Lon, h.AltitudeFt, h.TrueTrackDeg, h.GroundSpeedKts));
        }

        return lt;
    }
}
