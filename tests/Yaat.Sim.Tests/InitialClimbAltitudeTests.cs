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
        return TestRunwayFactory.Make(designator: "28", airportId: "KSFO", elevationFt: FieldElevation);
    }

    private static double RunResolve(
        DepartureInstruction departure,
        int? assignedAltitude,
        bool isVfr,
        int cruiseAltitude,
        AircraftCategory category = AircraftCategory.Jet
    )
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
        double alt = RunResolve(new ClosedTrafficDeparture(PatternDirection.Right), 3000, isVfr: true, cruiseAltitude: 0);
        Assert.Equal(3000, alt);
    }

    [Fact]
    public void ClosedTraffic_NoAlt_UsesPatternAltitude_Jet()
    {
        // Jet pattern alt = 1500 AGL → 1500 + 100 = 1600
        double alt = RunResolve(new ClosedTrafficDeparture(PatternDirection.Left), null, isVfr: true, cruiseAltitude: 0, AircraftCategory.Jet);
        Assert.Equal(FieldElevation + 1500, alt);
    }

    [Fact]
    public void ClosedTraffic_NoAlt_UsesPatternAltitude_Piston()
    {
        // Piston pattern alt = 1000 AGL → 1000 + 100 = 1100
        double alt = RunResolve(new ClosedTrafficDeparture(PatternDirection.Right), null, isVfr: true, cruiseAltitude: 0, AircraftCategory.Piston);
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
        double alt = RunResolve(new DefaultDeparture(), null, isVfr: true, cruiseAltitude: 0, AircraftCategory.Jet);
        Assert.Equal(FieldElevation + 1500, alt);
    }

    [Fact]
    public void Vfr_NoCruise_UsesPatternAltitude_Turboprop()
    {
        double alt = RunResolve(new DefaultDeparture(), null, isVfr: true, cruiseAltitude: 0, AircraftCategory.Turboprop);
        Assert.Equal(FieldElevation + 1000, alt);
    }

    [Fact]
    public void Ifr_NoAlt_WithCruise_ClimbsToCruise()
    {
        double alt = RunResolve(new DefaultDeparture(), null, isVfr: false, cruiseAltitude: 35000);
        Assert.Equal(35000, alt);
    }

    [Fact]
    public void Ifr_NoAlt_NoCruise_SelfClear1500Agl()
    {
        double alt = RunResolve(new DefaultDeparture(), null, isVfr: false, cruiseAltitude: 0);
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
            new()
            {
                Name = "SUNOL",
                Latitude = 37.5,
                Longitude = -121.8,
            },
            new()
            {
                Name = "TRACY",
                Latitude = 37.7,
                Longitude = -121.4,
            },
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

    // ── OnTick completion tests ──

    private static (InitialClimbPhase phase, AircraftState aircraft, PhaseContext ctx) SetUpPhase(
        DepartureInstruction departure,
        int? assignedAltitude,
        int cruiseAltitude,
        bool isVfr = false
    )
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
            Phases = phaseList,
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = FieldElevation,
            Logger = NullLogger.Instance,
        };

        phase.OnStart(ctx);
        return (phase, aircraft, ctx);
    }

    [Fact]
    public void MR270_NoAltitude_CompletesOnHeadingReached()
    {
        // Runway heading 280 + 270 right = 190 (normalized)
        var (phase, ac, ctx) = SetUpPhase(new RelativeTurnDeparture(270, TurnDirection.Right), assignedAltitude: null, cruiseAltitude: 35000);

        // Aircraft well below cruise, heading not yet reached
        ac.Altitude = 2000;
        ac.Heading = 280;
        Assert.False(phase.OnTick(ctx), "Should not complete — heading not reached");

        // Heading reached, altitude still well below cruise
        ac.Heading = 190;
        Assert.True(phase.OnTick(ctx), "Should complete — heading reached (altitude irrelevant for heading-only CTO)");
    }

    [Fact]
    public void MR270_WithAltitude_RequiresBothHeadingAndAltitude()
    {
        var (phase, ac, ctx) = SetUpPhase(new RelativeTurnDeparture(270, TurnDirection.Right), assignedAltitude: 3000, cruiseAltitude: 35000);

        // Neither met
        ac.Altitude = 2000;
        ac.Heading = 280;
        Assert.False(phase.OnTick(ctx), "Neither heading nor altitude met");

        // Only heading met
        ac.Heading = 190;
        ac.Altitude = 2000;
        Assert.False(phase.OnTick(ctx), "Only heading met, not altitude");

        // Only altitude met
        ac.Heading = 280;
        ac.Altitude = 3000;
        Assert.False(phase.OnTick(ctx), "Only altitude met, not heading");

        // Both met
        ac.Heading = 190;
        ac.Altitude = 3000;
        Assert.True(phase.OnTick(ctx), "Both heading and altitude met");
    }

    [Fact]
    public void FlyHeading_NoAltitude_CompletesOnHeadingReached()
    {
        var (phase, ac, ctx) = SetUpPhase(new FlyHeadingDeparture(180, null), assignedAltitude: null, cruiseAltitude: 35000);

        ac.Altitude = 1500;
        ac.Heading = 280;
        Assert.False(phase.OnTick(ctx), "Heading not reached");

        ac.Heading = 180;
        Assert.True(phase.OnTick(ctx), "Heading reached");
    }

    [Fact]
    public void DefaultDeparture_NoAltitude_CompletesAtSelfClear()
    {
        var (phase, ac, ctx) = SetUpPhase(new DefaultDeparture(), assignedAltitude: null, cruiseAltitude: 35000);

        // Below self-clear (field + 1500 = 1600)
        ac.Altitude = 1000;
        Assert.False(phase.OnTick(ctx), "Below self-clear altitude");

        // At self-clear
        ac.Altitude = FieldElevation + 1500;
        Assert.True(phase.OnTick(ctx), "At self-clear altitude");
    }

    [Fact]
    public void DefaultDeparture_WithAltitude_CompletesAtAssignedAltitude()
    {
        var (phase, ac, ctx) = SetUpPhase(new DefaultDeparture(), assignedAltitude: 5000, cruiseAltitude: 35000);

        ac.Altitude = 4000;
        Assert.False(phase.OnTick(ctx), "Below assigned altitude");

        ac.Altitude = 5000;
        Assert.True(phase.OnTick(ctx), "At assigned altitude");
    }

    [Fact]
    public void RunwayHeading_CompletesAtSelfClear()
    {
        var (phase, ac, ctx) = SetUpPhase(new RunwayHeadingDeparture(), assignedAltitude: null, cruiseAltitude: 35000);

        ac.Altitude = 1000;
        Assert.False(phase.OnTick(ctx), "Below self-clear");

        ac.Altitude = FieldElevation + 1500;
        Assert.True(phase.OnTick(ctx), "At self-clear altitude");
    }

    // ── Navigation route tests ──

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
