using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// The dimensions a tug move is planned with: fuselage length, wingspan, wheelbase, and the category the planner's
/// fallbacks key on. <see cref="FromType"/> reads them for an ICAO type from the FAA record, with the fallbacks the tug
/// planner applies when the record lacks a figure.
/// </summary>
public sealed record AircraftFootprint
{
    /// <summary>
    /// The ICAO type designator the dimensions describe. It is part of the record's equality, which the row-clearance
    /// cache (<see cref="TugRowClearances"/>) compares footprints by.
    /// </summary>
    public required string TypeCode { get; init; }

    /// <summary>
    /// Fuselage length, feet: the FAA record's, else the CWT-bucket estimate (<see cref="AircraftLength.ResolveFt"/>).
    /// </summary>
    public required double LengthFt { get; init; }

    /// <summary>Wingspan, feet: the FAA record's, else by category (<see cref="ResolveWingspanFt"/>).</summary>
    public required double WingspanFt { get; init; }

    /// <summary>
    /// Wheelbase, feet, from the FAA record; null when the record has none. <see cref="TugKinematics.TurnRadiusFt"/> falls
    /// back by <see cref="Category"/> when it is null or not positive.
    /// </summary>
    public required double? WheelbaseFt { get; init; }

    /// <summary>The performance category; keys the wingspan and turn-radius fallbacks.</summary>
    public required AircraftCategory Category { get; init; }

    /// <summary>The footprint of an ICAO type, from its FAA record and the tug planner's fallbacks.</summary>
    /// <param name="aircraftType">ICAO type designator, prefixes and suffixes allowed.</param>
    /// <returns>The footprint.</returns>
    public static AircraftFootprint FromType(string aircraftType)
    {
        FaaAircraftRecord? record = FaaAircraftDatabase.Get(aircraftType);
        AircraftCategory category = AircraftCategorization.Categorize(aircraftType);
        return new AircraftFootprint
        {
            TypeCode = aircraftType,
            LengthFt = record?.LengthFt ?? AircraftLength.CwtFallbackLengthFt(aircraftType),
            WingspanFt = WingspanOf(record, category),
            WheelbaseFt = record?.WheelbaseFt,
            Category = category,
        };
    }

    /// <summary>The wingspan of an ICAO type, feet (<see cref="WingspanOf"/>).</summary>
    /// <param name="aircraftType">ICAO type designator, prefixes and suffixes allowed.</param>
    /// <returns>The wingspan, feet.</returns>
    public static double ResolveWingspanFt(string aircraftType) =>
        WingspanOf(FaaAircraftDatabase.Get(aircraftType), AircraftCategorization.Categorize(aircraftType));

    /// <summary>
    /// The wingspan, feet: the FAA record's when it has a positive one, else by category — Jet 118 ft, Turboprop 90 ft,
    /// Piston 36 ft, Helicopter 40 ft. The fallbacks are judgement calls.
    /// </summary>
    /// <param name="record">The type's FAA record, or null when the database has none.</param>
    /// <param name="category">The type's performance category.</param>
    /// <returns>The wingspan, feet.</returns>
    internal static double WingspanOf(FaaAircraftRecord? record, AircraftCategory category) =>
        (record?.WingspanFt is { } span && (span > 0.0)) ? span : FallbackWingspanFt(category);

    private static double FallbackWingspanFt(AircraftCategory category) =>
        category switch
        {
            AircraftCategory.Jet => 118.0,
            AircraftCategory.Turboprop => 90.0,
            AircraftCategory.Piston => 36.0,
            AircraftCategory.Helicopter => 40.0,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown aircraft category"),
        };
}
