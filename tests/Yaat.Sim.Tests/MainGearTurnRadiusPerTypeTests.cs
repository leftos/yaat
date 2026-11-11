using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory, string?)"/> over the real FAA aircraft characteristics
/// database: the category's comfortable radius raised to the type's steering-limit radius (0.466 × wheelbase, the 65°
/// nose-wheel limit), never lowered, and the category value for a type with no usable wheelbase.
/// </summary>
public class MainGearTurnRadiusPerTypeTests
{
    public MainGearTurnRadiusPerTypeTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void A388_RaisedToItsSteeringLimitRadius()
    {
        double wheelbaseFt = WheelbaseOf("A388");

        double radiusFt = CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet, "A388");

        Assert.Equal(TurnAboutFit.SteeringLimitRadiusPerWheelbaseFt * wheelbaseFt, radiusFt, 1e-9);
        Assert.True(radiusFt > 45.0, $"A388 radius {radiusFt:F1} ft");
    }

    [Fact]
    public void B77W_RaisedAboveTheJetValue()
    {
        double radiusFt = CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet, "B77W");

        Assert.True(radiusFt > 45.0, $"B77W radius {radiusFt:F1} ft");
    }

    [Fact]
    public void B738_KeepsTheJetValue() => Assert.Equal(25.0, CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet, "B738"));

    [Fact]
    public void C172_KeepsThePistonValue() => Assert.Equal(15.0, CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Piston, "C172"));

    [Theory]
    [InlineData(null)]
    [InlineData("ZZZZ")]
    public void NoRecord_KeepsTheCategoryValue(string? aircraftType)
    {
        Assert.Null(FaaAircraftDatabase.Get(aircraftType));

        Assert.Equal(
            CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet),
            CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet, aircraftType)
        );
    }

    /// <summary>
    /// A non-jet whose ACD wheelbase is at least <see cref="TurnAboutFit.BrokenRowWheelbaseRatio"/> of its length has a broken
    /// row: its wheelbase is not used, so it keeps its category's value rather than a radius raised off a bad figure.
    /// </summary>
    [Fact]
    public void NonJetBrokenRow_KeepsTheCategoryValue()
    {
        List<(string Type, AircraftCategory Category)> broken =
        [
            .. FaaAircraftDatabase
                .Records.Where(kv =>
                    (AircraftCategorization.Categorize(kv.Key) != AircraftCategory.Jet)
                    && (kv.Value.WheelbaseFt is > 0 and { } wheelbaseFt)
                    && (kv.Value.LengthFt is > 0 and { } lengthFt)
                    && ((wheelbaseFt / lengthFt) >= TurnAboutFit.BrokenRowWheelbaseRatio)
                )
                .Select(kv => (kv.Key, AircraftCategorization.Categorize(kv.Key))),
        ];

        if (broken.Count == 0)
        {
            // The committed FAA database holds no broken non-jet row, so there is nothing for this case to check.
            Assert.Empty(broken);
            return;
        }

        Assert.All(
            broken,
            b => Assert.Equal(CategoryPerformance.MainGearTurnRadiusFt(b.Category), CategoryPerformance.MainGearTurnRadiusFt(b.Category, b.Type))
        );
    }

    /// <summary>
    /// An A346 (wheelbase ~108 ft) has a main-gear radius just over the 50 ft look-ahead cap; pure pursuit on a straight
    /// segment holds its look-ahead at that radius rather than throwing on an inverted clamp.
    /// </summary>
    [Fact]
    public void A346_PurePursuitOnAStraight_DoesNotThrow()
    {
        Assert.True(CategoryPerformance.MainGearTurnRadiusFt(AircraftCategory.Jet, "A346") > GroundNavigator.LookAheadCapFt);
        var from = new GroundNode
        {
            Id = 1,
            Position = new LatLon(37.0, -122.0),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var to = new GroundNode
        {
            Id = 2,
            Position = GeoMath.ProjectPoint(from.Position, new TrueHeading(90.0), 400.0 / GeoMath.FeetPerNm),
            Type = GroundNodeType.TaxiwayIntersection,
        };
        var edge = new GroundEdge
        {
            Nodes = [from, to],
            TaxiwayName = "A",
            DistanceNm = GeoMath.DistanceNm(from.Position, to.Position),
        };
        var segment = new TaxiRouteSegment
        {
            Edge = new DirectionalEdge
            {
                Edge = edge,
                FromNode = from,
                ToNode = to,
            },
            TaxiwayName = "A",
        };
        var route = new TaxiRoute { Segments = [segment], HoldShortPoints = [] };
        var aircraft = new AircraftState
        {
            Callsign = "NAV",
            AircraftType = "A346",
            Position = from.Position,
            TrueHeading = new TrueHeading(90.0),
            IndicatedAirspeed = 10.0,
            IsOnGround = true,
        };
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.25,
            Runway = null,
            FieldElevation = 0,
            GroundLayout = null,
            Logger = NullLogger.Instance,
        };
        var nav = new GroundNavigator { MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category) };
        nav.SetupSegment(route, ctx, _ => true);

        Exception? thrown = Record.Exception(() =>
        {
            for (int tick = 0; tick < 4; tick++)
            {
                FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
                nav.Tick(ctx, isLastSegment: true, _ => true);
            }
        });

        Assert.Null(thrown);
    }

    /// <summary>
    /// Every FAA type's look-ahead clamp is ordered: from no reach to a reach past the cap, the look-ahead never falls
    /// below the type's main-gear radius and never throws.
    /// </summary>
    [Fact]
    public void EveryType_LookAheadBoundsAreOrdered()
    {
        Assert.All(
            FaaAircraftDatabase.Records.Keys,
            type =>
            {
                AircraftCategory category = AircraftCategorization.Categorize(type);
                double floorFt = CategoryPerformance.MainGearTurnRadiusFt(category, type);
                double shortFt = GroundNavigator.LookAheadFt(category, type, 0.0);
                double longFt = GroundNavigator.LookAheadFt(category, type, 1000.0);
                Assert.Equal(floorFt, shortFt);
                Assert.Equal(Math.Max(floorFt, GroundNavigator.LookAheadCapFt), longFt);
            }
        );
    }

    /// <summary>
    /// <see cref="TurnAboutFit.IsBrokenRow"/> on real rows: the AT5T's ACD wheelbase is longer than the aircraft (broken), a
    /// C208's is a normal tricycle wheelbase (usable).
    /// </summary>
    [Theory]
    [InlineData("AT5T", true)]
    [InlineData("C208", false)]
    public void IsBrokenRow_RealNonJetRows(string type, bool expectedBroken)
    {
        FaaAircraftRecord record = FaaAircraftDatabase.Get(type) ?? throw new InvalidOperationException($"the FAA database has no {type}");
        AircraftCategory category = AircraftCategorization.Categorize(type);
        Assert.NotEqual(AircraftCategory.Jet, category);

        Assert.Equal(expectedBroken, TurnAboutFit.IsBrokenRow(record, category, WheelbaseOf(type)));
    }

    private static double WheelbaseOf(string type) =>
        FaaAircraftDatabase.Get(type)?.WheelbaseFt ?? throw new InvalidOperationException($"the FAA database has no {type} wheelbase");
}
