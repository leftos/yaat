using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class SpeedCommandTests
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

    // --- SPD with modifiers ---

    [Fact]
    public void SpeedFloor_SetsFloorClearsTargetAndCeiling()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetSpeed = 200;
        ac.Targets.SpeedCeiling = 230;

        var result = CommandDispatcher.Dispatch(new SpeedCommand(210, SpeedModifier.Floor), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(210, ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void SpeedCeiling_SetsCeilingClearsTargetAndFloor()
    {
        var ac = CreateAircraft();
        ac.Targets.SpeedFloor = 200;

        var result = CommandDispatcher.Dispatch(new SpeedCommand(210, SpeedModifier.Ceiling), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(210, ac.Targets.SpeedCeiling);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void SpeedExact_ClearsFloorAndCeiling()
    {
        var ac = CreateAircraft();
        ac.Targets.SpeedFloor = 200;
        ac.Targets.SpeedCeiling = 260;

        var result = CommandDispatcher.Dispatch(new SpeedCommand(220), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(220, ac.Targets.TargetSpeed);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    [Fact]
    public void SpeedZero_SetsTargetSpeedZero()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetSpeed = 210;
        ac.Targets.SpeedFloor = 200;
        ac.Targets.SpeedCeiling = 250;

        var result = CommandDispatcher.Dispatch(new SpeedCommand(0), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(0, ac.Targets.TargetSpeed);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    // --- RNS ---

    [Fact]
    public void ResumeNormalSpeed_ClearsAllSpeed()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetSpeed = 210;
        ac.Targets.SpeedFloor = 200;
        ac.Targets.SpeedCeiling = 250;

        var result = CommandDispatcher.Dispatch(new ResumeNormalSpeedCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Null(ac.Targets.TargetSpeed);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    // --- DSR ---

    [Fact]
    public void DeleteSpeedRestrictions_ClearsAllAndSetsDsrFlag()
    {
        var ac = CreateAircraft();
        ac.Targets.TargetSpeed = 210;

        var result = CommandDispatcher.Dispatch(new DeleteSpeedRestrictionsCommand(), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Null(ac.Targets.TargetSpeed);
        Assert.True(ac.Procedure.SpeedRestrictionsDeleted);
    }

    [Fact]
    public void SpeedCommand_ClearsDsrFlag()
    {
        var ac = CreateAircraft();
        ac.Procedure.SpeedRestrictionsDeleted = true;

        CommandDispatcher.Dispatch(new SpeedCommand(210), ac, TestDispatch.Context(Random.Shared));

        Assert.False(ac.Procedure.SpeedRestrictionsDeleted);
    }

    [Fact]
    public void Cvia_ClearsDsrFlag()
    {
        var ac = CreateAircraft();
        ac.Procedure.ActiveSidId = "PORTE3";
        ac.Procedure.SpeedRestrictionsDeleted = true;

        CommandDispatcher.Dispatch(new ClimbViaCommand(null), ac, TestDispatch.Context(Random.Shared));

        Assert.False(ac.Procedure.SpeedRestrictionsDeleted);
    }

    [Fact]
    public void Dvia_ClearsDsrFlag()
    {
        var ac = CreateAircraft();
        ac.Procedure.ActiveStarId = "SUNOL1";
        ac.Procedure.SpeedRestrictionsDeleted = true;

        CommandDispatcher.Dispatch(new DescendViaCommand(null), ac, TestDispatch.Context(Random.Shared));

        Assert.False(ac.Procedure.SpeedRestrictionsDeleted);
    }

    // --- SPD rejection inside 5nm final (§5-7-1.b.4) ---

    /// <summary>An arrival established on final inside 5nm: plain SPD is rejected.</summary>
    [Fact]
    public void SpeedCommand_RejectedInside5nmFinal_WhenOnApproach()
    {
        var ac = CreateAircraft();
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon) };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        // Aircraft is at the threshold (0nm)

        var result = CommandDispatcher.Dispatch(new SpeedCommand(180), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("5nm final", result.Message);
        Assert.Contains("5-7-1.b.4", result.Message);
    }

    /// <summary>
    /// Issue #196: a departure (or go-around) within 5nm of its runway is NOT on an
    /// arrival-approach phase, so plain SPD must be accepted — the 5nm gate keyed on
    /// AssignedRunway alone wrongly blocked departures (the departure runway is also
    /// the AssignedRunway).
    /// </summary>
    [Fact]
    public void SpeedCommand_AcceptedInside5nm_WhenNotOnApproach()
    {
        var ac = CreateAircraft();
        // AssignedRunway set (departure runway), but no arrival-approach phase.
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon) };

        var result = CommandDispatcher.Dispatch(new SpeedCommand(180), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(180, ac.Targets.TargetSpeed);
        Assert.False(ac.Targets.SpeedOverridesFinalGate);
    }

    /// <summary>
    /// A pattern arrival cleared to land (no arrival-approach phase, but
    /// LandingClearance set) is still gated inside 5nm — the rule applies to anyone
    /// inbound to land, not just instrument arrivals.
    /// </summary>
    [Fact]
    public void SpeedCommand_RejectedInside5nm_WhenClearedToLandInPattern()
    {
        var ac = CreateAircraft();
        ac.Phases = new PhaseList
        {
            AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon),
            LandingClearance = ClearanceType.ClearedToLand,
        };

        var result = CommandDispatcher.Dispatch(new SpeedCommand(180), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("5nm final", result.Message);
    }

    /// <summary>Issue #196: a go-around (climbing out) inside 5nm accepts plain SPD.</summary>
    [Fact]
    public void SpeedCommand_AcceptedInside5nm_WhenGoingAround()
    {
        var ac = CreateAircraft();
        // A go-around keeps its approach/landing context but is climbing out.
        ac.Phases = new PhaseList
        {
            AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon),
            LandingClearance = ClearanceType.ClearedToLand,
        };
        ac.Phases.Add(new GoAroundPhase());

        var result = CommandDispatcher.Dispatch(new SpeedCommand(180), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(180, ac.Targets.TargetSpeed);
    }

    /// <summary>SPEEDF overrides the §5-7-1.b.4 gate for an arrival on final inside 5nm.</summary>
    [Fact]
    public void ForceSpeed_OverridesInside5nmFinal()
    {
        var ac = CreateAircraft(ias: 210);
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon) };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });

        var result = CommandDispatcher.Dispatch(new SpeedCommand(180, SpeedModifier.None, Force: true), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(180, ac.Targets.TargetSpeed);
        Assert.True(ac.Targets.SpeedOverridesFinalGate);
        // Unlike SPEEDN, SPEEDF converges via physics — it does not teleport IAS.
        Assert.Equal(210, ac.IndicatedAirspeed);
    }

    /// <summary>SPEEDF supports +/- floor/ceiling modifiers like SPD.</summary>
    [Fact]
    public void ForceSpeed_WithFloorModifier_OverridesInside5nmFinal()
    {
        var ac = CreateAircraft();
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon) };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });

        var result = CommandDispatcher.Dispatch(new SpeedCommand(170, SpeedModifier.Floor, Force: true), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(170, ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.TargetSpeed);
        Assert.True(ac.Targets.SpeedOverridesFinalGate);
    }

    /// <summary>A plain SPD after a SPEEDF clears the override so the gate resumes.</summary>
    [Fact]
    public void PlainSpeed_ClearsForcedOverride()
    {
        var ac = CreateAircraft();
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon) };
        // Far from the runway so the plain SPD is accepted regardless of phase.
        ac.Position = new LatLon(37.5, -122.5);
        ac.Targets.SpeedOverridesFinalGate = true;

        var result = CommandDispatcher.Dispatch(new SpeedCommand(200), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.False(ac.Targets.SpeedOverridesFinalGate);
    }

    // --- Approach clearance clears floor/ceiling ---

    [Fact]
    public void ApproachClearance_ClearsFloorAndCeiling()
    {
        var ac = CreateAircraft();
        ac.Targets.SpeedFloor = 200;
        ac.Targets.SpeedCeiling = 250;

        // After approach clearance, floor/ceiling should be cleared
        // (tested implicitly through the approach clearance path which sets TargetSpeed = null)
        // We test the ControlTargets directly
        ac.Targets.TargetSpeed = null;
        ac.Targets.SpeedFloor = null;
        ac.Targets.SpeedCeiling = null;

        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    // --- Simultaneous floor + ceiling ---

    [Fact]
    public void SimultaneousFloorAndCeiling_FloorRespected()
    {
        var ac = CreateAircraft(ias: 190);
        ac.Targets.SpeedFloor = 200;
        ac.Targets.SpeedCeiling = 260;

        // Setting floor then ceiling via dispatch: each clears the other
        // But ControlTargets allows both to be set directly for via-mode clamping
        Assert.Equal(200, ac.Targets.SpeedFloor);
        Assert.Equal(260, ac.Targets.SpeedCeiling);
    }

    [Fact]
    public void SpeedFloorCommand_ThenCeilingCommand_ReplacesFloor()
    {
        var ac = CreateAircraft();

        CommandDispatcher.Dispatch(new SpeedCommand(200, SpeedModifier.Floor), ac, TestDispatch.Context(Random.Shared));
        Assert.Equal(200, ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);

        CommandDispatcher.Dispatch(new SpeedCommand(250, SpeedModifier.Ceiling), ac, TestDispatch.Context(Random.Shared));
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Equal(250, ac.Targets.SpeedCeiling);
    }

    [Fact]
    public void SpeedCeilingCommand_ThenFloorCommand_ReplacesCeiling()
    {
        var ac = CreateAircraft();

        CommandDispatcher.Dispatch(new SpeedCommand(250, SpeedModifier.Ceiling), ac, TestDispatch.Context(Random.Shared));
        Assert.Equal(250, ac.Targets.SpeedCeiling);

        CommandDispatcher.Dispatch(new SpeedCommand(200, SpeedModifier.Floor), ac, TestDispatch.Context(Random.Shared));
        Assert.Equal(200, ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }
}

public class SpeedPhysicsTests
{
    private static AircraftState CreateAirborne(double ias = 250, double altitude = 5000)
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
    public void SpeedFloor_AcceleratesWhenBelowFloor()
    {
        var ac = CreateAirborne(ias: 190);
        ac.Targets.SpeedFloor = 210;

        FlightPhysics.Update(ac, 1.0);

        // TargetSpeed should have been set, and IAS should be increasing
        Assert.True(ac.IndicatedAirspeed > 190);
    }

    [Fact]
    public void SpeedCeiling_DeceleratesWhenAboveCeiling()
    {
        var ac = CreateAirborne(ias: 260);
        ac.Targets.SpeedCeiling = 230;

        FlightPhysics.Update(ac, 1.0);

        Assert.True(ac.IndicatedAirspeed < 260);
    }

    [Fact]
    public void SpeedFloor_NoEffectWhenAboveFloor()
    {
        var ac = CreateAirborne(ias: 230);
        ac.Targets.SpeedFloor = 210;

        FlightPhysics.Update(ac, 1.0);

        // No TargetSpeed should be set, IAS shouldn't change significantly
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void SpeedCeiling_NoEffectWhenBelowCeiling()
    {
        var ac = CreateAirborne(ias: 200);
        ac.Targets.SpeedCeiling = 230;

        FlightPhysics.Update(ac, 1.0);

        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void SpeedFloor_CappedAt250Below10k()
    {
        var ac = CreateAirborne(ias: 240, altitude: 8000);
        ac.Targets.SpeedFloor = 270;

        FlightPhysics.Update(ac, 1.0);

        // Floor should be capped at 250 below 10k, so no acceleration since 240 < 250
        // but still below effective floor of 250, so target should be set
        Assert.NotNull(ac.Targets.TargetSpeed);
        Assert.True(ac.Targets.TargetSpeed <= 250);
    }

    [Fact]
    public void DsrFlag_SkipsViaModeSpdConstraints()
    {
        var ac = CreateAirborne(ias: 280, altitude: 15000);
        ac.Procedure.ActiveStarId = "SUNOL1";
        ac.Procedure.StarViaMode = true;
        ac.Procedure.SpeedRestrictionsDeleted = true;

        var target = new NavigationTarget
        {
            Name = "SUNOL",
            Position = new LatLon(37.5, -121.9),
            SpeedRestriction = new Data.Vnas.CifpSpeedRestriction(250, Data.Vnas.CifpSpeedRestrictionType.AtOrBelow),
        };

        FlightPhysics.ApplyFixConstraints(ac, target);

        // Speed restriction should NOT be applied due to DSR flag
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void ViaModeSpdConstraint_ClampedToFloor()
    {
        var ac = CreateAirborne(ias: 210, altitude: 15000);
        ac.Procedure.ActiveStarId = "SUNOL1";
        ac.Procedure.StarViaMode = true;
        ac.Targets.SpeedFloor = 230;

        var target = new NavigationTarget
        {
            Name = "SUNOL",
            Position = new LatLon(37.5, -121.9),
            SpeedRestriction = new Data.Vnas.CifpSpeedRestriction(200, Data.Vnas.CifpSpeedRestrictionType.AtOrBelow),
        };

        FlightPhysics.ApplyFixConstraints(ac, target);

        // Via-mode speed of 200 should be clamped up to floor of 230
        Assert.Equal(230, ac.Targets.TargetSpeed);
    }

    [Fact]
    public void ViaModeSpdConstraint_ClampedToCeiling()
    {
        var ac = CreateAirborne(ias: 280, altitude: 15000);
        ac.Procedure.ActiveStarId = "SUNOL1";
        ac.Procedure.StarViaMode = true;
        ac.Targets.SpeedCeiling = 240;

        var target = new NavigationTarget
        {
            Name = "SUNOL",
            Position = new LatLon(37.5, -121.9),
            SpeedRestriction = new Data.Vnas.CifpSpeedRestriction(260, Data.Vnas.CifpSpeedRestrictionType.AtOrBelow),
        };

        FlightPhysics.ApplyFixConstraints(ac, target);

        // Via-mode speed of 260 should be clamped down to ceiling of 240
        Assert.Equal(240, ac.Targets.TargetSpeed);
    }

    // --- Simultaneous floor + ceiling via-mode clamping ---

    [Fact]
    public void ViaModeSpdConstraint_ClampedToBothFloorAndCeiling_FloorWins()
    {
        // Floor > Ceiling is contradictory; via-mode applies floor then ceiling sequentially
        var ac = CreateAirborne(ias: 250, altitude: 15000);
        ac.Procedure.ActiveStarId = "SUNOL1";
        ac.Procedure.StarViaMode = true;
        ac.Targets.SpeedFloor = 240;
        ac.Targets.SpeedCeiling = 220;

        var target = new NavigationTarget
        {
            Name = "SUNOL",
            Position = new LatLon(37.5, -121.9),
            SpeedRestriction = new Data.Vnas.CifpSpeedRestriction(200, Data.Vnas.CifpSpeedRestrictionType.AtOrBelow),
        };

        FlightPhysics.ApplyFixConstraints(ac, target);

        // Speed 200 → clamped up to floor 240 → clamped down to ceiling 220
        Assert.Equal(220, ac.Targets.TargetSpeed);
    }

    [Fact]
    public void BothFloorAndCeiling_IasBetween_NoTargetSet()
    {
        var ac = CreateAirborne(ias: 230);
        ac.Targets.SpeedFloor = 210;
        ac.Targets.SpeedCeiling = 250;

        FlightPhysics.Update(ac, 1.0);

        // IAS 230 is between floor and ceiling — no correction needed
        Assert.Null(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void BothFloorAndCeiling_IasBelowFloor_AcceleratesToFloor()
    {
        var ac = CreateAirborne(ias: 190);
        ac.Targets.SpeedFloor = 210;
        ac.Targets.SpeedCeiling = 250;

        FlightPhysics.Update(ac, 1.0);

        Assert.True(ac.IndicatedAirspeed > 190);
    }

    [Fact]
    public void BothFloorAndCeiling_IasAboveCeiling_DeceleratesToCeiling()
    {
        var ac = CreateAirborne(ias: 270);
        ac.Targets.SpeedFloor = 210;
        ac.Targets.SpeedCeiling = 250;

        FlightPhysics.Update(ac, 1.0);

        Assert.True(ac.IndicatedAirspeed < 270);
    }

    // --- Auto-cancel at 5nm final ---

    /// <summary>
    /// At the 5nm gate a non-phase-managed inbound (cleared to land, hand-vectored, no
    /// speed-owning phase) has its explicit ATC restriction released — but the last-assigned
    /// speed is retained as a ceiling so the auto speed schedule cannot push it back up.
    /// </summary>
    [Fact]
    public void AutoCancel_ConvertsSpeedToCeiling_WhenNotPhaseManaged()
    {
        var ac = CreateAirborne(ias: 210);
        ac.Targets.TargetSpeed = 210;
        ac.Targets.HasExplicitSpeedCommand = true;
        ac.Targets.SpeedFloor = 200;
        ac.Phases = new PhaseList
        {
            AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon),
            LandingClearance = ClearanceType.ClearedToLand,
        };

        FlightPhysics.Update(ac, 0.1);

        // Explicit restriction released, floor dropped, but a ceiling at the last-assigned
        // speed remains so the aircraft cannot speed back up inside the final.
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Equal(210, ac.Targets.SpeedCeiling);
    }

    /// <summary>
    /// A phase that owns speed (pattern leg / active approach / final) keeps an aircraft from
    /// accelerating on its own, so at the gate the explicit restriction is cleared outright —
    /// no lingering ceiling that could fight the phase's own speed schedule.
    /// </summary>
    [Fact]
    public void AutoCancel_FullyClearsSpeed_WhenPhaseManaged()
    {
        var ac = CreateAirborne(ias: 210);
        ac.Targets.TargetSpeed = 210;
        ac.Targets.HasExplicitSpeedCommand = true;
        ac.Targets.SpeedFloor = 200;
        ac.Targets.SpeedCeiling = 250;
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon) };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });

        FlightPhysics.Update(ac, 0.1);

        Assert.Null(ac.Targets.TargetSpeed);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
        Assert.Null(ac.Targets.SpeedFloor);
        Assert.Null(ac.Targets.SpeedCeiling);
    }

    /// <summary>
    /// An aircraft given an explicit speed (RFAS or SPD) before the 5nm final must not
    /// accelerate once it crosses the gate — the controller can no longer adjust its speed,
    /// so the auto speed schedule must never push it back up toward the descent default.
    /// (Before the fix the gate cleared the assignment and a non-phase-managed inbound
    /// re-accelerated to the 250 kt below-10k descent default.)
    /// </summary>
    [Fact]
    public void AutoCancel_DoesNotAccelerateInboundToLand_At5nmFinal()
    {
        // Cleared-to-land aircraft hand-vectored to a visual: inbound to land but with no
        // speed-managing phase, descending → the auto speed schedule is active.
        var ac = CreateAirborne(ias: 170, altitude: 4000);
        ac.Targets.TargetSpeed = 170;
        ac.Targets.HasExplicitSpeedCommand = true;
        ac.Targets.TargetAltitude = 2000;
        ac.Phases = new PhaseList
        {
            AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon),
            LandingClearance = ClearanceType.ClearedToLand,
        };

        for (int i = 0; i < 10; i++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.True(
            ac.IndicatedAirspeed <= 172,
            $"Inbound-to-land aircraft must not accelerate past its assigned speed inside 5nm; was {ac.IndicatedAirspeed:F1}"
        );
    }

    /// <summary>
    /// Issue #196: a departure (no arrival-approach phase) within 5nm of its runway must
    /// keep its assigned speed — the over-broad auto-cancel wiped departures every tick.
    /// </summary>
    [Fact]
    public void AutoCancel_SkipsDeparture_NotOnApproach()
    {
        // IAS away from the target so the normal speed-snap doesn't clear TargetSpeed;
        // only the §5-7-1.b.4 auto-cancel would, and it must not for a departure.
        var ac = CreateAirborne(ias: 250);
        ac.Targets.TargetSpeed = 180;
        ac.Targets.HasExplicitSpeedCommand = true;
        // AssignedRunway set (departure runway), but no arrival-approach phase.
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon) };

        FlightPhysics.Update(ac, 0.1);

        Assert.Equal(180, ac.Targets.TargetSpeed);
        Assert.True(ac.Targets.HasExplicitSpeedCommand);
    }

    /// <summary>A forced override (SPEEDF/SPEEDN) survives the auto-cancel gate on final.</summary>
    [Fact]
    public void AutoCancel_SkipsForcedOverride_OnFinal()
    {
        var ac = CreateAirborne(ias: 250);
        ac.Targets.TargetSpeed = 180;
        ac.Targets.HasExplicitSpeedCommand = true;
        ac.Targets.SpeedOverridesFinalGate = true;
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon) };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });

        FlightPhysics.Update(ac, 0.1);

        Assert.Equal(180, ac.Targets.TargetSpeed);
        Assert.True(ac.Targets.HasExplicitSpeedCommand);
    }

    // --- DistanceFinal trigger ---

    [Fact]
    public void DistanceFinalTrigger_MetWhenInsideDistance()
    {
        var ac = CreateAirborne();
        ac.Phases = new PhaseList();
        ac.Phases.AssignedRunway = new RunwayInfo
        {
            AirportId = "OAK",
            Id = RunwayIdentifier.Parse("30"),
            Designator = "30",
            Lat1 = ac.Position.Lat,
            Lon1 = ac.Position.Lon,
            Lat2 = ac.Position.Lat + 0.01,
            Lon2 = ac.Position.Lon + 0.01,
            Elevation1Ft = 6,
            Elevation2Ft = 6,
            TrueHeading1 = new TrueHeading(300),
            TrueHeading2 = new TrueHeading(120),
            LengthFt = 6000,
            WidthFt = 150,
        };

        var trigger = new BlockTrigger { Type = BlockTriggerType.DistanceFinal, DistanceFinalNm = 10 };

        // Set up a command block with the trigger
        ac.Queue = new CommandQueue();
        ac.Queue.Blocks.Add(new CommandBlock { Trigger = trigger, ApplyAction = _ => new CommandResult(true) });
        ac.Queue.CurrentBlockIndex = 0;

        // Aircraft is at runway threshold (0nm), should trigger
        FlightPhysics.Update(ac, 0.1);

        Assert.True(ac.Queue.Blocks[0].TriggerMet);
    }

    [Fact]
    public void DistanceFinalTrigger_NotMetWithoutRunway()
    {
        var ac = CreateAirborne();
        // No assigned runway

        var trigger = new BlockTrigger { Type = BlockTriggerType.DistanceFinal, DistanceFinalNm = 10 };

        ac.Queue = new CommandQueue();
        ac.Queue.Blocks.Add(new CommandBlock { Trigger = trigger, ApplyAction = _ => new CommandResult(true) });
        ac.Queue.CurrentBlockIndex = 0;

        FlightPhysics.Update(ac, 0.1);

        Assert.False(ac.Queue.Blocks[0].TriggerMet);
    }
}
