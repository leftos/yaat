using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

public class PilotResponderTests
{
    public PilotResponderTests()
    {
        // Needed for AtFixCondition.FromName lookups in the at-fix-condition tests.
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft(string callsign, string? parkingSpot = null, bool isVfr = false)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Ground = new AircraftGroundOps { ParkingSpot = parkingSpot },
        };
        if (isVfr)
        {
            ac.FlightPlan.FlightRules = "VFR";
        }
        return ac;
    }

    private static CompoundCommand Compound(params ParsedCommand[] commands) => new([new ParsedBlock(null, commands.ToList())]);

    private static CompoundCommand CompoundWithCondition(BlockCondition condition, params ParsedCommand[] commands) =>
        new([new ParsedBlock(condition, commands.ToList())]);

    [Fact]
    public void BuildReadback_SingleAltitudeCommand_AppendsBracketAndSpokenCallsign()
    {
        var ac = MakeAircraft("AAL123");
        var compound = Compound(new DescendMaintainCommand(5000));

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.Equal("[AAL123] descend and maintain five thousand, american one twenty three.", result);
    }

    [Fact]
    public void BuildReadback_NNumber_SpokenForm()
    {
        var ac = MakeAircraft("N123AB");
        var compound = Compound(new ClimbMaintainCommand(3500));

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.StartsWith("[N123AB] climb and maintain three thousand five hundred, november one two three alpha bravo", result);
    }

    [Fact]
    public void BuildReadback_TwoCommandsInOneBlock_JoinedWithCommas()
    {
        var ac = MakeAircraft("AAL123");
        var compound = Compound(new DescendMaintainCommand(5000), new TurnRightCommand(new MagneticHeading(270)));

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.Contains("descend and maintain five thousand", result!);
        Assert.Contains("turn right heading two seven zero", result);
        Assert.EndsWith(", american one twenty three.", result);
    }

    [Fact]
    public void BuildReadback_AtFixCondition_PrependsLeadingClause()
    {
        var ac = MakeAircraft("AAL123");
        var compound = CompoundWithCondition(AtFixCondition.FromName("SUNOL"), new TurnLeftCommand(new MagneticHeading(180)));

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.Contains("at sunol, turn left heading one eight zero", result!);
    }

    [Fact]
    public void BuildReadback_AtFixCondition_ConditionSpokenOnceForBlock()
    {
        var ac = MakeAircraft("AAL123");
        var compound = CompoundWithCondition(
            AtFixCondition.FromName("SUNOL"),
            new TurnLeftCommand(new MagneticHeading(180)),
            new DescendMaintainCommand(5000)
        );

        var result = PilotResponder.BuildReadback(compound, ac);

        // The first command gets the "at sunol," lead; the second does not (same block).
        Assert.Contains("at sunol, turn left heading one eight zero", result!);
        Assert.DoesNotContain("at sunol, descend", result);
        Assert.Contains("descend and maintain five thousand", result);
    }

    [Fact]
    public void BuildReadback_NoVerbalizableCommands_ReturnsNull()
    {
        var ac = MakeAircraft("AAL123");
        var compound = Compound(new UnsupportedCommand("ZZZ 999"));

        var result = PilotResponder.BuildReadback(compound, ac);

        Assert.Null(result);
    }

    // --- BuildReadyToTaxi ---

    [Fact]
    public void BuildReadyToTaxi_WithKnownParkingSpot_IncludesLowercaseSpot()
    {
        var ac = MakeAircraft("N123AB", parkingSpot: "GATE B22");
        var result = PilotResponder.BuildReadyToTaxi(ac);

        Assert.Equal("[N123AB] ground, november one two three alpha bravo at gate b22, with information Alpha, ready to taxi.", result);
    }

    [Fact]
    public void BuildReadyToTaxi_WithoutParkingSpot_FallsBackToRamp()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildReadyToTaxi(ac);

        Assert.Equal("[AAL123] ground, american one twenty three at the ramp, with information Alpha, ready to taxi.", result);
    }

    // --- BuildHoldingShortReady ---

    [Fact]
    public void BuildHoldingShortReady_FormatsRunwaySpoken()
    {
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildHoldingShortReady(ac, "28R");

        Assert.Equal("[N123AB] tower, november one two three alpha bravo holding short runway two eight right, ready for departure.", result);
    }

    [Fact]
    public void BuildHoldingShortReady_AirlineCallsign_UsesTelephony()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildHoldingShortReady(ac, "9L");

        Assert.Contains("american one twenty three holding short runway nine left", result);
    }

    // --- BuildLinedUpReady ---

    [Fact]
    public void BuildLinedUpReady_FormatsRunwaySpoken()
    {
        var ac = MakeAircraft("N123AB");
        var result = PilotResponder.BuildLinedUpReady(ac, "28R");

        Assert.Equal("[N123AB] tower, november one two three alpha bravo runway two eight right, ready.", result);
    }

    // --- BuildOnFinal ---

    [Fact]
    public void BuildOnFinal_IfrWithIlsApproach_SpellsIlsBeforeRunway()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "I28R", distanceMilesForVfr: 0);

        Assert.Equal("[AAL123] tower, american one twenty three, ILS two eight right.", result);
    }

    [Fact]
    public void BuildOnFinal_IfrWithRnavApproachAndSuffix_IncludesSuffixLetter()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "R28R-Y", distanceMilesForVfr: 0);

        Assert.Equal("[AAL123] tower, american one twenty three, RNAV two eight right yankee.", result);
    }

    [Fact]
    public void BuildOnFinal_IfrWithVisualApproach_UsesExpandedPhrasing()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: true, approachId: "VIS28R", distanceMilesForVfr: 0);

        Assert.Equal("[AAL123] tower, american one twenty three, visual approach runway two eight right.", result);
    }

    [Fact]
    public void BuildOnFinal_VfrNoApproach_ReportsDistanceAndAtis()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 3);

        Assert.Equal("[N123AB] tower, november one two three alpha bravo three-mile final runway two eight right, with information Alpha.", result);
    }

    [Fact]
    public void BuildOnFinal_VfrUnderOneMile_ClampsToOneMile()
    {
        var ac = MakeAircraft("N123AB", isVfr: true);
        var result = PilotResponder.BuildOnFinal(ac, "28R", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 0);

        Assert.Contains("one-mile final", result);
    }

    [Fact]
    public void BuildOnFinal_IfrNoApproach_FallsBackToVfrTemplate()
    {
        var ac = MakeAircraft("AAL123");
        var result = PilotResponder.BuildOnFinal(ac, "9", ifrWithActiveApproach: false, approachId: null, distanceMilesForVfr: 5);

        Assert.Equal("[AAL123] tower, american one twenty three five-mile final runway nine, with information Alpha.", result);
    }
}
