using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Pure same-runway arrival-protection math — the simulated TRACON that keeps a scripted arrival from being
/// delivered inside the preceding arrival's runway occupancy time. §3-10-3.a.1 states the condition (the preceding
/// aircraft must be clear of the runway before the succeeding one crosses the threshold); §3-10-6.a is what
/// authorises <em>anticipating</em> it — "landing clearance to succeeding aircraft in a landing sequence need not be
/// withheld if you observe the positions of the aircraft and determine that prescribed runway separation will exist
/// when the aircraft crosses the landing threshold". <see cref="SimulationEngine"/> owns the per-tick orchestration
/// (pairing follower with leader on the same runway, the §5-7-1.b.4 window, and stamping
/// <see cref="ControlTargets.SpeedCeiling"/>); these helpers compute the numbers and are unit-tested directly.
///
/// <para><b>Calibration.</b> The airborne-leader constants are calibrated to <i>this sim's own</i> runway
/// occupancy, measured threshold-crossing to runway-clear across 14 SFO landings: 58.9–77.3 s, mean 68.7 s. The
/// dominant variable is exit distance rather than category (28R, exit far down, ran 71–77 s; 28L, exit at E, ran
/// 59–64 s — the same E75L type was 75.8 s on 28R against 61.7 s on 28L). If runway occupancy is retuned — the
/// vacate is the slow part, several arrivals braking to 5–8 kt while still inside 250 ft of the centerline — these
/// constants must be retuned with it. The landed-leader regime reads live state instead and self-corrects.</para>
///
/// <para><b>Clear of the runway.</b> AIM 2-3-5.a.1 and AIM 4-3-21.b: an aircraft exiting is not clear until <i>all
/// parts</i> of it have crossed the holding position marking. Both regimes therefore measure to
/// <see cref="TailClearanceNm"/> past the hold-short node — the same virtual target <see cref="RunwayExitPhase"/>
/// itself taxis to, so the prediction and the phase cannot disagree about where "clear" is. Each regime models the
/// speed profile its phase actually flies rather than the leader's present ground speed held to the end. A
/// <see cref="LandingPhase"/> leader brakes the rollout to the exit's turn-off speed by the branch point — a leg
/// covered at the constant-deceleration mean of the two — and then flies the exit at that turn-off speed. A
/// <see cref="RunwayExitPhase"/> leader is already on the exit, where its navigator holds the route's taxi ceiling
/// and brakes to the stop only over the last v²/2a: steady leg then braking leg, not one long brake from where it
/// stands. Modelling that whole remainder as one braking leg read the entry/2 mean over it and was ~13 s pessimistic
/// on a 1,286 ft exit at 30 kt (41 s against a 28 s measured vacate), which is a go-around fired for a leader that
/// will be clear. Stated simplification: the <see cref="RunwayExitPhase"/> regime measures the straight line from the
/// aircraft to its hold-short node rather than the curved exit path it will actually taxi, because the phase exposes
/// no remaining-path distance. That under-measures a curving exit and is the one term in this arithmetic still biased
/// optimistic.</para>
///
/// <para><b>Separation floor.</b> <see cref="WakeTurbulenceData.OnApproachWakeSeparationNm(string, AircraftCategory,
/// string, AircraftCategory)"/> returns zero for a non-wake pair and leaves the radar minimum to its caller, so the
/// required interval floors the separation at <see cref="TerminalRadarFloorNm"/> the way
/// <c>SimulationEngine.ApplyArrivalSpacing</c> does. §5-5-4.a.1/b.1 is 3 NM; §5-5-4.j's 2.5 NM relief requires a
/// documented average runway occupancy of 50 s or less and this sim measures 68.7 s, so 3 NM binds. The consequence
/// is that the radar floor, not the category constant, governs a jet pair: 3 NM at a 144-kt Vref is 75 s, above the
/// 70 s <see cref="JetIntervalSeconds"/>.</para>
///
/// <para><b>Speed floors.</b> §5-7-3.c.1 floors an arrival speed adjustment below 10,000 ft at 210 kt for a turbojet
/// (170 kt within 20 flying miles of the threshold), §5-7-3.c.2 at 200 kt for a reciprocating or turboprop aircraft
/// (150 kt within 20 miles), and §5-7-3.e at 60 kt for a helicopter — see <see cref="RegulatoryFloorKts"/>. The floor
/// <see cref="ProtectionCeilingKts"/> applies is <c>Max(Vref, Min(scheduled, regulatory))</c>: §5-7-3.f ("Lower
/// speeds may be assigned when operationally advantageous") is what authorises the <c>Min(scheduled, …)</c> collapse,
/// so a 110-kt piston is not floored at 200 kt. It is <em>not</em> a general licence to assign Vref — Vref is only a
/// valid speed with the gear and landing flaps out, so commanding it at 12 nm and 4,000 ft asserts a configuration
/// the aircraft does not have and contradicts §5-7-1.a.3.d ("allow aircraft to operate in a clean configuration as
/// long as circumstances permit"). Stated simplification: §5-7-3.c.1.b's "20 <i>flying</i> miles" is path distance
/// and this uses direct distance to the threshold, which errs permissive. Inside <see cref="TowerSpeedAuthorityNm"/>
/// that floor may drop as far as <see cref="FinalApproachSpeedKts"/>: the arrival is on the local controller's
/// frequency by then, so the simulated tower may say "reduce to final approach speed" under §5-7-3.f, and at that
/// range the aircraft is configuring for landing rather than being asked to hold Vref clean. Such an instruction is
/// <em>held</em> through the §5-7-1.b.4 window, because that paragraph forbids <em>issuing</em> speed adjustments
/// inside 5 nm / the FAF, not keeping one already issued. <see cref="SimulationEngine"/> decides when to reach for
/// that floor; the functions here only supply it.</para>
/// </summary>
public static class SameRunwayArrivalProtection
{
    /// <summary>Threshold interval behind an airborne jet leader (s) — see the calibration note on the class.</summary>
    public const double JetIntervalSeconds = 70.0;

    /// <summary>Threshold interval behind an airborne turboprop leader (s).</summary>
    public const double TurbopropIntervalSeconds = 60.0;

    /// <summary>Threshold interval behind an airborne piston leader (s). Also the default for any other category.</summary>
    public const double PistonIntervalSeconds = 50.0;

    /// <summary>Terminal radar separation minimum (NM), §5-5-4.a.1/b.1 — see the separation-floor note on the class.</summary>
    public const double TerminalRadarFloorNm = 3.0;

    /// <summary>
    /// Deadband (s) the predicted interval must open past the required one before an engagement is released. The
    /// engage test is the required interval itself; without a separate release test the boundary limit-cycles —
    /// the ceiling comes off, the follower accelerates at its 2.5 kt/s jet rate, the interval closes again and the
    /// protection re-engages, emitting a fresh terminal line each lap. Ten seconds is about what one speed
    /// adjustment buys at those gains, and matches what a controller does: issue one reduction and let it ride
    /// rather than take it off the moment the numbers tip over.
    /// </summary>
    public const double ReleaseHysteresisSeconds = 10.0;

    /// <summary>Distance to the threshold (NM) inside which the lower §5-7-3.c.1.b / §5-7-3.c.2.b floors apply.</summary>
    public const double RegulatoryFloorDistanceNm = 20.0;

    /// <summary>
    /// Distance to the threshold (NM) inside which the arrival is assumed to be on the local controller's frequency,
    /// so the simulated tower may assign final approach speed (§5-7-3.f). The assignment is issued outside the
    /// §5-7-1.b.4 window (5 nm / the FAF) and held inside it.
    /// </summary>
    public const double TowerSpeedAuthorityNm = 10.0;

    /// <summary>
    /// Distance to the threshold (NM) from which an arrival known to be inbound to a runway (an expected approach)
    /// but not yet cleared is spaced — the §5-7-3.c.1.b / §5-7-3.c.2.b 20-mile boundary, beyond which the pass could
    /// only ever assign the 210/200 figures.
    /// </summary>
    public const double PreClearanceRangeNm = RegulatoryFloorDistanceNm;

    /// <summary>§5-7-3.c.1.a — turbojet arrival below 10,000 ft, beyond 20 flying miles.</summary>
    public const double JetFloorKts = 210.0;

    /// <summary>§5-7-3.c.1.b — turbojet arrival within 20 flying miles of the threshold.</summary>
    public const double JetFloorWithin20Kts = 170.0;

    /// <summary>§5-7-3.c.2.a — reciprocating or turboprop arrival below 10,000 ft, beyond 20 flying miles.</summary>
    public const double RecipFloorKts = 200.0;

    /// <summary>§5-7-3.c.2.b — reciprocating or turboprop arrival within 20 flying miles of the threshold.</summary>
    public const double RecipFloorWithin20Kts = 150.0;

    /// <summary>§5-7-3.e — helicopters, at any distance.</summary>
    public const double HelicopterFloorKts = 60.0;

    /// <summary>Fuselage length (ft) assumed for a type the FAA database does not carry — the figure <see cref="RunwayExitPhase"/> uses.</summary>
    private const double DefaultAircraftLengthFt = 60.0;

    /// <summary>
    /// Live rollout state of a leader that has already touched down, used to refine the required interval from what
    /// the aircraft is actually doing instead of the per-category constant. Two legs along the path it will fly to
    /// vacate: one flown while braking from <paramref name="BrakingEntrySpeedKts"/> to
    /// <paramref name="BrakingExitSpeedKts"/>, and one flown steadily at <paramref name="SteadySpeedKts"/>. Their
    /// order along the path does not change the total, so the two landing regimes fill them differently — the
    /// landing rollout brakes onto its exit and then holds the turn-off speed, the aircraft already on the exit
    /// holds the taxi ceiling and then brakes to the stop. See <see cref="TryBuildRollout"/>.
    /// </summary>
    /// <param name="BrakingLegNm">Distance still to fly on the leg the leader is decelerating over.</param>
    /// <param name="BrakingEntrySpeedKts">Speed entering that leg, and the ceiling every other speed here is clamped to.</param>
    /// <param name="BrakingExitSpeedKts">Speed at the end of the braking leg — the exit's turn-off speed, or zero for a stop.</param>
    /// <param name="SteadyLegNm">Distance flown at <paramref name="SteadySpeedKts"/>.</param>
    /// <param name="SteadySpeedKts">Speed the steady leg is flown at: the exit's turn-off speed, or the exit route's taxi ceiling.</param>
    /// <param name="ElapsedSinceThresholdSeconds">
    /// Time already spent since the leader crossed the threshold, so the result stays an interval between threshold
    /// crossings rather than a time-from-now.
    /// </param>
    public readonly record struct LeaderRollout(
        double BrakingLegNm,
        double BrakingEntrySpeedKts,
        double BrakingExitSpeedKts,
        double SteadyLegNm,
        double SteadySpeedKts,
        double ElapsedSinceThresholdSeconds
    );

    /// <summary>
    /// Everything about the follower the ceiling arithmetic needs: what it is, where it is, and the speed band it may
    /// be held in.
    /// </summary>
    /// <param name="Category">Selects the §5-7-3 floor.</param>
    /// <param name="GroundSpeedKts">Present ground speed — converts the shortfall in seconds into a gap error in miles.</param>
    /// <param name="DistanceToThresholdNm">Direct distance to the landing threshold, against the §5-7-3.c 20-mile boundary.</param>
    /// <param name="VrefKts">Final approach speed — the hard floor no §5-7-3 figure may push the ceiling below.</param>
    /// <param name="ScheduledKts">The speed the follower would fly unconstrained; the ceiling is never raised above it.</param>
    public readonly record struct FollowerProfile(
        AircraftCategory Category,
        double GroundSpeedKts,
        double DistanceToThresholdNm,
        double VrefKts,
        double ScheduledKts
    );

    /// <summary>
    /// Seconds that must separate the leader's and the follower's threshold crossings for the leader to be clear of
    /// the runway in time. Two regimes: with <paramref name="rollout"/> null the leader is still airborne and the
    /// per-category constant applies (the follower is far out and its own ETA is coarse too); with
    /// <paramref name="rollout"/> supplied the leader has touched down and the interval is refined from its live
    /// state. Floored by the greater of <see cref="TerminalRadarFloorNm"/> and <paramref name="wakeSeparationNm"/>
    /// (from <see cref="WakeTurbulenceData.OnApproachWakeSeparationNm(string, AircraftCategory, string, AircraftCategory)"/>,
    /// which returns zero for a non-wake pair), converted to seconds at the follower's Vref.
    /// </summary>
    public static double RequiredThresholdIntervalSeconds(
        AircraftCategory leaderCategory,
        LeaderRollout? rollout,
        double wakeSeparationNm,
        double followerVrefKts
    )
    {
        double occupancy = rollout is { } live ? RolloutIntervalSeconds(live) : AirborneLeaderIntervalSeconds(leaderCategory);
        double separationNm = Math.Max(TerminalRadarFloorNm, wakeSeparationNm);
        return Math.Max(occupancy, TravelSeconds(separationNm, followerVrefKts));
    }

    /// <summary>
    /// Per-category threshold interval for a leader that has not touched down yet. Coarse by design — a single
    /// constant cannot follow the exit geometry, and the follower is far enough out that its own ETA is no better.
    /// Helicopters and any future category fall to the piston figure, the shortest occupancy of the three.
    /// </summary>
    public static double AirborneLeaderIntervalSeconds(AircraftCategory leaderCategory) =>
        leaderCategory switch
        {
            AircraftCategory.Jet => JetIntervalSeconds,
            AircraftCategory.Turboprop => TurbopropIntervalSeconds,
            _ => PistonIntervalSeconds,
        };

    /// <summary>
    /// The lowest speed §5-7-3 lets a controller assign this arrival at this distance: §5-7-3.c.1 for a turbojet,
    /// §5-7-3.c.2 for a reciprocating or turboprop aircraft, §5-7-3.e for a helicopter. The 20-mile boundary is
    /// §5-7-3.c.1.b / §5-7-3.c.2.b; see the class note on flying-vs-direct distance.
    /// </summary>
    public static double RegulatoryFloorKts(AircraftCategory category, double distanceToThresholdNm)
    {
        bool within20 = distanceToThresholdNm <= RegulatoryFloorDistanceNm;
        return category switch
        {
            AircraftCategory.Helicopter => HelicopterFloorKts,
            AircraftCategory.Jet => within20 ? JetFloorWithin20Kts : JetFloorKts,
            _ => within20 ? RecipFloorWithin20Kts : RecipFloorKts,
        };
    }

    /// <summary>
    /// True when the arrival is close enough to the threshold that the simulated local controller has it on frequency
    /// and may assign final approach speed under §5-7-3.f — see <see cref="TowerSpeedAuthorityNm"/>.
    /// </summary>
    public static bool IsInsideTowerSpeedAuthority(double distanceToThresholdNm) => distanceToThresholdNm <= TowerSpeedAuthorityNm;

    /// <summary>
    /// The "final approach speed" a tower instruction means: Vapp — <paramref name="vrefKts"/> plus the wind/gust
    /// additive <see cref="AircraftPerformance.WindApproachAdditive"/> supplies (the
    /// <see cref="Phases.Tower.FinalApproachPhase"/> Vapp formula), never bare Vref.
    /// </summary>
    public static double FinalApproachSpeedKts(double vrefKts, double windAdditiveKts) => vrefKts + windAdditiveKts;

    /// <summary>
    /// Path distance (nm) the leader must still cover past its hold-short node before <em>all parts</em> of it are
    /// across the holding position marking (AIM 2-3-5.a.1, AIM 4-3-21.b): half a fuselage length, which is exactly
    /// the offset <see cref="RunwayExitPhase"/> taxis to past that node.
    /// </summary>
    public static double TailClearanceNm(string aircraftType) =>
        (FaaAircraftDatabase.Get(aircraftType)?.LengthFt ?? DefaultAircraftLengthFt) / 2.0 / GeoMath.FeetPerNm;

    /// <summary>
    /// The live rollout of an aircraft that has landed and is vacating, or null when there is nothing to read it
    /// from — the aircraft is still airborne, no exit has been resolved (no ground layout, or the rollout planner has
    /// not committed yet), or it is in neither landing phase. A null is an <em>unknown</em>, never a "clear": the
    /// spacing pass falls back to the per-category constant and the occupied-runway go-around treats the runway as
    /// still occupied.
    ///
    /// <para>Two shapes of the same route. A <see cref="LandingPhase"/> aircraft still has the rollout leg down the
    /// centerline in front of it, braking to its exit's turn-off speed by the branch point, then the exit path and
    /// the tail clearance at that turn-off speed. A <see cref="RunwayExitPhase"/> aircraft is already on that exit:
    /// the rollout leg is behind it and what remains is the run to the hold-short node and past it, which its
    /// navigator flies at the exit route's taxi ceiling until the braking point and then brakes to the stop — see
    /// <see cref="ExitRollout"/>. A stopped aircraft reads as never clearing in either shape.</para>
    /// </summary>
    /// <param name="aircraft">The landed aircraft whose vacate is being predicted.</param>
    /// <param name="elapsedSinceThresholdSeconds">
    /// Seconds since it crossed the landing threshold, carried through to
    /// <see cref="RequiredThresholdIntervalSeconds"/> so that result stays an interval between threshold crossings.
    /// Pass zero when the caller wants a plain time-from-now (see <see cref="SecondsToRunwayClear"/>).
    /// </param>
    public static LeaderRollout? TryBuildRollout(AircraftState aircraft, double elapsedSinceThresholdSeconds)
    {
        if (!aircraft.IsOnGround)
        {
            return null;
        }

        double elapsed = Math.Max(0.0, elapsedSinceThresholdSeconds);
        double tailClearance = TailClearanceNm(aircraft.AircraftType);
        return aircraft.Phases?.CurrentPhase switch
        {
            LandingPhase { CandidateExit: { } exit } => new LeaderRollout(
                BrakingLegNm: GeoMath.DistanceNm(aircraft.Position, exit.BranchPointNode.Position),
                BrakingEntrySpeedKts: aircraft.GroundSpeed,
                BrakingExitSpeedKts: exit.TurnOffSpeed,
                SteadyLegNm: ExitPathDistanceNm(exit) + tailClearance,
                SteadySpeedKts: exit.TurnOffSpeed,
                ElapsedSinceThresholdSeconds: elapsed
            ),
            RunwayExitPhase { TargetHoldShortNode: { } holdShort } => ExitRollout(
                aircraft,
                GeoMath.DistanceNm(aircraft.Position, holdShort.Position) + tailClearance,
                elapsed
            ),
            _ => null,
        };
    }

    /// <summary>
    /// The vacate of an aircraft already on its exit path: steady, then the stop. <see cref="RunwayExitPhase"/> caps
    /// its navigator at <see cref="CategoryPerformance.TaxiSpeed"/> — bumped by
    /// <see cref="CategoryPerformance.TaxiExpediteMultiplier"/> on an expedited exit — and that navigator brakes to
    /// the hold-short stop at <see cref="CategoryPerformance.TaxiDecelRate"/>
    /// (<see cref="CategoryPerformance.ExpediteExitDecelRate"/> when expediting), so the remainder is one leg held at
    /// the ceiling and a final v²/2a of braking. The steady speed is never above the speed the leader is doing now:
    /// a slower one is not sped up to the ceiling (re-acceleration at <see cref="CategoryPerformance.TaxiAccelRate"/>
    /// is deliberately not modelled — it would only ever read the leader clear sooner, and §3-10-3.a.1 is the
    /// requirement this anticipates), and a stopped one has no speed to cover the steady leg with and so never
    /// clears. The braking leg is always taken from the present ground speed, which on the first ticks of an exit is
    /// still above the ceiling: v²/2a from there covers both the bleed down to the ceiling and the stop, and at a
    /// constant rate the two together take the same time however the steady leg sits between them. A remainder
    /// already inside that stopping distance has no steady leg left — it is all braking, from the present speed.
    /// </summary>
    private static LeaderRollout ExitRollout(AircraftState aircraft, double remainderNm, double elapsedSeconds)
    {
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        bool expediting = aircraft.Ground.IsExpeditingExit;
        double ceiling = CategoryPerformance.TaxiSpeed(category) * (expediting ? CategoryPerformance.TaxiExpediteMultiplier : 1.0);
        double decelRate = expediting ? CategoryPerformance.ExpediteExitDecelRate(category) : CategoryPerformance.TaxiDecelRate(category);
        double entry = Math.Max(0.0, aircraft.GroundSpeed);
        double steady = Math.Min(entry, ceiling);
        double stoppingNm = entry * entry / (2.0 * decelRate) / 3600.0;

        if (stoppingNm >= remainderNm)
        {
            return new LeaderRollout(
                BrakingLegNm: remainderNm,
                BrakingEntrySpeedKts: entry,
                BrakingExitSpeedKts: 0.0,
                SteadyLegNm: 0.0,
                SteadySpeedKts: steady,
                ElapsedSinceThresholdSeconds: elapsedSeconds
            );
        }

        return new LeaderRollout(
            BrakingLegNm: stoppingNm,
            BrakingEntrySpeedKts: entry,
            BrakingExitSpeedKts: 0.0,
            SteadyLegNm: remainderNm - stoppingNm,
            SteadySpeedKts: steady,
            ElapsedSinceThresholdSeconds: elapsedSeconds
        );
    }

    /// <summary>
    /// Seconds from now until the aircraft is clear of the runway: the braking leg at the constant-deceleration mean
    /// of its entry and exit speeds, plus the steady leg at its own speed. Positive infinity when a leg has distance
    /// left and no speed to cover it — an aircraft stopped on the pavement never clears at its present speed. The one
    /// arithmetic behind both the spacing pass's required interval and the go-around's "will it be clear by the
    /// threshold crossing" test, so the two cannot disagree about when an aircraft is off the runway.
    /// </summary>
    public static double SecondsToRunwayClear(LeaderRollout rollout)
    {
        double brakingExitSpeed = ClampToEntrySpeed(rollout.BrakingExitSpeedKts, rollout.BrakingEntrySpeedKts);
        return BrakingLegSeconds(rollout.BrakingLegNm, rollout.BrakingEntrySpeedKts, brakingExitSpeed)
            + TravelSeconds(rollout.SteadyLegNm, SteadySpeedKts(rollout));
    }

    /// <summary>
    /// True when the follower would cross the threshold sooner than <paramref name="requiredIntervalSeconds"/> after
    /// the leader. Both ETAs are seconds from now, so a leader that has already crossed carries a negative one. One
    /// threshold in, two out: the caller passes the plain required interval to engage and that interval plus
    /// <see cref="ReleaseHysteresisSeconds"/> while already engaged, so the release sits above the engagement.
    /// </summary>
    public static bool IsConflictPredicted(double leaderThresholdEtaSeconds, double followerThresholdEtaSeconds, double requiredIntervalSeconds) =>
        followerThresholdEtaSeconds < leaderThresholdEtaSeconds + requiredIntervalSeconds;

    /// <summary>
    /// Speed ceiling (kts) that opens the missing interval. <paramref name="shortfallSeconds"/> — how much sooner
    /// than the required interval the follower would arrive — is expressed as the distance the follower covers in
    /// that time and handed to <see cref="ArrivalSpacingManager.SpacingCeilingKts"/> as a pure gap error, so the
    /// floor, the scheduled-profile cap and the shared proportional gain all come from the one controller the
    /// generator stream already uses. The floor is <c>Max(Vref, Min(scheduled, <see cref="RegulatoryFloorKts"/>))</c>
    /// — see the speed-floor note on the class.
    ///
    /// <para>A follower with no ground speed covers no distance in the shortfall, so it contributes no gap error at
    /// all: without that guard an unbounded shortfall behind a stopped leader multiplies out to <c>∞ × 0</c> and
    /// returns NaN into <see cref="ControlTargets.SpeedCeiling"/> and the speed integrator.</para>
    /// </summary>
    public static double ProtectionCeilingKts(double leaderIasKts, double shortfallSeconds, FollowerProfile follower)
    {
        double groundSpeed = Math.Max(0.0, follower.GroundSpeedKts);
        double shortfallNm = groundSpeed > 0.0 ? Math.Max(0.0, shortfallSeconds) * groundSpeed / 3600.0 : 0.0;
        double floor = Math.Max(
            follower.VrefKts,
            Math.Min(follower.ScheduledKts, RegulatoryFloorKts(follower.Category, follower.DistanceToThresholdNm))
        );
        return ArrivalSpacingManager.SpacingCeilingKts(leaderIasKts, 0.0, shortfallNm, floor, follower.ScheduledKts);
    }

    /// <summary>
    /// The two vacate legs added to the time already elapsed since the leader's threshold crossing. A leader stopped
    /// on the runway never clears at its present speed, so the interval is unbounded — the follower then holds its
    /// lowest ceiling until the leader moves again, and the occupied-runway go-around remains the safety net.
    /// </summary>
    private static double RolloutIntervalSeconds(LeaderRollout rollout) =>
        Math.Max(0.0, rollout.ElapsedSinceThresholdSeconds) + SecondsToRunwayClear(rollout);

    /// <summary>
    /// The speed the steady leg is flown at: the planned speed, never above the one the leader is doing now. A leader
    /// already slower than its exit's turn-off speed will not accelerate to make the turn, and a stopped one covers
    /// nothing at all.
    /// </summary>
    private static double SteadySpeedKts(LeaderRollout rollout) => ClampToEntrySpeed(rollout.SteadySpeedKts, rollout.BrakingEntrySpeedKts);

    /// <summary>
    /// <paramref name="speedKts"/> held to the speed the leader is doing now: no leg of a plan may be flown faster
    /// than the aircraft is going, or the arithmetic reports it clear early.
    /// </summary>
    private static double ClampToEntrySpeed(double speedKts, double entrySpeedKts) => Math.Clamp(speedKts, 0.0, Math.Max(0.0, entrySpeedKts));

    /// <summary>
    /// Seconds to fly <paramref name="distanceNm"/> while decelerating from <paramref name="entrySpeedKts"/> to
    /// <paramref name="exitSpeedKts"/>: constant deceleration covers the leg at the mean of the two. An aircraft
    /// that is not moving now is not decelerating either — it never covers the leg, whatever its plan says.
    /// </summary>
    private static double BrakingLegSeconds(double distanceNm, double entrySpeedKts, double exitSpeedKts)
    {
        if (distanceNm <= 0.0)
        {
            return 0.0;
        }

        return entrySpeedKts > 0.0 ? TravelSeconds(distanceNm, (entrySpeedKts + exitSpeedKts) / 2.0) : double.PositiveInfinity;
    }

    /// <summary>
    /// Polyline distance (nm) from the exit's branch point through its path to the hold-short node — what the
    /// aircraft still has to taxi to reach the bar. Repeated nodes are skipped, so a degenerate exit whose
    /// branch point, path and hold short are the same node measures zero.
    /// </summary>
    private static double ExitPathDistanceNm(Phases.ResolvedExitInfo exit)
    {
        double total = 0.0;
        GroundNode previous = exit.BranchPointNode;
        foreach (GroundNode? node in exit.Path.Append(exit.HoldShortNode))
        {
            if (node.Id == previous.Id)
            {
                continue;
            }

            total += GeoMath.DistanceNm(previous.Position, node.Position);
            previous = node;
        }

        return total;
    }

    /// <summary>Seconds to cover <paramref name="distanceNm"/> at <paramref name="speedKts"/>; zero for a non-positive distance.</summary>
    private static double TravelSeconds(double distanceNm, double speedKts)
    {
        if (distanceNm <= 0.0)
        {
            return 0.0;
        }

        return speedKts > 0.0 ? distanceNm / speedKts * 3600.0 : double.PositiveInfinity;
    }
}
