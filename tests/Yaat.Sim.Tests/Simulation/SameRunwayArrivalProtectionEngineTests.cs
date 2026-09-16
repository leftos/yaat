using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The engine half of the same-runway arrival protection — <c>SimulationEngine.ApplySameRunwayArrivalProtection</c>
/// and the eligibility, release and announcement rules around it.
/// <see cref="SameRunwayArrivalProtectionTests"/> covers the pure arithmetic and
/// <c>SfoGroundControlArrivalGoAroundTests</c> covers the end-to-end path over a real recording; what is left — and
/// what this class pins — is the pass's own decision-making: who it may manage, when it hands the speed back, what it
/// hands back, and how often it says so.
///
/// <para>Every arm drives the pass through <see cref="SimulationEngine.TickPrePhysics"/> rather than
/// <c>TickOneSecond</c>. That is the real spine step the pass runs in (it is called there, before any physics), and
/// stopping there means the geometry the pass evaluates is exactly the geometry the arm set up: no aircraft moves and
/// no speed changes between the setup and the assertion, so a test that places a follower at a computed threshold
/// interval gets that interval rather than one a second of deceleration has already moved. It also leaves
/// <see cref="AircraftState.PendingNotifications"/> undrained, which is what makes the announcement arm countable.</para>
///
/// <para>Aircraft are injected on the OAK runway 30 final with <see cref="AircraftInitializer.InitializeOnFinal"/>,
/// the same construction <see cref="ArrivalGeneratorInTrailSpacingTests"/> uses, and none of them is a generator
/// arrival — a scenario-scripted stream that the generator spacing manager never touches is the whole reason this
/// pass exists.</para>
/// </summary>
public class SameRunwayArrivalProtectionEngineTests(ITestOutputHelper output)
{
    private const string ScenarioPath = "TestData/issue153-s2-oak-5-2-scenario.json";

    /// <summary>Distance (nm) the leader of a conflicting pair is placed at: far enough out to be airborne and slow.</summary>
    private const double LeaderDistanceNm = 8.0;

    /// <summary>Distance (nm) the follower of a conflicting pair is placed at — clear of the §5-7-1.b.4 window.</summary>
    private const double FollowerDistanceNm = 11.0;

    /// <summary>Distance (nm) a follower is moved back to when an arm wants the conflict to be unambiguously gone.</summary>
    private const double ClearOfConflictDistanceNm = 45.0;

    /// <summary>
    /// A published crossing-speed restriction (kt) parked on the follower's
    /// <see cref="ControlTargets.SpeedCeiling"/> before the pass engages, standing in for the one a scripted STAR
    /// arrival carries out of <see cref="FlightPhysics"/>. Chosen above anything the pass itself will ask for at
    /// <see cref="FollowerDistanceNm"/>, so the stamp genuinely displaces it.
    /// </summary>
    private const double PublishedCrossingCeilingKts = 210.0;

    /// <summary>
    /// How far past the required interval an arm places a follower when it wants to sit inside the release deadband:
    /// clear of the engage threshold, still short of
    /// <see cref="SameRunwayArrivalProtection.ReleaseHysteresisSeconds"/>.
    /// </summary>
    private const double DeadbandProbeSeconds = 5.0;

    [Fact]
    public void ProtectionReleases_WhenTheConflictClears()
    {
        var pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Pass();
        Report(pair, "engaged");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(pair.Follower.Approach.SameRunwayProtectionCeilingKts, pair.Follower.Targets.SpeedCeiling);

        pair.PlaceFollowerAt(ClearOfConflictDistanceNm);
        pair.Pass();
        Report(pair, "released");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Approach.SameRunwayProtectionDisplacedCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// The release must put back the ceiling it displaced, not null the field. A scenario-scripted arrival flies a
    /// STAR and can be carrying a published crossing-speed restriction (§5-7-1.b NOTE) that
    /// <see cref="FlightPhysics"/> stamps once, on the tick the fix is sequenced, and never re-stamps — so clearing
    /// the field outright deletes it for the rest of the flight, which only DELETE SPEED RESTRICTIONS (§5-7-2.e) may
    /// do.
    /// </summary>
    [Fact]
    public void ProtectionRelease_PutsBackTheDisplacedPublishedCeiling()
    {
        var pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Follower.Targets.SpeedCeiling = PublishedCrossingCeilingKts;

        pair.Pass();
        Report(pair, "engaged over a published ceiling");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(PublishedCrossingCeilingKts, pair.Follower.Approach.SameRunwayProtectionDisplacedCeilingKts);
        Assert.True(
            pair.Follower.Targets.SpeedCeiling < PublishedCrossingCeilingKts,
            $"the protection stamped {pair.Follower.Targets.SpeedCeiling:F0} kt, which does not lower the published "
                + $"{PublishedCrossingCeilingKts:F0} kt ceiling — this arm is no longer exercising a displacement"
        );

        pair.PlaceFollowerAt(ClearOfConflictDistanceNm);
        pair.Pass();
        Report(pair, "released over a published ceiling");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(PublishedCrossingCeilingKts, pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// The engaged half of the release deadband: an interval that has opened past the required one but by less than
    /// <see cref="SameRunwayArrivalProtection.ReleaseHysteresisSeconds"/> keeps the ceiling. Without the deadband the
    /// boundary limit-cycles — release, the follower accelerates at its jet rate, the interval closes, re-engage —
    /// with a fresh terminal line each lap. Read together with
    /// <see cref="FreshPair_DoesNotEngage_InsideTheReleaseDeadband"/>: the same interval, opposite outcomes, which is
    /// exactly what a deadband is.
    /// </summary>
    [Fact]
    public void EngagedProtection_HoldsThroughASmallIntervalImprovement()
    {
        var pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Pass();
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        pair.PlaceFollowerAtInterval(DeadbandProbeSeconds);
        pair.Pass();
        Report(pair, "after a small interval improvement");

        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(pair.Follower.Approach.SameRunwayProtectionCeilingKts, pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// The un-engaged half of the same deadband: the engage test is the required interval itself, so a pair that is
    /// already legal by any margin is left alone. The deadband widens the release, never the engagement.
    /// </summary>
    [Fact]
    public void FreshPair_DoesNotEngage_InsideTheReleaseDeadband()
    {
        var pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.PlaceFollowerAtInterval(DeadbandProbeSeconds);
        pair.Pass();
        Report(pair, "fresh pair inside the deadband");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// A human controller's speed assignment takes the aircraft off the simulated TRACON's books — the pass only
    /// spaces an arrival while it owns its speed (§5-7-4 retains a controller's assignment until it is deleted).
    /// </summary>
    [Fact]
    public void ControllerIssuedSpeed_HandsSpeedAuthorityBack()
    {
        var pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Pass();
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        var result = pair.Engine.SendCommand(pair.Follower.Callsign, "SPD 200");
        Assert.True(result.Success, result.Message);
        Assert.True(pair.Follower.Targets.SpeedCommandIsControllerIssued);

        pair.Pass();
        Report(pair, "after an instructor SPD");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// A scenario preset's speed does not. Both forms set
    /// <see cref="ControlTargets.HasExplicitSpeedCommand"/>, so that flag alone cannot tell a scripted
    /// <c>AT HEMAN SPD 200</c> from an instructor typing <c>SPD 200</c> — the provenance flag is what keeps the pass
    /// managing exactly the aircraft that need it, which is every arrival in a scripted stream.
    /// </summary>
    [Fact]
    public void ScenarioPresetSpeed_LeavesTheProtectionInCharge()
    {
        var pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        var result = CommandDispatcher.Dispatch(new SpeedCommand(200), pair.Follower, TestDispatch.Context(Random.Shared, isScenarioScripted: true));
        Assert.True(result.Success, result.Message);
        Assert.True(pair.Follower.Targets.HasExplicitSpeedCommand);
        Assert.False(pair.Follower.Targets.SpeedCommandIsControllerIssued);

        pair.Pass();
        Report(pair, "after a scripted SPD");

        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(pair.Follower.Approach.SameRunwayProtectionCeilingKts, pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// §5-7-1.b.4 — no speed adjustment inside the FAF or 5 nm from the runway, whichever is closer. The arm engages
    /// the same pair just outside the window first, so the release that follows is attributable to the window and not
    /// to the conflict having evaporated.
    /// </summary>
    [Fact]
    public void NoCeilingIsStamped_OnceTheFollowerIsInsideFiveMilesOnFinal()
    {
        var pair = ConflictingPair(leaderDistanceNm: 3.0, followerDistanceNm: 6.5);
        if (pair is null)
        {
            return;
        }

        pair.Pass();
        Report(pair, "engaged outside the §5-7-1.b.4 window");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        pair.PlaceFollowerAt(4.0);
        pair.Pass();
        Report(pair, "inside the §5-7-1.b.4 window");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// A live-traffic shadow flies its feed and skips the sim's speed integrator, so a ceiling stamped on it would
    /// change nothing while the instructor still read a "reduce speed" line for an instruction no one issued.
    /// </summary>
    [Fact]
    public void ShadowFollower_IsNotManaged_AndIsNeverAnnounced()
    {
        var pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Follower.LiveTraffic = new AircraftLiveTraffic();
        Assert.True(pair.Follower.IsShadow);

        pair.Pass();
        Report(pair, "shadow follower");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
        Assert.Empty(pair.Follower.PendingNotifications);
    }

    /// <summary>
    /// The guard is deliberately asymmetric: a shadow <em>ahead</em> is a real aircraft on its way to the same runway
    /// and stays in the stream as a leader, so the simulated aircraft behind it is still spaced against it. (A shadow
    /// leader is placed airborne rather than rolling out because a shadow never runs a phase, so it can never carry
    /// the resolved exit a landed leader's vacate prediction reads — the airborne regime is the one a shadow leader
    /// actually reaches.)
    /// </summary>
    [Fact]
    public void ShadowAhead_StillSpacesTheSimulatedFollowerBehindIt()
    {
        var pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Leader.LiveTraffic = new AircraftLiveTraffic();
        Assert.True(pair.Leader.IsShadow);

        pair.Pass();
        Report(pair, "shadow leader");

        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(pair.Follower.Approach.SameRunwayProtectionCeilingKts, pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// One terminal line per engagement, not one per tick — and a second one when a follower whose conflict cleared
    /// runs into a new one, which is the truth of what the controller had to do.
    /// </summary>
    [Fact]
    public void ProtectionAnnouncesOncePerEngagement_NotPerTick()
    {
        var pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        for (int i = 0; i < 5; i++)
        {
            pair.Pass();
        }

        var lines = SpacingLines(pair.Follower);
        output.WriteLine($"after five passes: {string.Join(" | ", lines)}");
        Assert.Single(lines);
        Assert.Contains("reduce speed to", lines[0], StringComparison.Ordinal);
        Assert.Contains("(in-trail spacing, 30)", lines[0], StringComparison.Ordinal);

        pair.PlaceFollowerAt(ClearOfConflictDistanceNm);
        pair.Pass();
        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        pair.PlaceFollowerAt(FollowerDistanceNm);
        pair.Pass();
        pair.Pass();

        lines = SpacingLines(pair.Follower);
        output.WriteLine($"after a release and a fresh engagement: {string.Join(" | ", lines)}");
        Assert.Equal(2, lines.Count);
    }

    private static List<string> SpacingLines(AircraftState aircraft) =>
        aircraft.PendingNotifications.Where(n => n.Contains("in-trail spacing", StringComparison.Ordinal)).ToList();

    private void Report(ArrivalPair pair, string label)
    {
        var layout = pair.Engine.World.GroundLayout;
        output.WriteLine(
            $"{label}: leader {pair.Leader.Callsign} "
                + $"{RunwayOccupancy.DistanceToAssignedThresholdNm(pair.Leader, pair.Runway, layout):F2} nm / "
                + $"{RunwayOccupancy.SecondsToAssignedThreshold(pair.Leader, pair.Runway, layout):F0}s @ {pair.Leader.IndicatedAirspeed:F0} kt, "
                + $"follower {pair.Follower.Callsign} "
                + $"{RunwayOccupancy.DistanceToAssignedThresholdNm(pair.Follower, pair.Runway, layout):F2} nm / "
                + $"{RunwayOccupancy.SecondsToAssignedThreshold(pair.Follower, pair.Runway, layout):F0}s @ {pair.Follower.IndicatedAirspeed:F0} kt, "
                + $"required {pair.RequiredIntervalSeconds():F0}s, "
                + $"ceiling {pair.Follower.Targets.SpeedCeiling?.ToString("F0") ?? "(none)"}, "
                + $"protection {pair.Follower.Approach.SameRunwayProtectionCeilingKts?.ToString("F0") ?? "(off)"}, "
                + $"displaced {pair.Follower.Approach.SameRunwayProtectionDisplacedCeilingKts?.ToString("F0") ?? "(none)"}"
        );
    }

    /// <summary>
    /// A turboprop leader with a faster jet follower behind it on the OAK runway 30 final — the shape the pass exists
    /// for. Returns null when the scenario, navdata or OAK layout is unavailable (silent skip).
    /// </summary>
    private static ArrivalPair? ConflictingPair(double leaderDistanceNm, double followerDistanceNm)
    {
        if (!File.Exists(ScenarioPath))
        {
            return null;
        }
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return null;
        }

        var engine = new SimulationEngine(groundData);
        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 1, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        if (engine.Scenario is null)
        {
            return null;
        }

        // The arrival generators would drop unrelated traffic into the same stream on the first pass; this class is
        // about the pass's decisions over a stream it was handed, so the spawners stay off.
        engine.Scenario.SoloArrivalGeneratorRatePercent = 0;

        var runway = engine.Scenario.Generators.Single(g => g.Config.Runway == "30").Runway;
        var leader = InjectArrival(engine, runway, "MANUAL1", "DH8D", leaderDistanceNm);
        var follower = InjectArrival(engine, runway, "MANUAL3", "B739", followerDistanceNm);
        return new ArrivalPair(engine, runway, leader, follower);
    }

    private static AircraftState InjectArrival(SimulationEngine engine, RunwayInfo runway, string callsign, string type, double distanceNm)
    {
        var category = AircraftCategorization.Categorize(type);
        var init = AircraftInitializer.InitializeOnFinal(runway, category, callsign, requestedDistanceNm: distanceNm, aircraftType: type);

        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = init.Position,
            TrueHeading = init.TrueHeading,
            Altitude = init.Altitude,
            IndicatedAirspeed = init.Speed,
            IsOnGround = false,
            IsGeneratorArrival = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
            Phases = init.Phases,
        };

        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    /// <summary>
    /// One leader/follower pair on one runway, with the setup moves the arms need: run the pass, and slide the
    /// follower along the final to a distance or to a threshold interval.
    /// </summary>
    private sealed record ArrivalPair(SimulationEngine Engine, RunwayInfo Runway, AircraftState Leader, AircraftState Follower)
    {
        /// <summary>Runs the pre-physics spine segment, which is where the protection pass lives.</summary>
        public void Pass() => Engine.TickPrePhysics();

        /// <summary>
        /// Slides the follower along the runway's own course to <paramref name="distanceNm"/> from the landing
        /// threshold, leaving its altitude, heading and speed alone so the interval arithmetic the pass does is
        /// driven by the one variable the arm changed. Iterates because the placement is measured with the same
        /// helper the pass uses, whose datum is the resolved landing threshold rather than the runway's own
        /// coordinates.
        /// </summary>
        public void PlaceFollowerAt(double distanceNm)
        {
            var layout = Engine.World.GroundLayout;
            for (int i = 0; i < 6; i++)
            {
                double error = distanceNm - RunwayOccupancy.DistanceToAssignedThresholdNm(Follower, Runway, layout);
                if (Math.Abs(error) < 0.005)
                {
                    return;
                }

                var heading = error >= 0 ? Runway.TrueHeading.ToReciprocal() : Runway.TrueHeading;
                Follower.Position = GeoMath.ProjectPoint(Follower.Position, heading, Math.Abs(error));
            }
        }

        /// <summary>
        /// Slides the follower back until its threshold crossing falls <paramref name="marginSeconds"/> later than
        /// the interval the leader needs — i.e. to a geometry that is legal on the engage test and still inside the
        /// release deadband when <paramref name="marginSeconds"/> is under
        /// <see cref="SameRunwayArrivalProtection.ReleaseHysteresisSeconds"/>.
        /// </summary>
        public void PlaceFollowerAtInterval(double marginSeconds)
        {
            double leaderEta = RunwayOccupancy.SecondsToAssignedThreshold(Leader, Runway, Engine.World.GroundLayout);
            double targetEta = leaderEta + RequiredIntervalSeconds() + marginSeconds;
            PlaceFollowerAt(Follower.GroundSpeed * targetEta / 3600.0);
        }

        /// <summary>
        /// The interval the pass requires between the two threshold crossings for this pair, from the same public
        /// helpers the pass itself calls — the arms place aircraft against it rather than re-deriving it.
        /// </summary>
        public double RequiredIntervalSeconds()
        {
            var leaderCategory = AircraftCategorization.Categorize(Leader.AircraftType);
            var followerCategory = AircraftCategorization.Categorize(Follower.AircraftType);
            double vref = AircraftPerformance.ApproachSpeed(Follower.AircraftType, followerCategory);
            double wakeNm = WakeTurbulenceData.OnApproachWakeSeparationNm(
                Leader.AircraftType,
                leaderCategory,
                Follower.AircraftType,
                followerCategory
            );
            double leaderEta = RunwayOccupancy.SecondsToAssignedThreshold(Leader, Runway, Engine.World.GroundLayout);
            return SameRunwayArrivalProtection.RequiredThresholdIntervalSeconds(
                leaderCategory,
                SameRunwayArrivalProtection.TryBuildRollout(Leader, -leaderEta),
                wakeNm,
                vref
            );
        }
    }
}
