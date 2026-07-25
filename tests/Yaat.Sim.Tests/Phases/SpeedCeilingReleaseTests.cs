using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;

namespace Yaat.Sim.Tests;

/// <summary>
/// A phase that imposes a <see cref="ControlTargets.SpeedCeiling"/> must release it on the way out.
///
/// <c>SpeedCeiling</c> has no UI representation — it is not an <c>Assigned*</c> field — so one left behind is a silent,
/// permanent cap. <see cref="ProcedureTurnPhase"/> re-applied the AIM 5-4-9.a.3 200 KIAS limit every tick but had no
/// <c>OnEnd</c>, and neither <c>PhaseList.Clear</c> nor the dispatcher's phase-clear block releases it, so a jet pulled
/// off the procedure turn with a vector stayed capped at 200 KIAS for the rest of the session.
/// </summary>
public sealed class SpeedCeilingReleaseTests
{
    public SpeedCeilingReleaseTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static (AircraftState Aircraft, PhaseContext Ctx) MakeFixture()
    {
        var aircraft = new AircraftState
        {
            Callsign = "SKW5131",
            AircraftType = "CRJ2",
            Position = new LatLon(37.99, -122.05),
            TrueHeading = new TrueHeading(360),
            Altitude = 4000,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan { Destination = "CCR" },
        };
        aircraft.Phases = new PhaseList();

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            FieldElevation = 26,
            Logger = NullLogger.Instance,
        };

        return (aircraft, ctx);
    }

    [Fact]
    public void ProcedureTurn_ReleasesItsSpeedCeiling_WhenThePhaseEnds()
    {
        var (aircraft, ctx) = MakeFixture();

        var pt = new ProcedureTurnPhase
        {
            FixName = "PTFIX",
            FixLat = 37.99,
            FixLon = -122.05,
            InboundCourseDeg = 360,
            PtOutboundCourseDeg = 225,
            MaxOutboundDistanceNm = 10,
            OneEightyTurnDirection = TurnDirection.Right,
            MinAltitudeFt = 3000,
        };
        aircraft.Phases!.Add(pt);

        pt.OnStart(ctx);
        pt.OnTick(ctx);

        // Precondition: the phase really did impose its cap, so this cannot pass vacuously.
        Assert.Equal(200.0, ctx.Targets.SpeedCeiling);

        pt.OnEnd(ctx, PhaseStatus.Completed);

        Assert.Null(ctx.Targets.SpeedCeiling);
    }

    /// <summary>
    /// A ceiling the controller set tighter than the procedure-turn cap was never overwritten by the phase, so it has
    /// to outlive it — releasing unconditionally would silently drop a real speed restriction.
    /// </summary>
    [Fact]
    public void ProcedureTurn_LeavesATighterControllerCeilingAlone()
    {
        var (aircraft, ctx) = MakeFixture();

        var pt = new ProcedureTurnPhase
        {
            FixName = "PTFIX",
            FixLat = 37.99,
            FixLon = -122.05,
            InboundCourseDeg = 360,
            PtOutboundCourseDeg = 225,
            MaxOutboundDistanceNm = 10,
            OneEightyTurnDirection = TurnDirection.Right,
            MinAltitudeFt = 3000,
        };
        aircraft.Phases!.Add(pt);

        ctx.Targets.SpeedCeiling = 180.0;

        pt.OnStart(ctx);
        pt.OnTick(ctx);
        Assert.Equal(180.0, ctx.Targets.SpeedCeiling);

        pt.OnEnd(ctx, PhaseStatus.Completed);

        Assert.Equal(180.0, ctx.Targets.SpeedCeiling);
    }
}
