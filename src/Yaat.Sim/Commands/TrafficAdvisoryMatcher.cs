using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Commands;

public enum SafetyAlertTurn
{
    Left,
    Right,
}

public enum SafetyAlertVertical
{
    Climb,
    Descend,
}

/// <summary>How closely an accepted structured traffic advisory matched the actual traffic it resolved.</summary>
public enum AdvisoryMatchQuality
{
    Exact,
    Imprecise,
}

public sealed record TrafficAdvisoryDetails(int Clock, int Miles, string Direction, string AircraftType, int? Altitude);

/// <summary>
/// VFR relative-position traffic advisory ("traffic off your nose and to the right, 2 miles, a Cessna").
/// <paramref name="Position"/> is one of the eight octant keywords (NOSE/NL/NR/L/R/LR/RR/TAIL).
/// </summary>
public sealed record TrafficRelativeDetails(string Position, int Miles, string AircraftType);

/// <summary>
/// VFR pattern-leg traffic advisory ("traffic on a 2-mile right base for runway 28R, a Mooney").
/// <paramref name="Side"/> is null for the final leg (a straight-in has no left/right base).
/// </summary>
public sealed record TrafficPatternDetails(PatternEntryLeg Leg, PatternDirection? Side, int Miles, string RunwayId, string AircraftType);

/// <summary>
/// VFR landmark traffic advisory ("traffic over the Oakland Coliseum, a Skyhawk"). <paramref name="FixName"/>
/// is a navdb fix identifier or alias (e.g. VFR reporting point VPCOL) resolved to a position at dispatch.
/// </summary>
public sealed record TrafficLandmarkDetails(string FixName, string AircraftType);

public sealed record FieldAdvisoryDetails(int Clock, int Miles);

public sealed record SafetyAlertDetails(int Clock, int Miles, SafetyAlertTurn? Turn, SafetyAlertVertical? Vertical);

/// <summary>A resolved traffic-advisory target plus a grade of how accurate the controller's call was.</summary>
public sealed record TrafficAdvisoryTargetMatch(TrafficAdvisoryCandidate Target, AdvisoryMatchQuality Quality, string ImpreciseDetail);

public static class TrafficAdvisoryMatcher
{
    private static readonly string[] Directions = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    // Relative-position keywords as octants of relative bearing off the nose, clockwise from 12 o'clock.
    // NOSE=0° (12), NR=45° (1-2), R=90° (3), RR=135° (4-5), TAIL=180° (6), LR=225° (7-8), L=270° (9), NL=315° (10-11).
    private static readonly string[] RelativePositions = ["NOSE", "NR", "R", "RR", "TAIL", "LR", "L", "NL"];

    // Aviation-reviewed tolerances (FAA 7110.65 §2-1-21; AIM §4-1-15 / FIG 4-1-2). A correctly issued
    // clock routinely differs from the pilot's view by 1-2 hours because the controller reads the target's
    // ground track while the pilot flies a drift-corrected heading, so an exact clock match is not required.
    // Clock loosens further when the recipient is maneuvering (its instantaneous heading is unreliable).
    // See docs/solo-training-evaluation.md (Advisory matching).
    private const int ClockGateSectorsStable = 2; // 60°
    private const int ClockGateSectorsManeuvering = 4; // 120°
    private const double ManeuveringBankDeg = 7.0;
    private const int MilesGateNm = 2;
    private const int DirectionGateOctants = 1; // 45°
    private const int AltitudeGateFt = 500;

    // "Exact" (no scoring penalty) bands. Whole-mile rounding makes a ±1 NM call correct, not imprecise.
    private const int ExactClockSectors = 1;
    private const int ExactMilesNm = 1;
    private const int ExactAltitudeFt = 100;

    // VFR descriptive-form gates (aviation-reviewed; see docs/solo-training-evaluation.md). Relative
    // position is an octant of relative bearing; pattern distance and landmark proximity gate the
    // candidate set, then lowest weighted error wins.
    private const int RelativeOctantGate = 1; // ±45°
    private const int PatternMilesGateNm = 2;
    private const double LandmarkRadiusNm = 2.0;
    private const double LandmarkExactNm = 1.0;

    /// <summary>
    /// Whether an aircraft banked <paramref name="bankDeg"/> counts as maneuvering, which widens and de-weights the clock
    /// gate because its instantaneous heading is unreliable.
    /// </summary>
    public static bool IsManeuvering(double bankDeg) => Math.Abs(bankDeg) > ManeuveringBankDeg;

    /// <summary>
    /// Resolve the aircraft a structured traffic advisory most likely describes. Candidates must fall within
    /// per-field tolerance gates; among those, the lowest weighted field error wins (clock is de-weighted and
    /// widened when the recipient is maneuvering). Returns the target plus an accuracy grade, or null with a
    /// reason in <paramref name="error"/> when nothing is close enough.
    /// </summary>
    public static TrafficAdvisoryTargetMatch? ResolveStructuredTrafficTarget(
        TrafficAdvisoryCandidate recipient,
        TrafficAdvisoryDetails details,
        IReadOnlyList<TrafficAdvisoryCandidate>? aircraft,
        out string error
    )
    {
        if (aircraft is null)
        {
            error = "Unable, aircraft list unavailable";
            return null;
        }

        int clockGate = recipient.IsManeuvering ? ClockGateSectorsManeuvering : ClockGateSectorsStable;
        double clockWeight = recipient.IsManeuvering ? 0.5 : 1.0;

        TrafficAdvisoryTargetMatch? best = null;
        double bestError = double.MaxValue;
        string? bestCallsign = null;

        foreach (TrafficAdvisoryCandidate target in aircraft)
        {
            if (target.Callsign.Equals(recipient.Callsign, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int clockError = SectorDistance(ClockFrom(recipient, target), details.Clock);
            int milesError = Math.Abs(MilesFrom(recipient, target) - details.Miles);
            int directionError = OctantDistance(DirectionIndexOf(DirectionFrom(target)), DirectionIndexOf(details.Direction));
            bool typeMatches = TypeMatches(target, details.AircraftType);
            int? altitudeError = details.Altitude is { } statedAltitude ? Math.Abs(AltitudeBucket(target) - statedAltitude) : null;

            if ((clockError > clockGate) || (milesError > MilesGateNm) || (directionError > DirectionGateOctants))
            {
                continue;
            }
            if (altitudeError is { } gateError && gateError > AltitudeGateFt)
            {
                continue;
            }

            double weighted =
                (clockError * clockWeight) + (milesError * 2.0) + (directionError * 2.0) + ((altitudeError ?? 0) / 100.0) + (typeMatches ? 0.0 : 5.0);

            bool better =
                (weighted < bestError)
                || ((weighted == bestError) && (bestCallsign is not null) && (string.CompareOrdinal(target.Callsign, bestCallsign) < 0));
            if (!better)
            {
                continue;
            }

            bestError = weighted;
            bestCallsign = target.Callsign;
            (AdvisoryMatchQuality quality, string? detail) = Grade(clockError, milesError, directionError, altitudeError, typeMatches);
            best = new TrafficAdvisoryTargetMatch(target, quality, detail);
        }

        error = best is null ? $"Unable, no aircraft matches {Describe(details)}" : "";
        return best;
    }

    /// <summary>
    /// Resolve a safety-alert target. A safety alert is duty-priority-one (§2-1-6): the same position
    /// leniency is allowed to find the conflict, but an ambiguous picture fails rather than guessing the
    /// wrong aircraft.
    /// </summary>
    public static TrafficAdvisoryCandidate? ResolveSafetyAlertTarget(
        TrafficAdvisoryCandidate recipient,
        SafetyAlertDetails details,
        IReadOnlyList<TrafficAdvisoryCandidate>? aircraft,
        out string error
    )
    {
        if (aircraft is null)
        {
            error = "Unable, aircraft list unavailable";
            return null;
        }

        int clockGate = recipient.IsManeuvering ? ClockGateSectorsManeuvering : ClockGateSectorsStable;

        var eligible = aircraft
            .Where(target => !target.Callsign.Equals(recipient.Callsign, StringComparison.OrdinalIgnoreCase))
            .Where(target =>
                (SectorDistance(ClockFrom(recipient, target), details.Clock) <= clockGate)
                && (Math.Abs(MilesFrom(recipient, target) - details.Miles) <= MilesGateNm)
            )
            .ToList();

        string description = $"traffic alert target {details.Clock} o'clock, {details.Miles} mile(s)";
        if (eligible.Count == 1)
        {
            error = "";
            return eligible[0];
        }

        error = eligible.Count == 0 ? $"Unable, no aircraft matches {description}" : $"Unable, {description} matches multiple aircraft";
        return null;
    }

    /// <summary>
    /// Resolve the aircraft a VFR relative-position advisory describes ("off your nose and to the right,
    /// 2 miles, a Cessna"). The position keyword is an octant of relative bearing off the nose; candidates
    /// must fall within one octant and the stated distance, and the lowest weighted error wins.
    /// </summary>
    public static TrafficAdvisoryTargetMatch? ResolveRelativeTrafficTarget(
        TrafficAdvisoryCandidate recipient,
        TrafficRelativeDetails details,
        IReadOnlyList<TrafficAdvisoryCandidate>? aircraft,
        out string error
    )
    {
        if (aircraft is null)
        {
            error = "Unable, aircraft list unavailable";
            return null;
        }

        int positionOctant = RelativeOctantOf(details.Position);
        TrafficAdvisoryTargetMatch? best = null;
        double bestError = double.MaxValue;
        string? bestCallsign = null;

        foreach (TrafficAdvisoryCandidate target in aircraft)
        {
            if (target.Callsign.Equals(recipient.Callsign, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int octantError = OctantDistance(RelativeBearingOctant(recipient, target), positionOctant);
            int milesError = Math.Abs(MilesFrom(recipient, target) - details.Miles);
            bool typeMatches = TypeMatches(target, details.AircraftType);

            if ((octantError > RelativeOctantGate) || (milesError > MilesGateNm))
            {
                continue;
            }

            double weighted = (octantError * 2.0) + (milesError * 2.0) + (typeMatches ? 0.0 : 5.0);
            if (!IsBetter(weighted, bestError, target.Callsign, bestCallsign))
            {
                continue;
            }

            bestError = weighted;
            bestCallsign = target.Callsign;
            var off = new List<string>();
            if (octantError > 0)
            {
                off.Add("position off");
            }
            if (milesError > ExactMilesNm)
            {
                off.Add($"distance off {milesError} NM");
            }
            if (!typeMatches)
            {
                off.Add("type mismatch");
            }
            best = new TrafficAdvisoryTargetMatch(
                target,
                off.Count == 0 ? AdvisoryMatchQuality.Exact : AdvisoryMatchQuality.Imprecise,
                string.Join(", ", off)
            );
        }

        error = best is null ? $"Unable, no traffic {details.Miles} mile(s) off your {CommandDescriber.RelativePositionWord(details.Position)}" : "";
        return best;
    }

    /// <summary>
    /// Resolve the aircraft a VFR pattern-leg advisory describes ("2-mile right base for runway 28R, a
    /// Mooney"). Each candidate's pattern position (classified from its active pattern/final phase against its own
    /// assigned runway) must be on the stated runway; the leg, side (ignored on final), and stated distance must
    /// match, and the lowest weighted error wins.
    /// </summary>
    public static TrafficAdvisoryTargetMatch? ResolvePatternTrafficTarget(
        TrafficAdvisoryCandidate recipient,
        TrafficPatternDetails details,
        IReadOnlyList<TrafficAdvisoryCandidate>? aircraft,
        out string error
    )
    {
        if (aircraft is null)
        {
            error = "Unable, aircraft list unavailable";
            return null;
        }

        string runwayId = RunwayIdentifier.NormalizeDesignator(details.RunwayId);
        TrafficAdvisoryTargetMatch? best = null;
        double bestError = double.MaxValue;
        string? bestCallsign = null;

        foreach (TrafficAdvisoryCandidate target in aircraft)
        {
            if (target.Callsign.Equals(recipient.Callsign, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!runwayId.Equals(target.AssignedRunwayId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (target.PatternPosition is not { } position)
            {
                continue;
            }
            if (position.Leg != details.Leg)
            {
                continue;
            }
            if ((details.Side is { } requestedSide) && (position.Side is { } actualSide) && (requestedSide != actualSide))
            {
                continue;
            }

            int milesError = Math.Abs((int)Math.Round(position.DistanceNm, MidpointRounding.AwayFromZero) - details.Miles);
            if (milesError > PatternMilesGateNm)
            {
                continue;
            }

            bool typeMatches = TypeMatches(target, details.AircraftType);
            double weighted = (milesError * 2.0) + (typeMatches ? 0.0 : 5.0);
            if (!IsBetter(weighted, bestError, target.Callsign, bestCallsign))
            {
                continue;
            }

            bestError = weighted;
            bestCallsign = target.Callsign;
            var off = new List<string>();
            if (milesError > ExactMilesNm)
            {
                off.Add($"distance off {milesError} NM");
            }
            if (!typeMatches)
            {
                off.Add("type mismatch");
            }
            best = new TrafficAdvisoryTargetMatch(
                target,
                off.Count == 0 ? AdvisoryMatchQuality.Exact : AdvisoryMatchQuality.Imprecise,
                string.Join(", ", off)
            );
        }

        error = best is null ? $"Unable, no traffic on {PatternLegPhrase(details.Leg)} for runway {details.RunwayId}" : "";
        return best;
    }

    // Controllers say "on final", never "on the final leg".
    private static string PatternLegPhrase(PatternEntryLeg leg) =>
        leg switch
        {
            PatternEntryLeg.Upwind => "the upwind leg",
            PatternEntryLeg.Crosswind => "the crosswind leg",
            PatternEntryLeg.Downwind => "the downwind leg",
            PatternEntryLeg.Base => "the base leg",
            PatternEntryLeg.Final => "final",
            _ => throw new ArgumentOutOfRangeException(nameof(leg), leg, "Unknown pattern leg"),
        };

    /// <summary>
    /// Resolve the aircraft a VFR landmark advisory describes ("over the Oakland Coliseum, a Skyhawk").
    /// The landmark position is resolved by the caller; candidates within <see cref="LandmarkRadiusNm"/>
    /// of it win by nearest distance (then type), and grade Exact within <see cref="LandmarkExactNm"/>.
    /// </summary>
    public static TrafficAdvisoryTargetMatch? ResolveLandmarkTrafficTarget(
        TrafficAdvisoryCandidate recipient,
        LatLon landmark,
        string aircraftType,
        IReadOnlyList<TrafficAdvisoryCandidate>? aircraft,
        out string error
    )
    {
        if (aircraft is null)
        {
            error = "Unable, aircraft list unavailable";
            return null;
        }

        TrafficAdvisoryTargetMatch? best = null;
        double bestError = double.MaxValue;
        string? bestCallsign = null;

        foreach (TrafficAdvisoryCandidate target in aircraft)
        {
            if (target.Callsign.Equals(recipient.Callsign, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double distanceNm = GeoMath.DistanceNm(target.Position, landmark);
            if (distanceNm > LandmarkRadiusNm)
            {
                continue;
            }

            bool typeMatches = TypeMatches(target, aircraftType);
            double weighted = distanceNm + (typeMatches ? 0.0 : 5.0);
            if (!IsBetter(weighted, bestError, target.Callsign, bestCallsign))
            {
                continue;
            }

            bestError = weighted;
            bestCallsign = target.Callsign;
            var off = new List<string>();
            if (distanceNm > LandmarkExactNm)
            {
                off.Add($"{distanceNm:0.0} NM from the landmark");
            }
            if (!typeMatches)
            {
                off.Add("type mismatch");
            }
            best = new TrafficAdvisoryTargetMatch(
                target,
                off.Count == 0 ? AdvisoryMatchQuality.Exact : AdvisoryMatchQuality.Imprecise,
                string.Join(", ", off)
            );
        }

        error = best is null ? "Unable, no traffic over that landmark" : "";
        return best;
    }

    private static bool IsBetter(double weighted, double bestError, string callsign, string? bestCallsign) =>
        (weighted < bestError) || ((weighted == bestError) && (bestCallsign is not null) && (string.CompareOrdinal(callsign, bestCallsign) < 0));

    private static int RelativeOctantOf(string position)
    {
        int index = Array.IndexOf(RelativePositions, position.Trim().ToUpperInvariant());
        return index < 0 ? 0 : index;
    }

    private static int RelativeBearingOctant(TrafficAdvisoryCandidate recipient, TrafficAdvisoryCandidate target)
    {
        double bearing = GeoMath.BearingTo(recipient.Position, target.Position);
        double relative = NormalizeDegrees(bearing - recipient.TrueHeadingDeg);
        return (int)Math.Round(relative / 45.0, MidpointRounding.AwayFromZero) % 8;
    }

    public static int ClockFrom(TrafficAdvisoryCandidate recipient, TrafficAdvisoryCandidate target)
    {
        double bearing = GeoMath.BearingTo(recipient.Position, target.Position);
        double relative = NormalizeDegrees(bearing - recipient.TrueHeadingDeg);
        int sector = (int)Math.Round(relative / 30.0, MidpointRounding.AwayFromZero) % 12;
        return sector == 0 ? 12 : sector;
    }

    public static int MilesFrom(TrafficAdvisoryCandidate recipient, TrafficAdvisoryCandidate target) =>
        Math.Max(1, (int)Math.Round(GeoMath.DistanceNm(recipient.Position, target.Position), MidpointRounding.AwayFromZero));

    public static string DirectionFrom(TrafficAdvisoryCandidate target)
    {
        int index = (int)Math.Round(NormalizeDegrees(target.TrueTrackDeg) / 45.0, MidpointRounding.AwayFromZero) % 8;
        return Directions[index];
    }

    private static (AdvisoryMatchQuality Quality, string Detail) Grade(
        int clockError,
        int milesError,
        int directionError,
        int? altitudeError,
        bool typeMatches
    )
    {
        var off = new List<string>();
        if (clockError > ExactClockSectors)
        {
            off.Add($"clock off {clockError} hr");
        }
        if (milesError > ExactMilesNm)
        {
            off.Add($"distance off {milesError} NM");
        }
        if (directionError > 0)
        {
            off.Add("direction off");
        }
        if (altitudeError is { } error && error > ExactAltitudeFt)
        {
            off.Add($"altitude off {error} ft");
        }
        if (!typeMatches)
        {
            off.Add("type mismatch");
        }

        return off.Count == 0 ? (AdvisoryMatchQuality.Exact, "") : (AdvisoryMatchQuality.Imprecise, string.Join(", ", off));
    }

    private static string Describe(TrafficAdvisoryDetails details)
    {
        string basePart =
            $"traffic {details.Clock} o'clock, {details.Miles} mile(s), {CommandDescriber.DirectionWord(details.Direction)}, {details.AircraftType}";
        return details.Altitude is { } altitude ? $"{basePart}, {altitude:N0}" : basePart;
    }

    private static int DirectionIndexOf(string direction)
    {
        int index = Array.IndexOf(Directions, direction.Trim().ToUpperInvariant());
        return index < 0 ? 0 : index;
    }

    private static int SectorDistance(int a, int b)
    {
        int diff = Math.Abs(a - b) % 12;
        return Math.Min(diff, 12 - diff);
    }

    private static int OctantDistance(int a, int b)
    {
        int diff = Math.Abs(a - b) % 8;
        return Math.Min(diff, 8 - diff);
    }

    private static bool TypeMatches(TrafficAdvisoryCandidate target, string requestedType)
    {
        string requested = NormalizeType(requestedType);
        string actual = NormalizeType(target.BaseAircraftType);
        if (requested.Equals(actual, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (requested.Length >= 3 && actual.Length >= 3 && requested[..3].Equals(actual[..3], StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        FaaAircraftRecord? record = FaaAircraftDatabase.Get(target.AircraftType);
        if (record is null)
        {
            return false;
        }

        return TokenContains(record.ModelFaa, requested)
            || TokenContains(record.ModelBada, requested)
            || TokenContains(record.Manufacturer, requested);
    }

    private static int AltitudeBucket(TrafficAdvisoryCandidate target) =>
        (int)Math.Round(target.AltitudeFt / 100.0, MidpointRounding.AwayFromZero) * 100;

    private static string NormalizeType(string value) => new([.. value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit)]);

    private static bool TokenContains(string? source, string token) =>
        !string.IsNullOrWhiteSpace(source) && NormalizeType(source).Contains(token, StringComparison.OrdinalIgnoreCase);

    private static double NormalizeDegrees(double degrees)
    {
        double normalized = degrees % 360.0;
        return normalized < 0.0 ? normalized + 360.0 : normalized;
    }
}
