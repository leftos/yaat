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

public sealed record TrafficAdvisoryDetails(int Clock, int Miles, string Direction, string AircraftType, int Altitude);

public sealed record FieldAdvisoryDetails(int Clock, int Miles);

public sealed record SafetyAlertDetails(int Clock, int Miles, SafetyAlertTurn? Turn, SafetyAlertVertical? Vertical);

internal static class TrafficAdvisoryMatcher
{
    private static readonly string[] Directions = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    public static AircraftState? ResolveStructuredTrafficTarget(
        AircraftState recipient,
        TrafficAdvisoryDetails details,
        IReadOnlyList<AircraftState>? aircraft,
        out string error
    )
    {
        return ResolveTarget(
            recipient,
            aircraft,
            target =>
                ClockFrom(recipient, target) == details.Clock
                && MilesFrom(recipient, target) == details.Miles
                && DirectionFrom(target).Equals(details.Direction, StringComparison.OrdinalIgnoreCase)
                && TypeMatches(target, details.AircraftType)
                && AltitudeMatches(target, details.Altitude),
            $"traffic {details.Clock} o'clock, {details.Miles} mile(s), {details.Direction}bound, {details.AircraftType}, {details.Altitude:N0}",
            out error
        );
    }

    public static AircraftState? ResolveSafetyAlertTarget(
        AircraftState recipient,
        SafetyAlertDetails details,
        IReadOnlyList<AircraftState>? aircraft,
        out string error
    )
    {
        return ResolveTarget(
            recipient,
            aircraft,
            target => ClockFrom(recipient, target) == details.Clock && MilesFrom(recipient, target) == details.Miles,
            $"traffic alert target {details.Clock} o'clock, {details.Miles} mile(s)",
            out error
        );
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

    private static AircraftState? ResolveTarget(
        AircraftState recipient,
        IReadOnlyList<AircraftState>? aircraft,
        Func<AircraftState, bool> predicate,
        string description,
        out string error
    )
    {
        if (aircraft is null)
        {
            error = "Unable, aircraft list unavailable";
            return null;
        }

        var matches = aircraft
            .Where(target => !target.Callsign.Equals(recipient.Callsign, StringComparison.OrdinalIgnoreCase))
            .Where(predicate)
            .ToList();

        if (matches.Count == 1)
        {
            error = "";
            return matches[0];
        }

        error = matches.Count == 0 ? $"Unable, no aircraft matches {description}" : $"Unable, {description} matches multiple aircraft";
        return null;
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

    private static bool AltitudeMatches(AircraftState target, int altitude) =>
        (int)Math.Round(target.Altitude / 100.0, MidpointRounding.AwayFromZero) * 100 == altitude;

    private static string NormalizeType(string value) => new(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool TokenContains(string? source, string token) =>
        !string.IsNullOrWhiteSpace(source) && NormalizeType(source).Contains(token, StringComparison.OrdinalIgnoreCase);

    private static double NormalizeDegrees(double degrees)
    {
        double normalized = degrees % 360.0;
        return normalized < 0.0 ? normalized + 360.0 : normalized;
    }
}
