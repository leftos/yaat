using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// YAAT-1943: a speed given with CTO in one block was silently dropped. The tower driver applied the takeoff clearance,
/// then the sibling had no tower handler and <c>ApplyTowerDriverSiblings</c> skipped the null result, so the speed never
/// reached the aircraft and the RPO saw only the clearance. Each test below pins what a sibling with no tower handler
/// does now: it is judged at the phase gate, refused as an "unable" advisory beside the driver's message (the
/// transmission stands) or applied when the phase allows it.
/// </summary>
public class TowerDriverSiblingTests
{
    private readonly ITestOutputHelper _output;

    public TowerDriverSiblingTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static CommandResult Dispatch(AircraftState aircraft, string text, Func<string, AircraftState?>? findAircraft = null)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text, aircraft.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, $"Parse failed for '{text}': {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(
            parsed.Value!,
            aircraft,
            TestDispatch.Context(new Random(42), validateDctFixes: false, findAircraft: findAircraft)
        );
    }

    /// <summary>
    /// The pattern fixture the downwind tests share: on a left downwind for 28 with a landing phase pending behind it, so
    /// a tower landing clearance has an approach ending to replace.
    /// </summary>
    private static AircraftState DownwindAircraft(string callsign, string type = "C172", double ias = 90, double altitude = 1100)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(100),
            TrueTrack = new TrueHeading(100),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            Approach = new AircraftApproachState(),
            FlightPlan = new AircraftFlightPlan { Destination = "KTEST", FlightRules = "VFR" },
            Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 0) },
        };
        ac.Phases.Add(new DownwindPhase());
        ac.Phases.Add(new LandingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        return ac;
    }

    /// <summary>
    /// "CTO, SPD 230" — the speed is refused by the same lined-up phase gate that refuses it on its own. The takeoff
    /// clearance stays applied, and the refusal reaches the RPO as an "unable …" advisory carrying the gate's reason.
    /// </summary>
    [Fact]
    public void CtoCommaSpeed_KeepsTheClearanceAndReportsTheRefusalAsAnAdvisory()
    {
        AircraftState ac = LinedUpAircraft.AtOak28R("TEST1");

        CommandResult result = Dispatch(ac, "CTO, SPD 230");

        _output.WriteLine($"CTO, SPD 230: Success={result.Success} Message={result.Message} Advisory={result.Advisory}");
        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.Contains("unable", result.Message);
        Assert.Contains("lined up and waiting on the runway", result.Message);
        Assert.NotNull(result.Advisory);
        Assert.Contains("unable", result.Advisory);
        Assert.Contains("lined up and waiting on the runway", result.Advisory);
        LinedUpAndWaitingPhase lineUp = Assert.IsType<LinedUpAndWaitingPhase>(ac.Phases!.CurrentPhase);
        Assert.True(lineUp.HasTakeoffClearance, "The takeoff clearance applied before the refused speed must stand");
    }

    /// <summary>
    /// "CTO, SPD 230; CVIA" — the refused speed does not fail the transmission, so the block after it still enqueues.
    /// A failure here would rewind the transmission and drop the climb via.
    /// </summary>
    [Fact]
    public void CtoCommaSpeed_ThenQueuedClimbVia_StillEnqueuesIt()
    {
        AircraftState ac = LinedUpAircraft.AtOak28R("TEST1");

        CommandResult result = Dispatch(ac, "CTO, SPD 230; CVIA");

        _output.WriteLine($"CTO, SPD 230; CVIA: Success={result.Success} Message={result.Message} Advisory={result.Advisory}");
        Assert.True(result.Success, result.Message);
        CommandBlock enqueued = Assert.Single(ac.Queue.Blocks);
        Assert.NotNull(enqueued.ParsedCommands);
        Assert.Contains(enqueued.ParsedCommands, cmd => cmd is ClimbViaCommand);
    }

    /// <summary>
    /// "CTO, CM 050" — a sibling the lined-up phase allows applies: the altitude reaches the aircraft and the block
    /// reports success.
    /// </summary>
    [Fact]
    public void CtoCommaAltitude_AppliesTheAltitudeSibling()
    {
        AircraftState ac = LinedUpAircraft.AtOak28R("TEST1");
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("CTO, CM 050", ac.FlightPlan.Route);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        Assert.Equal(2, parsed.Value!.Blocks[0].Commands.Count);

        CommandResult result = Dispatch(ac, "CTO, CM 050");

        _output.WriteLine($"CTO, CM 050: Success={result.Success} AssignedAltitude={ac.Targets.AssignedAltitude} Message={result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.Equal(5000, ac.Targets.AssignedAltitude!.Value);
    }

    /// <summary>
    /// "CTO, NODEL" — NODEL never reaches the phase gate (it is pure controller bookkeeping), so it applies beside the
    /// takeoff clearance exactly as it does alone, with no "unable" advisory.
    /// </summary>
    [Fact]
    public void CtoCommaNoDelete_AppliesWithoutTheGate()
    {
        AircraftState ac = LinedUpAircraft.AtOak28R("TEST1");

        CommandResult result = Dispatch(ac, "CTO, NODEL");

        _output.WriteLine($"CTO, NODEL: Success={result.Success} Message={result.Message} Advisory={result.Advisory}");
        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("unable", result.Message);
        Assert.Contains("Auto-delete cancelled", result.Message);
        Assert.True(ac.Ground.NoDeleteRequested);
        Assert.True(ac.Ground.AutoDeleteExempt);
    }

    /// <summary>
    /// "CLAND, FH 270" on a downwind: the heading clears the phase, so the sibling is skipped as before and the pattern
    /// leg's heading target is untouched.
    /// </summary>
    [Fact]
    public void ClandCommaHeading_OnDownwind_KeepsTheHeadingTarget()
    {
        AircraftState ac = DownwindAircraft("FOLL");
        TrueHeading? headingBefore = ac.Targets.TargetTrueHeading;

        CommandResult result = Dispatch(ac, "CLAND, FH 270");

        _output.WriteLine($"CLAND, FH 270: Success={result.Success} Heading={ac.Targets.TargetTrueHeading} Message={result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.Equal(headingBefore, ac.Targets.TargetTrueHeading);
    }

    /// <summary>
    /// "CLAND, FOLLOWF LEAD" on a downwind: FOLLOWF is FOLLOW with the traffic-in-sight requirement folded in, so the
    /// gate must read it as FOLLOW (which the downwind allows) and the follow must apply as a lone FOLLOWF would.
    /// </summary>
    [Fact]
    public void ClandCommaFollowF_OnDownwind_AppliesTheFollow()
    {
        AircraftState ac = DownwindAircraft("FOLL");
        AircraftState lead = DownwindAircraft("LEAD");

        CommandResult result = Dispatch(ac, "CLAND, FOLLOWF LEAD", cs => (cs == "LEAD") ? lead : null);

        _output.WriteLine($"CLAND, FOLLOWF LEAD: Success={result.Success} Following={ac.Approach.FollowingCallsign} Message={result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.Equal("LEAD", ac.Approach.FollowingCallsign);
        Assert.True(ac.Approach.HasReportedTrafficInSight, "FOLLOWF folds the traffic-in-sight report in");
    }

    /// <summary>
    /// "L360" then "CLAND, SPD 150" on a jet downwind: the turn reduces the speed when it starts; the speed sibling
    /// applies during it and the phase is notified, so the turn's end does not put the pre-turn speed back over the
    /// controller's 150.
    /// </summary>
    [Fact]
    public void TurnCommaSpeed_SpeedSurvivesTheEndOfTheTurn()
    {
        AircraftState ac = DownwindAircraft("FOLL", type: "B738", ias: 250, altitude: 3000);
        // Well above the holding speed the turn reduces to, so the turn has a speed of its own to put back.
        ac.Targets.TargetSpeed = 300;

        CommandResult turn = Dispatch(ac, "L360");
        Assert.True(turn.Success, turn.Message);
        Assert.IsType<MakeTurnPhase>(ac.Phases!.CurrentPhase);

        CommandResult result = Dispatch(ac, "CLAND, SPD 150");

        _output.WriteLine($"L360 then CLAND, SPD 150: Success={result.Success} Speed={ac.Targets.TargetSpeed} Message={result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.Equal(150, ac.Targets.AssignedSpeed!.Value);

        // The engine ends the turn once it completes; its OnEnd puts the captured pre-turn speed back unless the phase
        // was told a speed had been taken over mid-manoeuvre.
        ac.Phases.AdvanceToNext(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));

        Assert.Equal(150, ac.Targets.TargetSpeed!.Value);
        Assert.True(ac.Targets.HasExplicitSpeedCommand);
    }

    /// <summary>
    /// "CTO, PS FOO; CVIA" — an unsupported verb reaches the sibling path without throwing, and fails the transmission
    /// the way a lone unsupported command does; the block behind it is dropped.
    /// </summary>
    [Fact]
    public void CtoCommaUnsupported_ThenCvia_FailsTheTransmission()
    {
        AircraftState ac = LinedUpAircraft.AtOak28R("TEST1");

        CommandResult result = Dispatch(ac, "CTO, PS FOO; CVIA");

        _output.WriteLine($"CTO, PS FOO; CVIA: Success={result.Success} Message={result.Message}");
        Assert.False(result.Success, result.Message);
        Assert.Contains("Command not yet supported", result.Message);
        Assert.Empty(ac.Queue.Blocks);
    }
}
