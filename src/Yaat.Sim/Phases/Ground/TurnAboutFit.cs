using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Whether a type has room to turn about (a centred 180) on a taxiway of its own taxiway design group, and the pivot radius
/// it turns about on. The taxi gate refuses or keeps the route ahead for a type that does not fit, and the navigator draws
/// the turn about at the same radius and checks its pavement against the same half-width: one geometry for both.
/// </summary>
public static class TurnAboutFit
{
    private static readonly ILogger Log = SimLog.CreateLogger("TurnAboutFit");

    /// <summary>The types whose unrecognised TDG has already been logged, so each is warned about once per process.</summary>
    private static readonly ConcurrentDictionary<string, byte> WarnedTdgTypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The broken-row types already warned about, so each is warned about once per process.</summary>
    private static readonly ConcurrentDictionary<string, byte> WarnedBrokenRowTypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Half the 25 ft width of a TDG 1A/1B taxiway (AC 150/5300-13B Table 4-2), the narrowest; used when the type is unknown.</summary>
    public const double NarrowestHalfWidthFt = 12.5;

    /// <summary>
    /// The pivot radius per foot of wheelbase at the 65° nose-wheel steering limit (tan 25°), the turn centre to the
    /// main-gear axle midpoint.
    /// </summary>
    public const double SteeringLimitRadiusPerWheelbaseFt = 0.466;

    /// <summary>
    /// The wheelbase-to-length ratio at or above which a non-jet type counts as a taildragger, pivoting on a braked main
    /// wheel rather than steering with a nose gear. Measured over all 388 FAA ACD rows: the highest tricycle non-jet is
    /// 0.46 and the lowest taildragger 0.50. Jets are excluded — B2, LJ35 and the MD80/90 exceed it.
    /// </summary>
    public const double TaildraggerWheelbaseRatio = 0.48;

    /// <summary>
    /// The wheelbase-to-length ratio at or above which a non-jet type's ACD wheelbase is unusable (the row is broken):
    /// it is at least as long as the aircraft, so the type is judged by category instead. Measured over the same 388
    /// FAA ACD rows.
    /// </summary>
    public const double BrokenRowWheelbaseRatio = 0.9;

    /// <summary>
    /// The fit of <paramref name="aircraftType"/> (looked up in <see cref="FaaAircraftDatabase"/>) in
    /// <paramref name="category"/>; see <see cref="EvaluateRecord"/>.
    /// </summary>
    public static TurnAboutFitResult Evaluate(string? aircraftType, AircraftCategory category) =>
        EvaluateRecord(FaaAircraftDatabase.Get(aircraftType), category);

    /// <summary>
    /// The fit of the type <paramref name="record"/> describes, in <paramref name="category"/>.
    ///
    /// <para>
    /// The half-width is the type's TDG pavement half-width (AC 150/5300-13B Table 4-2). A non-jet with the main-gear
    /// width (MGW), wheelbase (WB) and length all known is judged by one of three rules:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// A wheelbase of at least <see cref="BrokenRowWheelbaseRatio"/> of the length is unusable (the ACD row is broken),
    /// and the type falls back to the category rule: a piston fits, a turboprop refuses.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// A wheelbase of at least <see cref="TaildraggerWheelbaseRatio"/> of the length is a taildragger, which pivots on a
    /// braked main wheel: its pivot radius is R = MGW/2, its tail-swing radius is rₜ = √(WB² + (MGW/2)²), and it fits
    /// when rₜ + MGW stays within twice the half-width — the wheels' lateral span once the pivot wheel is moved
    /// (rₜ − MGW)/2 off the centreline to use the full width; the turn is drawn centred, so the drawn tail swing crosses
    /// the edge by rₜ − half-width (a documented simplification).
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// Otherwise (and for every jet, and any record missing its length) the tricycle rule: R = max(MGW/2, 0.466·WB), and
    /// the type fits when both its outer main tyre (R + MGW/2) and its nose gear (√(R² + WB²)) stay within the half-width,
    /// whatever its TDG.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// With either gear figure missing, only a TDG 1A type that is not a jet fits; a jet is drawn at its category's
    /// tight-turn radius and every other type at the piston's. A type with no record, or with a TDG this does not
    /// recognise, fits only as a piston or helicopter, at the category's tight-turn radius against the narrowest
    /// half-width.
    /// </para>
    /// </summary>
    public static TurnAboutFitResult EvaluateRecord(FaaAircraftRecord? record, AircraftCategory category)
    {
        if (record is null)
        {
            return ByCategory(category);
        }

        if (TdgHalfWidthFt(record.Tdg) is not { } halfWidthFt)
        {
            WarnUnrecognisedTdg(record);
            return ByCategory(category);
        }

        if ((record.MainGearWidthFt is not { } gearWidthFt) || (record.WheelbaseFt is not { } wheelbaseFt))
        {
            return ByMissingGear(record, category, halfWidthFt);
        }

        return ByWheelbaseRatio(record, category, gearWidthFt, wheelbaseFt, halfWidthFt) ?? ByGear(gearWidthFt, wheelbaseFt, halfWidthFt);
    }

    /// <summary>
    /// The fit of a type whose ACD row lacks a gear figure: only a TDG 1A type that is not a jet fits, at its category's
    /// tight-turn radius (a jet's own, everything else the piston's) against the record's half-width.
    /// </summary>
    private static TurnAboutFitResult ByMissingGear(FaaAircraftRecord record, AircraftCategory category, double halfWidthFt)
    {
        bool fits = IsTdg1A(record.Tdg) && (category != AircraftCategory.Jet);
        AircraftCategory radiusCategory = (category == AircraftCategory.Jet) ? AircraftCategory.Jet : AircraftCategory.Piston;
        return new TurnAboutFitResult(fits, CategoryPerformance.TightTurnFloorRadiusFt(radiusCategory), halfWidthFt);
    }

    /// <summary>
    /// The fit of a non-jet with a known wheelbase judged by its wheelbase-to-length ratio: a broken row by category, a
    /// taildragger by <see cref="ByTaildragger"/>. Null when the tricycle rule applies — a jet, a record with no length,
    /// or a ratio below <see cref="TaildraggerWheelbaseRatio"/>.
    /// </summary>
    private static TurnAboutFitResult? ByWheelbaseRatio(
        FaaAircraftRecord record,
        AircraftCategory category,
        double gearWidthFt,
        double wheelbaseFt,
        double halfWidthFt
    )
    {
        if ((category == AircraftCategory.Jet) || (record.LengthFt is not { } lengthFt))
        {
            return null;
        }

        double wheelbaseToLength = wheelbaseFt / lengthFt;
        if (wheelbaseToLength >= BrokenRowWheelbaseRatio)
        {
            WarnBrokenRow(record);
            return ByCategory(category);
        }

        if (wheelbaseToLength >= TaildraggerWheelbaseRatio)
        {
            return ByTaildragger(gearWidthFt, wheelbaseFt, halfWidthFt);
        }

        return null;
    }

    /// <summary>
    /// The fit of a taildragger pivoting on a braked main wheel: radius <paramref name="gearWidthFt"/>/2, tail swing
    /// √(<paramref name="wheelbaseFt"/>² + radius²), and it fits when the swing plus the gear width stays within twice
    /// the half-width — the wheels' lateral span once the pivot wheel is moved (rₜ − MGW)/2 off the centreline to use
    /// the full width; the turn is drawn centred, so the drawn tail swing crosses the edge by rₜ − half-width (a
    /// documented simplification).
    /// </summary>
    private static TurnAboutFitResult ByTaildragger(double gearWidthFt, double wheelbaseFt, double halfWidthFt)
    {
        double radiusFt = gearWidthFt / 2.0;
        double tailSwingFt = Math.Sqrt((wheelbaseFt * wheelbaseFt) + (radiusFt * radiusFt));
        return new TurnAboutFitResult((tailSwingFt + gearWidthFt) <= (2.0 * halfWidthFt), radiusFt, halfWidthFt);
    }

    /// <summary>
    /// The fit of a type with a main gear <paramref name="gearWidthFt"/> wide and <paramref name="wheelbaseFt"/> of wheelbase.
    /// </summary>
    private static TurnAboutFitResult ByGear(double gearWidthFt, double wheelbaseFt, double halfWidthFt)
    {
        double radiusFt = Math.Max(gearWidthFt / 2.0, SteeringLimitRadiusPerWheelbaseFt * wheelbaseFt);
        double outerTyreFt = radiusFt + (gearWidthFt / 2.0);
        double noseFt = Math.Sqrt((radiusFt * radiusFt) + (wheelbaseFt * wheelbaseFt));
        return new TurnAboutFitResult(Math.Max(outerTyreFt, noseFt) <= halfWidthFt, radiusFt, halfWidthFt);
    }

    /// <summary>The fit of a type the database does not describe usably: pistons and helicopters fit, jets and turboprops do not.</summary>
    private static TurnAboutFitResult ByCategory(AircraftCategory category) =>
        new(
            (category == AircraftCategory.Piston) || (category == AircraftCategory.Helicopter),
            CategoryPerformance.TightTurnFloorRadiusFt(category),
            NarrowestHalfWidthFt
        );

    /// <summary>
    /// Half the taxiway width (ft) AC 150/5300-13B Table 4-2 gives taxiway design group <paramref name="tdg"/>; null when
    /// unrecognised.
    /// </summary>
    private static double? TdgHalfWidthFt(string? tdg) =>
        tdg?.Trim().ToUpperInvariant() switch
        {
            "1A" or "1B" => 12.5,
            "2A" or "2B" => 17.5,
            "3" or "4" => 25.0,
            "5" or "6" => 37.5,
            _ => null,
        };

    private static bool IsTdg1A(string? tdg) => string.Equals(tdg?.Trim(), "1A", StringComparison.OrdinalIgnoreCase);

    private static void WarnUnrecognisedTdg(FaaAircraftRecord record)
    {
        if (!WarnedTdgTypes.TryAdd(record.IcaoCode, 0))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(record.Tdg))
        {
            Log.LogWarning(
                "{Type}: has no taxiway design group in the FAA aircraft characteristics database; " + "its turn-about fit is judged by category",
                record.IcaoCode
            );
            return;
        }

        Log.LogWarning(
            "{Type}: unrecognised taxiway design group '{Tdg}' in the FAA aircraft characteristics database; "
                + "its turn-about fit is judged by category",
            record.IcaoCode,
            record.Tdg
        );
    }

    private static void WarnBrokenRow(FaaAircraftRecord record)
    {
        if (!WarnedBrokenRowTypes.TryAdd(record.IcaoCode, 0))
        {
            return;
        }

        Log.LogWarning(
            "{Type}: wheelbase is at least {Ratio} of its length in the FAA aircraft characteristics database; "
                + "its row is unusable and its turn-about fit is judged by category",
            record.IcaoCode,
            BrokenRowWheelbaseRatio
        );
    }
}

/// <summary>A type's turn-about fit (<see cref="TurnAboutFit.Evaluate"/>).</summary>
/// <param name="Fits">Whether the type may turn about on a taxiway of its design group.</param>
/// <param name="RadiusFt">The pivot radius (ft), turn centre to main-gear axle midpoint, its turn about is drawn on.</param>
/// <param name="HalfWidthFt">The taxiway half-width (ft) its turn about's pavement checks are made against.</param>
public readonly record struct TurnAboutFitResult(bool Fits, double RadiusFt, double HalfWidthFt);
