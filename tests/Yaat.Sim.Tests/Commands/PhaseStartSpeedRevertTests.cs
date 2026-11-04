using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// YAAT-1454: a phase that starts with a crossing's speed revert still pending. A 360 or a 270 clears the route and
/// applies the revert before the maneuver captures the speed it slows from, so its resume brings back the speed the
/// crossing replaced rather than the crossing speed. S-turns keep the route and with it the crossing, whose revert fires
/// at its fix. A hold, a pattern entry and an approach clearance drop the revert with nothing restored.
/// </summary>
public sealed class PhaseStartSpeedRevertTests
{
    private const double OriginalSpeedKts = 300;
    private const double CrossingSpeedKts = 250;

    /// <summary>East of PMD on its 090 radial side, west-bound: PMD lies ahead.</summary>
    private static readonly LatLon EastOfPmd = new(34.771654, -114.542825);

    private readonly ITestOutputHelper _output;

    public PhaseStartSpeedRevertTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState Airborne(double altitude, double ias, LatLon position, string destination = "KLAX")
    {
        var ac = new AircraftState
        {
            Callsign = "SWA123",
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(270),
            TrueTrack = new TrueHeading(270),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                HasFlightPlan = true,
                Departure = "KPHX",
                Destination = destination,
            },
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(270);
        ac.Targets.TargetAltitude = altitude;
        return ac;
    }

    private static LatLon FixPosition(string fixName)
    {
        (double lat, double lon) = Assert.IsType<(double Lat, double Lon)>(NavigationDatabase.Instance.GetFixPosition(fixName));
        return new LatLon(lat, lon);
    }

    private static void Send(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(
            parsed.Value!,
            aircraft,
            TestDispatch.Context(new SerializableRandom(1), validateDctFixes: false)
        );
        Assert.True(result.Success, $"'{text}': {result.Message}");
    }

    /// <summary>Phases then physics, a quarter second at a time, as the engine ticks them.</summary>
    private static void FlySeconds(AircraftState aircraft, int seconds)
    {
        for (int tick = 0; tick < seconds * 4; tick++)
        {
            var ctx = new PhaseContext
            {
                Aircraft = aircraft,
                Targets = aircraft.Targets,
                Category = AircraftCategorization.Categorize(aircraft.AircraftType),
                DeltaSeconds = 0.25,
                Runway = aircraft.Phases?.AssignedRunway,
                ActiveRunways = ActiveRunways.Empty,
                Logger = NullLogger.Instance,
            };
            PhaseRunner.Tick(aircraft, ctx);
            FlightPhysics.Update(aircraft, 0.25);
        }
    }

    private static void FlyUntilEnded<TPhase>(AircraftState aircraft)
        where TPhase : Phase
    {
        Assert.IsType<TPhase>(aircraft.Phases?.CurrentPhase);
        for (int second = 0; (second < 1_200) && (aircraft.Phases?.CurrentPhase is TPhase); second++)
        {
            FlySeconds(aircraft, 1);
        }

        Assert.False(aircraft.Phases?.CurrentPhase is TPhase, $"{typeof(TPhase).Name} never ended");
    }

    /// <summary>SPD 300 then CFIX PMD 110 250, flying the crossing speed.</summary>
    private static AircraftState FlyingThePmdCrossing()
    {
        AircraftState ac = Airborne(altitude: 15_000, ias: OriginalSpeedKts, EastOfPmd);
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250");
        Assert.Equal((double?)CrossingSpeedKts, ac.Targets.AssignedSpeed);

        // Slowed to the crossing speed and targeting it, as an aircraft flying the crossing is.
        ac.IndicatedAirspeed = CrossingSpeedKts;
        ac.Targets.TargetSpeed = CrossingSpeedKts;
        return ac;
    }

    [Fact]
    public void CfixThen360_ResumesThePendingSpeedAfterTheTurn()
    {
        AircraftState ac = FlyingThePmdCrossing();

        Send(ac, "R360");
        FlyUntilEnded<MakeTurnPhase>(ac);

        _output.WriteLine($"after the 360: assigned {ac.Targets.AssignedSpeed}, target {ac.Targets.TargetSpeed}, {ac.IndicatedAirspeed:F0} KIAS");
        Assert.Equal((double?)OriginalSpeedKts, ac.Targets.AssignedSpeed);
        Assert.Equal((double?)OriginalSpeedKts, ac.Targets.TargetSpeed);
    }

    /// <summary>
    /// S-turns keep the route, so the crossing toward PMD stays in force through them (AIM 4-4-12.f: a crossing speed lasts
    /// until its fix or until ATC ends it, and an S-turn is neither): 250 stays assigned, PMD keeps its revert, and the 300
    /// comes back past PMD.
    /// </summary>
    [Fact]
    public void CfixThenSTurns_KeepsTheCrossingUntilItsFix()
    {
        AircraftState ac = FlyingThePmdCrossing();

        ac.Phases = new PhaseList();
        ac.Phases.Add(new STurnPhase { InitialDirection = TurnDirection.Left, Count = 2 });
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        FlyUntilEnded<STurnPhase>(ac);

        _output.WriteLine($"after the S-turns: assigned {ac.Targets.AssignedSpeed}, target {ac.Targets.TargetSpeed}, {ac.IndicatedAirspeed:F0} KIAS");
        Assert.Equal((double?)CrossingSpeedKts, ac.Targets.AssignedSpeed);
        Assert.InRange(ac.IndicatedAirspeed, CrossingSpeedKts - 5, CrossingSpeedKts + 5);
        NavigationTarget pmd = Assert.Single(ac.Targets.NavigationRoute, fix => fix.Name == "PMD");
        Assert.True(NavigationCommandHandler.HasSpeedRevert(pmd));

        for (int second = 0; (second < 3_600) && ac.Targets.NavigationRoute.Exists(fix => fix.Name == "PMD"); second++)
        {
            FlySeconds(ac, 1);
        }

        Assert.False(ac.Targets.NavigationRoute.Exists(fix => fix.Name == "PMD"), "the aircraft never sequenced PMD");
        Assert.Equal((double?)OriginalSpeedKts, ac.Targets.AssignedSpeed);
    }

    [Fact]
    public void SmaxCfixThen360_HoldsSpeedMaxAfterTheTurn()
    {
        AircraftState ac = Airborne(altitude: 15_000, ias: 280, EastOfPmd);
        Send(ac, "SMAX");
        Send(ac, "CFIX PMD 110 250");
        Assert.False(ac.Targets.SpeedMaxHeld);
        ac.IndicatedAirspeed = CrossingSpeedKts;

        Send(ac, "R360");
        FlyUntilEnded<MakeTurnPhase>(ac);

        double before = ac.IndicatedAirspeed;
        Assert.True(ac.Targets.SpeedMaxHeld);
        FlySeconds(ac, 20);
        _output.WriteLine($"after the 360: {before:F0} KIAS, 20 s later {ac.IndicatedAirspeed:F0} KIAS");
        Assert.True(ac.Targets.SpeedMaxHeld);
        Assert.True(ac.IndicatedAirspeed > before + 2, $"IAS did not rise after the 360, {ac.IndicatedAirspeed:F0} KIAS");
    }

    /// <summary>A hold drops the pending revert: nothing is restored, and at the fix the aircraft slows to holding speed.</summary>
    [Fact]
    public void CfixThenHold_RestoresNothingAndFliesHoldingSpeed()
    {
        LatLon position = GeoMath.ProjectPoint(FixPosition("PMD"), new TrueHeading(90), 5);
        AircraftState ac = Airborne(altitude: 11_000, ias: OriginalSpeedKts, position);
        ac.Phases = new PhaseList();
        Send(ac, "SPD 300");
        Send(ac, "CFIX PMD 110 250");
        ac.IndicatedAirspeed = CrossingSpeedKts;

        Send(ac, "HOLD PMD 090 R");
        Assert.NotEqual((double?)OriginalSpeedKts, ac.Targets.AssignedSpeed);
        FlySeconds(ac, 180);

        double holding = AircraftPerformance.HoldingSpeed(ac.AircraftType, ac.Altitude);
        _output.WriteLine(
            $"in the hold: assigned {ac.Targets.AssignedSpeed}, target {ac.Targets.TargetSpeed}, {ac.IndicatedAirspeed:F0} KIAS, holding {holding:F0}"
        );
        Assert.NotEqual((double?)OriginalSpeedKts, ac.Targets.AssignedSpeed);
        Assert.True(ac.IndicatedAirspeed <= holding + 5, $"{ac.IndicatedAirspeed:F0} KIAS in the hold, holding speed {holding:F0}");
    }

    /// <summary>An approach clearance cancels the speeds assigned before it: the revert goes with nothing restored.</summary>
    [Fact]
    public void CfixThenApproachClearance_RestoresNothing()
    {
        AircraftState ac = Airborne(
            altitude: 6_000,
            ias: 250,
            GeoMath.ProjectPoint(FixPosition("SUNOL"), new TrueHeading(90), 10),
            destination: "KOAK"
        );
        Send(ac, "SPD 250");
        Send(ac, "CFIX SUNOL 060 210");
        Assert.Equal((double?)210, ac.Targets.AssignedSpeed);

        Send(ac, "CAPP I28R");

        Assert.NotEqual((double?)250, ac.Targets.AssignedSpeed);
        Assert.DoesNotContain(ac.Targets.NavigationRoute, NavigationCommandHandler.HasSpeedRevert);
    }

    /// <summary>A pattern entry owns the speed from its start: the revert goes with nothing restored.</summary>
    [Fact]
    public void CfixThenPatternEntry_RestoresNothing()
    {
        var ac = new AircraftState
        {
            Callsign = "N435C",
            AircraftType = "M20P",
            Position = new LatLon(37.87, -122.22),
            TrueHeading = new TrueHeading(180),
            Altitude = 3500,
            IndicatedAirspeed = 130,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "VFR",
                Destination = "KOAK",
            },
        };
        Send(ac, "SPD 130");
        Send(ac, "CFIX OAK 030 110");
        Assert.Equal((double?)110, ac.Targets.AssignedSpeed);

        Send(ac, "ERD 28R");

        // The entry phase starts on the next phase tick.
        FlySeconds(ac, 1);
        Assert.IsType<PatternEntryPhase>(ac.Phases?.CurrentPhase);
        Assert.NotEqual((double?)130, ac.Targets.AssignedSpeed);
        Assert.DoesNotContain(ac.Targets.NavigationRoute, NavigationCommandHandler.HasSpeedRevert);
    }
}
