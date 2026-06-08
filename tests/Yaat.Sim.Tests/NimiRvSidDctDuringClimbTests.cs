using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// Regression probe: unconditional DCT during an active RV SID heading hold must release
/// <see cref="InitialClimbPhase"/>'s internal hold so the phase can complete and the
/// aircraft is not stuck with <c>_rvSidActive == true</c> while following a DCT route.
/// </summary>
public class NimiRvSidDctDuringClimbTests
{
    private const double ExpectedRvHeading = 315.0;

    // Mirrors what InsertTowerPhasesAfterCurrent builds for a CTO whose CIFP path
    // resolves to a radar-vectors SID: route fixes from the CIFP plus the VM-leg
    // magnetic heading, with DefaultDeparture as the controller's clearance instruction
    // (the RV vector comes from SidDepartureHeadingMagnetic, not from Departure).
    // <paramref name="deferUntilMinAlt"/> selects between the two RV SID variants:
    //  - false: published VM leg with no CA-leg gate. _rvSidActive=true, _deferredTurnApplied=true,
    //    target heading set to the RV vector immediately.
    //  - true : CA-leg-before-VM-leg gating. _rvSidActive=true, _deferredTurnApplied=false,
    //    runway heading held until 400 ft AGL (IFR — no past-DER), then VM heading applies.
    private static (InitialClimbPhase Phase, AircraftState Aircraft, PhaseContext Ctx) BuildRvSidClimbHarness(bool deferUntilMinAlt = false)
    {
        const double fieldElev = 6.0;
        var runway = TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280.0, elevationFt: fieldElev);
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(37.728, -122.218),
            TrueHeading = new TrueHeading(280),
            Altitude = fieldElev + 1500,
            IndicatedAirspeed = 180,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KSAC",
                Route = "NIMI5 OAK V6 SAC",
                CruiseAltitude = 11000,
                FlightRules = "IFR",
            },
        };
        var climb = new InitialClimbPhase
        {
            Departure = new DefaultDeparture(),
            AssignedAltitude = 5000,
            DepartureRoute = [new NavigationTarget { Name = "FESIK", Position = new LatLon(38.2, -121.5) }],
            DepartureSidId = null,
            SidDepartureHeadingMagnetic = ExpectedRvHeading,
            RvSidDeferHeadingUntilMinAlt = deferUntilMinAlt,
            IsVfr = false,
            CruiseAltitude = 11000,
        };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = fieldElev,
            Logger = NullLogger.Instance,
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };
        ac.Phases.Phases.Add(climb);
        climb.Status = PhaseStatus.Active;
        climb.OnStart(ctx);
        Assert.Same(climb, ac.Phases.CurrentPhase);
        return (climb, ac, ctx);
    }

    [Fact]
    public void DctDuringRvSidHold_ReleasesRvSidActiveSoPhaseCanComplete()
    {
        TestVnasData.EnsureInitialized();
        var (phase, ac, ctx) = BuildRvSidClimbHarness();
        Assert.False(ac.HasLeftStudentFrequency);

        var parsed = CommandParser.ParseCompound("DCT FESIK");
        Assert.True(parsed.IsSuccess);

        var dispatch = CommandDispatcher.DispatchCompound(
            parsed.Value!,
            ac,
            TestDispatch.Context(new SerializableRandom(42), validateDctFixes: false)
        );
        Assert.True(dispatch.Success, dispatch.Message);

        Assert.NotEmpty(ac.Targets.NavigationRoute);

        // Simulate one sub-tick: phase then physics (mirrors SimulationWorld.Tick).
        bool complete = phase.OnTick(ctx);
        FlightPhysics.Update(ac, 1.0, _ => null);

        Assert.False(complete, "Phase should not complete on the first tick after DCT");
        var dto = Assert.IsType<InitialClimbPhaseDto>(phase.ToSnapshot());
        Assert.False(
            dto.RvSidActive,
            "DCT during the RV SID hold must release _rvSidActive; otherwise InitialClimb never completes while a route is active."
        );

        // Climb to the assigned altitude — phase must be able to exit once there.
        ac.Altitude = 5000;
        for (int i = 0; i < 30; i++)
        {
            if (phase.OnTick(ctx))
            {
                return;
            }

            FlightPhysics.Update(ac, 1.0, _ => null);
        }

        Assert.Fail("InitialClimbPhase should complete after DCT releases the RV SID hold and assigned altitude is reached.");
    }

    /// <summary>
    /// Conditional <c>AT FIX DCT</c> is enqueued at dispatch; <see cref="CommandDispatcher.BuildApplyAction"/>
    /// must call <see cref="Phase.OnCommandAccepted"/> when the block fires, not only on immediate dispatch.
    /// </summary>
    [Fact]
    public void AtFixDct_WhenQueuedBlockFires_ReleasesRvSidActive()
    {
        TestVnasData.EnsureInitialized();
        var (phase, ac, _) = BuildRvSidClimbHarness();

        var parsed = CommandParser.ParseCompound("AT FESIK DCT SUNOL");
        Assert.True(parsed.IsSuccess, parsed.Reason);

        var dispatch = CommandDispatcher.DispatchCompound(
            parsed.Value!,
            ac,
            TestDispatch.Context(new SerializableRandom(42), validateDctFixes: false)
        );
        Assert.True(dispatch.Success, dispatch.Message);

        var dtoBefore = Assert.IsType<InitialClimbPhaseDto>(phase.ToSnapshot());
        Assert.True(dtoBefore.RvSidActive, "RV SID hold should remain until the conditional block fires.");

        var block = Assert.Single(ac.Queue.Blocks);
        Assert.NotNull(block.ApplyAction);
        Assert.NotNull(block.Trigger);

        var applyResult = block.ApplyAction!(ac);
        Assert.True(applyResult.Success, applyResult.Message);

        var dtoAfter = Assert.IsType<InitialClimbPhaseDto>(phase.ToSnapshot());
        Assert.False(dtoAfter.RvSidActive, "AT-fix DCT firing from the queue must release _rvSidActive the same as an immediate DCT.");
    }

    /// <summary>
    /// When DCT fails at apply time (unprogrammed fix with validation on), the RV SID hold
    /// must stay armed — <see cref="Phase.OnCommandAccepted"/> must not run before success.
    /// </summary>
    [Fact]
    public void DctToUnprogrammedFix_WhenApplyFails_KeepsRvSidActive()
    {
        TestVnasData.EnsureInitialized();
        var (phase, ac, _) = BuildRvSidClimbHarness();

        var parsed = CommandParser.ParseCompound("DCT SUNOL");
        Assert.True(parsed.IsSuccess, parsed.Reason);

        var dispatch = CommandDispatcher.DispatchCompound(
            parsed.Value!,
            ac,
            TestDispatch.Context(new SerializableRandom(42), validateDctFixes: true)
        );
        Assert.False(dispatch.Success, "SUNOL is not on the filed route; DCT must fail when fix validation is on.");

        var dto = Assert.IsType<InitialClimbPhaseDto>(phase.ToSnapshot());
        Assert.True(dto.RvSidActive, "Failed DCT must not release the RV SID heading hold.");
    }

    /// <summary>
    /// RV SID with a CA leg before the VM leg (<c>RvSidDeferHeadingUntilMinAlt = true</c>):
    /// the aircraft holds runway heading until 400 ft AGL (IFR — no past-DER), then transitions
    /// to the VM heading. DCT issued before the gate releases must still clear both
    /// <c>_rvSidActive</c> AND the deferred-turn gate so the phase isn't trapped waiting
    /// for a vector transition that the controller has now overridden.
    /// </summary>
    [Fact]
    public void DctDuringDeferredRvSidHold_AlsoReleasesDeferredTurnGate()
    {
        TestVnasData.EnsureInitialized();
        var (phase, ac, ctx) = BuildRvSidClimbHarness(deferUntilMinAlt: true);

        var dtoBefore = Assert.IsType<InitialClimbPhaseDto>(phase.ToSnapshot());
        Assert.True(dtoBefore.RvSidActive, "Precondition: RV SID hold active.");
        Assert.False(dtoBefore.VfrTurnApplied, "Precondition: deferred-turn gate is still armed (CA-before-VM).");

        var parsed = CommandParser.ParseCompound("DCT FESIK");
        Assert.True(parsed.IsSuccess);

        var dispatch = CommandDispatcher.DispatchCompound(
            parsed.Value!,
            ac,
            TestDispatch.Context(new SerializableRandom(42), validateDctFixes: false)
        );
        Assert.True(dispatch.Success, dispatch.Message);

        // Run one sub-tick so OnTick sees the post-OnCommandAccepted state.
        phase.OnTick(ctx);
        FlightPhysics.Update(ac, 1.0, _ => null);

        var dtoAfter = Assert.IsType<InitialClimbPhaseDto>(phase.ToSnapshot());
        Assert.False(dtoAfter.RvSidActive, "DCT must release _rvSidActive even in the CA-before-VM variant.");
        Assert.True(
            dtoAfter.VfrTurnApplied,
            "DCT must clear the deferred-turn gate; otherwise OnTick would still try to apply the published RV vector on reaching 400 ft AGL."
        );
    }
}
