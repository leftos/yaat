using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

public class ExpediteCommandTests
{
    private static AircraftState CreateAircraft(double altitude = 5000, double ias = 250)
    {
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(360),
            TrueTrack = new TrueHeading(360),
            Altitude = altitude,
            IndicatedAirspeed = ias,
        };
    }

    [Fact]
    public void Expedite_SetsFlag_WhenClimbing()
    {
        var ac = CreateAircraft(altitude: 5000);
        ac.Targets.TargetAltitude = 10000;

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(ac.Procedure.IsExpediting);
    }

    [Fact]
    public void Expedite_Rejected_WhenNoAltitudeTarget()
    {
        var ac = CreateAircraft();

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.False(ac.Procedure.IsExpediting);
        Assert.Contains("climb or descent", result.Message!);
        AssertSpeakableRejection(result.Message!);
    }

    [Fact]
    public void ExpediteBare_LevelAtAssignedAltitude_MessageNamesLevelOff()
    {
        // The aircraft has an assignment — it is simply already at it. Saying
        // "requires an active altitude assignment" here is factually wrong.
        var ac = CreateAircraft(altitude: 2000);
        ac.Targets.AssignedAltitude = 2000;
        ac.Targets.TargetAltitude = null;

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.False(ac.Procedure.IsExpediting);
        Assert.Contains("Unable, level at 2000, no climb or descent to expedite", result.Message!);
        Assert.DoesNotContain("altitude assignment", result.Message!);
        AssertSpeakableRejection(result.Message!);
    }

    [Fact]
    public void Expedite_OnGroundWithRoute_SetsTaxiExpediting()
    {
        var ac = CreateAircraft();
        ac.IsOnGround = true;
        ac.Ground.AssignedTaxiRoute = new Yaat.Sim.Data.Airport.TaxiRoute { Segments = [], HoldShortPoints = [] };

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(ac.Ground.IsExpeditingTaxi);
        Assert.False(ac.Procedure.IsExpediting);
    }

    [Fact]
    public void Expedite_OnGroundWithoutRoute_Fails()
    {
        var ac = CreateAircraft();
        ac.IsOnGround = true;
        ac.Ground.AssignedTaxiRoute = null;

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.False(ac.Ground.IsExpeditingTaxi);
        Assert.Contains("taxi route", result.Message!);
    }

    [Fact]
    public void Expedite_WithAltitude_StaysAirborneSemantics_EvenOnGround()
    {
        // EXP <alt> is unambiguously a climb/descent verb — don't intercept it
        // for taxi context. On the ground it assigns the altitude like CM does,
        // and must never raise the taxi speed cap.
        var ac = CreateAircraft();
        ac.IsOnGround = true;
        ac.Ground.AssignedTaxiRoute = new Yaat.Sim.Data.Airport.TaxiRoute { Segments = [], HoldShortPoints = [] };

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(10000), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.False(ac.Ground.IsExpeditingTaxi);
        Assert.Equal(10000, ac.Targets.AssignedAltitude);
    }

    [Fact]
    public void NormalRate_ClearsFlag()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetAltitude = 10000;
        ac.Procedure.IsExpediting = true;
        ac.Targets.DesiredVerticalRate = 3000;

        var result = CommandDispatcher.Dispatch(new NormalRateCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.False(ac.Procedure.IsExpediting);
        Assert.Null(ac.Targets.DesiredVerticalRate);
    }

    [Fact]
    public void UpdateAltitude_ExpeditedClimb_UsesModestMultiplier()
    {
        TestVnasData.EnsureInitialized();

        var ac = CreateAircraft(altitude: 5000);
        ac.Targets.TargetAltitude = 10000;
        ac.Procedure.IsExpediting = true;

        // Record the climb without expedite for comparison
        var acNormal = CreateAircraft(altitude: 5000);
        acNormal.Targets.TargetAltitude = 10000;
        acNormal.Procedure.IsExpediting = false;

        FlightPhysics.Update(ac, 10.0);
        FlightPhysics.Update(acNormal, 10.0);

        // Climb is thrust-limited: the normal profile already flies near the optimum rate
        // (AIM 4-4-10), so expedite adds ~15%, not a performance unlock.
        double expClimb = ac.Altitude - 5000;
        double normClimb = acNormal.Altitude - 5000;
        Assert.True(expClimb > normClimb, $"Expedite climb {expClimb} should exceed normal {normClimb}");
        Assert.InRange(expClimb / normClimb, 1.10, 1.20);
    }

    // --- Expedite must produce a vertical rate the controller can actually see, without
    //     exceeding what the airframe can do "without an exceptional change in aircraft
    //     handling characteristics" (7110.65 PCG EXPEDITE). ---

    /// <summary>
    /// Direction-split contract: climb is thrust-limited (×1.15 up to a category cap), descent
    /// is drag-limited (×2.0 within a category floor/cap band), and expedite never reduces the
    /// rate that would otherwise apply. The constants here restate the spec on purpose — if the
    /// implementation drifts, this fails.
    /// </summary>
    [Theory]
    [InlineData("B738", 5000, 15000)] // jet, climb
    [InlineData("B738", 15000, 5000)] // jet, descent — the 4,000 fpm cap binds
    [InlineData("DH8D", 5000, 15000)] // turboprop, climb
    [InlineData("DH8D", 15000, 5000)] // turboprop, descent
    [InlineData("SR22", 2658, 1400)] // piston descent — the reported N2BP case; the floor binds
    [InlineData("SR22", 1400, 5000)] // piston, climb
    public void Expedite_FollowsTheDirectionSplitFormula(string type, double startAlt, int targetAlt)
    {
        TestVnasData.EnsureInitialized();

        double normalVs = VerticalSpeedAfterOneTick(type, startAlt, targetAlt, expedite: false);
        double expeditedVs = VerticalSpeedAfterOneTick(type, startAlt, targetAlt, expedite: true);

        Assert.NotEqual(0, normalVs);
        Assert.Equal(Math.Sign(normalVs), Math.Sign(expeditedVs));

        var cat = AircraftCategorization.Categorize(type);
        bool climb = targetAlt > startAlt;
        double normal = Math.Abs(normalVs);
        (double cap, double? floor) = (climb, cat) switch
        {
            (true, AircraftCategory.Jet) => (4000.0, (double?)null),
            (true, AircraftCategory.Turboprop) => (2500.0, null),
            (true, AircraftCategory.Piston) => (900.0, null),
            (false, AircraftCategory.Jet) => (4000.0, 2500.0),
            (false, AircraftCategory.Turboprop) => (2500.0, 1500.0),
            (false, AircraftCategory.Piston) => (1500.0, 1000.0),
            _ => throw new InvalidOperationException($"unexpected category {cat}"),
        };

        double expected = Math.Min(normal * (climb ? 1.15 : 2.0), cap);
        if (floor is { } f)
        {
            expected = Math.Max(expected, f);
        }

        expected = Math.Max(expected, normal);
        Assert.Equal(expected, Math.Abs(expeditedVs), 1.0);
    }

    [Fact]
    public void ExpeditedDescent_B738MidDescent_HitsTheJetCap()
    {
        // A 737 mid-descent already does ~3,000 fpm; doubling it would be an emergency
        // descent. The 4,000 fpm cap is what honors the PCG's handling-characteristics clause.
        TestVnasData.EnsureInitialized();

        double vs = VerticalSpeedAfterOneTick("B738", 15000, 5000, expedite: true);
        Assert.Equal(-4000, vs, 1.0);
    }

    [Fact]
    public void ExpeditedDescent_SR22_RaisedToThePistonFloor()
    {
        // The N2BP case: 500 fpm normal descent. Doubled and floored to 1,000 fpm —
        // a change the controller can actually see on the datablock.
        TestVnasData.EnsureInitialized();

        double vs = VerticalSpeedAfterOneTick("SR22", 2658, 1400, expedite: true);
        Assert.Equal(-1000, vs, 1.0);
    }

    [Fact]
    public void ExpeditedDescent_C208_RaisedToTheTurbopropFloor()
    {
        // The C208 profile publishes a 500 fpm descent everywhere; doubling gives 1,000 but a
        // turboprop asked to expedite can realistically hold 1,500 fpm — the floor supplies it.
        TestVnasData.EnsureInitialized();

        double vs = VerticalSpeedAfterOneTick("C208", 3000, 1000, expedite: true);
        Assert.Equal(-1500, vs, 1.0);
    }

    [Fact]
    public void Expedite_ScalesPhaseCommandedRate_WithoutTheFloor()
    {
        // Phases and the descent planner write DesiredVerticalRate directly. Expedite scales
        // that too (×2 descent, capped) — but the floor must NOT apply: a deliberately gentle
        // phase-commanded rate (a glidepath) may sit far below the category floor, and raising
        // it would fly the aircraft through its vertical path.
        TestVnasData.EnsureInitialized();

        var normal = CreateAircraft(altitude: 10000);
        normal.Targets.TargetAltitude = 5000;
        normal.Targets.DesiredVerticalRate = -1200;

        var expedited = CreateAircraft(altitude: 10000);
        expedited.Targets.TargetAltitude = 5000;
        expedited.Targets.DesiredVerticalRate = -1200;
        expedited.Procedure.IsExpediting = true;

        FlightPhysics.Update(normal, 1.0);
        FlightPhysics.Update(expedited, 1.0);

        Assert.Equal(-1200, normal.VerticalSpeed, 1);
        // 1200 × 2 = 2400 — below the 2,500 jet descent floor, which must not engage here.
        Assert.Equal(-2400, expedited.VerticalSpeed, 1);
    }

    [Fact]
    public void Expedite_NeverReducesAPhaseCommandedRate()
    {
        // A phase commanding a rate above the expedite cap (CLANDF's unclamped dive) must win:
        // expediting can never make an aircraft slower than it would otherwise be.
        TestVnasData.EnsureInitialized();

        var ac = CreateAircraft(altitude: 10000);
        ac.Targets.TargetAltitude = 2000;
        ac.Targets.DesiredVerticalRate = -6000;
        ac.Procedure.IsExpediting = true;

        FlightPhysics.Update(ac, 1.0);

        Assert.Equal(-6000, ac.VerticalSpeed, 1);
    }

    [Fact]
    public void ExpediteCommand_ReachesTheAltitudeSoonerThanPlainDescendMaintain()
    {
        // End-to-end through the command path, comparing what the controller would see:
        // DM 014 vs EXP 014 on identical aircraft.
        TestVnasData.EnsureInitialized();

        int plainSeconds = SecondsToLevelOff(new DescendMaintainCommand(1400));
        int expediteSeconds = SecondsToLevelOff(new ExpediteCommand(1400));

        Assert.True(expediteSeconds > 0, "plain DM never levelled off");
        Assert.True(expediteSeconds < plainSeconds, $"EXP 014 took {expediteSeconds}s vs DM 014 {plainSeconds}s — expedite saved nothing");

        // Doubled-then-floored rate (500 → 1,000 fpm) over the same 1,258 ft, less the shared
        // AIM 4-4-10 level-off taper inside the last 1,000 ft (which DM's 500 fpm never
        // triggers but the expedited descent does) => roughly two thirds of the time.
        Assert.InRange((double)expediteSeconds / plainSeconds, 0.60, 0.76);
    }

    /// <summary>
    /// EXP is in the "Altitude / Speed" category, which <c>DefaultProducesPilotUnable</c> opts in,
    /// so its rejection text is spoken as "unable, {reason}". <c>CleanUnableReason</c> only strips
    /// the leading token and the string's edges — an interior dash reaches the synthesiser, and a
    /// <c>:N0</c> thousands separator hands it a comma mid-number.
    /// </summary>
    private static void AssertSpeakableRejection(string message)
    {
        Assert.DoesNotContain("—", message);
        Assert.DoesNotContain("–", message);
        Assert.DoesNotMatch(@"\d,\d", message);
    }

    private static double VerticalSpeedAfterOneTick(string type, double startAlt, int targetAlt, bool expedite)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = type,
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(360),
            TrueTrack = new TrueHeading(360),
            Altitude = startAlt,
            IndicatedAirspeed = 200,
        };
        ac.Targets.TargetAltitude = targetAlt;
        ac.Procedure.IsExpediting = expedite;

        FlightPhysics.Update(ac, 1.0);
        return ac.VerticalSpeed;
    }

    /// <summary>Seconds for an SR22 at 2,658 ft to settle at 1,400 ft under the given command.</summary>
    private static int SecondsToLevelOff(ParsedCommand command)
    {
        var ac = new AircraftState
        {
            Callsign = "N2BP",
            AircraftType = "SR22",
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(360),
            TrueTrack = new TrueHeading(360),
            Altitude = 2658,
            IndicatedAirspeed = 115,
        };
        ac.Targets.TargetAltitude = 2000;
        ac.Targets.AssignedAltitude = 2000;

        var result = CommandDispatcher.Dispatch(command, ac, TestDispatch.Context(Random.Shared));
        Assert.True(result.Success, result.Message);

        for (int t = 1; t <= 600; t++)
        {
            FlightPhysics.Update(ac, 1.0);
            if (Math.Abs(ac.Altitude - 1400) < 1.0)
            {
                return t;
            }
        }

        return 0;
    }

    [Fact]
    public void ClimbMaintain_ClearsExpediteFlag()
    {
        var ac = CreateAircraft(altitude: 5000);
        ac.Targets.TargetAltitude = 10000;
        ac.Procedure.IsExpediting = true;

        CommandDispatcher.Dispatch(new ClimbMaintainCommand(15000), ac, TestDispatch.Context(Random.Shared));

        Assert.False(ac.Procedure.IsExpediting);
    }

    [Fact]
    public void DescendMaintain_ClearsExpediteFlag()
    {
        var ac = CreateAircraft(altitude: 10000);
        ac.Targets.TargetAltitude = 5000;
        ac.Procedure.IsExpediting = true;

        CommandDispatcher.Dispatch(new DescendMaintainCommand(3000), ac, TestDispatch.Context(Random.Shared));

        Assert.False(ac.Procedure.IsExpediting);
    }

    [Fact]
    public void Expedite_ClearedAtAltitudeSnap()
    {
        TestVnasData.EnsureInitialized();

        var ac = CreateAircraft(altitude: 9995);
        ac.Targets.TargetAltitude = 10000;
        ac.Procedure.IsExpediting = true;

        // Should snap to target and clear expedite
        FlightPhysics.Update(ac, 10.0);

        Assert.Equal(10000, ac.Altitude);
        Assert.False(ac.Procedure.IsExpediting);
    }

    // --- EXP <alt>: assigns the altitude and expedites to it ---

    [Fact]
    public void ExpediteWithAltitude_AssignsAndExpedites()
    {
        // The reported case: descending to 2,000, EXP 014 re-clears to 1,400.
        var ac = CreateAircraft(altitude: 2658);
        ac.Targets.TargetAltitude = 2000;
        ac.Targets.AssignedAltitude = 2000;

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(1400), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(1400, ac.Targets.AssignedAltitude);
        Assert.Equal(1400, ac.Targets.TargetAltitude);
        Assert.True(ac.Procedure.IsExpediting);
        Assert.Empty(ac.Queue.Blocks);
        Assert.Contains("Descend and maintain 1400, expedite descent", result.Message!);
    }

    [Fact]
    public void ExpediteWithAltitude_WhenLevel_Assigns()
    {
        // Second reported symptom: level at the assigned altitude, EXP 014 was
        // rejected as "requires an active altitude assignment".
        var ac = CreateAircraft(altitude: 2000);
        ac.Targets.AssignedAltitude = 2000;
        ac.Targets.TargetAltitude = null;

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(1400), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(1400, ac.Targets.AssignedAltitude);
        Assert.True(ac.Procedure.IsExpediting);
    }

    [Fact]
    public void ExpediteWithAltitude_AboveCurrent_UsesClimbSemantics()
    {
        var ac = CreateAircraft(altitude: 2000);
        ac.Targets.TargetAltitude = 2000;
        ac.Procedure.SidViaMode = true;
        ac.Procedure.SidViaCeiling = 5000;

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(10000), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(10000, ac.Targets.AssignedAltitude);
        Assert.True(ac.Procedure.IsExpediting);
        Assert.False(ac.Procedure.SidViaMode);
        Assert.Null(ac.Procedure.SidViaCeiling);
        Assert.Contains("Climb and maintain 10000, expedite climb", result.Message!);
    }

    [Fact]
    public void ExpediteWithAltitude_AtCurrentAltitude_Rejected()
    {
        var ac = CreateAircraft(altitude: 1400);
        ac.Targets.AssignedAltitude = 1400;

        var result = CommandDispatcher.Dispatch(new ExpediteCommand(1400), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.False(ac.Procedure.IsExpediting);
        Assert.Contains("Unable, already level at 1400", result.Message!);
        AssertSpeakableRejection(result.Message!);
    }

    [Fact]
    public void ExpediteWithAltitude_SupersedesQueuedAltitudeBlock()
    {
        // EXP <alt> now carries the Vertical dimension, so it must clear a queued
        // altitude block the same way DM does — including on the phase-transparent
        // fast path that DispatchCompound takes for a bare (unconditioned) command.
        var ac = CreateAircraft(altitude: 2658);
        ac.Targets.TargetAltitude = 2000;
        ac.Targets.AssignedAltitude = 2000;
        ac.Queue.Blocks.Add(
            new CommandBlock
            {
                Trigger = new BlockTrigger
                {
                    Type = BlockTriggerType.ReachFix,
                    FixName = "ORVIS",
                    FixLat = 37.5,
                    FixLon = -122.5,
                },
                Description = "CM 5000",
                SourceCommandText = "CM 050",
                Dimensions = CommandDimension.Vertical,
                Commands = { new TrackedCommand { Type = TrackedCommandType.Altitude } },
            }
        );

        var compound = new CompoundCommand([new ParsedBlock(null, [new ExpediteCommand(1400)])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(1400, ac.Targets.AssignedAltitude);
        Assert.Empty(ac.Queue.Blocks);
    }

    // --- LV trigger: `EXP; LV 050 NORM` is the documented way to expedite through
    //     an altitude and then resume the normal rate, so the ReachAltitude trigger
    //     must not step over its window at high vertical rates. ---

    [Theory]
    [InlineData(0.0)]
    [InlineData(5.0)]
    [InlineData(10.0)]
    [InlineData(15.0)]
    [InlineData(20.0)]
    [InlineData(25.0)]
    [InlineData(30.0)]
    [InlineData(35.0)]
    public void ReachAltitudeTrigger_FiresAtAnySubTickAlignment(double startOffsetFt)
    {
        TestVnasData.EnsureInitialized();

        var ac = CreateAircraft(altitude: 5200 + startOffsetFt);
        ac.Targets.TargetAltitude = 4000;
        ac.Targets.AssignedAltitude = 4000;

        // A phase-commanded dive rate; expedite takes it to 9,000 fpm = 37.5 ft per
        // 0.25 s sub-tick, well past the fixed ±10 ft trigger window.
        ac.Targets.DesiredVerticalRate = -6000;

        var compound = CommandParser.ParseCompound("EXP; LV 050 NORM");
        Assert.True(compound.IsSuccess);
        var dispatch = CommandDispatcher.DispatchCompound(compound.Value!, ac, TestDispatch.Context(Random.Shared));
        Assert.True(dispatch.Success, dispatch.Message);
        Assert.True(ac.Procedure.IsExpediting);

        // Sub-tick at the production cadence until well past 5,000.
        for (int i = 0; (i < 400) && (ac.Altitude > 4900); i++)
        {
            FlightPhysics.Update(ac, 0.25);
        }

        Assert.True(ac.Altitude <= 4900, $"aircraft never descended past 4,900 (stopped at {ac.Altitude:F0})");
        Assert.False(ac.Procedure.IsExpediting, $"LV 050 NORM never fired — trigger stepped over 5,000 (start {5200 + startOffsetFt:F0})");
    }

    // --- EXP argument parsing ---

    [Fact]
    public void Parse_Expedite_NoArg_PlainExpedite()
    {
        var cmd = CommandParser.Parse("EXP");
        var exp = Assert.IsType<ExpediteCommand>(cmd.Value);
        Assert.Null(exp.Altitude);
    }

    [Fact]
    public void Parse_Expedite_WithAltitude()
    {
        var cmd = CommandParser.Parse("EXP 11000");
        var exp = Assert.IsType<ExpediteCommand>(cmd.Value);
        Assert.Equal(11000, exp.Altitude);
    }

    [Fact]
    public void Parse_Expedite_BadAltitude_Fails()
    {
        var cmd = CommandParser.Parse("EXP JUNK");
        Assert.False(cmd.IsSuccess);
    }
}
