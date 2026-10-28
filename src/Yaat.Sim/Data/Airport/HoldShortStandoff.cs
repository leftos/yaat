using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

/// <summary>Which input decided a runway's base hold-short separation (<see cref="HoldShortStandoff.BaseSeparationFt"/>).</summary>
public enum HoldShortStandoffSource
{
    /// <summary>The runway's paint calibration.</summary>
    Calibrated,

    /// <summary>The vNAS map's authored <c>holdShortDistance</c>.</summary>
    Authored,

    /// <summary>The width, length, ILS and elevation rule, with nothing authored.</summary>
    Rule,

    /// <summary>The rule, over an authored value more than 15 ft below it on a runway in the 250 ft class.</summary>
    RuleOverAuthored,
}

/// <summary>A runway's base hold-short separation and the input that decided it.</summary>
/// <param name="Ft">The separation, feet from the runway centerline to the nearest point of the hold line.</param>
/// <param name="Source">The input that decided it.</param>
/// <param name="RuleFt">What the rule alone gives the runway.</param>
public readonly record struct HoldShortStandoffResult(double Ft, HoldShortStandoffSource Source, double RuleFt);

/// <summary>The runway facts the hold-short standoff rule reads (<see cref="HoldShortStandoff.BaseSeparationFt"/>).</summary>
/// <param name="WidthFt">The runway width.</param>
/// <param name="LengthFt">The runway length.</param>
/// <param name="IlsCategory">The highest ILS category over the runway's two ends (1, 2 or 3), or null for none.</param>
/// <param name="ElevationFt">The higher threshold elevation of the runway's two ends, feet MSL.</param>
public readonly record struct RunwayStandoffInputs(double WidthFt, double LengthFt, int? IlsCategory, double ElevationFt)
{
    /// <summary>The runway width (ft) when navdata is unavailable.</summary>
    public const double DefaultWidthFt = 150.0;

    /// <summary>The length a runway with no navdata counts as: long enough for the 250 ft row of a 100 ft or wider runway.</summary>
    private const double UnknownLengthFt = 5_000.0;

    private static readonly ILogger Log = SimLog.CreateLogger("RunwayStandoffInputs");

    /// <summary>The inputs of a runway <paramref name="widthFt"/> wide that navdata does not know: no ILS, elevation 0.</summary>
    /// <param name="widthFt">The runway width.</param>
    /// <returns>The inputs.</returns>
    public static RunwayStandoffInputs WithoutNavData(double widthFt) => new(widthFt, UnknownLengthFt, null, 0.0);

    /// <summary>
    /// The inputs of the runway <paramref name="id"/> at <paramref name="airportCode"/> from <paramref name="db"/>, or
    /// <see cref="WithoutNavData"/> at <see cref="DefaultWidthFt"/> when there is no database, no airport code or no runway
    /// record for either end.
    /// </summary>
    /// <param name="db">The navigation database, or null when none is loaded.</param>
    /// <param name="airportCode">The airport's navdata code, or null when the layout has none.</param>
    /// <param name="id">The runway.</param>
    /// <returns>The inputs.</returns>
    public static RunwayStandoffInputs Lookup(NavigationDatabase? db, string? airportCode, RunwayIdentifier id)
    {
        if ((db is null) || (airportCode is null))
        {
            return WithoutNavData(DefaultWidthFt);
        }

        RunwayInfo? runway = db.GetRunway(airportCode, id.End1) ?? db.GetRunway(airportCode, id.End2);
        if (runway is null)
        {
            Log.LogDebug(
                "{Airport} runway {Runway}: no navdata runway record, standoff inputs default to a {WidthFt} ft runway",
                airportCode,
                id,
                DefaultWidthFt
            );
            return WithoutNavData(DefaultWidthFt);
        }

        return Of(db, airportCode, runway);
    }

    /// <summary>The inputs of <paramref name="runway"/>, a runway record of <paramref name="airportCode"/> in <paramref name="db"/>.</summary>
    /// <param name="db">The navigation database the record came from.</param>
    /// <param name="airportCode">The airport's navdata code.</param>
    /// <param name="runway">The runway record.</param>
    /// <returns>The inputs.</returns>
    public static RunwayStandoffInputs Of(NavigationDatabase db, string airportCode, RunwayInfo runway)
    {
        int? end1 = db.LocalizerCategory(airportCode, runway.Id.End1);
        int? end2 = db.LocalizerCategory(airportCode, runway.Id.End2);
        int? ilsCategory = (end1, end2) switch
        {
            ({ } a, { } b) => Math.Max(a, b),
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null,
        };
        return new RunwayStandoffInputs(runway.WidthFt, runway.PavementLengthFt, ilsCategory, Math.Max(runway.Elevation1Ft, runway.Elevation2Ft));
    }
}

/// <summary>
/// The base hold-short separation S of a runway: the distance from its centerline to the nearest point of a hold line
/// on a taxiway that no painted bar pairs. By first match: the runway's paint calibration; the authored
/// <c>holdShortDistance</c>, unless it is more than 15 ft below the rule value on a runway in the 250 ft class; the rule,
/// from the AC 150/5300-13B runway design standards with the runway width standing in for the design group.
/// </summary>
public static class HoldShortStandoff
{
    private static readonly ILogger Log = SimLog.CreateLogger("HoldShortStandoff");

    /// <summary>The smallest rule value whose rows (AAC C/D/E) take the elevation term and the authored-value check.</summary>
    private const double ClassFloorFt = 250.0;

    /// <summary>How far an authored value may sit below a 250 ft class rule value before the rule is used instead.</summary>
    private const double AuthoredToleranceFt = 15.0;

    /// <summary>The fewest paint-paired bars a runway needs for its separation to be calibrated from paint (<see cref="CalibratedFt"/>).</summary>
    public const int MinCalibrationBars = 3;

    /// <summary>The runway length from which a 100 ft or wider runway is in the 250 ft row without an ILS.</summary>
    private const double LongRunwayFt = 5_000.0;

    /// <summary>The runway's base hold-short separation and the input that decided it.</summary>
    /// <param name="calibratedFt">The runway's paint-calibrated separation, or null when it has none.</param>
    /// <param name="authoredFt">The vNAS map's authored <c>holdShortDistance</c>, or null when it authors none.</param>
    /// <param name="inputs">The runway facts the rule reads.</param>
    /// <returns>The separation and its source.</returns>
    public static HoldShortStandoffResult BaseSeparationFt(double? calibratedFt, double? authoredFt, RunwayStandoffInputs inputs)
    {
        double ruleFt = RuleFt(inputs.WidthFt, inputs.LengthFt, inputs.IlsCategory, inputs.ElevationFt);
        if (calibratedFt is { } calibrated)
        {
            return new HoldShortStandoffResult(calibrated, HoldShortStandoffSource.Calibrated, ruleFt);
        }

        if (authoredFt is not { } authored)
        {
            return new HoldShortStandoffResult(ruleFt, HoldShortStandoffSource.Rule, ruleFt);
        }

        return (ruleFt >= ClassFloorFt) && (authored < ruleFt - AuthoredToleranceFt)
            ? new HoldShortStandoffResult(ruleFt, HoldShortStandoffSource.RuleOverAuthored, ruleFt)
            : new HoldShortStandoffResult(authored, HoldShortStandoffSource.Authored, ruleFt);
    }

    /// <summary>
    /// A runway's paint-calibrated separation from the near ends of its paint-paired bars (each bar's drawn end nearer the
    /// runway centerline, feet from it): their median, the mean of the middle two for an even count, rounded to 0.1 ft. Null
    /// when the runway has fewer than <see cref="MinCalibrationBars"/> paired bars. No elevation term: the paint is measured.
    /// </summary>
    /// <param name="nearEndsFt">The near-end cross-track distance of each paired bar, feet.</param>
    /// <returns>The calibrated separation, feet, or null.</returns>
    public static double? CalibratedFt(IReadOnlyCollection<double> nearEndsFt)
    {
        if (nearEndsFt.Count < MinCalibrationBars)
        {
            return null;
        }

        double[] sorted = [.. nearEndsFt.Order()];
        int middle = sorted.Length / 2;
        double median = (sorted.Length % 2 == 1) ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
        return Math.Round(median, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The taxiway width that stands in for the crossing taxiway's own, from the width of the runway it crosses (the layout
    /// carries no taxiway width): 75 ft from a 150 ft runway up, 50 ft from 100 ft, 35 ft from 75 ft, else 25 ft.
    /// </summary>
    /// <param name="runwayWidthFt">The crossed runway's width.</param>
    /// <returns>The taxiway width proxy, feet.</returns>
    public static double TaxiwayWidthProxyFt(double runwayWidthFt) =>
        runwayWidthFt switch
        {
            >= 150.0 => 75.0,
            >= 100.0 => 50.0,
            >= 75.0 => 35.0,
            _ => 25.0,
        };

    /// <summary>
    /// Where a taxiway's centerline meets the hold line, feet from the runway centerline:
    /// <c>D = S + (W_twy / 2) · |cos θ|</c>. The hold line is painted perpendicular to the taxiway centerline with its
    /// nearest point to the runway at <paramref name="baseFt"/>, so on a taxiway at an angle θ to the runway its centerline
    /// point sits farther out; a perpendicular taxiway adds nothing.
    /// </summary>
    /// <param name="baseFt">The base separation S, feet from the runway centerline to the nearest point of the hold line.</param>
    /// <param name="runwayWidthFt">The crossed runway's width, which picks the taxiway width proxy (<see cref="TaxiwayWidthProxyFt"/>).</param>
    /// <param name="angleFromRunwayDeg">The angle θ between the taxiway centerline and the runway centerline, degrees.</param>
    /// <returns>The centerline standoff D, feet.</returns>
    public static double CenterlineStandoffFt(double baseFt, double runwayWidthFt, double angleFromRunwayDeg) =>
        baseFt + (TaxiwayWidthProxyFt(runwayWidthFt) / 2.0 * Math.Abs(Math.Cos(angleFromRunwayDeg * Math.PI / 180.0)));

    /// <summary>
    /// The hash of the runway inputs the rule reads for every navdata runway of <paramref name="faa"/> (width, length,
    /// highest ILS category and higher threshold elevation, <see cref="RunwayStandoffInputs.Of"/>), in runway order: a
    /// lowercase SHA-256 hex string, empty when <paramref name="db"/> is null or knows no runway at the airport. Keys the
    /// precompute entry (<see cref="Precompute.PrecomputeKey.StandoffInputHash"/>), so a navdata or CIFP change that moves
    /// S stales it.
    /// </summary>
    /// <param name="db">The navigation database the layout parse reads, or null when none is loaded.</param>
    /// <param name="faa">The airport, FAA id.</param>
    /// <returns>The hash.</returns>
    public static string InputHash(NavigationDatabase? db, string faa)
    {
        if (db is null)
        {
            return string.Empty;
        }

        IReadOnlyList<RunwayInfo> runways = db.GetRunways(faa);
        if (runways.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (RunwayInfo runway in runways.OrderBy(r => $"{r.Id.End1}/{r.Id.End2}", StringComparer.Ordinal))
        {
            var inputs = RunwayStandoffInputs.Of(db, faa, runway);
            text.Append(CultureInfo.InvariantCulture, $"{runway.Id.End1}/{runway.Id.End2}:{inputs.WidthFt:R}:{inputs.LengthFt:R}:");
            text.Append(CultureInfo.InvariantCulture, $"{inputs.IlsCategory}:{inputs.ElevationFt:R}\n");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Logs a data warning naming the airport, the runway and both values when <paramref name="result"/> overrode the
    /// authored value with the rule; logs nothing otherwise.
    /// </summary>
    /// <param name="airportId">The airport, for the warning.</param>
    /// <param name="runwayName">The runway, for the warning.</param>
    /// <param name="authoredFt">The authored value <paramref name="result"/> was resolved with.</param>
    /// <param name="result">The resolved separation.</param>
    public static void WarnWhenRuleOverAuthored(string airportId, string runwayName, double? authoredFt, HoldShortStandoffResult result)
    {
        if (result.Source != HoldShortStandoffSource.RuleOverAuthored)
        {
            return;
        }

        Log.LogWarning(
            "{Airport} runway {Runway}: authored holdShortDistance {AuthoredFt} ft is more than {ToleranceFt} ft below the rule value "
                + "{RuleFt} ft; using the rule value",
            airportId,
            runwayName,
            authoredFt,
            AuthoredToleranceFt,
            result.RuleFt
        );
    }

    /// <summary>
    /// The rule value: the AC 150/5300-13B runway design standards row the runway's width, length and ILS category select, plus a foot
    /// per 100 ft of elevation from 250 ft up.
    /// </summary>
    private static double RuleFt(double widthFt, double lengthFt, int? ilsCategory, double elevationFt)
    {
        double rowFt = RowFt(widthFt, lengthFt, ilsCategory);
        return rowFt >= ClassFloorFt ? rowFt + (elevationFt / 100.0) : rowFt;
    }

    private static double RowFt(double widthFt, double lengthFt, int? ilsCategory)
    {
        bool anyIls = ilsCategory is not null;
        if ((widthFt >= 200.0) || ((widthFt >= 150.0) && (ilsCategory >= 2)))
        {
            return 280.0;
        }

        if (widthFt >= 100.0)
        {
            return ((lengthFt >= LongRunwayFt) || anyIls) ? 250.0 : 200.0;
        }

        if (widthFt >= 75.0)
        {
            return anyIls ? 175.0 : 150.0;
        }

        return anyIls ? 175.0 : 125.0;
    }
}
