using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Tests.Data;

/// <summary>
/// <see cref="AircraftFootprint.FromType"/> holds, for a type, exactly what the tug planner's per-type lookups returned:
/// the FAA record's figures and the planner's fallbacks for a type the record lacks or has no figure for.
/// </summary>
public class AircraftFootprintTests
{
    [Theory]
    [InlineData("B738", AircraftCategory.Jet)]
    [InlineData("C172", AircraftCategory.Piston)]
    [InlineData("DH8D", AircraftCategory.Turboprop)]
    [InlineData("A388", AircraftCategory.Jet)]
    [InlineData("R44", AircraftCategory.Helicopter)]
    [InlineData("ZZZZ", AircraftCategory.Jet)]
    public void FromType_MatchesTheLookupsItReplaces(string type, AircraftCategory category)
    {
        TestVnasData.EnsureInitialized();
        FaaAircraftRecord? record = FaaAircraftDatabase.Get(type);
        Assert.Equal(category, AircraftCategorization.Categorize(type));

        var footprint = AircraftFootprint.FromType(type);

        Assert.Equal(type, footprint.TypeCode);
        Assert.Equal(category, footprint.Category);
        Assert.Equal(AircraftLength.ResolveFt(type), footprint.LengthFt);
        Assert.Equal(ExpectedWingspanFt(record, category), footprint.WingspanFt);
        Assert.Equal(AircraftFootprint.ResolveWingspanFt(type), footprint.WingspanFt);
        Assert.Equal(record?.WheelbaseFt, footprint.WheelbaseFt);
        Assert.Equal(ExpectedRoutineTurnRadiusFt(record, category), TugKinematics.TurnRadiusFt(footprint, tight: false));
        Assert.Equal(CategoryPerformance.SimplePushbackDistanceNm(type) * GeoMath.FeetPerNm, TugMovePlanner.SimplePushbackFt(footprint));
    }

    [Theory]
    [InlineData(AircraftCategory.Jet, 118.0)]
    [InlineData(AircraftCategory.Turboprop, 90.0)]
    [InlineData(AircraftCategory.Piston, 36.0)]
    [InlineData(AircraftCategory.Helicopter, 40.0)]
    public void WingspanOf_NoRecord_FallsBackByCategory(AircraftCategory category, double expectedFt) =>
        Assert.Equal(expectedFt, AircraftFootprint.WingspanOf(null, category));

    /// <summary>The FAA record's wingspan, else the planner's category fallback.</summary>
    private static double ExpectedWingspanFt(FaaAircraftRecord? record, AircraftCategory category) =>
        (record?.WingspanFt is { } span && (span > 0.0))
            ? span
            : category switch
            {
                AircraftCategory.Jet => 118.0,
                AircraftCategory.Turboprop => 90.0,
                AircraftCategory.Piston => 36.0,
                _ => 40.0,
            };

    /// <summary>The FAA record's wheelbase, else the planner's category fallback radius, never below 1 ft.</summary>
    private static double ExpectedRoutineTurnRadiusFt(FaaAircraftRecord? record, AircraftCategory category) =>
        Math.Max(
            1.0,
            (record?.WheelbaseFt is { } wheelbase && (wheelbase > 0.0))
                ? wheelbase
                : category switch
                {
                    AircraftCategory.Jet => 50.0,
                    AircraftCategory.Turboprop => 30.0,
                    AircraftCategory.Piston => 7.0,
                    _ => 10.0,
                }
        );
}
