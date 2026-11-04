using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1451: a speed revert is never applied in a hold or inside the 5 nm final gate — the aircraft flies the holding
/// phase's speed or its approach speed (7110.65 §5-7-1.b.4) — and every revert on the route goes rather than waiting for
/// a fix past the gate. A crossing speed issued inside the gate is refused as a plain SPD is; one issued outside is
/// accepted and cancelled at the gate, ending the controller crossing speeds still ahead inside it.
/// </summary>
public sealed class SpeedRevertForbiddenTests
{
    /// <summary>The speed tolerance a test allows a flown speed against its figure (kt).</summary>
    private const double SpeedSnapKts = 2;

    /// <summary>Back from the landing threshold the gate opens: this is inside it.</summary>
    private const double InsideTheGateNm = 4.8;

    /// <summary>Back from the landing threshold every gate test starts outside: this is beyond the five.</summary>
    private const double OutsideTheGateNm = 8.0;

    /// <summary>How far from the landing threshold the crossing fix sits, on the extended centreline.</summary>
    private const double CrossingFixFromThresholdNm = 2.0;

    /// <summary>The fix name a crossing is stamped on when no navdata fix is named; only its position matters.</summary>
    private const string GateFixName = "FINL";

    /// <summary>The real fix the synthetic runway's approach is laid along, so CFIX commands parse.</summary>
    private const string ApproachFix = "SUNOL";

    private readonly ITestOutputHelper _output;

    public SpeedRevertForbiddenTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// SMAX is in force outside the gate; the crossing speed that replaces it on a fix inside the gate never re-arms it
    /// once the fix is passed: at the gate the crossing ends and the aircraft flies its approach speed there.
    /// </summary>
    [Fact]
    public void SmaxThenCrossingInsideTheFinalGate_PastTheFix_SmaxIsNotReArmed()
    {
        RunwayInfo runway = Runway();
        AircraftState ac = Arrival(runway, OutsideTheGateNm, ias: 220);
        Assert.True(Send(ac, "SMAX").Success);
        Assert.True(ac.Targets.SpeedMaxHeld);

        CommandResult crossing = CrossFix(ac, runway, speed: 200);
        Assert.True(crossing.Success, crossing.Message);
        Assert.False(ac.Targets.SpeedMaxHeld);
        NavigationTarget fix = Assert.Single(ac.Targets.NavigationRoute);
        Assert.True(fix.RevertSpeedMax, "premise: the crossing speed replaces SMAX and waits to re-arm it");

        LatLon threshold = Threshold(runway);
        FlyUntilInsideTheGate(ac, threshold);
        FlyOneSecond(ac);
        _output.WriteLine($"inside the gate at {GeoMath.DistanceNm(ac.Position, threshold):F2} nm");
        Assert.DoesNotContain(ac.Targets.NavigationRoute, target => target.RevertSpeedMax);
        Assert.Null(ac.Targets.AssignedSpeed);

        double? crossingIas = FlyUntilSequenced(ac, GateFixName);
        _output.WriteLine($"crossed {GateFixName} at {crossingIas:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.NotNull(crossingIas);
        Assert.False(ac.Targets.SpeedMaxHeld);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.DoesNotContain(ac.Targets.NavigationRoute, target => target.RevertSpeedMax);
    }

    /// <summary>
    /// A crossing speed stamped on the hold fix while holding does not re-arm SMAX past it: the hold's speed reverts go
    /// rather than waiting for a fix beyond the hold, and nothing is restored on the fix the aircraft just passed.
    /// </summary>
    [Fact]
    public void CrossingOnTheHoldFix_InAHold_NoSmaxReArmAndRevertsCleared()
    {
        LatLon holdFix = FixPosition("SUNOL");
        AircraftState ac = Airborne("B738", altitude: 6_000, ias: 210, position: GeoMath.ProjectPoint(holdFix, new TrueHeading(0), 1.5));
        Assert.True(Send(ac, "HOLD SUNOL 004 R").Success);
        Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.True(Send(ac, "SMAX").Success);
        Assert.True(ac.Targets.SpeedMaxHeld);

        // A CFIX reaches the hold phase's CanAcceptCommand gate and clears the hold, so the crossing speed it would leave
        // on the hold fix — SMAX replaced by it, waiting to re-arm past it — is written here instead.
        NavigationTarget holder = Assert.Single(ac.Targets.NavigationRoute);
        holder.SpeedRestriction = new CifpSpeedRestriction(250, CifpSpeedRestrictionType.AtOrBelow);
        holder.SpeedTransmissionId = 1;
        holder.RevertSpeedMax = true;
        ac.Targets.SpeedMaxHeld = false;
        ac.Targets.AssignedSpeed = 250;

        for (int second = 0; (second < 600) && ac.Targets.NavigationRoute.Exists(target => target.Name == "SUNOL"); second++)
        {
            FlyOneSecond(ac);
        }

        Assert.DoesNotContain(ac.Targets.NavigationRoute, target => target.Name == "SUNOL");
        Assert.False(ac.Targets.SpeedMaxHeld); // no SMAX re-arm past the crossing
        Assert.False(holder.RevertSpeedMax); // the crossed fix loses its reverts too
        Assert.Equal((double?)250, ac.Targets.AssignedSpeed); // no revert applied
    }

    /// <summary>
    /// A resume written with a crossing still ends the crossing speed at its fix: the hold clears the fix's reverts
    /// without applying the resume, and the dropped resume must not turn the crossed speed into a ceiling.
    /// </summary>
    [Fact]
    public void CrossingWithAResume_OnTheHoldFix_DoesNotBecomeACeiling()
    {
        LatLon holdFix = FixPosition("SUNOL");
        AircraftState ac = Airborne("B738", altitude: 12_000, ias: 250, position: GeoMath.ProjectPoint(holdFix, new TrueHeading(0), 1.5));
        Assert.True(Send(ac, "HOLD SUNOL 004 R").Success);
        Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);

        NavigationTarget holder = Assert.Single(ac.Targets.NavigationRoute);
        holder.SpeedRestriction = new CifpSpeedRestriction(220, CifpSpeedRestrictionType.AtOrBelow);
        holder.SpeedTransmissionId = 1;
        holder.RevertResume = SpeedResumeKind.Published;
        double? ceilingBefore = ac.Targets.SpeedCeiling;

        for (int second = 0; (second < 600) && ac.Targets.NavigationRoute.Exists(target => target.Name == "SUNOL"); second++)
        {
            FlyOneSecond(ac);
        }

        Assert.DoesNotContain(ac.Targets.NavigationRoute, target => target.Name == "SUNOL");
        Assert.Null(holder.RevertResume);
        Assert.NotEqual((double?)220, ac.Targets.SpeedCeiling);
        Assert.Equal(ceilingBefore, ac.Targets.SpeedCeiling);
    }

    /// <summary>A CFIX carrying a speed is a speed assignment, refused inside the 5 nm gate with SPD's own text and cite.</summary>
    [Fact]
    public void CfixWithSpeedInsideTheFinalGate_IsRefusedLikeSpd()
    {
        RunwayInfo runway = Runway();
        AircraftState ac = Arrival(runway, InsideTheGateNm, ias: 200);

        CommandResult spd = Send(ac, "SPD 180");
        Assert.False(spd.Success);
        Assert.Contains("5nm final", spd.Message);
        Assert.Contains("5-7-1.b.4", spd.Message);

        CommandResult crossing = CrossFix(ac, runway, speed: 180);
        Assert.False(crossing.Success);
        Assert.Equal(spd.Message, crossing.Message);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    /// <summary>
    /// A CFIX carrying a speed is accepted outside the gate and cancelled once the aircraft reaches it: the crossing and
    /// its assignment end, the target goes, and the aircraft cannot accelerate past the speed it was flying at the gate.
    /// </summary>
    [Fact]
    public void CfixWithSpeedOutsideTheGate_IsAcceptedThenCancelledAtTheGate()
    {
        (AircraftState ac, RunwayInfo runway) = FixApproach(ApproachFix, CrossingFixFromThresholdNm, OutsideTheGateNm, altitude: 3_000, ias: 220);

        CommandResult crossing = Send(ac, $"CFIX {ApproachFix} 030 200");
        Assert.True(crossing.Success, crossing.Message);
        Assert.Equal((double?)200, ac.Targets.AssignedSpeed);
        NavigationTarget fix = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal(200, fix.SpeedRestriction?.SpeedKts);
        Assert.NotNull(fix.SpeedTransmissionId);

        LatLon threshold = Threshold(runway);
        FlyUntilInsideTheGate(ac, threshold);
        FlyOneSecond(ac);
        _output.WriteLine($"inside the gate at {GeoMath.DistanceNm(ac.Position, threshold):F2} nm");

        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.Null(fix.SpeedRestriction);
        Assert.Null(fix.SpeedTransmissionId);
        Assert.NotNull(ac.Targets.SpeedCeiling);
        Assert.True((ac.Targets.TargetSpeed is null) || (ac.Targets.TargetSpeed <= ac.Targets.SpeedCeiling));

        double gateIas = ac.IndicatedAirspeed;
        double highest = FlyAndTrackHighestIas(ac, 60, gateIas);
        _output.WriteLine($"gate {gateIas:F0} kt, highest after {highest:F0} kt");
        Assert.True(highest <= gateIas + SpeedSnapKts, $"accelerated from {gateIas:F0} to {highest:F0} kt inside the gate");
    }

    /// <summary>
    /// A crossing speed cancelled at the gate releases the ATC restriction as a plain SPD's auto-cancel does: the descent
    /// schedule cannot accelerate a hand-vectored inbound toward its band speed inside the final (7110.65 §5-7-1.b.4).
    /// </summary>
    [Fact]
    public void CfixSpeedCancelledAtTheGate_DoesNotAccelerateDuringTheDescent()
    {
        (AircraftState ac, RunwayInfo runway) = FixApproach("HEC", CrossingFixFromThresholdNm, OutsideTheGateNm, altitude: 3_000, ias: 220);
        ac.Targets.TargetAltitude = 1_500;
        ac.Targets.AssignedAltitude = 1_500;
        Assert.True(Send(ac, "CFIX HEC 030 200").Success);

        LatLon threshold = Threshold(runway);
        FlyUntilInsideTheGate(ac, threshold);
        FlySeconds(ac, 2);
        double gateIas = ac.IndicatedAirspeed;
        Assert.Null(ac.Targets.AssignedSpeed);
        double ceiling = Assert.IsType<double>(ac.Targets.SpeedCeiling);
        _output.WriteLine($"gate {gateIas:F0} kt, ceiling {ceiling:F0} kt");

        double highest = FlyAndTrackHighestIas(ac, 60, gateIas);
        _output.WriteLine($"highest after {highest:F0} kt, {ac.Altitude:F0} ft");
        Assert.InRange(ceiling, gateIas - SpeedSnapKts, gateIas + SpeedSnapKts);
        Assert.True(highest <= gateIas + SpeedSnapKts, $"accelerated from {gateIas:F0} to {highest:F0} kt inside the gate");
    }

    /// <summary>
    /// A crossing speed above the present speed is cancelled at the gate rather than accelerated toward: the aircraft
    /// keeps the speed it had there, as a plain SPD's auto-cancel leaves it.
    /// </summary>
    [Fact]
    public void CfixSpeedAboveThePresentSpeed_CancelledAtTheGate_DoesNotAccelerate()
    {
        (AircraftState ac, RunwayInfo runway) = FixApproach("HEC", CrossingFixFromThresholdNm, 5.2, altitude: 2_500, ias: 200);
        CommandResult crossing = Send(ac, "CFIX HEC 025 250");
        Assert.True(crossing.Success, crossing.Message);
        Assert.Equal((double?)250, ac.Targets.TargetSpeed);

        LatLon threshold = Threshold(runway);
        FlyUntilInsideTheGate(ac, threshold);
        FlySeconds(ac, 1);
        double gateIas = ac.IndicatedAirspeed;
        Assert.Null(ac.Targets.AssignedSpeed);
        _output.WriteLine($"gate {gateIas:F0} kt, target {ac.Targets.TargetSpeed:F0} kt");

        double highest = FlyAndTrackHighestIas(ac, 60, gateIas);
        _output.WriteLine($"gate {gateIas:F0} kt, highest after {highest:F0} kt");
        Assert.True(highest <= gateIas + SpeedSnapKts, $"accelerated from {gateIas:F0} to {highest:F0} kt inside the gate");
    }

    /// <summary>
    /// A forced speed exempts the gate: the revert a crossing in the same transmission leaves behind is applied past the
    /// fix, so the aircraft flies the forced 160 rather than the crossing's 180.
    /// </summary>
    [Fact]
    public void ForcedSpeed_KeepsItsRevertPastACrossingInsideTheGate()
    {
        (AircraftState ac, _) = FixApproach(ApproachFix, CrossingFixFromThresholdNm, OutsideTheGateNm, altitude: 2_000, ias: 210);
        CommandResult crossing = Send(ac, $"CFIX {ApproachFix} 020 180, SPEEDF 160");
        Assert.True(crossing.Success, crossing.Message);
        Assert.True(ac.Targets.SpeedOverridesFinalGate);
        NavigationTarget fix = Assert.Single(ac.Targets.NavigationRoute);
        Assert.Equal(180, fix.SpeedRestriction?.SpeedKts);
        Assert.Equal((double?)160, fix.RevertSpeed);
        Assert.Equal((double?)180, ac.Targets.AssignedSpeed);

        double? crossingIas = FlyUntilSequenced(ac, ApproachFix);
        _output.WriteLine($"crossed {ApproachFix} at {crossingIas:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.NotNull(crossingIas);

        FlySeconds(ac, 60);
        Assert.Equal((double?)160, ac.Targets.AssignedSpeed);
        Assert.InRange(ac.IndicatedAirspeed, 160 - SpeedSnapKts, 160 + SpeedSnapKts);
    }

    /// <summary>
    /// A CFIX carrying a speed is an ATC speed act like SPD, so it ends a forced speed's override of the final gate: the
    /// crossing it sets is then cancelled at the gate like any other.
    /// </summary>
    [Fact]
    public void CfixWithSpeed_ClearsTheForcedSpeedOverride()
    {
        (AircraftState ac, RunwayInfo runway) = FixApproach(ApproachFix, CrossingFixFromThresholdNm, OutsideTheGateNm, altitude: 2_000, ias: 200);
        Assert.True(Send(ac, "SPEEDF 180").Success);
        Assert.True(ac.Targets.SpeedOverridesFinalGate);

        CommandResult crossing = Send(ac, $"CFIX {ApproachFix} 020 170");
        Assert.True(crossing.Success, crossing.Message);
        Assert.False(ac.Targets.SpeedOverridesFinalGate);
        NavigationTarget fix = Assert.Single(ac.Targets.NavigationRoute);

        FlyUntilInsideTheGate(ac, Threshold(runway));
        FlyOneSecond(ac);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.Null(fix.SpeedRestriction);
    }

    /// <summary>
    /// A constrained direct-to carrying a speed is an ATC speed act like SPD, so it ends a forced speed's override of the
    /// final gate: the crossing it sets is then cancelled at the gate like any other.
    /// </summary>
    [Fact]
    public void ConstrainedDirectWithSpeeds_ClearsTheForcedSpeedOverride()
    {
        (AircraftState ac, RunwayInfo runway) = FixApproach(ApproachFix, CrossingFixFromThresholdNm, OutsideTheGateNm, altitude: 2_000, ias: 200);
        Assert.True(Send(ac, "SPEEDF 180").Success);
        Assert.True(ac.Targets.SpeedOverridesFinalGate);

        var cmd = new ConstrainedForceDirectToCommand(
            [new ResolvedFix(ApproachFix, FixPosition(ApproachFix).Lat, FixPosition(ApproachFix).Lon)],
            [],
            new Dictionary<int, int> { [0] = 170 },
            null
        );
        CommandResult result = CommandDispatcher.Dispatch(cmd, ac, TestDispatch.Context(new SerializableRandom(1)));
        Assert.True(result.Success, result.Message);
        Assert.False(ac.Targets.SpeedOverridesFinalGate);
        NavigationTarget fix = Assert.Single(ac.Targets.NavigationRoute);

        FlyUntilInsideTheGate(ac, Threshold(runway));
        FlyOneSecond(ac);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.Null(fix.SpeedRestriction);
    }

    /// <summary>
    /// A level-off beneath an airspace shelf is not a hold: the pilot keeps the aircraft at its assigned speed,
    /// so a crossing's SMAX re-arm is applied past the fix as usual.
    /// </summary>
    [Fact]
    public void LevelOffBelowAShelf_IsNotAHold_SoSmaxReArms()
    {
        LatLon fix = FixPosition("SUNOL");
        AircraftState ac = Airborne("B738", altitude: 4_000, ias: 250, position: GeoMath.ProjectPoint(fix, new TrueHeading(0), 1.5));
        ac.Phases = new PhaseList();
        ac.Phases.Add(
            new AirspaceBoundaryHoldPhase
            {
                AirspaceClass = AirspaceClass.Bravo,
                ReferencePosition = fix,
                Mode = AirspaceHoldMode.LevelOff,
                LevelOffCeilingFtMsl = 3_500,
            }
        );
        Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases.CurrentPhase);
        Assert.True(Send(ac, "SMAX").Success);

        var holder = new NavigationTarget
        {
            Name = "SUNOL",
            Position = fix,
            SpeedRestriction = new CifpSpeedRestriction(250, CifpSpeedRestrictionType.AtOrBelow),
            SpeedTransmissionId = 1,
            RevertSpeedMax = true,
        };
        ac.Targets.NavigationRoute.Add(holder);
        ac.Targets.SpeedMaxHeld = false;
        ac.Targets.AssignedSpeed = 250;

        for (int second = 0; (second < 600) && ac.Targets.NavigationRoute.Exists(target => target.Name == "SUNOL"); second++)
        {
            FlyOneSecond(ac);
        }

        Assert.DoesNotContain(ac.Targets.NavigationRoute, target => target.Name == "SUNOL");
        Assert.True(ac.Targets.SpeedMaxHeld, "a level-off is not a hold: the revert re-arms SMAX past the crossing");
    }

    /// <summary>
    /// A fix the aircraft passes on the tick it is inside the gate loses its reverts with the rest of the route: a resume
    /// waiting on it is not applied, and the crossing speed the procedure publishes there still holds the aircraft.
    /// </summary>
    [Fact]
    public void ResumeRevert_OnAFixCrossedInsideTheGate_IsDropped()
    {
        (AircraftState ac, RunwayInfo _) = FixApproach(ApproachFix, 4.4, 4.6, altitude: 3_000, ias: 200);
        ac.Targets.AssignedSpeed = 200;
        var crossing = new NavigationTarget
        {
            Name = ApproachFix,
            Position = FixPosition(ApproachFix),
            SpeedRestriction = new CifpSpeedRestriction(190, CifpSpeedRestrictionType.AtOrBelow),
            RevertResume = SpeedResumeKind.Published,
        };
        ac.Targets.NavigationRoute.Add(crossing);

        Assert.NotNull(FlyUntilSequenced(ac, ApproachFix));
        Assert.Null(crossing.RevertResume);
        Assert.Equal((double?)200, ac.Targets.AssignedSpeed);
    }

    /// <summary>
    /// The gate ends controller crossing speeds only: a speed the procedure publishes on an approach fix inside the gate
    /// stays a crossing, and only the controller's pending revert on it goes.
    /// </summary>
    [Fact]
    public void PublishedSpeedOnAFixInsideTheGate_SurvivesTheCrossingCancel()
    {
        (AircraftState ac, RunwayInfo runway) = FixApproach(ApproachFix, 3.0, OutsideTheGateNm, altitude: 3_000, ias: 220);
        var fix = new NavigationTarget
        {
            Name = ApproachFix,
            Position = FixPosition(ApproachFix),
            SpeedRestriction = new CifpSpeedRestriction(210, CifpSpeedRestrictionType.AtOrBelow),
            RevertSpeed = 240,
            RevertAssignedSpeed = 240,
        };
        ac.Targets.NavigationRoute.Add(fix);

        FlyUntilInsideTheGate(ac, threshold: Threshold(runway));
        FlyOneSecond(ac);

        Assert.Equal(210, fix.SpeedRestriction?.SpeedKts);
        Assert.Null(fix.RevertSpeed);
        Assert.Null(fix.RevertAssignedSpeed);
    }

    /// <summary>A constrained direct-to that sets crossing speeds is refused inside the gate with SPD's own text and cite.</summary>
    [Fact]
    public void ConstrainedDirectWithSpeedsInsideTheFinalGate_IsRefusedLikeSpd()
    {
        RunwayInfo runway = Runway();
        AircraftState ac = Arrival(runway, InsideTheGateNm, ias: 200);
        CommandResult spd = Send(ac, "SPD 180");
        Assert.False(spd.Success);

        var cmd = new ConstrainedForceDirectToCommand(
            [new ResolvedFix(ApproachFix, FixPosition(ApproachFix).Lat, FixPosition(ApproachFix).Lon)],
            new Dictionary<int, ConstrainedFixAltitude> { [0] = new(3_000, CrossFixAltitudeType.At) },
            new Dictionary<int, int> { [0] = 200 },
            null
        );
        CommandResult result = CommandDispatcher.Dispatch(cmd, ac, TestDispatch.Context(new SerializableRandom(1)));

        Assert.False(result.Success);
        Assert.Equal(spd.Message, result.Message);
        Assert.Empty(ac.Targets.NavigationRoute);
    }

    /// <summary>A constrained direct-to with altitudes only is an altitude instruction, accepted inside the gate.</summary>
    [Fact]
    public void ConstrainedDirectAltitudesOnlyInsideTheFinalGate_IsAccepted()
    {
        RunwayInfo runway = Runway();
        AircraftState ac = Arrival(runway, InsideTheGateNm, ias: 200);
        var cmd = new ConstrainedForceDirectToCommand(
            [new ResolvedFix(ApproachFix, FixPosition(ApproachFix).Lat, FixPosition(ApproachFix).Lon)],
            new Dictionary<int, ConstrainedFixAltitude> { [0] = new(3_000, CrossFixAltitudeType.At) },
            null,
            null
        );
        CommandResult result = CommandDispatcher.Dispatch(cmd, ac, TestDispatch.Context(new SerializableRandom(1)));

        Assert.True(result.Success, result.Message);
        Assert.Single(ac.Targets.NavigationRoute);
    }

    /// <summary>A CFIX with an altitude only is an altitude instruction, accepted inside the gate.</summary>
    [Fact]
    public void CfixAltitudeOnlyInsideTheFinalGate_IsAccepted()
    {
        RunwayInfo runway = Runway();
        AircraftState ac = Arrival(runway, InsideTheGateNm, ias: 200);
        LatLon position = FixPosition(ApproachFix);
        var cmd = new CrossFixCommand(ApproachFix, position.Lat, position.Lon, 3_000, CrossFixAltitudeType.At, null);
        CommandResult result = CommandDispatcher.Dispatch(cmd, ac, TestDispatch.Context(new SerializableRandom(1)));

        Assert.True(result.Success, result.Message);
        Assert.Single(ac.Targets.NavigationRoute);
    }

    private static RunwayInfo Runway()
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("KSJC", "30L");
        Assert.NotNull(runway);
        return runway;
    }

    private static LatLon Threshold(RunwayInfo runway) => new(runway.ThresholdLatitude, runway.ThresholdLongitude);

    private static LatLon FixPosition(string fixName)
    {
        (double lat, double lon) = Assert.IsType<(double Lat, double Lon)>(NavigationDatabase.Instance.GetFixPosition(fixName));
        return new LatLon(lat, lon);
    }

    /// <summary>
    /// A synthetic runway whose landing threshold <paramref name="fixFromThresholdNm"/> beyond a real fix on the approach
    /// course, and an arrival cleared to land on it <paramref name="aircraftFromThresholdNm"/> back from that threshold,
    /// so a parsed CFIX on the fix lands inside the gate.
    /// </summary>
    private static (AircraftState Aircraft, RunwayInfo Runway) FixApproach(
        string fixName,
        double fixFromThresholdNm,
        double aircraftFromThresholdNm,
        double altitude,
        double ias
    )
    {
        var heading = new TrueHeading(280);
        LatLon threshold = GeoMath.ProjectPoint(FixPosition(fixName), heading, fixFromThresholdNm);
        RunwayInfo runway = TestRunwayFactory.Make(thresholdLat: threshold.Lat, thresholdLon: threshold.Lon, heading: heading.Degrees);
        AircraftState ac = ArrivalOnCourse(runway, aircraftFromThresholdNm, altitude, ias);
        return (ac, runway);
    }

    /// <summary>
    /// An arrival cleared to land on <paramref name="runway"/>, tracking its course on the extended centreline
    /// <paramref name="fromThresholdNm"/> back from the pavement threshold.
    /// </summary>
    private static AircraftState Arrival(RunwayInfo runway, double fromThresholdNm, double ias) =>
        ArrivalOnCourse(runway, fromThresholdNm, altitude: 3_000, ias);

    private static AircraftState ArrivalOnCourse(RunwayInfo runway, double fromThresholdNm, double altitude, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "SJC1",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(Threshold(runway), runway.TrueHeading.ToReciprocal(), fromThresholdNm),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = altitude,
            IndicatedAirspeed = ias,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                HasFlightPlan = true,
                Departure = "KPHX",
                Destination = "KSJC",
            },
            Phases = new PhaseList { AssignedRunway = runway, LandingClearance = ClearanceType.ClearedToLand },
        };
        ac.Targets.TargetTrueHeading = runway.TrueHeading;
        ac.Targets.TargetAltitude = altitude;
        return ac;
    }

    private static AircraftState Airborne(string type, double altitude, double ias, LatLon position)
    {
        var ac = new AircraftState
        {
            Callsign = "SWA123",
            AircraftType = type,
            Position = position,
            TrueHeading = new TrueHeading(0),
            TrueTrack = new TrueHeading(0),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                HasFlightPlan = true,
                Departure = "KOAK",
                Destination = "KLAX",
            },
            Phases = new PhaseList(),
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(0);
        ac.Targets.TargetAltitude = altitude;
        return ac;
    }

    private static CommandResult Send(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new SerializableRandom(1)));
    }

    /// <summary>A CFIX on a fix inside the gate, <see cref="CrossingFixFromThresholdNm"/> from the landing threshold.</summary>
    private static CommandResult CrossFix(AircraftState aircraft, RunwayInfo runway, int speed)
    {
        LatLon position = GeoMath.ProjectPoint(Threshold(runway), runway.TrueHeading.ToReciprocal(), CrossingFixFromThresholdNm);
        var cmd = new CrossFixCommand(GateFixName, position.Lat, position.Lon, (int)aircraft.Altitude, CrossFixAltitudeType.At, speed);
        return CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(new SerializableRandom(1)));
    }

    private static void FlyOneSecond(AircraftState aircraft)
    {
        for (int sub = 0; sub < 4; sub++)
        {
            FlightPhysics.Update(aircraft, 0.25);
        }
    }

    private static void FlySeconds(AircraftState aircraft, int seconds)
    {
        for (int second = 0; second < seconds; second++)
        {
            FlyOneSecond(aircraft);
        }
    }

    /// <summary>Flies <paramref name="seconds"/> and returns the highest IAS seen, started at <paramref name="startIas"/>.</summary>
    private static double FlyAndTrackHighestIas(AircraftState aircraft, int seconds, double startIas)
    {
        double highest = startIas;
        for (int second = 0; second < seconds; second++)
        {
            FlyOneSecond(aircraft);
            highest = Math.Max(highest, aircraft.IndicatedAirspeed);
        }

        return highest;
    }

    /// <summary>Flies until the aircraft is within the 5 nm gate, or 600 seconds have passed.</summary>
    private static void FlyUntilInsideTheGate(AircraftState aircraft, LatLon threshold)
    {
        for (int second = 0; (second < 600) && (GeoMath.DistanceNm(aircraft.Position, threshold) > 5.0); second++)
        {
            FlyOneSecond(aircraft);
        }
    }

    /// <summary>Flies until <paramref name="fixName"/> leaves the route, returning the IAS then, or null if it never does.</summary>
    private static double? FlyUntilSequenced(AircraftState aircraft, string fixName)
    {
        for (int second = 0; second < 3_600; second++)
        {
            FlyOneSecond(aircraft);
            if (!aircraft.Targets.NavigationRoute.Exists(fix => fix.Name == fixName))
            {
                return aircraft.IndicatedAirspeed;
            }
        }

        return null;
    }
}
