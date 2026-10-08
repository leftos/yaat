using System.Globalization;
using Yaat.Sim;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Where one aircraft is from another, as the context menu's For section and traffic list say it: the clock position
/// from the first aircraft's true heading to the true bearing of the second, the distance in whole nautical miles, and
/// the altitude difference rounded to 100 ft, with their wording (<c>AAL601 is at its 2 o'clock, 4 nm, 1,500 ft below</c>);
/// on the ground, what the other aircraft is doing and where, its distance in feet, and whether it is ahead or behind
/// (<c>SWA601 is holding short of 30 at W3, 300 ft ahead</c>).
/// </summary>
public static class RelativeGeometry
{
    /// <summary>
    /// The clock position, 1 to 12, of a target at true bearing <paramref name="bearingTrueDegrees"/> from an aircraft
    /// on true heading <paramref name="headingTrueDegrees"/>: 12 straight ahead, 3 off the right wing, 6 behind, each hour
    /// 30° wide and centred on its own mark.
    /// </summary>
    public static int ClockPosition(double headingTrueDegrees, double bearingTrueDegrees)
    {
        double relative = (((bearingTrueDegrees - headingTrueDegrees) % 360) + 360) % 360;
        int hour = (int)Math.Round(relative / 30, MidpointRounding.AwayFromZero) % 12;
        return (hour == 0) ? 12 : hour;
    }

    /// <summary>
    /// How far <paramref name="toFeet"/> is above (positive) or below (negative) <paramref name="fromFeet"/>, rounded to
    /// the nearest 100 ft; 0 when the difference is under 100 ft.
    /// </summary>
    public static int AltitudeDelta(double fromFeet, double toFeet)
    {
        double difference = toFeet - fromFeet;
        return (Math.Abs(difference) < 100) ? 0 : (int)(Math.Round(difference / 100, MidpointRounding.AwayFromZero) * 100);
    }

    /// <summary><paramref name="nm"/> rounded to a whole number of nautical miles, halves away from zero.</summary>
    public static int WholeNm(double nm) => (int)Math.Round(nm, MidpointRounding.AwayFromZero);

    /// <summary>The For line's altitude difference: <c>1,500 ft below</c>, <c>500 ft above</c>, or <c>same altitude</c> for 0.</summary>
    public static string AltitudeDeltaLine(int deltaFeet) => AltitudeDeltaText(deltaFeet, " ft");

    /// <summary>A traffic row's altitude difference: <c>1,500 below</c>, <c>500 above</c>, or <c>same altitude</c> for 0.</summary>
    public static string AltitudeDeltaColumn(int deltaFeet) => AltitudeDeltaText(deltaFeet, "");

    /// <summary>The For section's line: <c>{callsign} is at its {clock} o'clock, {nm} nm, {altitude difference}</c>.</summary>
    public static string Describe(string callsign, int clockPosition, int nm, int deltaFeet) =>
        $"{callsign} is at its {clockPosition} o'clock, {nm} nm, {AltitudeDeltaLine(deltaFeet)}";

    /// <summary>The For section's line naming where <paramref name="target"/> is from <paramref name="from"/>.</summary>
    public static string Describe(IMenuAircraft from, IMenuAircraft target)
    {
        MenuTrafficRow row = TrafficRow(from, target);
        return Describe(row.Callsign, row.ClockPosition, WholeNm(row.DistanceNm), row.AltitudeDeltaFeet);
    }

    /// <summary><paramref name="target"/> as a traffic row seen from <paramref name="from"/>.</summary>
    public static MenuTrafficRow TrafficRow(IMenuAircraft from, IMenuAircraft target) =>
        new(
            target.Callsign,
            TrafficType(target.DisplayAircraftType),
            GeoMath.DistanceNm(from.Position, target.Position),
            ClockPosition(from.HeadingDegrees, GeoMath.BearingTo(from.Position, target.Position)),
            AltitudeDelta(from.AltitudeFeet, target.AltitudeFeet)
        );

    /// <summary>
    /// The type a traffic row shows: the ICAO designator without the filed prefix or equipment suffix (<c>B77W/L</c> →
    /// <c>B77W</c>, <see cref="AircraftState.StripTypePrefix"/>); empty when the type is blank.
    /// </summary>
    public static string TrafficType(string aircraftType) =>
        string.IsNullOrWhiteSpace(aircraftType) ? "" : AircraftState.StripTypePrefix(aircraftType);

    /// <summary>The ground speed above which an aircraft counts as moving whatever its phase.</summary>
    private const double MovingGroundSpeedKnots = 1;

    /// <summary>
    /// Whether a ground aircraft lists under Moving rather than Parked or holding: pushing back or taxiing, or rolling
    /// faster than 1 kt in any other phase.
    /// </summary>
    public static bool IsMoving(IMenuAircraft aircraft) =>
        (aircraft.CurrentPhase is "Pushback" or "Taxiing") || (aircraft.GroundSpeedKnots > MovingGroundSpeedKnots);

    /// <summary>
    /// Whether a target at true bearing <paramref name="bearingTrueDegrees"/> lies within 90° either side of true heading
    /// <paramref name="headingTrueDegrees"/>.
    /// </summary>
    public static bool IsAhead(double headingTrueDegrees, double bearingTrueDegrees)
    {
        double relative = (((bearingTrueDegrees - headingTrueDegrees) % 360) + 360) % 360;
        return (relative <= 90) || (relative >= 270);
    }

    /// <summary>
    /// <paramref name="feet"/> rounded to the nearest 50 ft under 1,000 ft and to the nearest 100 ft from there, halves
    /// away from zero.
    /// </summary>
    public static int RoundedFeet(double feet)
    {
        int step = (feet < 1000) ? 50 : 100;
        return (int)(Math.Round(feet / step, MidpointRounding.AwayFromZero) * step);
    }

    /// <summary><paramref name="feet"/> rounded (<see cref="RoundedFeet"/>) with thousands commas: <c>2,100 ft</c>.</summary>
    public static string FeetText(double feet) => $"{RoundedFeet(feet).ToString("N0", CultureInfo.InvariantCulture)} ft";

    /// <summary><paramref name="target"/> as a ground traffic row seen from <paramref name="from"/>.</summary>
    public static MenuGroundTrafficRow GroundTrafficRow(IMenuAircraft from, IMenuAircraft target) =>
        new(target.Callsign, TrafficType(target.DisplayAircraftType), DistanceFeet(from, target), GroundStateColumn(target, IsAheadOf(from, target)))
        {
            IsMoving = IsMoving(target),
            IsSurfaceShadow = AircraftCommandApplicability.IsSurfaceShadow(target),
        };

    /// <summary>
    /// A ground traffic row's state: at a stand <c>{state} · gate {stand}</c> (<c>pushing back · gate 24</c>), elsewhere
    /// the state, the taxiway and whether it is <paramref name="ahead"/> or behind (<c>taxiing on W · ahead</c>,
    /// <c>holding short of 30 at W3 · behind</c>).
    /// </summary>
    public static string GroundStateColumn(IMenuAircraft target, bool ahead) =>
        (StandOf(target) is { } stand)
            ? $"{GroundState(target)} · gate {stand}"
            : $"{GroundState(target)}{TaxiwayPlace(target)} · {Direction(ahead)}";

    /// <summary>
    /// The For section's line for a ground pair: <c>{target} is {state} {place}, {feet} ft {ahead|behind}</c>, with
    /// <c> on {from}'s route</c> when <paramref name="onFromRoute"/> (<c>SWA601 is holding short of 30 at W3, 300 ft ahead
    /// on SWA602's route</c>); ahead or behind is from <paramref name="from"/>'s heading.
    /// </summary>
    public static string DescribeGround(IMenuAircraft from, IMenuAircraft target, bool onFromRoute)
    {
        string place = (StandOf(target) is { } stand) ? $" at gate {stand}" : TaxiwayPlace(target);
        string route = onFromRoute ? $" on {from.Callsign}'s route" : "";
        string distance = FeetText(DistanceFeet(from, target));
        string direction = Direction(IsAheadOf(from, target));
        return $"{target.Callsign} is {GroundState(target)}{place}, {distance} {direction}{route}";
    }

    /// <summary>
    /// What a ground aircraft is doing, in lower case: <c>pushing back</c>, <c>taxiing</c>, <c>at parking</c>,
    /// <c>holding short of 30</c> (the first end of the held bar's runway, or the bar's own name), <c>on the ground</c> for
    /// no phase, else the phase with its first letter lowered (<c>following SWA200</c>).
    /// </summary>
    public static string GroundState(IMenuAircraft aircraft)
    {
        string phase = aircraft.CurrentPhase;
        if (phase.StartsWith(HoldingShortPhase, StringComparison.Ordinal))
        {
            return HoldingShortState(phase);
        }

        return phase switch
        {
            "" => "on the ground",
            "At Parking" => "at parking",
            "Pushback" => "pushing back",
            "Holding After Pushback" => "holding after pushback",
            "Taxiing" => "taxiing",
            "Holding In Position" => "holding in position",
            "Crossing Runway" => "crossing the runway",
            "Clearing Runway" => "clearing the runway",
            "Runway Exit" => "exiting the runway",
            "Holding After Exit" => "holding after exit",
            "AirTaxi" => "air taxiing",
            "LiningUp" => "lining up",
            "LinedUpAndWaiting" => "lined up and waiting",
            "Takeoff" => "taking off",
            "Rejected Takeoff" => "rejecting takeoff",
            _ => char.ToLowerInvariant(phase[0]) + phase[1..],
        };
    }

    private const string HoldingShortPhase = "Holding Short";

    /// <summary>The prefix a runway hold-short phase puts on the runway id (<c>Holding Short RWY 30</c>), dropped from the wording.</summary>
    private const string RunwayPrefix = "RWY ";

    /// <summary>
    /// The held target's wording from a <c>Holding Short …</c> phase: <c>holding short of 30</c> for both
    /// <c>Holding Short 30/12</c> and <c>Holding Short RWY 30</c>, <c>holding short</c> when the phase names no target.
    /// </summary>
    private static string HoldingShortState(string phase)
    {
        string bar = phase[HoldingShortPhase.Length..].Trim();
        if (bar.StartsWith(RunwayPrefix, StringComparison.Ordinal))
        {
            bar = bar[RunwayPrefix.Length..].Trim();
        }

        return (bar.Length == 0) ? "holding short" : $"holding short of {bar.Split('/')[0]}";
    }

    private static double DistanceFeet(IMenuAircraft from, IMenuAircraft target) =>
        GeoMath.DistanceNm(from.Position, target.Position) * GeoMath.FeetPerNm;

    private static bool IsAheadOf(IMenuAircraft from, IMenuAircraft target) =>
        IsAhead(from.HeadingDegrees, GeoMath.BearingTo(from.Position, target.Position));

    private static string Direction(bool ahead) => ahead ? "ahead" : "behind";

    /// <summary>The stand an aircraft at parking or pushing back stands at; null in any other phase or with no stand.</summary>
    private static string? StandOf(IMenuAircraft aircraft) =>
        ((aircraft.CurrentPhase is "At Parking" or "Pushback") && !string.IsNullOrWhiteSpace(aircraft.ParkingSpot)) ? aircraft.ParkingSpot : null;

    /// <summary><c> at {taxiway}</c> while holding short, else <c> on {taxiway}</c>; empty with no current taxiway.</summary>
    private static string TaxiwayPlace(IMenuAircraft aircraft)
    {
        if (string.IsNullOrWhiteSpace(aircraft.CurrentTaxiway))
        {
            return "";
        }

        return aircraft.CurrentPhase.StartsWith(HoldingShortPhase, StringComparison.Ordinal)
            ? $" at {aircraft.CurrentTaxiway}"
            : $" on {aircraft.CurrentTaxiway}";
    }

    private static string AltitudeDeltaText(int deltaFeet, string unit)
    {
        if (deltaFeet == 0)
        {
            return "same altitude";
        }

        string feet = Math.Abs(deltaFeet).ToString("N0", CultureInfo.InvariantCulture);
        return $"{feet}{unit} {((deltaFeet < 0) ? "below" : "above")}";
    }
}
