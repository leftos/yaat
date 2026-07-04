namespace Yaat.Sim;

/// <summary>
/// ERAM en-route Short-Term Conflict Alert (STCA) detection (eram.md §377-383). Distinct from the terminal
/// STARS <see cref="ConflictAlertDetector"/>: predicts each target four minutes ahead by straight-line
/// extrapolation of ground track/speed, alerts on the swept closest approach (5 nm, or 3 nm when both
/// targets are at/below FL230), and models the vertical dimension as a data-block-altitude envelope rather
/// than a point. No approach-corridor suppression. The §377 facility gate is applied downstream per
/// subscriber, not here — this pass produces the facility-agnostic room-wide set.
///
/// Correlated-only: like the terminal detector, both targets must be associated (Mode C, supported). The
/// uncorrelated Mode-C-intruder path (§377 only requires one owned target, so a tracked-vs-intruder pair is
/// a real ERAM alert) is a known false-negative gap deferred with the CDB / uncorrelated-target work.
/// </summary>
public static class EramConflictDetector
{
    // Four-minute trajectory probe (eram.md §379).
    private const double LookAheadSeconds = 240.0;

    // Lateral minimum: 5 nm en route, 3 nm when both targets are in reduced-separation airspace (≤ FL230).
    private const double LateralNm = 5.0;
    private const double ReducedLateralNm = 3.0;
    private const double ReducedSeparationCeilingFt = 23000.0;

    private const double VerticalFt = 1000.0;

    // Hysteresis: an active alert holds until the swept approach or the vertical gap clears with margin.
    private const double ClearLateralMarginNm = 0.3;
    private const double ClearVerticalFt = 1100.0;

    public record ConflictPair(string CallsignA, string CallsignB, string Id);

    /// <summary>
    /// Detect ERAM STCA pairs. <paramref name="existingConflictIds"/> holds the ids currently latched (the
    /// keys of the room's ERAM conflict set) so a pair near the threshold stays alerted until it clears with
    /// margin, matching the terminal detector's hysteresis.
    /// </summary>
    public static List<ConflictPair> Detect(List<AircraftState> aircraft, IReadOnlySet<string> existingConflictIds)
    {
        var results = new List<ConflictPair>();
        var eligible = new List<AircraftState>(aircraft.Count);
        foreach (var ac in aircraft)
        {
            if (ConflictAlertDetector.IsEligible(ac))
            {
                eligible.Add(ac);
            }
        }

        for (int i = 0; i < eligible.Count; i++)
        {
            for (int j = i + 1; j < eligible.Count; j++)
            {
                var a = eligible[i];
                var b = eligible[j];
                string id = MakeConflictId(a.Callsign, b.Callsign);
                if (IsInConflict(a, b, existingConflictIds.Contains(id)))
                {
                    results.Add(new ConflictPair(a.Callsign, b.Callsign, id));
                }
            }
        }

        return results;
    }

    private static bool IsInConflict(AircraftState a, AircraftState b, bool alreadyInConflict)
    {
        double lateralNm = LateralClosestApproachNm(a, b);
        double verticalGapFt = VerticalGapFt(a, b);

        double lateralThreshold = BothInReducedSeparationAirspace(a, b) ? ReducedLateralNm : LateralNm;

        if (alreadyInConflict)
        {
            return (lateralNm < (lateralThreshold + ClearLateralMarginNm)) && (verticalGapFt < ClearVerticalFt);
        }

        return (lateralNm < lateralThreshold) && (verticalGapFt < VerticalFt);
    }

    // Reduced-separation eligibility uses present altitude (§379 is present-tense: "are both within").
    // A pair climbing through FL230 during the window keeps the threshold it has now.
    private static bool BothInReducedSeparationAirspace(AircraftState a, AircraftState b) =>
        (a.Altitude <= ReducedSeparationCeilingFt) && (b.Altitude <= ReducedSeparationCeilingFt);

    /// <summary>
    /// Minimum lateral separation over the four-minute window, computed analytically in a local tangent
    /// plane centered on <paramref name="a"/>. Exact for constant-velocity motion. A diverging pair yields
    /// its current separation (the closest approach is clamped to t = 0).
    /// </summary>
    private static double LateralClosestApproachNm(AircraftState a, AircraftState b)
    {
        double cosLat = Math.Cos(a.Position.Lat * (Math.PI / 180.0));
        double rx = (b.Position.Lon - a.Position.Lon) * cosLat * 60.0;
        double ry = (b.Position.Lat - a.Position.Lat) * 60.0;

        var (avx, avy) = VelocityNmPerSecond(a);
        var (bvx, bvy) = VelocityNmPerSecond(b);
        double vx = bvx - avx;
        double vy = bvy - avy;

        double vv = (vx * vx) + (vy * vy);
        double tStar = vv < 1e-12 ? 0.0 : -((rx * vx) + (ry * vy)) / vv;
        tStar = Math.Clamp(tStar, 0.0, LookAheadSeconds);

        double dx = rx + (vx * tStar);
        double dy = ry + (vy * tStar);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static (double Vx, double Vy) VelocityNmPerSecond(AircraftState ac)
    {
        double speed = ac.GroundSpeed / 3600.0;
        double trackRad = ac.TrueTrack.Degrees * (Math.PI / 180.0);
        return (speed * Math.Sin(trackRad), speed * Math.Cos(trackRad));
    }

    /// <summary>
    /// Gap between the two targets' vertical envelopes (0 when they overlap). Each envelope is the interval
    /// between the target's current altitude and its data-block altitude — modeling both the §381 assumption
    /// that a climbing/descending target levels off at its assignment and the §383 assumption that a level
    /// target may start moving toward an assignment that differs from its current altitude.
    /// </summary>
    private static double VerticalGapFt(AircraftState a, AircraftState b)
    {
        var (loA, hiA) = AltitudeEnvelopeFt(a);
        var (loB, hiB) = AltitudeEnvelopeFt(b);
        return Math.Max(0.0, Math.Max(loA - hiB, loB - hiA));
    }

    private static (double Lo, double Hi) AltitudeEnvelopeFt(AircraftState ac)
    {
        double current = ac.Altitude;
        int? dataBlock = DataBlockAltitudeFt(ac);
        // With a data-block altitude the envelope is time-independent (§383: a level target may move toward
        // its assignment "at any time"), so pairing it with the swept lateral CPA is spec-faithful. Without
        // one, the vertical path is actually time-determined; the vertical-speed fallback below is therefore
        // deliberately conservative (it may over-alert on a descent whose co-altitude moment is not the
        // lateral CPA). Acceptable because a correlated IFR target almost always has a filed cruise altitude.
        double other = dataBlock ?? (current + (ac.VerticalSpeed * LookAheadSeconds / 60.0));
        return (Math.Min(current, other), Math.Max(current, other));
    }

    /// <summary>
    /// The ERAM data-block altitude (in feet) used for conflict prediction: the controller-entered
    /// interim/procedure altitude if present, else the filed hard (cruise) altitude. The three ERAM
    /// interim fields are stored in <b>hundreds of feet</b> (CRC's canonical unit) and converted to feet
    /// here; the precedence — local interim &gt; procedure &gt; interim — mirrors CRC's own FDB field-B
    /// resolver (<c>TrackRepository</c>: <c>LocalInterimAltitude ?? ProcedureAltitude ?? InterimAltitude</c>).
    /// Null when no assignment is known (the caller falls back to the vertical-speed projection).
    /// </summary>
    private static int? DataBlockAltitudeFt(AircraftState ac)
    {
        int? interimHundreds = ac.Eram.LocalInterimAltitude ?? ac.Eram.ProcedureAltitude ?? ac.Eram.InterimAltitude;
        if (interimHundreds is int hundreds && hundreds > 0)
        {
            return hundreds * 100;
        }

        return ac.FlightPlan.CruiseAltitude > 0 ? ac.FlightPlan.CruiseAltitude : null;
    }

    internal static string MakeConflictId(string callsignA, string callsignB)
    {
        int cmp = string.Compare(callsignA, callsignB, StringComparison.Ordinal);
        string first = cmp <= 0 ? callsignA : callsignB;
        string second = cmp <= 0 ? callsignB : callsignA;
        return $"ESTCA_{first}_{second}";
    }
}
