using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Simulation.RejectedTakeoffTestRig;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// GitHub issue #416: the rejected-takeoff scan treated a preceding <em>departing</em> aircraft as
/// no obstacle at all, so a departure would begin its roll behind one still on the pavement.
/// 7110.65 §3-9-6.a is a "does not begin takeoff roll until" rule: from a standstill the preceding
/// departure must have departed and crossed the runway end, or be airborne with the §3-9-6.a landmark distance
/// ahead. Once the roll is underway §3-9-6 is spent and the question is a collision one: the trailer
/// rejects only when its own projected roll catches the leader while the leader is still on the
/// ground (AIM 4-4-1.a — a clearance never authorizes unsafe operation). An opposite-direction
/// roller on the same pavement blocks outright: it earns no spacing credit for the distance it is
/// covering toward the trailer.
/// </summary>
public class Issue416PrecedingDepartureTests
{
    public Issue416PrecedingDepartureTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// §3-9-6.a: the leader is neither past the runway end nor airborne two seconds from now, so the
    /// standstill trailer may not begin its roll — it declines and holds (P/CG ABORT: there is no
    /// maneuver to terminate before the roll starts).
    /// </summary>
    [Fact]
    public void Standstill_LeaderRollingTooClose_DeclinesClearance()
    {
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 0);
        var lead = MakeRollingLeader(runway, downfieldFt: 2000, groundSpeedKts: 60, rejecting: false);
        Assert.Equal(RunwayUseKind.Departing, RunwayOccupancy.Classify(lead, runway, null)?.Kind);
        var ctx = Ctx(dep, runway, lead, autoReject: true);
        dep.Phases!.Start(ctx);

        double maxIas = RunStandstill(dep, ctx, seconds: 20);

        Assert.True(dep.IsOnGround);
        Assert.True(maxIas < 5.0, $"A declined clearance must not roll, peak IAS={maxIas:F1}");
        Assert.True(
            dep.Phases?.CurrentPhase is HoldingInPositionPhase,
            $"Declined departure should hold in position, was {dep.Phases?.CurrentPhase?.Name ?? "(none)"}"
        );
    }

    /// <summary>
    /// §3-9-6.a.4: the leader is airborne with more than the 6,000 ft Category III landmark ahead
    /// (still short of the runway end), so the succeeding departure may begin its roll.
    /// </summary>
    [Fact]
    public void Standstill_LeaderAirborneWithSrsSpacing_DoesNotBlock()
    {
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 0);
        var lead = MakeAirborneLeader(runway, downfieldFt: 7000, iasKts: 160, aglFt: 300);
        Assert.Equal(RunwayUseKind.Departing, RunwayOccupancy.Classify(lead, runway, null)?.Kind);
        Assert.True(7000 < runway.PavementLengthFt, "fixture: the leader must still be short of the runway end");
        var ctx = Ctx(dep, runway, lead, autoReject: true);
        dep.Phases!.Start(ctx);

        var (airborne, _) = RunRoll(dep, ctx, lead, seconds: 120);

        Assert.True(airborne, $"An airborne leader with SRS spacing must not block the roll (IAS={dep.IndicatedAirspeed:F0})");
    }

    /// <summary>
    /// §3-9-6.a needs the leader airborne or past the runway end: an aircraft rejecting its own
    /// takeoff is neither, however far down the runway it is, so the standstill trailer still
    /// declines.
    /// </summary>
    [Fact]
    public void Standstill_LeaderRejectingFarAhead_StillDeclines()
    {
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 0);
        var lead = MakeRollingLeader(runway, downfieldFt: 5000, groundSpeedKts: 80, rejecting: true);
        var ctx = Ctx(dep, runway, lead, autoReject: true);
        dep.Phases!.Start(ctx);

        double maxIas = RunStandstill(dep, ctx, seconds: 20);

        Assert.True(maxIas < 5.0, $"A declined clearance must not roll, peak IAS={maxIas:F1}");
        Assert.True(
            dep.Phases?.CurrentPhase is HoldingInPositionPhase,
            $"Declined departure should hold in position, was {dep.Phases?.CurrentPhase?.Name ?? "(none)"}"
        );
    }

    /// <summary>
    /// Roll underway, §3-9-6 spent: the leader is faster and accelerating, so the trailer's projected
    /// roll never catches it before it flies. No reject — the takeoff continues.
    /// </summary>
    [Fact]
    public void Rolling_LeaderOutrunsTrailer_NoReject()
    {
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 60);
        var lead = MakeRollingLeader(runway, downfieldFt: 6000, groundSpeedKts: 120, rejecting: false);
        Assert.Equal(RunwayUseKind.Departing, RunwayOccupancy.Classify(lead, runway, null)?.Kind);
        var ctx = Ctx(dep, runway, lead, autoReject: true);
        dep.Phases!.Start(ctx);

        var (airborne, _) = RunRoll(dep, ctx, lead, seconds: 120);

        Assert.True(airborne, $"A leader that outruns the trailer must not trigger a reject (IAS={dep.IndicatedAirspeed:F0})");
    }

    /// <summary>
    /// Roll underway in the low-speed regime with the leader braking to a stop ahead: the rendezvous
    /// happens while the leader is still on the ground, so the trailer rejects and stops short
    /// (AIM 4-4-1.a, 14 CFR 91.3(a)).
    /// </summary>
    [Fact]
    public void Rolling_LowSpeed_LeaderRejectingAhead_Rejects()
    {
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 60);
        var lead = MakeRollingLeader(runway, downfieldFt: 2500, groundSpeedKts: 80, rejecting: true);
        var ctx = Ctx(dep, runway, lead, autoReject: true);
        dep.Phases!.Start(ctx);

        var (airborne, minSepFt) = RunRoll(dep, ctx, lead, seconds: 120);

        Assert.False(airborne, $"The trailer must reject, not lift off (IAS={dep.IndicatedAirspeed:F0})");
        Assert.True(dep.IndicatedAirspeed < 1.0, $"Trailer should be stopped, IAS={dep.IndicatedAirspeed:F0}");
        Assert.True(minSepFt > 500, $"Trailer must stop short of the leader, min separation {minSepFt:F0} ft");
        Assert.True(
            dep.Phases?.CurrentPhase is HoldingInPositionPhase,
            $"Trailer should hold in position awaiting instructions, was {dep.Phases?.CurrentPhase?.Name ?? "(none)"}"
        );
    }

    /// <summary>
    /// An opposite-direction departure rolling toward the trailer from the far end is
    /// <see cref="RunwayUseKind.Departing"/> too (the axis test is modulo 180). Projected along the
    /// trailer's runway direction it runs past the runway end, which must earn it no §3-9-6.a
    /// credit: the trailer declines.
    /// </summary>
    [Fact]
    public void OppositeDirectionRoller_AtFarEnd_Blocks()
    {
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 0);
        var lead = MakeOpposingLeader(runway, downfieldFt: runway.PavementLengthFt - 100, groundSpeedKts: 60);
        Assert.Equal(RunwayUseKind.Departing, RunwayOccupancy.Classify(lead, runway, null)?.Kind);
        var ctx = Ctx(dep, runway, lead, autoReject: true);
        dep.Phases!.Start(ctx);

        double maxIas = RunStandstill(dep, ctx, seconds: 20);

        Assert.True(maxIas < 5.0, $"A declined clearance must not roll, peak IAS={maxIas:F1}");
        Assert.True(
            dep.Phases?.CurrentPhase is HoldingInPositionPhase,
            $"Declined departure should hold in position, was {dep.Phases?.CurrentPhase?.Name ?? "(none)"}"
        );
    }

    /// <summary>
    /// The distance reported for a head-on roller is the closing one. A trailer in the high-speed
    /// regime rejects only for an occupant it cannot overfly, so treating the closing aircraft as
    /// parked would let it roll on: 8,000 ft is comfortably past the liftoff-plus-climb margin, while
    /// the ~1,700 ft left when the two runs actually meet is not (AIM 4-4-1.a, 14 CFR 91.3(a)).
    /// </summary>
    [Fact]
    public void OppositeDirectionRoller_ClosingDistance_RejectsHighSpeedRoll()
    {
        var runway = Runway28R();
        var dep = MakeRollingDeparture(runway, iasKts: 90);
        var lead = MakeOpposingLeader(runway, downfieldFt: 8000, groundSpeedKts: 60);
        Assert.Equal(RunwayUseKind.Departing, RunwayOccupancy.Classify(lead, runway, null)?.Kind);
        var cat = AircraftCategorization.Categorize(dep.AircraftType);
        Assert.True(
            GroundFrame.IasForGroundSpeed(dep, dep.IndicatedAirspeed) > CategoryPerformance.LowSpeedRejectThresholdKts(cat),
            "fixture: the trailer must be in the high-speed regime, where only the overfly math can reject"
        );
        Assert.True(
            RejectedTakeoff.CanOverfly(dep, cat, 8000 - RejectedTakeoff.StopMarginFt),
            "fixture: the stationary distance would be overflyable"
        );
        var ctx = Ctx(dep, runway, lead, autoReject: true);
        dep.Phases!.Start(ctx);

        var (airborne, _) = RunRoll(dep, ctx, lead, seconds: 120);

        Assert.False(airborne, $"A closing head-on roller must be judged on the closing distance (IAS={dep.IndicatedAirspeed:F0})");
        Assert.True(dep.IndicatedAirspeed < 1.0, $"Trailer should be stopped, IAS={dep.IndicatedAirspeed:F0}");
        Assert.True(
            dep.Phases?.CurrentPhase is HoldingInPositionPhase,
            $"Trailer should hold in position awaiting instructions, was {dep.Phases?.CurrentPhase?.Name ?? "(none)"}"
        );
    }

    /// <summary>Ticks a standstill departure for <paramref name="seconds"/> and returns the peak IAS it ever reached.</summary>
    private static double RunStandstill(AircraftState departure, PhaseContext ctx, int seconds)
    {
        double maxIas = 0;
        for (int t = 0; t < seconds; t++)
        {
            PhaseRunner.Tick(departure, ctx);
            IntegrateGroundDisplacement(departure);
            maxIas = Math.Max(maxIas, departure.IndicatedAirspeed);
        }

        return maxIas;
    }
}
