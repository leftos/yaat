using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// <c>PUSHM</c> end to end on the real SFO ramp: the tug move a ZOA ground controller makes off gate D15 into
/// the six alley, <c>PUSH $6A</c> then <c>PUSH $6B</c> in the ground video, issued here as one
/// <c>PUSHM $6A $6B</c>. The engine is driven rather than the phase ticked directly —
/// <see cref="FlightPhysics"/> is the only integrator of ground speed, so a bare OnTick loop would leave the
/// aircraft parked on the stand.
///
/// <para>The move is two legs of different kinds: D15 is a stand, so leg 1 can only be a push (the tug
/// reverses the aircraft tail-first out of the gate), and spot 6B sits ahead of the nose from 6A, so leg 2 is
/// a pull (the tug tows it forward). The alley case is the one the bug report hits: an aircraft already out of
/// its gate, holding in the alley, given a move whose first leg is ahead of it.</para>
///
/// <para>Every node is resolved by name — ids are geometry-coupled and renumber whenever the layout is
/// regenerated. A missing SFO layout silently skips, the repo's convention for absent test data.</para>
/// </summary>
public class SfoPushRouteE2ETests(ITestOutputHelper output)
{
    private const string Gate = "D15";
    private const string AlleySpot = "6A";
    private const string EndSpot = "6B";
    private const string AircraftType = "B738";
    private const string MoveCommand = "PUSHM $6A $6B";
    private const string TaxiOutCommand = "TAXI A";

    /// <summary>The spot the redirect ends on — the third marking in the same alley, between 6A and 6B.</summary>
    private const string RedirectEndSpot = "6";

    /// <summary>The move a redirect issues mid-move: the same alley, stopping one marking short of the original.</summary>
    private const string RedirectCommand = "PUSHM $6A $6";

    /// <summary>Tick budget for the whole move: ~520 ft reversed at 5 kt then ~140 ft towed at 3 kt, with slack.</summary>
    private const int MoveBudgetSeconds = 300;

    /// <summary>How long the move runs before the mid-move HOLD is issued.</summary>
    private const int MidMoveSeconds = 20;

    /// <summary>
    /// How long the move runs before the mid-move taxi clearance. Later than the hold: a taxi route only
    /// resolves once the aircraft is far enough out of the gate to reach the movement area, and leg 1 is still
    /// running (its target is ~520 ft away at 5 kt), so a leg is still queued behind it.
    /// </summary>
    private const int TaxiCueSeconds = 60;

    /// <summary>How long a held aircraft is watched for movement.</summary>
    private const int HoldObservationSeconds = 15;

    /// <summary>How long the aircraft is watched for a tug leg re-arming behind a taxi clearance.</summary>
    private const int TaxiObservationSeconds = 60;

    /// <summary>How often the per-second trajectory sample is written to the test output.</summary>
    private const int TrajectoryLogInterval = 5;

    /// <summary>Ground speed (kt) below which the aircraft counts as stopped.</summary>
    private const double AtRestSpeedKts = 0.5;

    /// <summary>How close to the spot's rest point the move must finish.</summary>
    private const double OnSpotToleranceFt = 20.0;

    /// <summary>How close to the spot's nose-out heading the nose must finish.</summary>
    private const double NoseOutToleranceDeg = 12.0;

    /// <summary>How far a held aircraft may drift before it counts as still moving.</summary>
    private const double FrozenToleranceFt = 2.0;

    /// <summary>Fuselage length assumed for a type the FAA database does not carry, matching the handler's.</summary>
    private const double DefaultFuselageLengthFt = 110.0;

    /// <summary>
    /// The documented case. <c>PUSHM $6A $6B</c> off gate D15 is accepted, runs to completion, and leaves the
    /// aircraft stopped on spot 6B nose-out — the nosewheel on the marking, which puts the centroid a
    /// half-fuselage behind it, the same geometry <c>PUSH $spot</c> ends in.
    /// </summary>
    [Fact]
    public void PushmFromD15_ComesToRestNoseOutOnTheSecondSpot()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var ac = SfoGroundHarness.SpawnParked(ground, "PSM1", AircraftType, Gate);
        var result = ground.Engine.SendCommand(ac.Callsign, MoveCommand);
        output.WriteLine($"{Gate} stand heading {ac.TrueHeading.Degrees:F0}° — '{MoveCommand}' → success={result.Success} \"{result.Message}\"");
        Assert.True(result.Success, $"'{MoveCommand}' off {Gate} was refused: {result.Message}");

        var run = TickMove(ground, ac, MoveBudgetSeconds);
        var rest = SpotRest(ground.Layout, EndSpot);
        double offRestFt = DistanceFt(ac.Position, rest.Position);
        double offNoseOutDeg = new TrueHeading(rest.OutHeadingDeg).AbsAngleTo(ac.TrueHeading);

        output.WriteLine($"legs: {Describe(run.Legs)}, finished t={run.CompletedSecond}s, phase={PhaseName(ac)}");
        output.WriteLine(
            $"at rest {offRestFt:F1} ft off the {EndSpot} rest point, nose {ac.TrueHeading.Degrees:F1}° "
                + $"vs nose-out {rest.OutHeadingDeg:F1}° ({offNoseOutDeg:F1}° off), gs={ac.GroundSpeed:F2}kt"
        );

        Assert.True(
            run.CompletedSecond > 0,
            $"the move never finished within {MoveBudgetSeconds}s (phase={PhaseName(ac)}, legs={Describe(run.Legs)})"
        );
        Assert.True(
            offRestFt <= OnSpotToleranceFt,
            $"the move ended {offRestFt:F1} ft from spot {EndSpot}'s rest point, past the {OnSpotToleranceFt:F0} ft tolerance"
        );
        Assert.True(
            offNoseOutDeg <= NoseOutToleranceDeg,
            $"the nose finished {offNoseOutDeg:F1}° off spot {EndSpot}'s {rest.OutHeadingDeg:F0}° nose-out heading, past the "
                + $"{NoseOutToleranceDeg:F0}° tolerance — a spot pushback leaves the aircraft pointed out to taxi"
        );
        Assert.True(ac.GroundSpeed <= AtRestSpeedKts, $"the aircraft was still moving at {ac.GroundSpeed:F2} kt when the move ended");
        Assert.IsType<AtParkingPhase>(ac.Phases?.CurrentPhase);
    }

    /// <summary>
    /// The leg kinds the planner chose are the kinds that run. Leg 1 off the stand reverses the aircraft, so
    /// <c>Ground.PushbackTrueHeading</c> — the heading <see cref="FlightPhysics"/> displaces along, tail-first —
    /// is set for the whole leg; leg 2 tows it forward onto 6B, so that field stays null and the aircraft moves
    /// nose-first.
    /// </summary>
    [Fact]
    public void PushmFromD15_RunsLegOneAsAPushAndLegTwoAsAPull()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var ac = SfoGroundHarness.SpawnParked(ground, "PSM2", AircraftType, Gate);
        var result = ground.Engine.SendCommand(ac.Callsign, MoveCommand);
        Assert.True(result.Success, $"'{MoveCommand}' off {Gate} was refused: {result.Message}");

        var run = TickMove(ground, ac, MoveBudgetSeconds);
        output.WriteLine($"legs: {Describe(run.Legs)}, finished t={run.CompletedSecond}s");

        Assert.Equal(2, run.Legs.Count);
        Assert.Equal(PushbackLegKind.Push, run.Legs[0].Kind);
        Assert.Equal(PushbackLegKind.Pull, run.Legs[1].Kind);
        Assert.True(
            run.Legs[0].EverPushHeadingSet && !run.Legs[0].EverPushHeadingNullUnderWay,
            "leg 1 off the stand moved on a tick with Ground.PushbackTrueHeading null — that tick took the aircraft nose-first out of its gate"
        );
        Assert.False(
            run.Legs[1].EverPushHeadingSet,
            "leg 2 set Ground.PushbackTrueHeading — that field reverses the aircraft, and a pull leg tows it forward"
        );
    }

    /// <summary>
    /// The bug report's case: an aircraft already off its gate, holding in the alley, is not on a stand, so a
    /// first leg ahead of the nose is a tow rather than a refusal. It runs as a pull.
    /// </summary>
    [Fact]
    public void PushmFromTheAlley_FirstLegAheadOfTheNose_RunsAsAPull()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var from = Spot(ground.Layout, EndSpot);
        var toward = Spot(ground.Layout, AlleySpot);
        var facing = new TrueHeading(GeoMath.BearingTo(from.Position, toward.Position));
        var ac = SfoGroundHarness.SpawnAt(ground, "PSM3", AircraftType, (from, facing), new HoldingAfterPushbackPhase());

        var result = ground.Engine.SendCommand(ac.Callsign, MoveCommand);
        output.WriteLine($"holding in the alley facing {facing.Degrees:F0}° — '{MoveCommand}' → success={result.Success} \"{result.Message}\"");
        Assert.True(result.Success, $"'{MoveCommand}' from the alley was refused: {result.Message}");

        var run = TickMove(ground, ac, MoveBudgetSeconds);
        output.WriteLine($"legs: {Describe(run.Legs)}");

        Assert.NotEmpty(run.Legs);
        Assert.Equal(PushbackLegKind.Pull, run.Legs[0].Kind);
        Assert.False(
            run.Legs[0].EverPushHeadingSet,
            "the first leg set Ground.PushbackTrueHeading — the aircraft was reversed toward a target that was ahead of its nose"
        );
    }

    /// <summary>
    /// <c>HOLD</c> stops the tug where it is without dropping the move, and <c>RES</c> puts it back under way on
    /// the very leg it was running — not on the next one, and not on a fresh plan.
    /// </summary>
    [Fact]
    public void PushmHeldMidMove_ResumesOnTheLegItWasRunning()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var ac = SfoGroundHarness.SpawnParked(ground, "PSM4", AircraftType, Gate);
        Assert.True(ground.Engine.SendCommand(ac.Callsign, MoveCommand) is { Success: true });
        SfoGroundHarness.TickUntil(ground.Engine, () => false, MidMoveSeconds, null);

        var running = Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        var hold = ground.Engine.SendCommand(ac.Callsign, "HOLD");
        Assert.True(hold.Success, $"'HOLD' during the tug move was refused: {hold.Message}");

        var heldAt = ac.Position;
        SfoGroundHarness.TickUntil(ground.Engine, () => false, HoldObservationSeconds, null);
        double driftFt = DistanceFt(ac.Position, heldAt);
        output.WriteLine(
            $"held on leg {running.Kind} at t={MidMoveSeconds}s: drifted {driftFt:F1} ft over {HoldObservationSeconds}s, gs={ac.GroundSpeed:F2}kt"
        );

        Assert.True(driftFt <= FrozenToleranceFt, $"the held aircraft drifted {driftFt:F1} ft — HOLD stops the tug");
        Assert.Same(running, ac.Phases?.CurrentPhase);

        var resume = ground.Engine.SendCommand(ac.Callsign, "RES");
        Assert.True(resume.Success, $"'RES' after the hold was refused: {resume.Message}");
        Assert.Same(running, ac.Phases?.CurrentPhase);

        var run = TickMove(ground, ac, MoveBudgetSeconds);
        var rest = SpotRest(ground.Layout, EndSpot);
        double offRestFt = DistanceFt(ac.Position, rest.Position);
        output.WriteLine($"resumed legs: {Describe(run.Legs)}, finished t={run.CompletedSecond}s, {offRestFt:F1} ft off the {EndSpot} rest point");

        Assert.True(run.CompletedSecond > 0, $"the resumed move never finished within {MoveBudgetSeconds}s (phase={PhaseName(ac)})");
        Assert.True(offRestFt <= OnSpotToleranceFt, $"the resumed move ended {offRestFt:F1} ft from spot {EndSpot}'s rest point");
    }

    /// <summary>
    /// A taxi clearance mid-move drops the whole move, not just the leg under way: with the remaining legs
    /// still queued behind the running one, clearing the phase has to take them with it or a leg re-arms behind
    /// the taxi and reverses the aircraft mid-clearance.
    /// </summary>
    [Fact]
    public void TaxiMidMove_DropsEveryQueuedLeg()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var ac = SfoGroundHarness.SpawnParked(ground, "PSM5", AircraftType, Gate);
        Assert.True(ground.Engine.SendCommand(ac.Callsign, MoveCommand) is { Success: true });
        SfoGroundHarness.TickUntil(ground.Engine, () => false, TaxiCueSeconds, null);

        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        int queuedLegs = ac.Phases!.Phases.Count(p => p is PushbackPhase);
        output.WriteLine($"at t={TaxiCueSeconds}s the move has {queuedLegs} tug legs in the phase list");
        Assert.True(queuedLegs >= 2, $"the move only queued {queuedLegs} tug leg(s) — this case needs a leg still pending behind the running one");

        var taxi = ground.Engine.SendCommand(ac.Callsign, TaxiOutCommand);
        output.WriteLine($"'{TaxiOutCommand}' mid-move → success={taxi.Success} \"{taxi.Message}\"");
        Assert.True(taxi.Success, $"'{TaxiOutCommand}' during the tug move was refused: {taxi.Message}");

        var stillLive = ac.Phases!.Phases.Where(p => (p is PushbackPhase) && (p.Status is PhaseStatus.Active or PhaseStatus.Pending)).ToList();
        Assert.True(stillLive.Count == 0, $"{stillLive.Count} tug leg(s) survived the taxi clearance — the remaining legs must go with the move");

        int reArmedSecond = SfoGroundHarness.TickUntil(ground.Engine, () => ac.Phases?.CurrentPhase is PushbackPhase, TaxiObservationSeconds, null);
        output.WriteLine($"after the taxi clearance: phase={PhaseName(ac)}, gs={ac.GroundSpeed:F2}kt");
        Assert.True(reArmedSecond < 0, $"a queued tug leg re-armed behind the taxi clearance at t={reArmedSecond}s");
    }

    /// <summary>
    /// A move already under way may be redirected: a second <c>PUSHM</c> replaces it whole, so the aircraft
    /// finishes on the new terminus and no leg of the original — the one running or the ones still queued behind
    /// it — survives the swap. The ground-view menu offers the command while a move is running, and an RPO
    /// redirecting an attached tug is ordinary.
    /// </summary>
    [Fact]
    public void PushmMidMove_ReplacesTheRunningMoveAndItsQueuedLegs()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var ac = SfoGroundHarness.SpawnParked(ground, "PSM7", AircraftType, Gate);
        Assert.True(ground.Engine.SendCommand(ac.Callsign, MoveCommand) is { Success: true });
        SfoGroundHarness.TickUntil(ground.Engine, () => false, MidMoveSeconds, null);

        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        var originalLegs = ac.Phases!.Phases.OfType<PushbackPhase>().ToList();
        Assert.True(
            originalLegs.Count >= 2,
            $"the move only queued {originalLegs.Count} tug leg(s) — this case needs a leg still pending behind the running one"
        );

        var redirect = ground.Engine.SendCommand(ac.Callsign, RedirectCommand);
        output.WriteLine($"'{RedirectCommand}' at t={MidMoveSeconds}s into '{MoveCommand}' → success={redirect.Success} \"{redirect.Message}\"");
        Assert.True(redirect.Success, $"'{RedirectCommand}' during the tug move was refused: {redirect.Message}");

        var survivors = ac.Phases!.Phases.Where(p => originalLegs.Any(leg => ReferenceEquals(leg, p))).ToList();
        Assert.True(
            survivors.Count == 0,
            $"{survivors.Count} leg(s) of the original move survived the redirect — a fresh move replaces the one running and everything behind it"
        );

        var run = TickMove(ground, ac, MoveBudgetSeconds);
        var rest = SpotRest(ground.Layout, RedirectEndSpot);
        double offRestFt = DistanceFt(ac.Position, rest.Position);
        double offOldEndFt = DistanceFt(ac.Position, SpotRest(ground.Layout, EndSpot).Position);
        output.WriteLine(
            $"redirected legs: {Describe(run.Legs)}, finished t={run.CompletedSecond}s — {offRestFt:F1} ft off spot "
                + $"{RedirectEndSpot}'s rest point, {offOldEndFt:F1} ft off spot {EndSpot}'s"
        );

        Assert.True(
            run.CompletedSecond > 0,
            $"the redirected move never finished within {MoveBudgetSeconds}s (phase={PhaseName(ac)}, legs={Describe(run.Legs)})"
        );
        Assert.True(
            offRestFt <= OnSpotToleranceFt,
            $"the redirected move ended {offRestFt:F1} ft from spot {RedirectEndSpot}'s rest point, past the {OnSpotToleranceFt:F0} ft tolerance"
        );
        Assert.True(
            offOldEndFt > OnSpotToleranceFt,
            $"the aircraft came to rest {offOldEndFt:F1} ft from spot {EndSpot}, the terminus of the move the redirect was supposed to replace"
        );
    }

    /// <summary>
    /// A move the planner refuses changes nothing: the aircraft keeps the phase it was in, still active, so a
    /// mis-clicked second point cannot leave it stranded off its stand.
    /// </summary>
    [Fact]
    public void RefusedPushm_LeavesTheAircraftsPhaseUntouched()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var ac = SfoGroundHarness.SpawnParked(ground, "PSM6", AircraftType, Gate);
        var before = Assert.IsType<AtParkingPhase>(ac.Phases?.CurrentPhase);
        var bar = NearestRunwayHoldShort(ground.Layout, ac.Position);

        string command = $"PUSHM ${AlleySpot} #{bar.Id}";
        var result = ground.Engine.SendCommand(ac.Callsign, command);
        output.WriteLine(
            $"'{command}' (second point is the {bar.Name ?? "unnamed"} holding position) → success={result.Success} \"{result.Message}\""
        );

        Assert.False(result.Success, $"a leg to a runway holding position was accepted: {result.Message}");
        Assert.Contains("Unable", result.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Same(before, ac.Phases?.CurrentPhase);
        Assert.Equal(PhaseStatus.Active, before.Status);
        Assert.Null(ac.Ground.PushbackTrueHeading);
    }

    /// <summary>What one leg of a tug move did while it ran.</summary>
    private sealed class LegTrace
    {
        /// <summary>Which way the tug moved the aircraft over the leg.</summary>
        internal required PushbackLegKind Kind { get; init; }

        /// <summary>The second the leg was first observed running.</summary>
        internal required int StartedSecond { get; init; }

        /// <summary>The last second the leg was observed running.</summary>
        internal int EndedSecond { get; set; }

        /// <summary>True when <c>Ground.PushbackTrueHeading</c> was set on any tick of the leg.</summary>
        internal bool EverPushHeadingSet { get; set; }

        /// <summary>
        /// True when it was null on a tick the aircraft was actually moving. Ticks where it is stationary are
        /// not sampled: a leg whose nose has to swing more than the phase's alignment window first rotates in
        /// place with no push heading set, which is the tug hooking up rather than a leg running the wrong way.
        /// </summary>
        internal bool EverPushHeadingNullUnderWay { get; set; }

        /// <summary>The closest the aircraft ever came to the point the leg is steering for, in feet.</summary>
        internal double ClosestToTargetFt { get; set; } = double.PositiveInfinity;
    }

    /// <summary>What one tug move did: its legs in order, and the second the last one ended (-1 on budget).</summary>
    /// <param name="Legs">The legs observed, in the order they ran.</param>
    /// <param name="CompletedSecond">The second the move left its last leg, or -1 when the budget ran out.</param>
    private readonly record struct MoveRun(IReadOnlyList<LegTrace> Legs, int CompletedSecond);

    /// <summary>
    /// Ticks the engine until no tug leg is running, recording one <see cref="LegTrace"/> per leg and writing a
    /// trajectory sample every <see cref="TrajectoryLogInterval"/> seconds.
    /// </summary>
    /// <param name="ground">Engine + layout from <see cref="SfoGroundHarness.Build"/>.</param>
    /// <param name="ac">The aircraft the move is driving.</param>
    /// <param name="budgetSeconds">Tick budget.</param>
    /// <returns>The legs the move ran and when it finished.</returns>
    private MoveRun TickMove(SfoGround ground, AircraftState ac, int budgetSeconds)
    {
        var legs = new List<LegTrace>();
        PushbackPhase? running = null;
        int completed = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => (legs.Count > 0) && (ac.Phases?.CurrentPhase is not PushbackPhase),
            budgetSeconds,
            second =>
            {
                if (ac.Phases?.CurrentPhase is not PushbackPhase phase)
                {
                    return;
                }

                if (!ReferenceEquals(phase, running))
                {
                    running = phase;
                    legs.Add(new LegTrace { Kind = phase.Kind, StartedSecond = second });
                }

                var leg = legs[^1];
                leg.EndedSecond = second;
                leg.EverPushHeadingSet |= ac.Ground.PushbackTrueHeading is not null;
                leg.EverPushHeadingNullUnderWay |= (ac.Ground.PushbackTrueHeading is null) && (ac.GroundSpeed > AtRestSpeedKts);
                double toTargetFt = LegTargetDistanceFt(ac, phase);
                leg.ClosestToTargetFt = Math.Min(leg.ClosestToTargetFt, toTargetFt);
                if ((second % TrajectoryLogInterval) == 0)
                {
                    output.WriteLine(
                        $"  t={second, 3}s leg{legs.Count} {phase.Kind, -4} gs={ac.GroundSpeed, 5:F2}kt "
                            + $"nose={ac.TrueHeading.Degrees, 6:F1}° push={Describe(ac.Ground.PushbackTrueHeading)} "
                            + $"toTarget={toTargetFt, 7:F1}ft pos=({ac.Position.Lat:F6},{ac.Position.Lon:F6})"
                    );
                }
            }
        );

        return new MoveRun(legs, completed);
    }

    /// <summary>
    /// Where a spot pushback leaves the aircraft: the centroid a half-fuselage behind the marking along the
    /// spot's nose-out heading, so the nosewheel sits on the mark. Recomputed here from the layout rather than
    /// read off the phase, so the test pins the geometry and not the handler's own arithmetic.
    /// </summary>
    /// <param name="layout">The SFO ground layout.</param>
    /// <param name="spotName">Name of the ramp spot.</param>
    /// <returns>The rest point and the spot's nose-out heading in degrees true.</returns>
    private static (LatLon Position, double OutHeadingDeg) SpotRest(AirportGroundLayout layout, string spotName)
    {
        var spot = Spot(layout, spotName);
        Assert.True(layout.TryGetSpotOutboundHeading(spot, out double outBearingDeg), $"spot '{spotName}' has no outbound heading in the layout");
        double halfLengthNm = ((FaaAircraftDatabase.Get(AircraftType)?.LengthFt ?? DefaultFuselageLengthFt) / 2.0) / GeoMath.FeetPerNm;
        return (GeoMath.ProjectPoint(spot.Position, new TrueHeading(outBearingDeg).ToReciprocal(), halfLengthNm), outBearingDeg);
    }

    /// <summary>
    /// How far the aircraft is from the point the leg comes to rest on: the pull-forward point when the leg has
    /// one (a spot push stages behind the mark and creeps onto it), else the leg's own target.
    /// </summary>
    /// <param name="ac">The aircraft the leg is moving.</param>
    /// <param name="phase">The leg running.</param>
    /// <returns>Distance in feet, or infinity for a leg with no target.</returns>
    private static double LegTargetDistanceFt(AircraftState ac, PushbackPhase phase)
    {
        if ((phase.PullForwardLatitude is { } pullLat) && (phase.PullForwardLongitude is { } pullLon))
        {
            return DistanceFt(ac.Position, new LatLon(pullLat, pullLon));
        }

        if ((phase.TargetLatitude is { } targetLat) && (phase.TargetLongitude is { } targetLon))
        {
            return DistanceFt(ac.Position, new LatLon(targetLat, targetLon));
        }

        return double.PositiveInfinity;
    }

    private static GroundNode Spot(AirportGroundLayout layout, string spotName) =>
        layout.FindSpotNodeByName(spotName) ?? throw new InvalidOperationException($"the SFO layout has no spot named '{spotName}'");

    /// <summary>The runway holding position nearest <paramref name="from"/> — a point no tug move may reach.</summary>
    private static GroundNode NearestRunwayHoldShort(AirportGroundLayout layout, LatLon from)
    {
        GroundNode? best = null;
        double bestNm = double.MaxValue;
        foreach (var node in layout.Nodes.Values)
        {
            if (node.Type != GroundNodeType.RunwayHoldShort)
            {
                continue;
            }

            double distNm = GeoMath.DistanceNm(from, node.Position);
            if (distNm < bestNm)
            {
                bestNm = distNm;
                best = node;
            }
        }

        Assert.True(best is not null, "the SFO layout carries no runway holding positions");
        return best!;
    }

    private static string Describe(IReadOnlyList<LegTrace> legs) =>
        legs.Count == 0
            ? "(none)"
            : string.Join(", ", legs.Select(l => $"{l.Kind} t={l.StartedSecond}-{l.EndedSecond}s closest={l.ClosestToTargetFt:F1}ft"));

    private static string Describe(TrueHeading? heading) => heading is { } hdg ? $"{hdg.Degrees:F1}°" : "null";

    private static double DistanceFt(LatLon from, LatLon to) => GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;

    private static string PhaseName(AircraftState ac) => ac.Phases?.CurrentPhase?.Name ?? "null";
}
