using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="PushbackPhase"/> running one leg of a tug move in each direction: a push, where the tug reverses
/// the aircraft tail-first and <c>Ground.PushbackTrueHeading</c> is what
/// <see cref="FlightPhysics.UpdatePosition"/> displaces along, and a pull, where the tug tows it nose-first
/// with that field left null so the aircraft moves the ordinary way.
///
/// <para>Both run on the real SFO layout around stand B12: taxilane Yankee sits ~186 ft behind the stand and
/// taxiway Alpha a further ~218 ft beyond it, which is the geometry <see cref="SfoYankeePushTests"/> pins the
/// single-leg push against. Each leg is one <see cref="TugMove"/>: a to-point push or pull, or a line capture.
/// Every node is resolved by name — ids renumber whenever the layout is regenerated.</para>
/// </summary>
public class PushbackPullLegTests(ITestOutputHelper output)
{
    private const string Stand = "B12";
    private const string LaneTaxiway = "Y";
    private const string MovementTaxiway = "A";
    private const string AircraftType = "B738";

    /// <summary>Tick budget for one leg: ~200 ft at the 3 kt tow speed is ~40 s, with room for the arc.</summary>
    private const int LegBudgetSeconds = 180;

    /// <summary>How close to the leg's target the aircraft must come to rest.</summary>
    private const double OnTargetToleranceFt = 15.0;

    /// <summary>How far off the bearing to the target the steering case starts.</summary>
    private const double StartOffsetDeg = 15.0;

    /// <summary>How close to the bearing it travelled a steered nose must finish.</summary>
    private const double SteeredToleranceDeg = 6.0;

    /// <summary>How far ahead of the start the line the capture case pulls onto passes, in feet.</summary>
    private const double LineOffsetFt = 40.0;

    /// <summary>How far off the start nose the capture case's line runs.</summary>
    private const double LineOffsetDeg = 90.0;

    /// <summary>Tick budget for the capture leg.</summary>
    private const int CaptureObservationSeconds = 180;

    /// <summary>Headroom on the per-second curvature bound: the chord is shorter than the arc, and the steps are discrete.</summary>
    private const double CurvatureSlack = 1.05;

    /// <summary>Floating-point slack on the per-second curvature bound, radians.</summary>
    private const double CurvatureSlackRad = 0.002;

    /// <summary>
    /// A pull leg tows the aircraft nose-first: the pushback heading stays null for the whole leg — which is
    /// what makes <see cref="FlightPhysics"/> displace the aircraft forward rather than backward — and the
    /// aircraft comes to rest on the target.
    /// </summary>
    [Fact]
    public void PullLeg_TowsNoseFirst_PushbackHeadingStaysNullAndTheLegCloses()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var start = NearestNodeOnTaxiway(ground.Layout, LaneTaxiway, StandPosition(ground));
        var target = NearestNodeOnTaxiway(ground.Layout, MovementTaxiway, start.Position);
        double bearingDeg = GeoMath.BearingTo(start.Position, target.Position);

        var run = RunLeg(ground, new LegSetup("PUL1", PushbackLegKind.Pull, start, bearingDeg, target));

        double startDistFt = DistanceFt(run.StartPosition, target.Position);
        double finalDistFt = DistanceFt(run.Aircraft.Position, target.Position);
        output.WriteLine(
            $"pull leg: {startDistFt:F0} ft to target, finished t={run.CompletedSecond}s at {finalDistFt:F0} ft, "
                + $"pushHdg at rest={Describe(run.Aircraft.Ground.PushbackTrueHeading)}"
        );

        Assert.True(run.CompletedSecond > 0, $"the pull leg never completed within {LegBudgetSeconds}s (phase={PhaseName(run.Aircraft)})");
        Assert.False(
            run.EverPushbackHeadingSet,
            "the pull leg set Ground.PushbackTrueHeading — that field makes FlightPhysics displace the aircraft tail-first, "
                + "which is a push; a tug towing an aircraft forward must leave it null"
        );
        Assert.True(run.EverPushbackHeadingNull, "the pull leg never ran a tick at all — nothing was sampled");
        Assert.Null(run.Aircraft.Ground.PushbackTrueHeading);
        Assert.True(
            finalDistFt <= OnTargetToleranceFt,
            $"the pull leg came to rest {finalDistFt:F0} ft from its target, past the {OnTargetToleranceFt:F0} ft tolerance "
                + $"(it started {startDistFt:F0} ft away)"
        );
    }

    /// <summary>
    /// The push leg is unchanged by the pull leg sharing its pursuit arc: the tug reverses the aircraft
    /// tail-first off the stand, <c>Ground.PushbackTrueHeading</c> is set on every tick of the leg, and the
    /// aircraft comes to rest on the target.
    /// </summary>
    [Fact]
    public void PushLeg_StillReversesTailFirst_PushbackHeadingSetOnEveryTick()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var stand = FindStand(ground);
        var target = NearestNodeOnTaxiway(ground.Layout, LaneTaxiway, stand.Position);

        // Nose away from the target so the tail points at it — the pose a stand pushing onto its lane is in,
        // and inside the phase's alignment window so the leg starts reversing without an in-place pivot.
        double noseDeg = new TrueHeading(GeoMath.BearingTo(stand.Position, target.Position)).ToReciprocal().Degrees;
        var run = RunLeg(ground, new LegSetup("PSH1", PushbackLegKind.Push, stand, noseDeg, target));

        double startDistFt = DistanceFt(run.StartPosition, target.Position);
        double finalDistFt = DistanceFt(run.Aircraft.Position, target.Position);
        output.WriteLine($"push leg: nose {noseDeg:F0}°, {startDistFt:F0} ft to target, finished t={run.CompletedSecond}s at {finalDistFt:F0} ft");

        Assert.True(run.CompletedSecond > 0, $"the push leg never completed within {LegBudgetSeconds}s (phase={PhaseName(run.Aircraft)})");
        Assert.True(run.EverPushbackHeadingSet, "the push leg never set Ground.PushbackTrueHeading — the aircraft was not being reversed");
        Assert.False(
            run.EverPushbackHeadingNull,
            "the push leg left Ground.PushbackTrueHeading null on a tick of the leg — the aircraft would have moved nose-first that tick"
        );
        Assert.True(
            finalDistFt <= OnTargetToleranceFt,
            $"the push leg came to rest {finalDistFt:F0} ft from its target, past the {OnTargetToleranceFt:F0} ft tolerance "
                + $"(it started {startDistFt:F0} ft away)"
        );
    }

    /// <summary>
    /// A pull leg steers. Started with its target off to one side, the nose curves onto the bearing to it
    /// rather than holding the heading it began on, so at rest the nose lies along the track the aircraft
    /// actually travelled.
    /// </summary>
    [Fact]
    public void PullLeg_TargetOffToOneSide_SteersTheNoseOntoTheBearingItTravels()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var start = NearestNodeOnTaxiway(ground.Layout, LaneTaxiway, StandPosition(ground));
        var target = NearestNodeOnTaxiway(ground.Layout, MovementTaxiway, start.Position);
        double startHeadingDeg = new TrueHeading(GeoMath.BearingTo(start.Position, target.Position) + StartOffsetDeg).Degrees;

        var run = RunLeg(ground, new LegSetup("PUL2", PushbackLegKind.Pull, start, startHeadingDeg, target));
        Assert.True(run.CompletedSecond > 0, $"the pull leg never completed within {LegBudgetSeconds}s (phase={PhaseName(run.Aircraft)})");

        double travelledBearingDeg = GeoMath.BearingTo(run.StartPosition, run.Aircraft.Position);
        double finalNoseDeg = run.Aircraft.TrueHeading.Degrees;
        double offTravelledDeg = new TrueHeading(travelledBearingDeg).AbsAngleTo(run.Aircraft.TrueHeading);
        double turnedDeg = new TrueHeading(startHeadingDeg).AbsAngleTo(run.Aircraft.TrueHeading);
        output.WriteLine(
            $"steered pull leg: start nose {startHeadingDeg:F1}°, travelled bearing {travelledBearingDeg:F1}°, "
                + $"final nose {finalNoseDeg:F1}° — {offTravelledDeg:F1}° off the track, {turnedDeg:F1}° off the start heading"
        );

        Assert.True(
            offTravelledDeg <= SteeredToleranceDeg,
            $"the nose finished {offTravelledDeg:F1}° off the {travelledBearingDeg:F1}° bearing it travelled, past the "
                + $"{SteeredToleranceDeg:F0}° tolerance — a pull leg leads with the nose, so the nose must end up along the track"
        );
        Assert.True(
            turnedDeg > SteeredToleranceDeg,
            $"the nose finished {turnedDeg:F1}° off the {startHeadingDeg:F1}° heading it started on — it never steered, it slid "
                + "sideways to the target"
        );
    }

    /// <summary>
    /// One end of the aircraft is the tug's to steer, and it is steered by curvature alone. A pull onto a line
    /// running 90° off the way the nose points, 40 ft to the side, has to turn hard; measured from the positions
    /// the engine actually produced, every second's nose turn stays within the distance moved that second over
    /// the turn radius, and a second without movement turns the nose not at all.
    /// </summary>
    [Fact]
    public void PullLineCapture_NeverTurnsTheNoseTighterThanTheRadius()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var start = NearestNodeOnTaxiway(ground.Layout, LaneTaxiway, StandPosition(ground));
        double noseDeg = GeoMath.BearingTo(start.Position, NearestNodeOnTaxiway(ground.Layout, MovementTaxiway, start.Position).Position);
        double lineDeg = noseDeg + LineOffsetDeg;
        var linePoint = GeoMath.ProjectPoint(start.Position, new TrueHeading(noseDeg), LineOffsetFt / GeoMath.FeetPerNm);
        var move = TugMove.ViaLine(PushbackLegKind.Pull, linePoint, lineDeg, stopAt: null);
        var planned = TugKinematics.Simulate(new TugPose(start.Position, noseDeg), [move], AircraftType, 1.0).End.Position;
        var phase = new PushbackPhase
        {
            Move = move,
            PlannedEnd = planned,
            ContinuesIntoNextMove = false,
        };
        var ac = SfoGroundHarness.SpawnAt(ground, "PUL4", AircraftType, (start, new TrueHeading(noseDeg)), phase);
        double radiusFt = TugKinematics.TurnRadiusFt(AircraftType, tight: false);

        var previousPosition = ac.Position;
        var previousNose = ac.TrueHeading;
        double worstRatio = 0.0;
        double totalTurnDeg = 0.0;
        int completed = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => ac.Phases?.CurrentPhase is not PushbackPhase,
            CaptureObservationSeconds,
            second =>
            {
                double movedFt = DistanceFt(previousPosition, ac.Position);
                double turnDeg = previousNose.AbsAngleTo(ac.TrueHeading);
                double boundRad = ((movedFt / radiusFt) * CurvatureSlack) + CurvatureSlackRad;
                output.WriteLine($"  t={second, 3}s moved={movedFt, 5:F2}ft turned={turnDeg, 5:F2}° nose={ac.TrueHeading.Degrees, 6:F1}°");
                Assert.True(
                    (turnDeg * Math.PI / 180.0) <= boundRad,
                    $"t={second}s: the nose turned {turnDeg:F2}° over {movedFt:F2} ft, tighter than R={radiusFt:F1} ft"
                );
                worstRatio = Math.Max(worstRatio, movedFt > 0.5 ? (turnDeg * Math.PI / 180.0) / movedFt * radiusFt : 0.0);
                totalTurnDeg += turnDeg;
                previousPosition = ac.Position;
                previousNose = ac.TrueHeading;
            }
        );

        output.WriteLine($"capture finished t={completed}s, turned {totalTurnDeg:F0}° in all, worst turn/(moved/R) {worstRatio:F2}");
        Assert.True(completed > 0, $"the capture never completed within {CaptureObservationSeconds}s (phase={PhaseName(ac)})");
        Assert.True(totalTurnDeg > 60.0, $"the capture turned only {totalTurnDeg:F0}°, so the bound proved nothing");
        Assert.True(new TrueHeading(lineDeg).AbsAngleTo(ac.TrueHeading) <= 2.0, $"the capture ended with the nose on {ac.TrueHeading.Degrees:F1}°");
    }

    /// <summary>
    /// The leg kind rides the snapshot, and a snapshot written before the field existed restores as a push —
    /// the behaviour every pushback had then. Pure DTO work, so it needs no layout.
    /// </summary>
    [Fact]
    public void Kind_SurvivesTheSnapshot_AndAKindlessSnapshotRestoresAsPush()
    {
        var target = new LatLon(37.6188, -122.3750);
        var phase = new PushbackPhase
        {
            Move = TugMove.ToPoint(PushbackLegKind.Pull, target),
            PlannedEnd = target,
            ContinuesIntoNextMove = false,
        };

        var dto = Assert.IsType<PushbackPhaseDto>(phase.ToSnapshot());
        Assert.Equal(PushbackLegKind.Pull, dto.Kind);
        Assert.Equal(PushbackLegKind.Pull, PushbackPhase.FromSnapshot(dto).Kind);

        string json = JsonSerializer.Serialize<PhaseDto>(dto, RecordingJsonOptions.Default);
        var written = JsonNode.Parse(json)?.AsObject();
        Assert.NotNull(written);
        Assert.True(written.Remove("Kind"), $"the pushback snapshot carries no Kind property to remove: {json}");

        var restored = Assert.IsType<PushbackPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(written.ToJsonString(), RecordingJsonOptions.Default));
        Assert.Equal(PushbackLegKind.Push, restored.Kind);
        Assert.Equal(PushbackLegKind.Push, PushbackPhase.FromSnapshot(restored).Kind);
    }

    /// <summary>
    /// A stand push-off snapshotted mid-move, through JSON and back, restores every field it carries — the move and
    /// its flags, the planned end, the stand flag, the facing amendment, the progress, the last position and the
    /// dwell clock — and the restored phase finishes the move on the same planned end.
    /// </summary>
    [Fact]
    public void MidMoveSnapshot_RoundTripsEveryField_AndTheRestoredMoveFinishes()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var stand = FindStand(ground);
        var target = NearestNodeOnTaxiway(ground.Layout, LaneTaxiway, stand.Position);
        double noseDeg = new TrueHeading(GeoMath.BearingTo(stand.Position, target.Position)).ToReciprocal().Degrees;
        var standPose = new TugPose(stand.Position, noseDeg);
        var move = TugMove.ToPoint(PushbackLegKind.Push, target.Position) with { Creep = true, DwellBefore = true, Tight = true };
        var phase = new PushbackPhase
        {
            Move = move,
            PlannedEnd = target.Position,
            StartsAtStand = true,
            ContinuesIntoNextMove = false,
            Amendment = TugAmendment.For(TugGoal.TaxiwayLine(target, LaneTaxiway, noseDeg), standPose),
        };
        Assert.NotNull(phase.Amendment);
        var ac = SfoGroundHarness.SpawnAt(ground, "PSH2", AircraftType, (stand, new TrueHeading(noseDeg)), phase);
        SfoGroundHarness.TickUntil(ground.Engine, () => false, 15, null);
        Assert.Same(phase, ac.Phases?.CurrentPhase);

        var dto = Assert.IsType<PushbackPhaseDto>(phase.ToSnapshot());
        string json = JsonSerializer.Serialize<PhaseDto>(dto, RecordingJsonOptions.Default);
        output.WriteLine(json);
        var readBack = Assert.IsType<PushbackPhaseDto>(JsonSerializer.Deserialize<PhaseDto>(json, RecordingJsonOptions.Default));
        var restored = PushbackPhase.FromSnapshot(readBack);
        string rewritten = JsonSerializer.Serialize<PhaseDto>(restored.ToSnapshot(), RecordingJsonOptions.Default);

        Assert.Equal(json, rewritten);
        Assert.Equal(move, restored.Move);
        Assert.Equal(phase.PlannedEnd, restored.PlannedEnd);
        Assert.True(restored.StartsAtStand);
        Assert.Equal(phase.Amendment, restored.Amendment);
        Assert.True(dto.ProgressDistanceFt > 0.0, "the snapshot was taken before the move had gone anywhere");
        Assert.Equal(PushbackPhase.DwellSeconds, dto.DwellElapsedSeconds);
        Assert.Null(dto.LegacyTargetLatitude);
        Assert.Equal(phase.HasLeftTheStand(ac), restored.HasLeftTheStand(ac));

        // A fresh engine, so the original aircraft still on the same path cannot hold the restored one.
        var restoredGround = SfoGroundHarness.Build(output, autoCross: false)!.Value;
        var twin = SfoGroundHarness.SpawnAt(restoredGround, "PSH3", AircraftType, (stand, new TrueHeading(noseDeg)), restored);
        twin.Position = ac.Position;
        twin.TrueHeading = ac.TrueHeading;
        twin.IndicatedAirspeed = ac.IndicatedAirspeed;
        twin.Ground.PushbackTrueHeading = ac.Ground.PushbackTrueHeading;
        int finished = SfoGroundHarness.TickUntil(
            restoredGround.Engine,
            () => twin.Phases?.CurrentPhase is not PushbackPhase,
            LegBudgetSeconds,
            null
        );

        double offEndFt = DistanceFt(twin.Position, target.Position);
        output.WriteLine($"restored move finished t={finished}s, {offEndFt:F2} ft off the planned end");
        Assert.True(finished > 0, "the restored move never finished");
        Assert.True(offEndFt <= 3.0, $"the restored move ended {offEndFt:F2} ft off its planned end");
    }

    /// <summary>One leg to run: who flies it, which way the tug moves them, from where on what heading, to what.</summary>
    /// <param name="Callsign">Callsign to spawn under.</param>
    /// <param name="Kind">Whether the tug reverses the aircraft or tows it forward.</param>
    /// <param name="From">Layout node the aircraft starts on.</param>
    /// <param name="StartHeadingDeg">True heading the nose starts on.</param>
    /// <param name="Target">Layout node the leg ends on.</param>
    private readonly record struct LegSetup(string Callsign, PushbackLegKind Kind, GroundNode From, double StartHeadingDeg, GroundNode Target);

    /// <summary>What one leg run did: the aircraft, where it began, when the leg ended, and what the pushback heading did.</summary>
    /// <param name="Aircraft">The aircraft the leg moved.</param>
    /// <param name="StartPosition">Where the leg began.</param>
    /// <param name="CompletedSecond">The second the phase ended, or -1 when the budget ran out.</param>
    /// <param name="EverPushbackHeadingSet">True when <c>Ground.PushbackTrueHeading</c> was set on any tick of the leg.</param>
    /// <param name="EverPushbackHeadingNull">True when it was null on any tick of the leg.</param>
    private readonly record struct LegRun(
        AircraftState Aircraft,
        LatLon StartPosition,
        int CompletedSecond,
        bool EverPushbackHeadingSet,
        bool EverPushbackHeadingNull
    );

    /// <summary>
    /// Spawns an aircraft in a <see cref="PushbackPhase"/> running the leg and ticks the engine until the phase
    /// ends, sampling the pushback heading every second the leg is still under way. The engine tick is what
    /// runs <see cref="FlightPhysics"/>, which is the only integrator of ground speed — a bare OnTick loop
    /// would leave the aircraft parked.
    /// </summary>
    /// <param name="ground">Engine + layout from <see cref="SfoGroundHarness.Build"/>.</param>
    /// <param name="setup">The leg to run.</param>
    /// <returns>What the run did.</returns>
    private LegRun RunLeg(SfoGround ground, LegSetup setup)
    {
        var phase = new PushbackPhase
        {
            Move = TugMove.ToPoint(setup.Kind, setup.Target.Position),
            PlannedEnd = setup.Target.Position,
            ContinuesIntoNextMove = false,
        };
        var ac = SfoGroundHarness.SpawnAt(ground, setup.Callsign, AircraftType, (setup.From, new TrueHeading(setup.StartHeadingDeg)), phase);
        var startPosition = ac.Position;

        bool everSet = false;
        bool everNull = false;
        int completed = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => ac.Phases?.CurrentPhase is not PushbackPhase,
            LegBudgetSeconds,
            second =>
            {
                if (ac.Phases?.CurrentPhase is not PushbackPhase)
                {
                    return;
                }

                everSet |= ac.Ground.PushbackTrueHeading is not null;
                everNull |= ac.Ground.PushbackTrueHeading is null;
                if (second % 5 == 0)
                {
                    output.WriteLine(
                        $"  t={second, 3}s gs={ac.GroundSpeed, 5:F2}kt nose={ac.TrueHeading.Degrees, 5:F1}° "
                            + $"push={Describe(ac.Ground.PushbackTrueHeading)} toTarget={DistanceFt(ac.Position, setup.Target.Position), 6:F0}ft"
                    );
                }
            }
        );

        return new LegRun(ac, startPosition, completed, everSet, everNull);
    }

    private static GroundNode FindStand(SfoGround ground)
    {
        var stand = ground.Layout.FindParkingByName(Stand);
        Assert.True(stand is not null, $"the SFO layout has no parking named '{Stand}'");
        return stand!;
    }

    private static LatLon StandPosition(SfoGround ground) => FindStand(ground).Position;

    /// <summary>The node of <paramref name="taxiway"/> nearest <paramref name="from"/>, resolved by taxiway name.</summary>
    private static GroundNode NearestNodeOnTaxiway(AirportGroundLayout layout, string taxiway, LatLon from)
    {
        GroundNode? best = null;
        double bestNm = double.MaxValue;
        foreach (var node in layout.GetNodesOnTaxiway(taxiway))
        {
            double distNm = GeoMath.DistanceNm(from, node.Position);
            if (distNm < bestNm)
            {
                bestNm = distNm;
                best = node;
            }
        }

        Assert.True(best is not null, $"the SFO layout has no nodes on taxiway '{taxiway}'");
        return best!;
    }

    private static double DistanceFt(LatLon from, LatLon to) => GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;

    private static string Describe(TrueHeading? heading) => heading is { } hdg ? $"{hdg.Degrees:F1}°" : "null";

    private static string PhaseName(AircraftState ac) => ac.Phases?.CurrentPhase?.Name ?? "null";
}
