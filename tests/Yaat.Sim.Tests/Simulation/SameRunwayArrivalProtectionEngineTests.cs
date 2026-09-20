using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
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

    /// <summary>
    /// Indicated airspeed (kt) an expected-approach arrival is injected at — the speed the motivating bundle's
    /// scripted <c>CFIX CEPIN 3000 210</c> crossing restriction left its arrival flying while the CAPP waited in the
    /// queue, and above every §5-7-3.c figure the pass may ask for.
    /// </summary>
    private const double ExpectedArrivalSpeedKts = 210.0;

    /// <summary>
    /// Distances (nm) for the arm that pins the <see cref="SameRunwayArrivalProtection.PreClearanceRangeNm"/>
    /// boundary: a pair that genuinely conflicts, with the follower just outside the 20-mile membership line, so the
    /// arm fails for the boundary and not because there was nothing to space.
    /// </summary>
    private const double BeyondPreClearanceLeaderDistanceNm = 18.0;

    /// <summary>Follower distance (nm) for that arm — just outside <see cref="SameRunwayArrivalProtection.PreClearanceRangeNm"/>.</summary>
    private const double BeyondPreClearanceFollowerDistanceNm = 21.0;

    /// <summary>
    /// Leader distance (nm) for the arms about the simulated tower's own speed instruction: close enough in that the
    /// follower behind it is inside <see cref="SameRunwayArrivalProtection.TowerSpeedAuthorityNm"/> while the pair
    /// still conflicts.
    /// </summary>
    private const double TowerAuthorityLeaderDistanceNm = 3.0;

    /// <summary>Follower distance (nm) for those arms — inside the 10-mile tower line, outside the §5-7-1.b.4 window.</summary>
    private const double TowerAuthorityFollowerDistanceNm = 8.0;

    /// <summary>How far below the tower's final approach speed an arm parks a competing ceiling that must not be raised.</summary>
    private const double LowerCeilingMarginKts = 5.0;

    /// <summary>
    /// Distance (nm) an arm slides a follower to when it wants it inside
    /// <see cref="SameRunwayArrivalProtection.TowerSpeedAuthorityNm"/> and still behind a leader placed at
    /// <see cref="LeaderDistanceNm"/>. Deliberately not the leader's own distance: on a tie the stream orders by
    /// callsign, so which aircraft is the follower — and therefore which branch holds the reduction — would depend on
    /// the placement iteration's residual error rather than on the geometry the arm set up.
    /// </summary>
    private const double InsideTowerBoundaryDistanceNm = 9.0;

    [Fact]
    public void ProtectionReleases_WhenTheConflictClears()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
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
    /// STAR and can be carrying a published crossing-speed restriction (§5-7-1.d NOTE) that
    /// <see cref="FlightPhysics"/> stamps once, on the tick the fix is sequenced, and never re-stamps — so clearing
    /// the field outright deletes it for the rest of the flight, which only DELETE SPEED RESTRICTIONS (§5-7-4.d) may
    /// do.
    /// </summary>
    [Fact]
    public void ProtectionRelease_PutsBackTheDisplacedPublishedCeiling()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
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
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
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
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
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
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Pass();
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        CommandResult result = pair.Engine.SendCommand(pair.Follower.Callsign, "SPD 200");
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
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        CommandResult result = CommandDispatcher.Dispatch(
            new SpeedCommand(200),
            pair.Follower,
            TestDispatch.Context(Random.Shared, isScenarioScripted: true)
        );
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
    ///
    /// <para>The engagement is made outside <see cref="SameRunwayArrivalProtection.TowerSpeedAuthorityNm"/>, where
    /// the simulated approach controller may still issue, and the arm runs with the student on the tower position so
    /// the simulated tower's §5-7-3.f instruction cannot take the follower instead: what it pins is the §5-7-3.c
    /// floor path. That path is released at the window even though a reduction already issued is otherwise held
    /// across the 10-mile boundary — <see cref="FasInstruction_IsNotCancelledAtFiveMiles"/> and
    /// <see cref="TowerStudent_ApproachReductionIssuedOutsideTenMiles_HoldsAcrossTheBoundary"/> are the mirrors.</para>
    /// </summary>
    [Fact]
    public void NoCeilingIsStamped_OnceTheFollowerIsInsideFiveMilesOnFinal()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "TWR";

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
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
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
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
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
    /// runs into a new one, which is the truth of what the controller had to do. The release between them speaks its
    /// own §5-7-4 line, so the full exchange is reduce / resume / reduce.
    /// </summary>
    [Fact]
    public void ProtectionAnnouncesOncePerEngagement_NotPerTick()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        for (int i = 0; i < 5; i++)
        {
            pair.Pass();
        }

        List<string> lines = SpacingLines(pair.Follower);
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
        Assert.Equal(2, lines.Count(l => l.Contains("reduce speed to", StringComparison.Ordinal)));
        Assert.Single(lines, l => l.Contains("resume normal speed", StringComparison.Ordinal));
        Assert.Equal(3, lines.Count);
    }

    /// <summary>
    /// Lever 1 — an arrival known to be inbound to the runway by its <see cref="AircraftApproachState.Expected"/>
    /// approach is spaced <em>before</em> it is cleared for that approach. This is the scripted
    /// <c>CFIX … ; CAPP …</c> composition the motivating bundle flew: the CAPP waits in the command queue behind a
    /// fix condition, so until it fires the aircraft carries no <c>AssignedRunway</c> and runs no approach phase at
    /// all, and a pass that waited for the clearance had barely any of the 20→5 nm window left to work in. The
    /// ceiling is still the §5-7-3.c.1(b) turbojet figure — being early does not license a lower assignment.
    /// </summary>
    [Fact]
    public void ExpectedApproachFollower_IsSpacedBeforeItsApproachClearance()
    {
        ArrivalPair? pair = ExpectedApproachPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            output.WriteLine("skipped: scenario, navdata, OAK layout or an approach to runway 30 is unavailable");
            return;
        }

        output.WriteLine($"{pair.Follower.Callsign} expects approach {pair.Follower.Approach.Expected}");
        pair.Pass();
        Report(pair, "expected-approach follower");

        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(pair.Follower.Approach.SameRunwayProtectionCeilingKts, pair.Follower.Targets.SpeedCeiling);

        List<string> lines = SpacingLines(pair.Follower);
        Assert.Single(lines);
        Assert.Contains("reduce speed to 170", lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Membership is being on the final course, not merely carrying the expected approach: an aircraft tracking 90°
    /// across the landing course is being vectored somewhere else first and its threshold ETA is not a prediction of
    /// anything. With no phase to read, "on final" is the track test — the same one
    /// <c>ApproachCommandHandler.IsOnFinal</c> applies to a downwind.
    /// </summary>
    [Fact]
    public void ExpectedApproachFollower_OffTheFinalCourse_IsNotSpaced()
    {
        ArrivalPair? pair = ExpectedApproachPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            output.WriteLine("skipped: scenario, navdata, OAK layout or an approach to runway 30 is unavailable");
            return;
        }

        var across = new TrueHeading((pair.Runway.TrueHeading.Degrees + 90.0) % 360.0);
        pair.Follower.TrueHeading = across;
        pair.Follower.TrueTrack = across;

        pair.Pass();
        Report(pair, "expected-approach follower across the final course");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
        Assert.Empty(SpacingLines(pair.Follower));
    }

    /// <summary>
    /// The track test is momentary in a way the clearance test is not: a not-yet-cleared follower drifting a few
    /// degrees, or being turned onto a base leg for a second, leaves the ±45° window and comes straight back. Releasing
    /// there would speak "resume normal speed" and the next pass would say "reduce speed to" again — the alternate
    /// decreases and increases §5-7-1's lead and §5-7-1.a.3(e) tell the controller to avoid — so the pass holds the
    /// reduction, silently, for <see cref="SameRunwayArrivalProtection.ReleaseHysteresisSeconds"/>.
    /// </summary>
    [Fact]
    public void PreClearanceFollower_LeavingTheTrackWindowBriefly_KeepsItsReductionSilently()
    {
        ArrivalPair? pair = ExpectedApproachPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            output.WriteLine("skipped: scenario, navdata, OAK layout or an approach to runway 30 is unavailable");
            return;
        }

        pair.Pass();
        Report(pair, "engaged before the approach clearance");
        double stamped = Assert.IsType<double>(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        TurnFollowerAcrossTheFinalCourse(pair);
        for (int pass = 0; pass < BriefDropoutPasses; pass++)
        {
            pair.Pass();
        }

        Report(pair, "briefly outside the track window");
        Assert.Equal(stamped, pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(stamped, pair.Follower.Targets.SpeedCeiling);
        Assert.Equal(BriefDropoutPasses, pair.Follower.Approach.SameRunwayProtectionDropoutSeconds);

        TurnFollowerOntoTheFinalCourse(pair);
        pair.Pass();
        Report(pair, "back inside the track window");

        // One instruction for the whole episode, nothing terminating it in between, and the ceiling never moved.
        Assert.Single(ReductionLines(pair.Follower));
        Assert.Empty(ReleaseLines(pair.Follower));
        Assert.Empty(ReleaseRestatementLines(pair.Follower));
        Assert.Equal(stamped, pair.Follower.Targets.SpeedCeiling);
        Assert.Equal(0.0, pair.Follower.Approach.SameRunwayProtectionDropoutSeconds);
    }

    /// <summary>
    /// The debounce is a hold, not a licence: an arrival that stays off the final course past
    /// <see cref="SameRunwayArrivalProtection.ReleaseHysteresisSeconds"/> is being vectored somewhere else and its
    /// speed goes back — out loud. Being vectored off does not cancel the reduction on its own (§5-7-1.e cancels a
    /// published restriction on a vector; §5-7-1.d lists only an approach or a climb via/descend via clearance as
    /// cancelling an assigned one), so the aircraft is still flying it and §5-7-4's lead applies: advise it when the
    /// adjustment is no longer needed. This is the one release that is announced without the inbound test, which by
    /// construction no expired hold can pass.
    /// </summary>
    [Fact]
    public void PreClearanceFollower_OutsideTheTrackWindowPastTheHysteresis_IsReleased()
    {
        ArrivalPair? pair = ExpectedApproachPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            output.WriteLine("skipped: scenario, navdata, OAK layout or an approach to runway 30 is unavailable");
            return;
        }

        pair.Pass();
        Report(pair, "engaged before the approach clearance");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        TurnFollowerAcrossTheFinalCourse(pair);
        for (int pass = 0; pass < SustainedDropoutPasses; pass++)
        {
            pair.Pass();
        }

        Report(pair, "outside the track window past the hysteresis");
        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Approach.SameRunwayProtectionDisplacedCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
        Assert.Equal(0.0, pair.Follower.Approach.SameRunwayProtectionDropoutSeconds);
        Assert.Single(ReductionLines(pair.Follower));

        string line = Assert.Single(ReleaseLines(pair.Follower));
        output.WriteLine($"release line: {line}");
        Assert.Contains($"→ {pair.Follower.Callsign}: resume normal speed (in-trail spacing, 30)", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hold is for the track test dropping out and for nothing else. A follower still on the final course whose
    /// conflict has simply opened up has nothing left to be protected from, and §5-7-1's lead — "terminate speed
    /// adjustments when no longer needed" — has its speed handed back on that tick rather than ten seconds later.
    /// </summary>
    [Fact]
    public void PreClearanceFollower_WhoseConflictClears_IsReleasedAtOnce()
    {
        ArrivalPair? pair = ExpectedApproachPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            output.WriteLine("skipped: scenario, navdata, OAK layout or an approach to runway 30 is unavailable");
            return;
        }

        pair.Pass();
        Report(pair, "engaged before the approach clearance");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        // Only the interval opens: the follower stays on the landing course and inside the pre-clearance range, so
        // nothing about its stream membership has changed and the release cannot be attributed to the track test.
        pair.PlaceFollowerAtInterval(SameRunwayArrivalProtection.ReleaseHysteresisSeconds + DeadbandProbeSeconds);
        pair.Pass();
        Report(pair, "conflict cleared, still on the final course");

        double distance = RunwayOccupancy.DistanceToAssignedThresholdNm(pair.Follower, pair.Runway, pair.Engine.World.GroundLayout);
        Assert.InRange(distance, FiveMileWindowNm, SameRunwayArrivalProtection.PreClearanceRangeNm);
        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
        Assert.Equal(0.0, pair.Follower.Approach.SameRunwayProtectionDropoutSeconds);
        Assert.Single(ReleaseLines(pair.Follower));
    }

    /// <summary>
    /// The hold belongs to the simulated approach controller, so it ends the moment that controller does. With the
    /// instructor's setting switched off mid-drop-out there is no position in the sim holding the arrival to anything:
    /// the speed comes back on that tick — the release loop's standing promise — and silently, because the position
    /// whose instruction it was no longer exists.
    /// </summary>
    [Fact]
    public void PreClearanceFollower_DroppedOut_IsReleasedAtOnceWhenTheSettingGoesOff()
    {
        ArrivalPair? pair = ExpectedApproachPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            output.WriteLine("skipped: scenario, navdata, OAK layout or an approach to runway 30 is unavailable");
            return;
        }

        pair.Pass();
        Report(pair, "engaged before the approach clearance");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        TurnFollowerAcrossTheFinalCourse(pair);
        pair.Pass();
        Report(pair, "one second into the drop-out");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(1.0, pair.Follower.Approach.SameRunwayProtectionDropoutSeconds);

        pair.Engine.Scenario!.AutoArrivalSpacingOnOccupiedRunway = false;
        pair.Pass();
        Report(pair, "with the setting switched off mid-drop-out");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
        Assert.Equal(0.0, pair.Follower.Approach.SameRunwayProtectionDropoutSeconds);
        Assert.Empty(ReleaseLines(pair.Follower));
    }

    /// <summary>
    /// The student taking the track mid-drop-out is a handoff, not a release — the speed goes with the track — and the
    /// drop-out clock goes with it too. A stale one left on the aircraft would be read as most of a hysteresis already
    /// spent the next time the pass owned it.
    /// </summary>
    [Fact]
    public void Handover_MidDropout_ZeroesTheDropoutClock()
    {
        ArrivalPair? pair = ExpectedApproachPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            output.WriteLine("skipped: scenario, navdata, OAK layout or an approach to runway 30 is unavailable");
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "TWR";
        pair.Engine.Scenario.StudentPosition = new TrackOwner(
            StudentTowerCallsign,
            FacilityId: "OAK",
            Subset: 3,
            SectorId: "T",
            OwnerType: TrackOwnerType.Stars
        );

        pair.Pass();
        Report(pair, "engaged with the student on the tower position");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        TurnFollowerAcrossTheFinalCourse(pair);
        pair.Pass();
        pair.Pass();
        Report(pair, "two seconds into the drop-out");
        Assert.Equal(2.0, pair.Follower.Approach.SameRunwayProtectionDropoutSeconds);

        TurnFollowerOntoTheFinalCourse(pair);
        pair.Follower.Track.Owner = pair.Engine.Scenario.StudentPosition;
        pair.Follower.Track.HandoffAccepted = true;
        pair.Pass();
        Report(pair, "after the student took the track");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(0.0, pair.Follower.Approach.SameRunwayProtectionDropoutSeconds);
    }

    /// <summary>
    /// The pre-clearance stream reaches out to <see cref="SameRunwayArrivalProtection.PreClearanceRangeNm"/> and no
    /// further — the §5-7-3.c.1(b) / §5-7-3.c.2(b) 20-mile boundary, beyond which the only speeds this pass could
    /// assign are the 210/200 figures an arrival at that range is already flying. The pair conflicts, so the arm
    /// fails for the boundary and not for want of anything to space.
    /// </summary>
    [Fact]
    public void ExpectedApproachFollower_BeyondTwentyMiles_IsNotSpaced()
    {
        ArrivalPair? pair = ExpectedApproachPair(BeyondPreClearanceLeaderDistanceNm, BeyondPreClearanceFollowerDistanceNm);
        if (pair is null)
        {
            output.WriteLine("skipped: scenario, navdata, OAK layout or an approach to runway 30 is unavailable");
            return;
        }

        pair.Pass();
        Report(pair, "expected-approach follower beyond 20 nm");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
        Assert.Empty(SpacingLines(pair.Follower));
    }

    /// <summary>
    /// Lever 2 — inside <see cref="SameRunwayArrivalProtection.TowerSpeedAuthorityNm"/> the arrival is on the local
    /// controller's frequency and configuring to land, so the simulated tower may say "reduce to final approach
    /// speed" (§5-7-3.f, lower speeds when operationally advantageous) instead of stopping at the §5-7-3.c.1(b)
    /// 170-kt floor. The assigned figure is Vapp — Vref plus the wind additive — never bare Vref, and the line
    /// carries no number, so there is nothing to round to 5-kt increments (§5-7-1.g). The sim speaks for the local
    /// controller only while the student is working a ground position; the arm sets one.
    /// </summary>
    [Fact]
    public void GroundStudent_InsideTenMiles_TheTowerReducesToFinalApproachSpeed()
    {
        ArrivalPair? pair = ConflictingPair(TowerAuthorityLeaderDistanceNm, TowerAuthorityFollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "GND";

        pair.Pass();
        Report(pair, "inside the tower's speed authority");

        List<string> lines = SpacingLines(pair.Follower);
        output.WriteLine($"lines: {string.Join(" | ", lines)}");
        Assert.Single(lines);
        Assert.Contains("reduce to final approach speed", lines[0], StringComparison.Ordinal);
        Assert.Contains("(in-trail spacing, 30)", lines[0], StringComparison.Ordinal);

        string label = lines[0].Split(' ')[0];
        Assert.True(
            (label == "TWR") || (AtcPositionTypeClassifier.Classify(label) == "TWR"),
            $"the instruction is attributed to '{label}', which is not a local-control position"
        );

        Assert.True(pair.Follower.Approach.SameRunwayProtectionFasInstructed);
        Assert.Equal(pair.FinalApproachSpeedKts(), pair.Follower.Targets.SpeedCeiling);
        Assert.Equal(pair.FinalApproachSpeedKts(), pair.Follower.Approach.SameRunwayProtectionCeilingKts);
    }

    /// <summary>
    /// Outside that range the arrival is still the approach controller's and still clean, so the floor stays at the
    /// §5-7-3.c.1(b) figure and no instruction is latched — Vref with the gear and flaps up is a speed the aircraft
    /// does not have (§5-7-1.a.3(d)).
    /// </summary>
    [Fact]
    public void OutsideTenMiles_TheFloorStaysAtTheRegulatoryFigure()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Pass();
        Report(pair, "outside the tower's speed authority");

        List<string> lines = SpacingLines(pair.Follower);
        Assert.Single(lines);
        Assert.Contains("reduce speed to 170", lines[0], StringComparison.Ordinal);
        Assert.False(pair.Follower.Approach.SameRunwayProtectionFasInstructed);
    }

    /// <summary>
    /// §5-7-1.b.4 forbids <em>issuing</em> a speed adjustment inside the FAF or 5 nm, not keeping one already issued:
    /// an aircraft told to fly final approach speed at 8 nm is not told to speed up again at 5. The mirror of
    /// <see cref="NoCeilingIsStamped_OnceTheFollowerIsInsideFiveMilesOnFinal"/>, which is that same window with the
    /// §5-7-3.c floor path — the one that is an adjustment the pass would have to re-issue each tick.
    /// </summary>
    [Fact]
    public void FasInstruction_IsNotCancelledAtFiveMiles()
    {
        ArrivalPair? pair = ConflictingPair(TowerAuthorityLeaderDistanceNm, TowerAuthorityFollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "GND";

        pair.Pass();
        Assert.True(pair.Follower.Approach.SameRunwayProtectionFasInstructed);

        pair.PlaceFollowerAt(4.0);
        pair.Pass();
        Report(pair, "inside the §5-7-1.b.4 window with an instruction already issued");

        Assert.True(pair.Follower.Approach.SameRunwayProtectionFasInstructed);
        Assert.Equal(pair.FinalApproachSpeedKts(), pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// Nor is it cancelled because the conflict that prompted it has opened up: the aircraft was told to fly final
    /// approach speed and is flying it until it lands, goes around, or someone else takes its speed. §5-7-1's
    /// "terminate speed adjustments when no longer needed" is about the controller's judgement, and taking the
    /// instruction back the moment the arithmetic tips over is exactly the alternate decrease/increase that same
    /// paragraph tells a controller to avoid.
    /// </summary>
    [Fact]
    public void FasInstruction_HoldsAfterTheConflictClears()
    {
        ArrivalPair? pair = ConflictingPair(TowerAuthorityLeaderDistanceNm, TowerAuthorityFollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "GND";

        pair.Pass();
        Assert.True(pair.Follower.Approach.SameRunwayProtectionFasInstructed);

        pair.PlaceFollowerAt(ClearOfConflictDistanceNm);
        pair.Pass();
        Report(pair, "with the conflict long gone");

        Assert.True(pair.Follower.Approach.SameRunwayProtectionFasInstructed);
        Assert.Equal(pair.FinalApproachSpeedKts(), pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// The instruction is spoken once even though the ceiling itself is re-stamped every tick. It has to be
    /// re-stamped: <c>FlightPhysics.AutoCancelSpeedAtFinal</c> nulls
    /// <see cref="ControlTargets.SpeedCeiling"/> at the §5-7-1.b.4 window and a scripted <c>RNS</c> clears it too, so
    /// a stamp-once instruction would silently evaporate. A wiped ceiling is therefore re-applied without a second
    /// terminal line — the controller said it once.
    /// </summary>
    [Fact]
    public void FasInstruction_IsAnnouncedOnce_AcrossACeilingWipe()
    {
        ArrivalPair? pair = ConflictingPair(TowerAuthorityLeaderDistanceNm, TowerAuthorityFollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "GND";

        pair.Pass();
        pair.Follower.Targets.SpeedCeiling = null;
        pair.Pass();
        pair.Pass();
        Report(pair, "after a ceiling wipe");

        var lines = pair.Follower.PendingNotifications.Where(n => n.Contains("final approach speed", StringComparison.Ordinal)).ToList();
        output.WriteLine($"lines: {string.Join(" | ", lines)}");
        Assert.Single(lines);
        Assert.Equal(pair.FinalApproachSpeedKts(), pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// Nothing new is issued inside <see cref="SameRunwayArrivalProtection.TowerSpeedAuthorityNm"/>: the arrival is
    /// on the local controller's frequency there, so the simulated approach controller has stopped talking to it, and
    /// the simulated tower speaks only when the student is not the one working a tower position. With the student on
    /// the tower and a conflict that first appears inside the boundary, the pass has nothing it may say.
    /// </summary>
    [Fact]
    public void TowerStudent_InsideTenMiles_GetsNoNewAdjustment()
    {
        ArrivalPair? pair = ConflictingPair(TowerAuthorityLeaderDistanceNm, TowerAuthorityFollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "TWR";

        pair.Pass();
        Report(pair, "with the student on the tower position, inside 10 nm");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
        Assert.Empty(SpacingLines(pair.Follower));
        Assert.False(pair.Follower.Approach.SameRunwayProtectionFasInstructed);
    }

    /// <summary>
    /// A human controller's speed assignment ends the instruction as it ends any other engagement (§5-7-4 — a
    /// controller's assignment is retained until deleted): the aircraft is theirs now, latch included.
    /// </summary>
    [Fact]
    public void ControllerIssuedSpeed_ClearsTheFasInstruction()
    {
        ArrivalPair? pair = ConflictingPair(TowerAuthorityLeaderDistanceNm, TowerAuthorityFollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "GND";

        pair.Pass();
        Assert.True(pair.Follower.Approach.SameRunwayProtectionFasInstructed);

        CommandResult result = pair.Engine.SendCommand(pair.Follower.Callsign, "SPD 200");
        Assert.True(result.Success, result.Message);

        pair.Pass();
        Report(pair, "after an instructor SPD over an instruction");

        Assert.False(pair.Follower.Approach.SameRunwayProtectionFasInstructed);
        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// The instruction lowers a ceiling, it never raises one. A generator arrival is already being held by the
    /// in-trail spacing manager, whose floor is bare Vref — below the Vapp a tower instruction means — so there is
    /// nothing for this pass to stamp and it leaves the aircraft alone. (The arm parks a scripted speed on the
    /// follower so that the spacing manager, handing authority back over the explicit command, leaves its own ceiling
    /// standing rather than clearing it — a scripted speed does not take the aircraft off this pass's books, which
    /// <see cref="ScenarioPresetSpeed_LeavesTheProtectionInCharge"/> pins.)
    /// </summary>
    [Fact]
    public void GeneratorArrival_WithALowerGeneratorCeiling_IsLeftAlone()
    {
        ArrivalPair? pair = ConflictingPair(TowerAuthorityLeaderDistanceNm, TowerAuthorityFollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "GND";
        pair.Follower.IsGeneratorArrival = true;
        CommandResult result = CommandDispatcher.Dispatch(
            new SpeedCommand(200),
            pair.Follower,
            TestDispatch.Context(Random.Shared, isScenarioScripted: true)
        );
        Assert.True(result.Success, result.Message);

        double generatorCeiling = pair.FinalApproachSpeedKts() - LowerCeilingMarginKts;
        pair.Follower.Targets.SpeedCeiling = generatorCeiling;

        pair.Pass();
        Report(pair, "over a lower generator ceiling");

        Assert.Equal(generatorCeiling, pair.Follower.Targets.SpeedCeiling);
        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.False(pair.Follower.Approach.SameRunwayProtectionFasInstructed);
        Assert.Empty(SpacingLines(pair.Follower));
    }

    /// <summary>
    /// The whole pass is a scenario setting. With it off the stream is not walked at all: no prediction, no ceiling,
    /// no line — the delivery is the instructor's to manage, and the occupied-runway go-around is still the net.
    /// </summary>
    [Fact]
    public void SettingOff_TheStreamIsNotManaged()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.AutoArrivalSpacingOnOccupiedRunway = false;

        pair.Pass();
        Report(pair, "with the setting off");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
        Assert.Empty(SpacingLines(pair.Follower));
    }

    /// <summary>
    /// Switching the setting off mid-session hands the speed back on the very next tick rather than leaving an
    /// orphaned ceiling on an aircraft nobody is managing any more: the release loop runs whether or not the stream
    /// is walked, so an engagement the pass owns is released exactly as it is when the conflict clears.
    /// </summary>
    [Fact]
    public void SettingSwitchedOff_ReleasesAnEngagedCeiling()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Pass();
        Report(pair, "engaged before the setting is switched off");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        pair.Engine.Scenario!.AutoArrivalSpacingOnOccupiedRunway = false;
        pair.Pass();
        Report(pair, "after the setting is switched off");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// A reduction the simulated approach controller issued outside
    /// <see cref="SameRunwayArrivalProtection.TowerSpeedAuthorityNm"/> stays in force when the arrival crosses the
    /// boundary. The controller may not issue a new adjustment in there — the aircraft is on the tower's frequency —
    /// but a speed already assigned is flown until somebody takes it back, so the ceiling is re-stamped at the same
    /// figure and no second line is spoken. It still goes at the §5-7-1.b.4 window like any other adjustment.
    /// </summary>
    [Fact]
    public void TowerStudent_ApproachReductionIssuedOutsideTenMiles_HoldsAcrossTheBoundary()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "TWR";

        pair.Pass();
        Report(pair, "engaged outside the tower boundary");
        double? stamped = pair.Follower.Approach.SameRunwayProtectionCeilingKts;
        Assert.NotNull(stamped);
        Assert.Single(SpacingLines(pair.Follower));

        pair.PlaceFollowerAt(InsideTowerBoundaryDistanceNm);
        pair.Pass();
        Report(pair, "held inside the tower boundary");

        Assert.Equal(stamped, pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(stamped, pair.Follower.Targets.SpeedCeiling);
        Assert.Single(SpacingLines(pair.Follower));

        pair.PlaceFollowerAt(4.0);
        pair.Pass();
        Report(pair, "released at the §5-7-1.b.4 window");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// The other end of the held reduction: the tower student accepting the handoff takes the aircraft over, and the
    /// speed it is flying comes with it (§5-4-5.h.3, §5-4-6.c — the receiving controller inherits the restrictions).
    /// The pass stops owning the follower, but the assigned ceiling is left standing rather than handed back: the
    /// aircraft does not accelerate on the tick the student takes the track. From there the ceiling lapses like any
    /// other assigned speed — the student's own speed command, or the auto-cancel at the §5-7-1.b.4 window.
    /// </summary>
    [Fact]
    public void TowerStudent_AcceptingTheHandoff_LeavesTheAssignedSpeedStanding()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "TWR";
        pair.Engine.Scenario.StudentPosition = new TrackOwner(
            "OAK_TWR",
            FacilityId: "OAK",
            Subset: 3,
            SectorId: "T",
            OwnerType: TrackOwnerType.Stars
        );

        pair.Pass();
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        pair.PlaceFollowerAt(InsideTowerBoundaryDistanceNm);
        pair.Pass();
        Report(pair, "held inside the tower boundary, handoff not yet taken");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        double? stamped = pair.Follower.Targets.SpeedCeiling;
        Assert.NotNull(stamped);
        int linesBefore = SpacingLines(pair.Follower).Count;

        pair.Follower.Track.Owner = pair.Engine.Scenario.StudentPosition;
        pair.Follower.Track.HandoffAccepted = true;
        pair.Pass();
        Report(pair, "after the student took the track");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Approach.SameRunwayProtectionDisplacedCeilingKts);
        Assert.Equal(stamped, pair.Follower.Targets.SpeedCeiling);
        Assert.Equal(linesBefore, SpacingLines(pair.Follower).Count);

        pair.Pass();
        Report(pair, "a tick later, with the student still holding the track");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(stamped, pair.Follower.Targets.SpeedCeiling);
        Assert.Equal(linesBefore, SpacingLines(pair.Follower).Count);
    }

    /// <summary>
    /// A reduction that somebody cancelled is not re-imposed by the hold. <c>RNS</c> nulls
    /// <see cref="ControlTargets.SpeedCeiling"/> without claiming the speed for a human controller, so the hold inside
    /// the tower boundary — which re-stamps what was already assigned — must read the empty ceiling as "there is
    /// nothing left to hold" and let the pass release rather than silently putting the reduction back next tick.
    /// </summary>
    [Fact]
    public void TowerStudent_ScriptedResumeNormalSpeedInsideTenMiles_EndsTheHeldReduction()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "TWR";

        pair.Pass();
        Report(pair, "engaged outside the tower boundary");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        int linesBefore = SpacingLines(pair.Follower).Count;

        pair.PlaceFollowerAt(InsideTowerBoundaryDistanceNm);
        pair.Pass();
        Report(pair, "held inside the tower boundary");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        CommandResult result = CommandDispatcher.Dispatch(
            new ResumeNormalSpeedCommand(),
            pair.Follower,
            TestDispatch.Context(Random.Shared, isScenarioScripted: true)
        );
        Assert.True(result.Success, result.Message);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);

        pair.Pass();
        Report(pair, "after a scripted RNS inside the tower boundary");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
        Assert.Equal(linesBefore, SpacingLines(pair.Follower).Count);
    }

    /// <summary>
    /// The front of the stream keeps what it was already flying while it is inside
    /// <see cref="SameRunwayArrivalProtection.TowerSpeedAuthorityNm"/> — the aircraft ahead landing does not withdraw
    /// a reduction the arrival is flying on the tower's frequency, where nobody in the sim may re-issue it. Outside
    /// the boundary the same front-of-stream aircraft is released: there the simulated approach controller is talking
    /// to it and has no conflict left to space.
    /// </summary>
    [Fact]
    public void FrontOfStream_InsideTenMiles_KeepsAHeldReduction_AndReleasesOutside()
    {
        ArrivalPair? held = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (held is null)
        {
            return;
        }

        held.Engine.Scenario!.StudentPositionType = "TWR";

        held.Pass();
        Report(held, "engaged outside the tower boundary");
        double? stamped = held.Follower.Approach.SameRunwayProtectionCeilingKts;
        Assert.NotNull(stamped);

        held.PlaceFollowerAt(InsideTowerBoundaryDistanceNm);
        held.Engine.World.RemoveAircraft(held.Leader.Callsign);
        held.Pass();
        Report(held, "alone at the front of the stream, inside the tower boundary");

        Assert.Equal(stamped, held.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(stamped, held.Follower.Targets.SpeedCeiling);

        ArrivalPair? released = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (released is null)
        {
            return;
        }

        released.Engine.Scenario!.StudentPositionType = "TWR";

        released.Pass();
        Assert.NotNull(released.Follower.Approach.SameRunwayProtectionCeilingKts);

        released.Engine.World.RemoveAircraft(released.Leader.Callsign);
        released.Pass();
        Report(released, "alone at the front of the stream, outside the tower boundary");

        Assert.Null(released.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(released.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// With the student on approach control the pass does not run at all (the student is the approach controller),
    /// so a conflict that first appears inside the tower boundary gets no tower-level instruction either — the
    /// inside-10 nm mirror of <see cref="ApproachStudent_TheStreamIsNotManaged"/>.
    /// </summary>
    [Fact]
    public void ApproachStudent_InsideTenMiles_GetsNoTowerLevelAdjustment()
    {
        ArrivalPair? pair = ConflictingPair(TowerAuthorityLeaderDistanceNm, TowerAuthorityFollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "APP";

        pair.Pass();
        Report(pair, "with the student on the approach position, inside 10 nm");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
        Assert.Empty(SpacingLines(pair.Follower));
        Assert.False(pair.Follower.Approach.SameRunwayProtectionFasInstructed);
    }

    /// <summary>
    /// The pass speaks for the simulated approach controller, which only exists while the student works a position
    /// below it. A student on APP <em>is</em> the approach controller: spacing the stream for them would be the sim
    /// doing their job, so the stream walk does not run at all — no ceiling, and nothing said.
    /// </summary>
    [Fact]
    public void ApproachStudent_TheStreamIsNotManaged()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "APP";

        pair.Pass();
        Report(pair, "with the student on the approach position");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
        Assert.Empty(SpacingLines(pair.Follower));
    }

    /// <summary>The same for a student on the center position — they own the arrival stream before approach does.</summary>
    [Fact]
    public void CenterStudent_TheStreamIsNotManaged()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "CTR";

        pair.Pass();
        Report(pair, "with the student on the center position");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
        Assert.Empty(SpacingLines(pair.Follower));
    }

    /// <summary>
    /// A student moving up to the approach position mid-session (a position change is a recorded action) must get the
    /// stream back the way switching the setting off gives it back: the release loop runs unconditionally, so the
    /// ceiling the pass was holding is handed over on the next tick rather than left frozen on the aircraft.
    /// </summary>
    [Fact]
    public void ApproachStudentTakingOver_ReleasesAnEngagedCeiling()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "GND";

        pair.Pass();
        Report(pair, "engaged with the student on the ground position");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        pair.Engine.Scenario!.StudentPositionType = "APP";

        pair.Pass();
        Report(pair, "after the student took the approach position");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// The pass lifting its own standing reduction is a speed adjustment being terminated, and §5-7-4 has the
    /// controller say so: "resume normal speed" (§5-7-4.a) where nothing else constrains the arrival.
    /// </summary>
    [Fact]
    public void Release_WithNothingPublishedUnderneath_SaysResumeNormalSpeed()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Pass();
        Report(pair, "engaged");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Empty(ReleaseLines(pair.Follower));

        pair.PlaceFollowerAt(ClearOfConflictDistanceNm);
        pair.Pass();
        Report(pair, "released with nothing published underneath");

        string line = Assert.Single(ReleaseLines(pair.Follower));
        output.WriteLine($"release line: {line}");
        Assert.Contains($"→ {pair.Follower.Callsign}: resume normal speed (in-trail spacing, 30)", line, StringComparison.Ordinal);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// A ceiling this pass displaced is not by itself something published: a <c>DEPART</c>-at-fix crossing speed
    /// becomes a bare <see cref="ControlTargets.SpeedCeiling"/> with the route cleared out behind it. §5-7-4.c scopes
    /// "resume published speed" to "subsequent published speed restrictions on the route or procedure", of which there
    /// are none here; "resume normal speed" would cancel a speed that still stands (§5-7-4.a's NOTE); and §5-7-4's lead
    /// bars saying nothing. So the speed being handed back is restated — §5-7-2.a.1, "MAINTAIN (specific speed) KNOTS".
    /// </summary>
    [Fact]
    public void Release_OverDisplacedCeilingWithNoPublishedRestrictionAhead_RestatesTheSpeed()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Follower.Targets.SpeedCeiling = PublishedCrossingCeilingKts;
        // Premise: the ceiling is all there is — no fix on the route still carries a restriction to resume to.
        Assert.DoesNotContain(pair.Follower.Targets.NavigationRoute, fix => fix.SpeedRestriction is not null);

        pair.Pass();
        Report(pair, "engaged over a displaced ceiling");
        Assert.Equal(PublishedCrossingCeilingKts, pair.Follower.Approach.SameRunwayProtectionDisplacedCeilingKts);

        pair.PlaceFollowerAt(ClearOfConflictDistanceNm);
        pair.Pass();
        Report(pair, "released over a displaced ceiling with nothing published ahead");

        string line = Assert.Single(ReleaseRestatementLines(pair.Follower));
        output.WriteLine($"release line: {line}");
        Assert.Contains($"→ {pair.Follower.Callsign}: maintain 210 knots (in-trail spacing, 30)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("resume", line, StringComparison.Ordinal);
        Assert.Empty(ReleaseLines(pair.Follower));
        Assert.Equal(PublishedCrossingCeilingKts, pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// §5-7-1.b.4 — inside 5 nm the controller issues no speed adjustment, and "resume normal speed" is one: the
    /// release at the window happens silently, the way the pilot is already making their own adjustments there (AIM
    /// 4-4-12.g). The arm engages outside the window first so the silence is attributable to the boundary.
    /// </summary>
    [Fact]
    public void Release_InsideFiveMiles_IsSilent()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "TWR";

        pair.Pass();
        Report(pair, "engaged outside the §5-7-1.b.4 window");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        pair.PlaceFollowerAt(4.0);
        pair.Pass();
        Report(pair, "released inside the §5-7-1.b.4 window");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Empty(ReleaseLines(pair.Follower));
    }

    /// <summary>
    /// A reduction stamped before the approach clearance stays in force past it, so it has to be restated (§5-7-1.c;
    /// AIM 4-4-12.g — the pilot may otherwise take the clearance as cancelling the assigned speed). The figure is
    /// the standing ceiling in 5-knot increments (§5-7-1.g) and the ceiling itself is left exactly where it was.
    /// </summary>
    [Fact]
    public void ApproachClearance_WhileProtectionHoldsAReduction_RestatesTheSpeed()
    {
        ArrivalPair? pair = ExpectedApproachPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            output.WriteLine("skipped: scenario, navdata, OAK layout or an approach to runway 30 is unavailable");
            return;
        }

        pair.Pass();
        Report(pair, "engaged before the approach clearance");
        double stamped = Assert.IsType<double>(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        CommandResult result = pair.Engine.SendCommand(pair.Follower.Callsign, $"CAPP {pair.Follower.Approach.Expected}");
        Assert.True(result.Success, result.Message);

        List<string> restated = RestatementLines(pair.Follower);
        output.WriteLine($"restatement: {string.Join(" | ", restated)}");
        string line = Assert.Single(restated);
        Assert.Contains($"→ {pair.Follower.Callsign}: maintain {RestatedSpeedKts} knots", line, StringComparison.Ordinal);
        Assert.Equal(stamped, pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(stamped, pair.Follower.Targets.SpeedCeiling);
    }

    /// <summary>
    /// A published crossing restriction the aircraft has not reached yet is still a published restriction, so the
    /// phrase is "resume published speed" (§5-7-4.c) even with nothing on the ceiling underneath the pass.
    /// <see cref="FlightPhysics"/> only turns such a fix into a <see cref="ControlTargets.SpeedCeiling"/> on the tick
    /// it sequences, so the displaced ceiling being null says nothing about what is ahead — reading it alone would put
    /// "resume normal speed" on an arrival that §5-7-4.a's NOTE reserves the phrase away from.
    /// </summary>
    [Fact]
    public void Release_WithAnUnsequencedPublishedSpeedAhead_SaysResumePublishedSpeed()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Follower.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = UnsequencedFixName,
                Position = pair.Follower.Position,
                SpeedRestriction = new CifpSpeedRestriction(UnsequencedCrossingSpeedKts, CifpSpeedRestrictionType.AtOrBelow),
            }
        );

        pair.Pass();
        Report(pair, "engaged with a published crossing speed still ahead");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Approach.SameRunwayProtectionDisplacedCeilingKts); // premise: nothing underneath the stamp

        pair.PlaceFollowerAt(ClearOfConflictDistanceNm);
        pair.Pass();
        Report(pair, "released with that restriction still unsequenced");

        string line = Assert.Single(ReleaseLines(pair.Follower));
        output.WriteLine($"release line: {line}");
        Assert.Contains($"→ {pair.Follower.Callsign}: resume published speed (in-trail spacing, 30)", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pass speaks for the simulated approach controller, so once that controller's issuing gate closes under a
    /// standing reduction — the instructor switching the setting off mid-session — the ceiling still comes back but
    /// nothing is said: there is no longer a position in the sim whose instruction it would be.
    /// </summary>
    [Fact]
    public void Release_AfterTheApproachControllerGateCloses_IsSilent()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Pass();
        Report(pair, "engaged");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        pair.Engine.Scenario!.AutoArrivalSpacingOnOccupiedRunway = false;
        pair.Pass();
        Report(pair, "released after the gate closed");

        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Null(pair.Follower.Targets.SpeedCeiling);
        Assert.Empty(ReleaseLines(pair.Follower));
    }

    /// <summary>
    /// A handoff of the track to the student defers the release entirely: §5-4-5.b bars the transferring controller
    /// from changing the aircraft's speed from the moment the handoff is initiated, and §5-4-6.c has the receiving
    /// controller comply with the restrictions it was issued under, so the reduction stands for the student to inherit
    /// instead of being handed back the tick its conflict clears.
    /// </summary>
    [Fact]
    public void Release_DuringAHandoffToTheStudent_IsDeferred()
    {
        ArrivalPair? pair = ConflictingPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            return;
        }

        pair.Pass();
        Report(pair, "engaged");
        double stamped = Assert.IsType<double>(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        pair.Engine.Scenario!.StudentPosition = TrackOwner.CreateNonNas(StudentTowerCallsign);
        pair.Engine.Scenario.StudentPositionType = "TWR";
        pair.Follower.Track.Owner = TrackOwner.CreateNonNas(SimulatedApproachCallsign);
        CommandResult handoff = TrackEngine.ApplyHandoff(pair.Follower, pair.Engine.Scenario, identity: null, tcpCode: null, redirect: null);
        Assert.True(handoff.Success, handoff.Message);

        pair.PlaceFollowerAt(ClearOfConflictDistanceNm);
        pair.Pass();
        Report(pair, "conflict cleared while the handoff is pending");

        Assert.Equal(stamped, pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(stamped, pair.Follower.Targets.SpeedCeiling);
        Assert.Empty(ReleaseLines(pair.Follower));
    }

    /// <summary>
    /// The approach clearance restates a figure the approach controller assigned, not the tower's own instruction: a
    /// latched "reduce to final approach speed" (§5-7-3.f) names no number and belongs to the local controller, so the
    /// clearance carries no restatement at all.
    /// </summary>
    [Fact]
    public void ApproachClearance_WithTheTowerInstructionLatched_DoesNotRestate()
    {
        ArrivalPair? pair = ExpectedApproachPair(TowerAuthorityLeaderDistanceNm, TowerAuthorityFollowerDistanceNm);
        if (pair is null)
        {
            output.WriteLine("skipped: scenario, navdata, OAK layout or an approach to runway 30 is unavailable");
            return;
        }

        pair.Engine.Scenario!.StudentPositionType = "GND";

        pair.Pass();
        Report(pair, "the tower instruction latched");
        Assert.True(pair.Follower.Approach.SameRunwayProtectionFasInstructed);
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        CommandResult result = pair.Engine.SendCommand(pair.Follower.Callsign, $"CAPP {pair.Follower.Approach.Expected}");
        Assert.True(result.Success, result.Message);

        output.WriteLine($"spacing lines after the clearance: {string.Join(" | ", SpacingLines(pair.Follower))}");
        Assert.Empty(RestatementLines(pair.Follower));
    }

    /// <summary>
    /// <c>PTAC</c> issues an approach clearance of its own — heading, altitude, "cleared approach" — so §5-7-1.c binds
    /// it exactly as it binds <c>CAPP</c>: a reduction the pass is holding this arrival to outlives the clearance and
    /// has to be restated, or AIM 4-4-12.g has the pilot taking the clearance as cancelling it. Same line, same figure.
    /// </summary>
    [Fact]
    public void Ptac_WithAStandingInTrailReduction_RestatesTheSpeed()
    {
        ArrivalPair? pair = ExpectedApproachPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            output.WriteLine("skipped: scenario, navdata, OAK layout or an approach to runway 30 is unavailable");
            return;
        }

        pair.Pass();
        Report(pair, "engaged before the PTAC");
        Assert.NotNull(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        CommandResult result = pair.Engine.SendCommand(pair.Follower.Callsign, $"PTAC PH PA {pair.Follower.Approach.Expected}");
        Assert.True(result.Success, result.Message);

        List<string> restated = RestatementLines(pair.Follower);
        output.WriteLine($"restatement: {string.Join(" | ", restated)}");
        string line = Assert.Single(restated);
        Assert.Contains($"→ {pair.Follower.Callsign}: maintain {RestatedSpeedKts} knots", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The restatement is the controller saying the speed again, not a second assignment: the ceiling the pass stamped
    /// is left exactly where it was and the aircraft is still flying it after the clearance, while the clearance's own
    /// §5-7-1.d cancellation takes the explicit target away — which is what makes the restatement necessary.
    /// </summary>
    [Fact]
    public void Ptac_WithAStandingInTrailReduction_LeavesTheCeilingStanding()
    {
        ArrivalPair? pair = ExpectedApproachPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            output.WriteLine("skipped: scenario, navdata, OAK layout or an approach to runway 30 is unavailable");
            return;
        }

        pair.Pass();
        Report(pair, "engaged before the PTAC");
        double stamped = Assert.IsType<double>(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        CommandResult result = pair.Engine.SendCommand(pair.Follower.Callsign, $"PTAC PH PA {pair.Follower.Approach.Expected}");
        Assert.True(result.Success, result.Message);

        Report(pair, "after the PTAC");
        Assert.Equal(stamped, pair.Follower.Approach.SameRunwayProtectionCeilingKts);
        Assert.Equal(stamped, pair.Follower.Targets.SpeedCeiling);
        Assert.Null(pair.Follower.Targets.TargetSpeed);
    }

    /// <summary>
    /// With nothing standing there is nothing to restate: a <c>PTAC</c> to an arrival the pass has looked at and left
    /// alone carries no speed line at all, rather than inventing one out of whatever the aircraft happens to be flying.
    /// </summary>
    [Fact]
    public void Ptac_WithNoReduction_SaysNothingAboutSpeed()
    {
        ArrivalPair? pair = ExpectedApproachPair(LeaderDistanceNm, FollowerDistanceNm);
        if (pair is null)
        {
            output.WriteLine("skipped: scenario, navdata, OAK layout or an approach to runway 30 is unavailable");
            return;
        }

        pair.PlaceFollowerAt(ClearOfConflictDistanceNm);
        pair.Pass();
        Report(pair, "no conflict, so no reduction");
        Assert.Null(pair.Follower.Approach.SameRunwayProtectionCeilingKts);

        CommandResult result = pair.Engine.SendCommand(pair.Follower.Callsign, $"PTAC PH PA {pair.Follower.Approach.Expected}");
        Assert.True(result.Success, result.Message);

        Assert.Empty(RestatementLines(pair.Follower));
        Assert.Empty(SpacingLines(pair.Follower));
    }

    /// <summary>KSJC 12R/30L is authored <c>"threshold": "1297 - 2537"</c>: landing 30L starts 2,537 ft downfield.</summary>
    private const double SjcDisplacementFt = 2537.0;

    /// <summary>§5-7-1.b.4's no-adjustment window (nm) — the boundary the two datums fall on opposite sides of.</summary>
    private const double FinalAdjustmentWindowNm = 5.0;

    /// <summary>
    /// Distance (nm) from KSJC 30L's pavement end that is inside <see cref="FinalAdjustmentWindowNm"/> measured from
    /// there and outside it measured from the landing threshold 2,537 ft further down.
    /// </summary>
    private const double SjcProbeFromPavementNm = 4.8;

    /// <summary>
    /// The pass measures an arrival against <em>the runway's</em> own field, never against whatever layout the
    /// aircraft happens to carry. An airborne arrival often carries none — a scripted one with no destination, a
    /// live-traffic shadow — and <see cref="SimulationWorld.GroundLayout"/> is the scenario's primary airport, which
    /// knows nothing of a satellite's runways: read either of those and a displaced runway's landing threshold
    /// vanishes. On KSJC 30L, whose landing threshold is <see cref="SjcDisplacementFt"/> ft (0.42 nm) down the
    /// pavement, that is the difference between an arrival being inside the §5-7-1.b.4 window and outside it — at
    /// <see cref="SjcProbeFromPavementNm"/> nm from the pavement end it is 5.2 nm from the threshold it may land on,
    /// and the simulated approach controller may still slow it. Both arrivals, the one carrying its field's layout and
    /// the one carrying nothing, have to be given that same answer.
    /// </summary>
    [Fact]
    public void ProtectionPass_UsesTheRunwaysLayout_WhenTheAircraftCarriesNone()
    {
        SecondaryFieldProbe? probe = SjcDisplacedArrivals();
        if (probe is null)
        {
            output.WriteLine("skipped: scenario, navdata, the OAK or SJC layout, or KSJC 30L is unavailable");
            return;
        }

        (SimulationEngine engine, RunwayInfo runway, AirportGroundLayout layout, AircraftState carrying, AircraftState bare) = probe.Value;
        SimScenarioState scenario = Assert.IsType<SimScenarioState>(engine.Scenario);

        // Premise: the primary airport's layout carries no displacement for a KSJC runway, so measuring through it is
        // measuring to the pavement end — 0.42 nm short of where this arrival may touch down.
        Assert.Equal(0.0, LandingThreshold.DisplacementFt(runway, engine.World.GroundLayout));
        Assert.Equal(SjcDisplacementFt, LandingThreshold.DisplacementFt(runway, layout));

        double pavementDatumNm = RunwayOccupancy.DistanceToAssignedThresholdNm(bare, runway, engine.World.GroundLayout);
        double landingDatumNm = RunwayOccupancy.DistanceToAssignedThresholdNm(bare, runway, layout);
        output.WriteLine($"primary-layout datum {pavementDatumNm:F2} nm, landing datum {landingDatumNm:F2} nm");
        Assert.InRange(pavementDatumNm, 4.5, FinalAdjustmentWindowNm); // premise: inside the window on the pavement datum
        Assert.True(landingDatumNm > FinalAdjustmentWindowNm, $"premise: outside the window on the landing datum ({landingDatumNm:F2} nm)");

        Assert.True(
            engine.IsProtectionEligible(bare, runway, scenario, preClearance: true),
            "an arrival carrying no layout was measured to the pavement end, not to the runway's landing threshold"
        );
        Assert.True(
            engine.IsProtectionEligible(carrying, runway, scenario, preClearance: true),
            "the arrival carrying its field's layout got a different answer from the identical one that carries none"
        );
    }

    /// <summary>
    /// Two identical pre-clearance arrivals on KSJC 30L's final — a runway at neither the scenario's primary airport
    /// nor any generator's — one carrying the SJC layout and one carrying nothing. Null when the scenario, navdata,
    /// either layout or the runway is unavailable (silent skip).
    /// </summary>
    private static SecondaryFieldProbe? SjcDisplacedArrivals()
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
        if ((groundData.GetLayout("OAK") is null) || (groundData.GetLayout("SJC") is not { } sjcLayout))
        {
            return null;
        }
        if (NavigationDatabase.Instance.GetRunway("KSJC", "30L") is not { } runway)
        {
            return null;
        }

        var engine = new SimulationEngine(groundData);
        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 1, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        if (engine.Scenario is null)
        {
            return null;
        }

        engine.Scenario.SoloArrivalGeneratorRatePercent = 0;
        engine.Scenario.AutoArrivalSpacingOnOccupiedRunway = true;

        AircraftState carrying = InjectSecondaryFieldArrival(engine, runway, "SJC1", sjcLayout);
        AircraftState bare = InjectSecondaryFieldArrival(engine, runway, "SJC2", layout: null);
        return new SecondaryFieldProbe(engine, runway, sjcLayout, carrying, bare);
    }

    /// <summary>
    /// A pre-clearance arrival — no phase, no assigned runway, tracking the landing course — placed
    /// <see cref="SjcProbeFromPavementNm"/> nm back from <paramref name="runway"/>'s pavement end on its extended
    /// centreline, with <paramref name="layout"/> bound to it or nothing at all.
    /// </summary>
    private static AircraftState InjectSecondaryFieldArrival(SimulationEngine engine, RunwayInfo runway, string callsign, AirportGroundLayout? layout)
    {
        var pavement = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B739",
            Position = GeoMath.ProjectPoint(pavement, runway.TrueHeading.ToReciprocal(), SjcProbeFromPavementNm),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = 1600,
            IndicatedAirspeed = ExpectedArrivalSpeedKts,
            IsOnGround = false,
            IsGeneratorArrival = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KSJC" },
            Phases = null,
        };
        aircraft.Ground.Layout = layout;

        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    /// <summary>The two arrivals of <see cref="SjcDisplacedArrivals"/>, with the engine and the runway they are on.</summary>
    private readonly record struct SecondaryFieldProbe(
        SimulationEngine Engine,
        RunwayInfo Runway,
        AirportGroundLayout Layout,
        AircraftState Carrying,
        AircraftState Bare
    );

    /// <summary>
    /// The figure (kt) the approach clearance restates for this fixture, written out rather than computed from the
    /// function under test. At <see cref="FollowerDistanceNm"/> the jet follower's reduction bottoms out on
    /// §5-7-3.c.1(b)'s 170 kt — a floor, not an arithmetic result — so it is already a 5-knot increment and the
    /// restatement says it unchanged.
    /// </summary>
    private const int RestatedSpeedKts = 170;

    /// <summary>Name of the fix an arm parks an unsequenced published crossing speed on.</summary>
    private const string UnsequencedFixName = "CEPIN";

    /// <summary>The published crossing speed (kt) on that fix — the figure the motivating bundle's own STAR carried.</summary>
    private const int UnsequencedCrossingSpeedKts = 210;

    /// <summary>The student's own position in the handoff arm — a tower student, the room this pass is built for.</summary>
    private const string StudentTowerCallsign = "OAK_TWR";

    /// <summary>The position handing the track over in that arm: the approach controller the sim is playing.</summary>
    private const string SimulatedApproachCallsign = "NCT_APP";

    /// <summary>
    /// Passes a brief drop-out out of the track window lasts. The pass runs in the pre-physics segment, once per
    /// simulated second, so a pass is a second: half the hysteresis is comfortably inside it.
    /// </summary>
    private const int BriefDropoutPasses = (int)(SameRunwayArrivalProtection.ReleaseHysteresisSeconds / 2.0);

    /// <summary>Passes a drop-out needs to out-run the hysteresis: one per second of it, plus the pass that releases.</summary>
    private const int SustainedDropoutPasses = (int)SameRunwayArrivalProtection.ReleaseHysteresisSeconds + 1;

    /// <summary>
    /// The §5-7-1.b.4 window (nm), written out because the engine's own constant is private: the lower bound the
    /// conflict-cleared arm checks its follower is still outside of.
    /// </summary>
    private const double FiveMileWindowNm = 5.0;

    /// <summary>
    /// Turns the follower 90° across the landing course, which is outside the ±45° window
    /// <c>ApproachCommandHandler.IsOnFinal</c> allows — the only thing that makes a not-yet-cleared arrival part of
    /// the runway's stream, so this alone drops it out of the pass's walk.
    /// </summary>
    private static void TurnFollowerAcrossTheFinalCourse(ArrivalPair pair)
    {
        var across = new TrueHeading((pair.Runway.TrueHeading.Degrees + 90.0) % 360.0);
        pair.Follower.TrueHeading = across;
        pair.Follower.TrueTrack = across;
    }

    /// <summary>Puts it back on the landing course, where the stream walk picks it up again.</summary>
    private static void TurnFollowerOntoTheFinalCourse(ArrivalPair pair)
    {
        pair.Follower.TrueHeading = pair.Runway.TrueHeading;
        pair.Follower.TrueTrack = pair.Runway.TrueHeading;
    }

    /// <summary>The engagement lines — the reductions the pass has actually issued, one per engagement.</summary>
    private static List<string> ReductionLines(AircraftState aircraft) =>
        [.. aircraft.PendingNotifications.Where(n => n.Contains(": reduce speed to ", StringComparison.Ordinal))];

    private static List<string> SpacingLines(AircraftState aircraft) =>
        [.. aircraft.PendingNotifications.Where(n => n.Contains("in-trail spacing", StringComparison.Ordinal))];

    /// <summary>The §5-7-4 line the pass says when it terminates its own reduction.</summary>
    private static List<string> ReleaseLines(AircraftState aircraft) =>
        [.. aircraft.PendingNotifications.Where(n => n.Contains(": resume ", StringComparison.Ordinal))];

    /// <summary>
    /// The §5-7-2.a.1 line a release speaks when the speed it hands back is restated rather than resumed — told apart
    /// from the approach clearance's restatement by its tail, which names the runway the spacing was for.
    /// </summary>
    private static List<string> ReleaseRestatementLines(AircraftState aircraft) =>
        [
            .. aircraft.PendingNotifications.Where(n =>
                n.Contains(": maintain ", StringComparison.Ordinal) && (!n.Contains("restated with the approach clearance", StringComparison.Ordinal))
            ),
        ];

    /// <summary>The §5-7-1.c line the approach clearance carries when a reduction is standing.</summary>
    private static List<string> RestatementLines(AircraftState aircraft) =>
        [.. aircraft.PendingNotifications.Where(n => n.Contains("restated with the approach clearance", StringComparison.Ordinal))];

    private void Report(ArrivalPair pair, string label)
    {
        AirportGroundLayout? layout = pair.Engine.World.GroundLayout;
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
    private static ArrivalPair? ConflictingPair(double leaderDistanceNm, double followerDistanceNm) =>
        Pair(leaderDistanceNm, followerDistanceNm, expectedApproachFollower: false);

    /// <summary>
    /// The same pair, except the follower carries only an expected approach — no clearance, no runway, no phase —
    /// the state a scripted <c>CFIX … ; CAPP …</c> arrival is in until its queued clearance fires. Also null when the
    /// navdata carries no approach to runway 30 (silent skip).
    /// </summary>
    private static ArrivalPair? ExpectedApproachPair(double leaderDistanceNm, double followerDistanceNm) =>
        Pair(leaderDistanceNm, followerDistanceNm, expectedApproachFollower: true);

    private static ArrivalPair? Pair(double leaderDistanceNm, double followerDistanceNm, bool expectedApproachFollower)
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

        // The pass is gated off on the Sim side (pre-feature recordings replay faithfully), so every arm that wants
        // it has to switch it on the way a live session's client preference does.
        engine.Scenario.AutoArrivalSpacingOnOccupiedRunway = true;

        RunwayInfo runway = engine.Scenario.Generators.Single(g => g.Config.Runway == "30").Runway;
        AircraftState leader = InjectArrival(engine, runway, "MANUAL1", "DH8D", leaderDistanceNm);
        AircraftState? follower = expectedApproachFollower
            ? InjectExpectedArrival(engine, runway, "MANUAL3", "B739", followerDistanceNm)
            : InjectArrival(engine, runway, "MANUAL3", "B739", followerDistanceNm);
        return follower is null ? null : new ArrivalPair(engine, runway, leader, follower);
    }

    private static AircraftState InjectArrival(SimulationEngine engine, RunwayInfo runway, string callsign, string type, double distanceNm)
    {
        AircraftCategory category = AircraftCategorization.Categorize(type);
        PhaseInitResult init = AircraftInitializer.InitializeOnFinal(runway, category, callsign, requestedDistanceNm: distanceNm, aircraftType: type);

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
    /// An arrival that knows where it is going but has not been cleared there: no <c>AssignedRunway</c>, no phase,
    /// only <see cref="AircraftApproachState.Expected"/> and a destination. Placed on the runway's own final with its
    /// track along the landing course, because with no phase to read "on final" is a track test. Null when the
    /// navdata carries no approach to this runway (silent skip).
    /// </summary>
    private static AircraftState? InjectExpectedArrival(SimulationEngine engine, RunwayInfo runway, string callsign, string type, double distanceNm)
    {
        if (NavigationDatabase.Instance.ResolveApproachCandidates(runway.AirportId, runway.Designator).FirstOrDefault() is not { } approachId)
        {
            return null;
        }

        AircraftCategory category = AircraftCategorization.Categorize(type);
        PhaseInitResult init = AircraftInitializer.InitializeOnFinal(runway, category, callsign, requestedDistanceNm: distanceNm, aircraftType: type);

        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = init.Position,
            TrueHeading = init.TrueHeading,
            TrueTrack = init.TrueHeading,
            Altitude = init.Altitude,
            IndicatedAirspeed = ExpectedArrivalSpeedKts,
            IsOnGround = false,
            IsGeneratorArrival = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
            Phases = null,
        };
        aircraft.Approach.Expected = approachId;

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
            AirportGroundLayout? layout = Engine.World.GroundLayout;
            for (int i = 0; i < 6; i++)
            {
                double error = distanceNm - RunwayOccupancy.DistanceToAssignedThresholdNm(Follower, Runway, layout);
                if (Math.Abs(error) < 0.005)
                {
                    return;
                }

                TrueHeading heading = error >= 0 ? Runway.TrueHeading.ToReciprocal() : Runway.TrueHeading;
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
        /// The speed a tower instruction to "reduce to final approach speed" means for this follower — Vref plus the
        /// session's wind additive, from the same helpers the pass calls.
        /// </summary>
        public double FinalApproachSpeedKts()
        {
            AircraftCategory category = AircraftCategorization.Categorize(Follower.AircraftType);
            double vref = AircraftPerformance.ApproachSpeed(Follower.AircraftType, category);
            return SameRunwayArrivalProtection.FinalApproachSpeedKts(
                vref,
                AircraftPerformance.WindApproachAdditive(Engine.World.Weather, Runway.TrueHeading.Degrees)
            );
        }

        /// <summary>
        /// The interval the pass requires between the two threshold crossings for this pair, from the same public
        /// helpers the pass itself calls — the arms place aircraft against it rather than re-deriving it.
        /// </summary>
        public double RequiredIntervalSeconds()
        {
            AircraftCategory leaderCategory = AircraftCategorization.Categorize(Leader.AircraftType);
            AircraftCategory followerCategory = AircraftCategorization.Categorize(Follower.AircraftType);
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
