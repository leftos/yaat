using Microsoft.Extensions.Logging;
using Yaat.Sim.Data;

namespace Yaat.Sim.LiveTraffic;

/// <summary>
/// Drives shadow aircraft (real traffic from an external feed) in place of <see cref="FlightPhysics"/>.
/// Between samples the aircraft is dead-reckoned along the last sample's track, ground speed and
/// vertical speed; a fresh sample is adopted unconditionally (real data wins). The airborne pose is
/// written as an <b>air vector</b> — heading and IAS such that the computed
/// <see cref="AircraftState.GroundSpeed"/> equals the sampled ground speed under the room's wind —
/// so every ground-speed-derived readout agrees with the motion on the scope.
/// </summary>
public static class LiveTrafficKinematics
{
    private static readonly ILogger Log = SimLog.CreateLogger("LiveTrafficKinematics");

    /// <summary>Dead-reckoned jump above which adopting a sample is logged.</summary>
    public const double JumpLogThresholdNm = 0.3;

    /// <summary>Weight of the newest altitude-delta estimate when smoothing a derived vertical speed.</summary>
    private const double DerivedVerticalSpeedAlpha = 0.5;

    /// <summary>How far an airport may be for its elevation to floor a shadow's dead-reckoned altitude.</summary>
    public const double FloorLookupRangeNm = 5;

    /// <summary>
    /// Longest an ASDE-X surface sample is extrapolated. A taxiing aircraft follows a graph the projection knows
    /// nothing about, so past this the target freezes — a visibly frozen surface target is more honest than a
    /// smoothly moving wrong one (a 59 s backlog sample would otherwise taxi ~2 000 ft in a straight line).
    /// </summary>
    public const double AsdexProjectionCapSeconds = 15;

    /// <summary>Nominal update interval of each source (observation cadence, before SCDS delivery jitter).</summary>
    public static double SweepSeconds(LiveTrafficSource source) =>
        source switch
        {
            LiveTrafficSource.Stars => 4.5,
            LiveTrafficSource.Eram => 12.0,
            LiveTrafficSource.Asdex => 1.0,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown live-traffic source"),
        };

    /// <summary>
    /// Seconds of delivery silence before a track coasts — a generous backstop, not the primary signal. SCDS is
    /// <em>selective</em>: per-track delivery gaps on healthy tracks measure p50 8 / p90 22 / p99 45 s for TAIS and
    /// ASDE-X (suppressed unchanged reports; SFDPS is the steadiest at p99 16 s, measured 2026-08-31), so silence
    /// usually means "nothing changed" — dead reckoning stays valid — while the authentic loss signals are the feed's
    /// own coast flags (<see cref="LiveTrafficSample.SourceCoasting"/> → CST at once) and explicit track drops (the
    /// feed host removes the shadow promptly). A silence CST therefore only says the pipe itself went quiet; erring
    /// long is the safe side because a false CST attaches 7110.65 duties (§5-3-4, §5-4-5.e, §5-13-7) that do not
    /// apply to a healthy track.
    /// </summary>
    public static double CoastAfterSeconds(LiveTrafficSource source) =>
        source switch
        {
            LiveTrafficSource.Stars => 45,
            LiveTrafficSource.Eram => 45,
            LiveTrafficSource.Asdex => 30,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown live-traffic source"),
        };

    /// <summary>
    /// Seconds of delivery silence before the feed host removes a shadow that the store still carries — the backstop
    /// beyond <see cref="CoastAfterSeconds"/> for a feed that died without saying so. Explicit lifecycle beats it:
    /// a track the store dropped (TAIS <c>terminated</c>/<c>drop</c>, SFDPS <c>DROPPED</c>/<c>COMPLETED</c>, reaped)
    /// is removed on the next sync, and one that merely left the room's scope goes at
    /// <c>ShadowTrafficSync.OutOfScopeRemovalSeconds</c>. The server's <c>ShadowTrafficSync</c> delegates here so the
    /// sim's ghost rules and the room's removal share one schedule.
    /// </summary>
    public static double RemovalAfterSeconds(LiveTrafficSource source) =>
        source switch
        {
            LiveTrafficSource.Stars => 90,
            LiveTrafficSource.Eram => 150,
            LiveTrafficSource.Asdex => 60,
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown live-traffic source"),
        };

    /// <summary>
    /// Ground acceleration (kt/s) as the least-squares slope of the feed's tracker-smoothed ground speeds over the
    /// samples inside <paramref name="windowSeconds"/> of the latest one. Differencing positions would be hopeless at
    /// ASDE-X noise (≈ 20 ft, 1 Hz: σ ≈ 7 kt/s over 2 s); the reported speeds are already filtered (σ ≈ 1–2 kt), so a
    /// 4 s window resolves ≈ 0.7 kt/s. Null while coasting, or with fewer than three samples / under two seconds of
    /// span — too little to call.
    /// </summary>
    public static double? GroundAcceleration(AircraftLiveTraffic lt, double windowSeconds)
    {
        if (lt.IsCoasting || (lt.History.Count < 3))
        {
            return null;
        }

        double latest = lt.History[^1].ObservedAtSimSeconds;
        double sumT = 0,
            sumV = 0,
            sumTT = 0,
            sumTV = 0;
        int n = 0;
        double earliest = latest;
        foreach (var h in lt.History)
        {
            if (h.ObservedAtSimSeconds < latest - windowSeconds)
            {
                continue;
            }

            double t = h.ObservedAtSimSeconds - latest;
            earliest = Math.Min(earliest, h.ObservedAtSimSeconds);
            sumT += t;
            sumV += h.GroundSpeedKts;
            sumTT += t * t;
            sumTV += t * h.GroundSpeedKts;
            n++;
        }

        double denominator = (n * sumTT) - (sumT * sumT);
        if ((n < 3) || (latest - earliest < MinAccelerationSpanSeconds) || (Math.Abs(denominator) < 1e-9))
        {
            return null;
        }

        return ((n * sumTV) - (sumT * sumV)) / denominator;
    }

    /// <summary>Shortest sample span (s) a ground-acceleration estimate may rest on.</summary>
    public const double MinAccelerationSpanSeconds = 2.0;

    /// <summary>
    /// Builds a new shadow aircraft from its first sample. The transponder carries the reported code
    /// (never a pool-assigned one); there are no phases and the command queue stays empty.
    /// </summary>
    public static AircraftState CreateShadow(string callsign, string aircraftType, LiveTrafficSample sample, AircraftFlightPlan flightPlan)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            FlightPlan = flightPlan,
            LiveTraffic = new AircraftLiveTraffic { Source = sample.Source, ObservedAtSimSeconds = double.NegativeInfinity },
            SpawnedAtSeconds = sample.ObservedAtSimSeconds,
        };
        if (sample.BeaconCode is { } code)
        {
            ac.Transponder.Code = code;
            ac.Transponder.AssignedCode = code;
        }

        Apply(ac, sample);
        return ac;
    }

    /// <summary>
    /// Adopts a fresh sample. Returns false (state untouched) when the sample is not newer than the
    /// stored one — out of order, or a lower-priority source arriving after a fresher one.
    /// </summary>
    public static bool Apply(AircraftState ac, LiveTrafficSample sample)
    {
        var lt = ac.LiveTraffic ?? throw new InvalidOperationException($"{ac.Callsign} is not a shadow aircraft");
        if (sample.ObservedAtSimSeconds <= lt.ObservedAtSimSeconds)
        {
            Log.LogDebug(
                "Ignoring stale live sample for {Callsign}: observed {Observed}s ≤ stored {Stored}s",
                ac.Callsign,
                sample.ObservedAtSimSeconds,
                lt.ObservedAtSimSeconds
            );
            return false;
        }

        bool hadSample = !double.IsNegativeInfinity(lt.ObservedAtSimSeconds);
        if (hadSample)
        {
            double jumpNm = GeoMath.DistanceNm(ac.Position, sample.Position);
            if (jumpNm > JumpLogThresholdNm)
            {
                Log.LogDebug("Live sample for {Callsign} jumps {Jump:F2} nm from the dead-reckoned position", ac.Callsign, jumpNm);
            }
        }

        lt.SampleVerticalSpeed = sample.VerticalSpeedFpm ?? DeriveVerticalSpeed(lt, sample);
        lt.History.Add(
            new LiveTrafficHistoryPoint(
                sample.ObservedAtSimSeconds,
                sample.Lat,
                sample.Lon,
                sample.AltitudeFt,
                sample.TrueTrackDeg,
                sample.GroundSpeedKts
            )
        );
        if (lt.History.Count > AircraftLiveTraffic.HistoryCapacity)
        {
            lt.History.RemoveAt(0);
        }

        lt.AssignedAltitudeFt = sample.AssignedAltitudeFt ?? lt.AssignedAltitudeFt;
        lt.InterimAltitudeFt = sample.InterimAltitudeFt ?? lt.InterimAltitudeFt;
        lt.ClearedHeadingDeg = sample.ClearedHeadingDeg ?? lt.ClearedHeadingDeg;
        lt.ClearedSpeedKts = sample.ClearedSpeedKts ?? lt.ClearedSpeedKts;
        lt.ClearanceText = sample.ClearanceText ?? lt.ClearanceText;
        if (sample.AirborneHold is { } hold)
        {
            lt.AirborneHold = hold;
            if (!hold)
            {
                lt.HoldFix = null;
            }
        }

        lt.HoldFix = sample.HoldFix ?? lt.HoldFix;
        lt.Source = sample.Source;
        lt.ObservedAtSimSeconds = sample.ObservedAtSimSeconds;
        // Zero-latency baseline; the engine's apply paths overwrite it with the actual second of delivery.
        lt.AppliedAtSimSeconds = sample.ObservedAtSimSeconds;
        lt.SecondsSinceSample = 0;
        lt.SamplePosition = sample.Position;
        lt.SampleAltitude = sample.AltitudeFt;
        lt.SampleGroundSpeed = sample.GroundSpeedKts;
        lt.SampleTrueTrack = sample.TrueTrackDeg;
        lt.FloorAltitudeFt = NavigationDatabase.InstanceOrNull?.FindNearestAirportElevation(sample.Position, FloorLookupRangeNm) ?? 0;
        lt.SourceCoasting = sample.SourceCoasting;
        lt.IsCoasting = sample.SourceCoasting;

        if (sample.BeaconCode is { } code && code != ac.Transponder.Code)
        {
            ac.Transponder.Code = code;
            ac.Transponder.AssignedCode = code;
        }

        ac.IsOnGround = sample.IsOnGround;
        ac.Position = sample.Position;
        ac.Altitude = sample.AltitudeFt;
        ac.VerticalSpeed = lt.SampleVerticalSpeed;
        ac.TrueTrack = new TrueHeading(sample.TrueTrackDeg);
        WritePose(ac, lt);
        return true;
    }

    private static double DeriveVerticalSpeed(AircraftLiveTraffic lt, LiveTrafficSample sample)
    {
        if (lt.History.Count == 0)
        {
            return 0;
        }

        var previous = lt.History[^1];
        double dt = sample.ObservedAtSimSeconds - previous.ObservedAtSimSeconds;
        if (dt <= 0)
        {
            return lt.SampleVerticalSpeed;
        }

        double raw = (sample.AltitudeFt - previous.AltitudeFt) / dt * 60.0;
        return lt.SampleVerticalSpeed == 0 ? raw : (DerivedVerticalSpeedAlpha * raw) + ((1 - DerivedVerticalSpeedAlpha) * lt.SampleVerticalSpeed);
    }

    /// <summary>
    /// Ages the current sample to <paramref name="simNowSeconds"/> and re-derives the pose from it, so a
    /// sample that was already seconds old when it arrived (feed latency) puts the target where it is now
    /// rather than where it was when observed. Every live or replayed sample goes through this after
    /// <see cref="Apply"/>; because it is a function of the sample time and the sim clock only, live and
    /// replay age the same sample identically.
    /// </summary>
    public static void Resync(AircraftState ac, double simNowSeconds, WeatherProfile? weather)
    {
        var lt = ac.LiveTraffic ?? throw new InvalidOperationException($"{ac.Callsign} is not a shadow aircraft");
        lt.SecondsSinceSample = Math.Max(0, simNowSeconds - lt.ObservedAtSimSeconds);
        Advance(ac, 0, weather, simNowSeconds);
    }

    /// <summary>
    /// One physics sub-tick for a shadow: advances the sample clock, re-derives position and altitude
    /// from the latest sample, and fills the per-tick caches <see cref="FlightPhysics.Update"/> would
    /// otherwise own (declination, wind components) so magnetic readouts and the ground-speed getter work.
    /// </summary>
    public static void Advance(AircraftState ac, double deltaSeconds, WeatherProfile? weather, double simTimeSeconds)
    {
        var lt = ac.LiveTraffic ?? throw new InvalidOperationException($"{ac.Callsign} is not a shadow aircraft");
        lt.SecondsSinceSample += deltaSeconds;
        double t = lt.SecondsSinceSample;
        // Coast measures delivery silence (time since the last applied sample), not observation age: the feed
        // carries a constant delivery latency that says nothing about whether the track is still updating.
        lt.DeliverySilenceSeconds = Math.Max(0, simTimeSeconds - lt.AppliedAtSimSeconds);
        lt.IsCoasting = lt.SourceCoasting || (lt.DeliverySilenceSeconds > CoastAfterSeconds(lt.Source));

        double tProj = (lt.Source == LiveTrafficSource.Asdex) ? Math.Min(t, AsdexProjectionCapSeconds) : t;
        var track = new TrueHeading(lt.SampleTrueTrack);
        ac.Position = GeoMath.ProjectPoint(lt.SamplePosition, track, lt.SampleGroundSpeed * tProj / 3600.0);

        // A climb/descent projects only to the feed's interim/assigned altitude (what the real trackers do with
        // their predicted position): at a 50 s en-route horizon an unclamped 2 000 fpm projection overshoots a
        // level-off by more than the 1 000 ft separation standard. The observed altitude side of the clamp keeps
        // a stale clearance from dragging the target off its own Mode C.
        double altitude = lt.SampleAltitude + (lt.SampleVerticalSpeed * tProj / 60.0);
        if ((lt.InterimAltitudeFt ?? lt.AssignedAltitudeFt) is { } clearedTo)
        {
            altitude = lt.SampleVerticalSpeed switch
            {
                > 0 => Math.Min(altitude, Math.Max(clearedTo, lt.SampleAltitude)),
                < 0 => Math.Max(altitude, Math.Min(clearedTo, lt.SampleAltitude)),
                _ => altitude,
            };
        }

        ac.Altitude = Math.Max(lt.FloorAltitudeFt, altitude);
        ac.VerticalSpeed = lt.SampleVerticalSpeed;
        ac.TrueTrack = track;

        // A shadow's declination is a display readout of live traffic, not replayed simulation state, so the process day suffices.
        FlightPhysics.RefreshDeclinationCache(ac, MagneticDeclination.EvaluationDateUtc);
        ac.WindComponents = WindInterpolator.GetWindComponents(weather, ac.Altitude, simTimeSeconds, WindVariation.PhaseSecondsFor(ac.Callsign));

        WritePose(ac, lt);
    }

    /// <summary>
    /// Heading and IAS for the current sample against the cached <see cref="AircraftState.WindComponents"/>
    /// (zero until the first <see cref="Advance"/> has cached the room wind), so a freshly created or freshly
    /// sampled shadow already reports the sampled ground speed rather than a zero pose.
    /// </summary>
    private static void WritePose(AircraftState ac, AircraftLiveTraffic lt)
    {
        if (ac.IsOnGround)
        {
            WriteSurfacePose(ac, lt);
        }
        else
        {
            WriteAirVector(ac, lt);
        }
    }

    /// <summary>On the surface motion is wheel-driven: track = heading and IAS carries the ground speed.</summary>
    private static void WriteSurfacePose(AircraftState ac, AircraftLiveTraffic lt)
    {
        ac.TrueHeading = new TrueHeading(lt.SampleTrueTrack);
        ac.IndicatedAirspeed = lt.SampleGroundSpeed;
    }

    /// <summary>
    /// Heading and IAS such that TAS·heading + wind reproduces the sampled ground vector, so the
    /// computed <see cref="AircraftState.GroundSpeed"/> equals the sample's ground speed.
    /// </summary>
    private static void WriteAirVector(AircraftState ac, AircraftLiveTraffic lt)
    {
        double trackRad = lt.SampleTrueTrack * Math.PI / 180.0;
        double airN = (lt.SampleGroundSpeed * Math.Cos(trackRad)) - ac.WindComponents.N;
        double airE = (lt.SampleGroundSpeed * Math.Sin(trackRad)) - ac.WindComponents.E;
        double tas = Math.Sqrt((airN * airN) + (airE * airE));
        double headingDeg = tas > 0.1 ? Math.Atan2(airE, airN) * 180.0 / Math.PI : lt.SampleTrueTrack;
        if (headingDeg < 0)
        {
            headingDeg += 360.0;
        }

        ac.TrueHeading = new TrueHeading(headingDeg);
        ac.IndicatedAirspeed = WindInterpolator.TasToIas(tas, ac.Altitude);
    }
}
