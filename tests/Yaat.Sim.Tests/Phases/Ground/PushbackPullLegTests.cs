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
/// single-leg push against. The push case is here as the pin the pull refactor must not move; the pull cases
/// are the new leg kind. Every node is resolved by name — ids renumber whenever the layout is regenerated.</para>
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

    /// <summary>How far off the bearing to the target the steering case starts, inside the phase's 20° alignment window.</summary>
    private const double StartOffsetDeg = 15.0;

    /// <summary>How close to the bearing it travelled a steered nose must finish.</summary>
    private const double SteeredToleranceDeg = 6.0;

    /// <summary>Length of the short nudge leg the two-nose-writer case runs, in feet.</summary>
    private const double NudgeLegFt = 15.0;

    /// <summary>How far off the travel bearing that case's nose starts, just inside the phase's alignment window.</summary>
    private const double StartLagDeg = 19.0;

    /// <summary>How far off the travel bearing that case's final facing sits, on the side the nose is turning toward.</summary>
    private const double FacingOffsetDeg = 90.0;

    /// <summary>Tick budget for the nudge leg — long enough that the nose settles even if the leg never closes.</summary>
    private const int NudgeObservationSeconds = 60;

    /// <summary>Floating-point slack on the per-second turn-rate ceiling.</summary>
    private const double TurnRateToleranceDeg = 0.1;

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
    /// One end of the aircraft is the tug's to steer, and only one thing may steer it per tick. On a pull leg
    /// the pursuit arc and the final-facing rotation both write the nose, so a leg run with a facing well off
    /// the bearing it travels must still never turn the nose faster than
    /// <see cref="CategoryPerformance.PushbackTurnRate"/> — a tug cannot steer the nose gear twice in one tick.
    ///
    /// <para>The leg is a short nudge deliberately: the pursuit arc is still curving the nose onto the bearing
    /// it travels when the leg captures its target and the final-facing rotation takes the nose over, which is
    /// where two writers would compound. The nose starts inside the phase's 20°
    /// alignment window so the leg begins moving without an in-place pivot, offset to the side the facing is
    /// <em>not</em> on, so both writers pull the nose the same way.</para>
    /// </summary>
    [Fact]
    public void PullLeg_FacingWellOffTheTravelBearing_NeverTurnsTheNoseFasterThanTheTug()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        var start = NearestNodeOnTaxiway(ground.Layout, LaneTaxiway, StandPosition(ground));
        double bearingDeg = GeoMath.BearingTo(start.Position, NearestNodeOnTaxiway(ground.Layout, MovementTaxiway, start.Position).Position);
        var target = GeoMath.ProjectPoint(start.Position, new TrueHeading(bearingDeg), NudgeLegFt / GeoMath.FeetPerNm);

        var phase = new PushbackPhase
        {
            Kind = PushbackLegKind.Pull,
            TargetLatitude = target.Lat,
            TargetLongitude = target.Lon,
            TargetHeading = new TrueHeading(bearingDeg - FacingOffsetDeg).ToDisplayInt(),
        };
        var ac = SfoGroundHarness.SpawnAt(ground, "PUL4", AircraftType, (start, new TrueHeading(bearingDeg + StartLagDeg)), phase);

        double turnRateDegPerSec = CategoryPerformance.PushbackTurnRate(AircraftCategorization.Categorize(AircraftType));
        double previousNoseDeg = ac.TrueHeading.Degrees;
        double worstDeltaDeg = 0;
        int worstSecond = 0;
        SfoGroundHarness.TickUntil(
            ground.Engine,
            () => ac.Phases?.CurrentPhase is not PushbackPhase,
            NudgeObservationSeconds,
            second =>
            {
                double deltaDeg = new TrueHeading(previousNoseDeg).AbsAngleTo(ac.TrueHeading);
                previousNoseDeg = ac.TrueHeading.Degrees;
                if (deltaDeg > worstDeltaDeg)
                {
                    worstDeltaDeg = deltaDeg;
                    worstSecond = second;
                }

                output.WriteLine(
                    $"  t={second, 3}s nose={ac.TrueHeading.Degrees, 6:F1}° turned={deltaDeg, 5:F2}°/s "
                        + $"toTarget={DistanceFt(ac.Position, target), 5:F0}ft phase={PhaseName(ac)}"
                );
            }
        );

        output.WriteLine(
            $"pull leg with facing {phase.TargetHeading:000}° against a {bearingDeg:F0}° travel bearing: "
                + $"fastest nose turn {worstDeltaDeg:F2}°/s at t={worstSecond}s, tug limit {turnRateDegPerSec:F2}°/s"
        );

        Assert.True(
            worstDeltaDeg <= (turnRateDegPerSec + TurnRateToleranceDeg),
            $"the nose turned {worstDeltaDeg:F2}° in the second at t={worstSecond}s, past the tug's {turnRateDegPerSec:F2}°/s — "
                + "the pursuit arc and the final-facing rotation both wrote the nose that tick; a pull leg has one nose writer"
        );
    }

    /// <summary>
    /// The leg kind rides the snapshot, and a snapshot written before the field existed restores as a push —
    /// the behaviour every pushback had then. Pure DTO work, so it needs no layout.
    /// </summary>
    [Fact]
    public void Kind_SurvivesTheSnapshot_AndAKindlessSnapshotRestoresAsPush()
    {
        var phase = new PushbackPhase
        {
            Kind = PushbackLegKind.Pull,
            TargetLatitude = 37.6188,
            TargetLongitude = -122.3750,
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
            Kind = setup.Kind,
            TargetLatitude = setup.Target.Position.Lat,
            TargetLongitude = setup.Target.Position.Lon,
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
