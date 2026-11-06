using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Faa;

/// <summary>
/// Whether a type may accept a land-and-hold-short clearance, and why not (<see cref="LahsoGroupMinima.Judge"/>).
/// </summary>
/// <param name="Eligible">True when the type can hold short within the available landing distance.</param>
/// <param name="Reason">
/// Why the crew declines, for the instructor line (for example <c>B738 needs 7,000 ft, 5,412 ft available</c>); null when eligible.
/// </param>
internal readonly record struct LahsoEligibility(bool Eligible, string? Reason);

/// <summary>
/// The LAHSO aircraft group distance minima of JO 7110.118C para 14.b Table 1 ("LAHSO Aircraft Group/Distance Minima"), and
/// the judge of whether a type can accept a hold-short given the available landing distance (ALD).
/// </summary>
internal static class LahsoGroupMinima
{
    private static readonly ILogger Log = SimLog.CreateLogger("LahsoGroupMinima");

    /// <summary>
    /// The largest ALD a type may need without a Flight Standards letter of authorization: JO 7110.118C para 10.c requires one for
    /// "aircraft requiring greater than 8,000 feet of ALD" (Group 9 above the lowest band, and Group 10), which the sim does not model.
    /// </summary>
    private const int MaxDistanceWithoutAuthorizationFt = 8000;

    /// <summary>The highest LAHSO group JO 7110.118C para 14.b Table 1 defines (Group 10, "greater than 8,000").</summary>
    private const int HighestGroup = 10;

    /// <summary>
    /// Table 1 as published, in feet: one row per group 1–9, one column per field-elevation band (Sea Level–999, 1,000–1,999, …,
    /// 6,000–6,999, 7,000 &amp; Above). Group 2's 7,000 &amp; Above cell is 3,500 as published. Group 10 ("greater than 8,000") has
    /// no figure.
    /// </summary>
    private static readonly int[][] Table =
    [
        [2500, 2550, 2600, 2650, 2700, 2750, 2800, 2850],
        [3000, 3050, 3100, 3150, 3200, 3250, 3300, 3500],
        [3500, 3550, 3600, 3650, 3700, 3750, 3800, 3850],
        [4000, 4050, 4100, 4150, 4200, 4250, 4300, 4350],
        [4500, 4550, 4600, 4650, 4700, 4750, 4800, 4850],
        [5000, 5100, 5200, 5300, 5400, 5500, 5600, 5700],
        [6000, 6100, 6200, 6300, 6400, 6500, 6600, 6700],
        [7000, 7100, 7200, 7300, 7400, 7500, 7600, 7700],
        [8000, 8100, 8200, 8300, 8400, 8500, 8600, 8700],
    ];

    /// <summary>
    /// The landing distance JO 7110.118C para 14.b Table 1 requires of <paramref name="group"/> at a field of
    /// <paramref name="fieldElevationFt"/>, in feet; null for Group 10 and any group outside 1–9. A field below sea level reads the
    /// Sea Level–999 band, and one at 7,000 ft or above the 7,000 &amp; Above band.
    /// </summary>
    public static int? RequiredDistanceFt(int group, double fieldElevationFt)
    {
        if ((group < 1) || (group > Table.Length))
        {
            return null;
        }

        int band = (int)Math.Clamp(Math.Floor(fieldElevationFt / 1000.0), 0, Table[0].Length - 1);
        return Table[group - 1][band];
    }

    /// <summary>
    /// Whether <paramref name="aircraftType"/> can accept a hold-short with <paramref name="availableLandingDistanceFt"/> of ALD
    /// (JO 7110.118C para 10.b: the distance to the hold-short point, capped at the runway's landing distance available) at a field
    /// of <paramref name="fieldElevationFt"/>. Declines a helicopter (para 14.a: LAHSO only on pilot request, judged before the
    /// ACD), a type with no ACD record or no LAHSO group (para 14.b: ineligible), a type needing more than 8,000 ft (para 10.c: a
    /// letter of authorization) and a type whose Table 1 minimum exceeds the ALD.
    /// </summary>
    public static LahsoEligibility Judge(string aircraftType, double availableLandingDistanceFt, double fieldElevationFt)
    {
        string? reason = DeclineReason(aircraftType, availableLandingDistanceFt, fieldElevationFt);
        if (reason is null)
        {
            return new LahsoEligibility(true, null);
        }

        Log.LogDebug(
            "{Type} declines LAHSO (ALD {Ald:F0} ft, field elevation {Elevation:F0} ft): {Reason}",
            aircraftType,
            availableLandingDistanceFt,
            fieldElevationFt,
            reason
        );
        return new LahsoEligibility(false, reason);
    }

    private static string? DeclineReason(string aircraftType, double availableLandingDistanceFt, double fieldElevationFt)
    {
        if (AircraftCategorization.Categorize(aircraftType) == AircraftCategory.Helicopter)
        {
            return $"{aircraftType} is a helicopter";
        }

        FaaAircraftRecord? record = FaaAircraftDatabase.Get(aircraftType);
        if (record is null)
        {
            Log.LogDebug("{Type} has no FAA ACD record, so no LAHSO group", aircraftType);
            return $"{aircraftType} is not in the FAA aircraft data";
        }

        return GroupDeclineReason(aircraftType, record.LahsoGroup, availableLandingDistanceFt, fieldElevationFt);
    }

    /// <summary>
    /// Why a type in LAHSO group <paramref name="group"/> declines with <paramref name="availableLandingDistanceFt"/> of ALD at a field
    /// of <paramref name="fieldElevationFt"/>, or null when it can accept. A group that is null or outside the 1–10 Table 1 defines is
    /// no group (para 14.b: ineligible); a group needing more than 8,000 ft needs a letter of authorization (para 10.c).
    /// </summary>
    internal static string? GroupDeclineReason(string aircraftType, int? group, double availableLandingDistanceFt, double fieldElevationFt)
    {
        if ((group is not { } knownGroup) || (knownGroup < 1) || (knownGroup > HighestGroup))
        {
            Log.LogDebug(
                "{Type} has an FAA ACD record with no LAHSO group ({Group})",
                aircraftType,
                group?.ToString(CultureInfo.InvariantCulture) ?? "null"
            );
            return $"{aircraftType} has no LAHSO group";
        }

        int? requiredFt = RequiredDistanceFt(knownGroup, fieldElevationFt);
        if ((requiredFt is null) || (requiredFt > MaxDistanceWithoutAuthorizationFt))
        {
            return $"{aircraftType} needs a letter of authorization";
        }

        return availableLandingDistanceFt < requiredFt
            ? $"{aircraftType} needs {Feet(requiredFt.Value)} ft, {Feet(Math.Floor(availableLandingDistanceFt))} ft available"
            : null;
    }

    private static string Feet(double feet) => feet.ToString("N0", CultureInfo.InvariantCulture);
}
