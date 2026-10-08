using System.Globalization;
using Yaat.Sim;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Where one aircraft is from another, as the context menu's For section and traffic list say it: the clock position
/// from the first aircraft's true heading to the true bearing of the second, the distance in whole nautical miles, and
/// the altitude difference rounded to 100 ft, with their wording (<c>AAL601 is at its 2 o'clock, 4 nm, 1,500 ft below</c>).
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
            target.DisplayAircraftType,
            GeoMath.DistanceNm(from.Position, target.Position),
            ClockPosition(from.HeadingDegrees, GeoMath.BearingTo(from.Position, target.Position)),
            AltitudeDelta(from.AltitudeFeet, target.AltitudeFeet)
        );

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
