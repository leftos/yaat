using Yaat.Sim.Data.Faa;

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

public sealed record FieldAdvisoryDetails(int Clock, int Miles);

public sealed record SafetyAlertDetails(int Clock, int Miles, SafetyAlertTurn? Turn, SafetyAlertVertical? Vertical);

/// <summary>A resolved traffic-advisory target plus a grade of how accurate the controller's call was.</summary>
public sealed record TrafficAdvisoryTargetMatch(AircraftState Target, AdvisoryMatchQuality Quality, string ImpreciseDetail);

internal static class TrafficAdvisoryMatcher
{
    private static readonly string[] Directions = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

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

    /// <summary>
    /// Resolve the aircraft a structured traffic advisory most likely describes. Candidates must fall within
    /// per-field tolerance gates; among those, the lowest weighted field error wins (clock is de-weighted and
    /// widened when the recipient is maneuvering). Returns the target plus an accuracy grade, or null with a
    /// reason in <paramref name="error"/> when nothing is close enough.
    /// </summary>
    public static TrafficAdvisoryTargetMatch? ResolveStructuredTrafficTarget(
        AircraftState recipient,
        TrafficAdvisoryDetails details,
        IReadOnlyList<AircraftState>? aircraft,
        out string error
    )
    {
        if (aircraft is null)
        {
            error = "Unable, aircraft list unavailable";
            return null;
        }

        bool maneuvering = Math.Abs(recipient.BankAngle) > ManeuveringBankDeg;
        int clockGate = maneuvering ? ClockGateSectorsManeuvering : ClockGateSectorsStable;
        double clockWeight = maneuvering ? 0.5 : 1.0;

        TrafficAdvisoryTargetMatch? best = null;
        double bestError = double.MaxValue;
        string? bestCallsign = null;

        foreach (var target in aircraft)
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
            var (quality, detail) = Grade(clockError, milesError, directionError, altitudeError, typeMatches);
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
    public static AircraftState? ResolveSafetyAlertTarget(
        AircraftState recipient,
        SafetyAlertDetails details,
        IReadOnlyList<AircraftState>? aircraft,
        out string error
    )
    {
        if (aircraft is null)
        {
            error = "Unable, aircraft list unavailable";
            return null;
        }

        bool maneuvering = Math.Abs(recipient.BankAngle) > ManeuveringBankDeg;
        int clockGate = maneuvering ? ClockGateSectorsManeuvering : ClockGateSectorsStable;

        var eligible = aircraft
            .Where(target => !target.Callsign.Equals(recipient.Callsign, StringComparison.OrdinalIgnoreCase))
            .Where(target =>
                (SectorDistance(ClockFrom(recipient, target), details.Clock) <= clockGate)
                && (Math.Abs(MilesFrom(recipient, target) - details.Miles) <= MilesGateNm)
            )
            .ToList();

        var description = $"traffic alert target {details.Clock} o'clock, {details.Miles} mile(s)";
        if (eligible.Count == 1)
        {
            error = "";
            return eligible[0];
        }

        error = eligible.Count == 0 ? $"Unable, no aircraft matches {description}" : $"Unable, {description} matches multiple aircraft";
        return null;
    }

    public static int ClockFrom(AircraftState recipient, AircraftState target)
    {
        double bearing = GeoMath.BearingTo(recipient.Position, target.Position);
        double relative = NormalizeDegrees(bearing - recipient.TrueHeading.Degrees);
        int sector = (int)Math.Round(relative / 30.0, MidpointRounding.AwayFromZero) % 12;
        return sector == 0 ? 12 : sector;
    }

    public static int MilesFrom(AircraftState recipient, AircraftState target) =>
        Math.Max(1, (int)Math.Round(GeoMath.DistanceNm(recipient.Position, target.Position), MidpointRounding.AwayFromZero));

    public static string DirectionFrom(AircraftState target)
    {
        int index = (int)Math.Round(NormalizeDegrees(target.TrueTrack.Degrees) / 45.0, MidpointRounding.AwayFromZero) % 8;
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
        var basePart = $"traffic {details.Clock} o'clock, {details.Miles} mile(s), {details.Direction}bound, {details.AircraftType}";
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

    private static bool TypeMatches(AircraftState target, string requestedType)
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

        var record = FaaAircraftDatabase.Get(target.AircraftType);
        if (record is null)
        {
            return false;
        }

        return TokenContains(record.ModelFaa, requested)
            || TokenContains(record.ModelBada, requested)
            || TokenContains(record.Manufacturer, requested);
    }

    private static int AltitudeBucket(AircraftState target) => (int)Math.Round(target.Altitude / 100.0, MidpointRounding.AwayFromZero) * 100;

    private static string NormalizeType(string value) => new(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool TokenContains(string? source, string token) =>
        !string.IsNullOrWhiteSpace(source) && NormalizeType(source).Contains(token, StringComparison.OrdinalIgnoreCase);

    private static double NormalizeDegrees(double degrees)
    {
        double normalized = degrees % 360.0;
        return normalized < 0.0 ? normalized + 360.0 : normalized;
    }
}
