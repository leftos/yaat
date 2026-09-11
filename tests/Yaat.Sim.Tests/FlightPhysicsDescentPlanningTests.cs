using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Tests;

/// <summary>
/// The crossing-restriction planner's vertical rate is transient: it is recomputed every tick from the
/// next constrained fix in the route, so a vector that drops the route releases it and the aircraft
/// returns to its category/profile descent rate (GitHub issue #429).
/// </summary>
public sealed class FlightPhysicsDescentPlanningTests
{
    private const string FixName = "SANGO";
    private const string AircraftType = "A319";
    private const double CruiseAltitudeFt = 22000;
    private const double CrossingAltitudeFt = 11000;
    private const double StartDistanceNm = 70;

    public FlightPhysicsDescentPlanningTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// An A319 at 22,000 ft, 70 nm from SANGO with "cross SANGO at 11,000" on the route — the shape
    /// UAL1486 was in when the instructor vectored it off the restriction.
    /// </summary>
    private static AircraftState MakeArrivalCrossingFix()
    {
        var fix = NavigationDatabase.Instance.GetFixPosition(FixName);
        Assert.NotNull(fix);

        var fixPosition = new LatLon(fix.Value.Lat, fix.Value.Lon);
        var start = GeoMath.ProjectPoint(fixPosition, new TrueHeading(20), StartDistanceNm);
        var inbound = new TrueHeading(GeoMath.BearingTo(start, fixPosition));

        var aircraft = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = AircraftType,
            Position = start,
            TrueHeading = inbound,
            TrueTrack = inbound,
            Altitude = CruiseAltitudeFt,
            IndicatedAirspeed = 290,
            IsOnGround = false,
        };

        aircraft.Targets.TargetTrueHeading = inbound;
        aircraft.Targets.TargetAltitude = CrossingAltitudeFt;
        aircraft.Targets.AssignedAltitude = CrossingAltitudeFt;
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = FixName,
                Position = fixPosition,
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, (int)CrossingAltitudeFt),
            }
        );

        return aircraft;
    }

    /// <summary>What FH 225 does to the targets: the route goes away, a vector heading replaces it.</summary>
    private static void VectorOffTheRoute(AircraftState aircraft)
    {
        var vector = new MagneticHeading(225);
        aircraft.Targets.NavigationRoute.Clear();
        aircraft.Targets.TargetTrueHeading = vector.ToTrue(aircraft.Declination);
        aircraft.Targets.AssignedMagneticHeading = vector;
    }

    private static void Tick(AircraftState aircraft, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            FlightPhysics.Update(aircraft, 1.0);
        }
    }

    private static double ProfileDescentRate(double altitudeFt) => AircraftPerformance.DescentRate(AircraftType, AircraftCategory.Jet, altitudeFt);

    [Fact]
    public void CrossingRestrictionFarOut_DescendsAtTheComputedGentleRate()
    {
        var aircraft = MakeArrivalCrossingFix();

        Tick(aircraft, 5);

        double profileRate = ProfileDescentRate(aircraft.Altitude);
        Assert.NotNull(aircraft.Targets.PlannedVerticalRate);
        Assert.True(aircraft.Targets.PlannedVerticalRate < 0, $"planner rate {aircraft.Targets.PlannedVerticalRate} should be a descent");
        Assert.True(
            Math.Abs(aircraft.VerticalSpeed) < profileRate * 0.6,
            $"expected a gentle planned descent well below the {profileRate:F0} fpm profile rate, got {aircraft.VerticalSpeed:F0} fpm"
        );
    }

    [Fact]
    public void VectorThatDropsTheRoute_ReturnsToTheProfileDescentRate()
    {
        var aircraft = MakeArrivalCrossingFix();

        Tick(aircraft, 5);
        VectorOffTheRoute(aircraft);
        Tick(aircraft, 4);

        double altitudeBeforeTick = aircraft.Altitude;
        Tick(aircraft, 1);

        Assert.Null(aircraft.Targets.PlannedVerticalRate);
        Assert.True(aircraft.Altitude > CrossingAltitudeFt + 1000, $"the level-off taper must not be in play: alt {aircraft.Altitude:F0}");
        Assert.Equal(ProfileDescentRate(altitudeBeforeTick), Math.Abs(aircraft.VerticalSpeed), 6);
    }

    [Fact]
    public void ExpediteAfterTheVector_ScalesTheProfileRate()
    {
        var aircraft = MakeArrivalCrossingFix();

        Tick(aircraft, 5);
        VectorOffTheRoute(aircraft);
        Tick(aircraft, 4);

        aircraft.Procedure.IsExpediting = true;
        double altitudeBeforeTick = aircraft.Altitude;
        Tick(aircraft, 1);

        double profileRate = ProfileDescentRate(altitudeBeforeTick);
        Assert.True(
            Math.Abs(aircraft.VerticalSpeed) > profileRate,
            $"expedite must scale the profile rate: {aircraft.VerticalSpeed:F0} fpm vs profile {profileRate:F0} fpm"
        );
        Assert.Equal(
            CategoryPerformance.ExpediteVerticalRate(AircraftCategory.Jet, profileRate, climbing: false),
            Math.Abs(aircraft.VerticalSpeed),
            6
        );
    }

    [Fact]
    public void PhaseCommandedRate_StillWinsOverThePlanner()
    {
        var aircraft = MakeArrivalCrossingFix();
        aircraft.Targets.DesiredVerticalRate = -500;

        Tick(aircraft, 3);

        Assert.Equal(500, Math.Abs(aircraft.VerticalSpeed), 6);
    }
}
