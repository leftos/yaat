using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests InitialClimbPhase altitude resolution priority:
/// 1. Explicit AssignedAltitude
/// 2. ClosedTrafficDeparture → pattern altitude
/// 3. VFR + CruiseAltitude → cruise
/// 4. VFR no cruise → pattern altitude
/// 5. IFR → 1500 AGL
/// </summary>
public class InitialClimbAltitudeTests
{
    private const double FieldElevation = 100;

    private static RunwayInfo MakeRunway()
    {
        return new RunwayInfo
        {
            AirportId = "KSFO",
            RunwayId = "28",
            TrueHeading = 280,
            LengthFt = 10000,
            WidthFt = 150,
            ThresholdLatitude = 37.0,
            ThresholdLongitude = -122.0,
            EndLatitude = 37.01,
            EndLongitude = -122.01,
            ElevationFt = FieldElevation,
        };
    }

    private static double RunResolve(
        DepartureInstruction departure,
        int? assignedAltitude,
        bool isVfr,
        int cruiseAltitude,
        AircraftCategory category = AircraftCategory.Jet)
    {
        var phase = new InitialClimbPhase
        {
            Departure = departure,
            AssignedAltitude = assignedAltitude,
            IsVfr = isVfr,
            CruiseAltitude = cruiseAltitude,
        };

        var runway = MakeRunway();
        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Heading = 280,
            Altitude = FieldElevation + 400,
            GroundSpeed = 180,
            Phases = phaseList,
        };
        var targets = aircraft.Targets;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = category,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        phase.OnStart(ctx);

        Assert.NotNull(targets.TargetAltitude);
        return targets.TargetAltitude.Value;
    }

    [Fact]
    public void ExplicitAltitude_UsedRegardlessOfFlightRules()
    {
        double alt = RunResolve(new DefaultDeparture(), 5000, isVfr: true, cruiseAltitude: 0);
        Assert.Equal(5000, alt);
    }

    [Fact]
    public void ExplicitAltitude_OverridesClosedTrafficDefault()
    {
        double alt = RunResolve(
            new ClosedTrafficDeparture(PatternDirection.Right), 3000, isVfr: true, cruiseAltitude: 0);
        Assert.Equal(3000, alt);
    }

    [Fact]
    public void ClosedTraffic_NoAlt_UsesPatternAltitude_Jet()
    {
        // Jet pattern alt = 1500 AGL → 1500 + 100 = 1600
        double alt = RunResolve(
            new ClosedTrafficDeparture(PatternDirection.Left), null, isVfr: true, cruiseAltitude: 0,
            AircraftCategory.Jet);
        Assert.Equal(FieldElevation + 1500, alt);
    }

    [Fact]
    public void ClosedTraffic_NoAlt_UsesPatternAltitude_Piston()
    {
        // Piston pattern alt = 1000 AGL → 1000 + 100 = 1100
        double alt = RunResolve(
            new ClosedTrafficDeparture(PatternDirection.Right), null, isVfr: true, cruiseAltitude: 0,
            AircraftCategory.Piston);
        Assert.Equal(FieldElevation + 1000, alt);
    }

    [Fact]
    public void Vfr_WithCruiseAltitude_UsesCruise()
    {
        double alt = RunResolve(new DefaultDeparture(), null, isVfr: true, cruiseAltitude: 4500);
        Assert.Equal(4500, alt);
    }

    [Fact]
    public void Vfr_NoCruise_UsesPatternAltitude_Jet()
    {
        double alt = RunResolve(
            new DefaultDeparture(), null, isVfr: true, cruiseAltitude: 0, AircraftCategory.Jet);
        Assert.Equal(FieldElevation + 1500, alt);
    }

    [Fact]
    public void Vfr_NoCruise_UsesPatternAltitude_Turboprop()
    {
        double alt = RunResolve(
            new DefaultDeparture(), null, isVfr: true, cruiseAltitude: 0, AircraftCategory.Turboprop);
        Assert.Equal(FieldElevation + 1000, alt);
    }

    [Fact]
    public void Ifr_NoAlt_SelfClear1500Agl()
    {
        double alt = RunResolve(new DefaultDeparture(), null, isVfr: false, cruiseAltitude: 35000);
        Assert.Equal(FieldElevation + 1500, alt);
    }

    [Fact]
    public void Ifr_WithAlt_UsesExplicit()
    {
        double alt = RunResolve(new DefaultDeparture(), 10000, isVfr: false, cruiseAltitude: 35000);
        Assert.Equal(10000, alt);
    }

    [Fact]
    public void Ifr_RunwayHeading_NoAlt_SelfClear()
    {
        double alt = RunResolve(new RunwayHeadingDeparture(), null, isVfr: false, cruiseAltitude: 0);
        Assert.Equal(FieldElevation + 1500, alt);
    }

    [Fact]
    public void NavigationRoute_AppliedOnStart()
    {
        var route = new List<NavigationTarget>
        {
            new() { Name = "SUNOL", Latitude = 37.5, Longitude = -121.8 },
            new() { Name = "TRACY", Latitude = 37.7, Longitude = -121.4 },
        };

        var phase = new InitialClimbPhase
        {
            Departure = new DefaultDeparture(),
            DepartureRoute = route,
            IsVfr = false,
            CruiseAltitude = 0,
        };

        var runway = MakeRunway();
        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Heading = 280,
            Altitude = FieldElevation + 400,
            GroundSpeed = 180,
            Phases = phaseList,
        };
        var targets = aircraft.Targets;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        phase.OnStart(ctx);

        Assert.Equal(2, targets.NavigationRoute.Count);
        Assert.Equal("SUNOL", targets.NavigationRoute[0].Name);
        Assert.Equal(37.5, targets.NavigationRoute[0].Latitude, 1);
        Assert.Equal("TRACY", targets.NavigationRoute[1].Name);
    }

    [Fact]
    public void NoNavigationRoute_LeavesEmpty()
    {
        var phase = new InitialClimbPhase
        {
            Departure = new RunwayHeadingDeparture(),
            IsVfr = true,
            CruiseAltitude = 0,
        };

        var runway = MakeRunway();
        var phaseList = new PhaseList { AssignedRunway = runway };
        var aircraft = new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Heading = 280,
            Altitude = FieldElevation + 400,
            GroundSpeed = 180,
            Phases = phaseList,
        };
        var targets = aircraft.Targets;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        phase.OnStart(ctx);

        Assert.Empty(targets.NavigationRoute);
    }
}
