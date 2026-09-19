using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// <c>RNS</c> to an aircraft that was slowed on a long final. AIM 4-4-12.f.1 — "resume normal speed" terminates the
/// ATC speed adjustment and the pilot returns to the normal profile — but clearing the speed fields does not achieve
/// that on final: <see cref="FinalApproachPhase.ManagesSpeed"/> suppresses the physics auto-schedule and the phase
/// writes no speed target before its own deceleration stages, which only ever bleed downward, so the aircraft used to
/// hold whatever speed it had been reduced to for the rest of the approach.
///
/// <para>The aircraft is a scenario-scripted arrival (not a generator one) on the OAK runway 30 final, so neither the
/// generator in-trail spacing manager nor the same-runway protection pass — which is switched off by default — has any
/// hand in the speeds here: the only two writers are the instructor's commands and the phase.</para>
///
/// <para><b>Two types, because 91.117 binds one of them.</b> Part of that final passes under SFO's Class B shelf,
/// where 14 CFR 91.117(c) caps the aircraft at 200 kt — under the ~222 kt clean profile speed a B739's schedule asks
/// for, and over the ~172 kt a DH8D's does. The command writes the profile speed either way (the regulatory cap is
/// physics' to apply, not the command's), so the arms assert the aircraft comes back up to the lower of its profile
/// speed and what 91.117 allows it, which is the whole schedule for the turboprop and the 200 kt cap for the jet.</para>
/// </summary>
public class ResumeNormalSpeedOnFinalTests(ITestOutputHelper output)
{
    private const string ScenarioPath = "TestData/issue153-s2-oak-5-2-scenario.json";

    /// <summary>A turboprop, whose clean profile speed is under the Class B shelf cap and so is holdable in full.</summary>
    private const string TurbopropCallsign = "QXE4";

    private const string TurbopropType = "DH8D";

    /// <summary>A jet, whose clean profile speed is above that cap on this final.</summary>
    private const string JetCallsign = "SWA2";

    private const string JetType = "B739";

    /// <summary>
    /// How far (nm) outside <see cref="ArrivalSpacingManager.SpeedRestoreGateNm"/> the arrival is placed. The gate is
    /// per-callsign (the approach-flap reach gate carries a deterministic jitter), so the arms derive the distance from
    /// it rather than naming one.
    /// </summary>
    private const double OutsideGateMarginNm = 2.5;

    /// <summary>How far (kt) below its flyable profile speed the instructor's explicit <c>SPD</c> slows the arrival.</summary>
    private const double ReductionKts = 40.0;

    /// <summary>Seconds allowed for the aircraft to settle on the commanded speed, and for it to come back up afterwards.</summary>
    private const int SettleSeconds = 90;

    /// <summary>Seconds allowed for the approach from the reduction point to the touchdown.</summary>
    private const int ApproachSeconds = 900;

    /// <summary>How close (kt) to the speed it should be flying the arrival has to get back.</summary>
    private const double ProfileToleranceKts = 5.0;

    /// <summary>14 CFR 91.117(c) — under a Class B shelf.</summary>
    private const double ClassBShelfSpeedLimitKts = 200.0;

    /// <summary>14 CFR 91.117(a) — below 10,000 ft MSL.</summary>
    private const double Below10kSpeedLimitKts = 250.0;

    /// <summary>
    /// The instructor slows an arrival 40 kt on a long final, then resumes normal speed: the aircraft flies its profile
    /// speed again (within the ±10 kt a complying pilot holds, AIM 4-4-12.c, and within whatever 91.117 allows it
    /// there), and still makes its own approach — the restored target is not an explicit ATC speed, so the phase's
    /// deceleration stages take it over at their gates and deliver it to the threshold at final approach speed rather
    /// than fast.
    /// </summary>
    [Fact]
    public void ResumeNormalSpeed_OnALongFinal_GivesTheProfileSpeedBack()
    {
        (SimulationEngine Engine, RunwayInfo Runway)? setup = ArrivalOnLongFinal(
            TurbopropCallsign,
            TurbopropType,
            RestoreGateNm(TurbopropCallsign, TurbopropType) + OutsideGateMarginNm
        );
        if (setup is null)
        {
            output.WriteLine("SKIP: scenario, navdata or the OAK layout is unavailable");
            return;
        }
        (SimulationEngine engine, RunwayInfo runway) = setup.Value;

        double scheduled = SlowTheArrival(engine, TurbopropCallsign, TurbopropType, runway, ReductionKts);
        AircraftState aircraft = engine.FindAircraft(TurbopropCallsign)!;

        CommandResult resume = engine.SendCommand(TurbopropCallsign, "RNS");
        Assert.True(resume.Success, resume.Message);

        Assert.NotNull(aircraft.Targets.TargetSpeed);
        Assert.Equal(ScheduledKts(aircraft, runway), aircraft.Targets.TargetSpeed!.Value, 0.5);
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand);
        Assert.False(aircraft.Targets.SpeedCommandIsControllerIssued);

        (double maxIas, double minCapKts) = TickForSeconds(engine, TurbopropCallsign, SettleSeconds, runway);
        double flyable = Math.Min(scheduled, minCapKts);
        output.WriteLine($"{SettleSeconds}s after RNS: reached {maxIas:F0} kt against {flyable:F0} kt flyable ({scheduled:F0} kt scheduled)");
        Assert.Equal(scheduled, flyable);
        Assert.True(
            maxIas >= flyable - ProfileToleranceKts,
            $"the arrival did not come back up to its profile speed: {maxIas:F0} kt against {flyable:F0} kt"
        );

        double vapp = FinalApproachSpeedKts(engine, TurbopropCallsign, runway);
        double touchdownIas = FlyToLanding(engine, TurbopropCallsign, runway);
        output.WriteLine($"reached the landing phase at {touchdownIas:F0} kt (final approach speed {vapp:F0} kt)");
        Assert.True(touchdownIas <= vapp + 5.0, $"the arrival arrived fast: {touchdownIas:F0} kt against {vapp:F0} kt final approach speed");
    }

    /// <summary>
    /// The command hands back the profile speed; what the aircraft may actually fly is still 14 CFR 91.117's to say. A
    /// B739's ~222 kt clean schedule is above the 200 kt this final's Class B shelf allows, so the target written is
    /// the schedule and the speed flown is the cap — the command does not quietly assign an illegal speed, and it does
    /// not refuse to resume either.
    /// </summary>
    [Fact]
    public void ResumeNormalSpeed_UnderTheClassBShelf_GivesBackOnlyWhatIsLegal()
    {
        (SimulationEngine Engine, RunwayInfo Runway)? setup = ArrivalOnLongFinal(
            JetCallsign,
            JetType,
            RestoreGateNm(JetCallsign, JetType) + OutsideGateMarginNm
        );
        if (setup is null)
        {
            output.WriteLine("SKIP: scenario, navdata or the OAK layout is unavailable");
            return;
        }
        (SimulationEngine engine, RunwayInfo runway) = setup.Value;

        double scheduled = SlowTheArrival(engine, JetCallsign, JetType, runway, ReductionKts);
        AircraftState aircraft = engine.FindAircraft(JetCallsign)!;

        CommandResult resume = engine.SendCommand(JetCallsign, "RNS");
        Assert.True(resume.Success, resume.Message);
        Assert.Equal(ScheduledKts(aircraft, runway), Assert.IsType<double>(aircraft.Targets.TargetSpeed), 0.5);

        (double maxIas, double minCapKts) = TickForSeconds(engine, JetCallsign, SettleSeconds, runway);
        output.WriteLine($"{SettleSeconds}s after RNS: reached {maxIas:F0} kt, tightest 91.117 cap in the window {minCapKts:F0} kt");
        Assert.Equal(ClassBShelfSpeedLimitKts, minCapKts);
        Assert.True(
            maxIas >= minCapKts - ProfileToleranceKts,
            $"the arrival did not come back up to the {minCapKts:F0} kt it may fly: {maxIas:F0} kt"
        );
        Assert.True(maxIas <= minCapKts + 1.0, $"the arrival was flown past the {minCapKts:F0} kt 91.117 allows it: {maxIas:F0} kt");
        Assert.True(scheduled > minCapKts, $"premise: the jet's {scheduled:F0} kt schedule must be above the cap");
    }

    /// <summary>
    /// Slows the arrival <paramref name="reductionKts"/> below the fastest speed it may fly where it is, with an
    /// explicit <c>SPD</c>, and waits for it to settle there. Returns the scheduled profile speed, and pins the two
    /// premises the restore turns on: outside the restore gate and more than the deadband slow.
    /// </summary>
    private double SlowTheArrival(SimulationEngine engine, string callsign, string aircraftType, RunwayInfo runway, double reductionKts)
    {
        AircraftState aircraft = engine.FindAircraft(callsign)!;
        double scheduled = ScheduledKts(aircraft, runway);
        double commanded = Math.Round(Math.Min(scheduled, RegulatoryCapKts(aircraft)) - reductionKts);
        CommandResult reduction = engine.SendCommand(callsign, $"SPD {commanded:F0}");
        Assert.True(reduction.Success, reduction.Message);
        SettleAt(engine, callsign, commanded);

        aircraft = engine.FindAircraft(callsign)!;
        double distanceNm = AlongFinalNm(aircraft, runway);
        double gate = ArrivalSpacingManager.SpeedRestoreGateNm(AircraftCategorization.Categorize(aircraftType), callsign);
        output.WriteLine(
            $"before RNS: {callsign} {aircraft.IndicatedAirspeed:F0} kt at {distanceNm:F1} nm "
                + $"(scheduled {scheduled:F0} kt, restore gate {gate:F1} nm)"
        );
        Assert.True(distanceNm >= gate, $"premise: the arrival must still be outside the restore gate ({distanceNm:F1} nm vs {gate:F1} nm)");
        Assert.True(
            aircraft.IndicatedAirspeed < scheduled - ArrivalSpacingManager.SpeedRestoreDeadbandKts,
            $"premise: the arrival must be more than the deadband slow ({aircraft.IndicatedAirspeed:F0} kt vs {scheduled:F0} kt scheduled)"
        );
        return scheduled;
    }

    /// <summary>
    /// Inside the ±10 kt band a complying pilot holds (AIM 4-4-12.c) there is nothing to give back, so the command
    /// writes no target at all and leaves the aircraft to the phase — the same judgement the generator stream's restore
    /// makes with the same deadband.
    /// </summary>
    [Fact]
    public void ResumeNormalSpeed_InsideTheDeadband_WritesNoTarget()
    {
        (SimulationEngine Engine, RunwayInfo Runway)? setup = ArrivalOnLongFinal(
            TurbopropCallsign,
            TurbopropType,
            RestoreGateNm(TurbopropCallsign, TurbopropType) + OutsideGateMarginNm
        );
        if (setup is null)
        {
            output.WriteLine("SKIP: scenario, navdata or the OAK layout is unavailable");
            return;
        }
        (SimulationEngine engine, RunwayInfo runway) = setup.Value;

        AircraftState aircraft = engine.FindAircraft(TurbopropCallsign)!;
        double scheduled = ScheduledKts(aircraft, runway);
        Assert.True(
            scheduled < RegulatoryCapKts(aircraft),
            $"premise: the turboprop's {scheduled:F0} kt profile speed must be one it may legally hold here"
        );

        double commanded = Math.Round(scheduled - (ArrivalSpacingManager.SpeedRestoreDeadbandKts / 2.0));
        CommandResult reduction = engine.SendCommand(TurbopropCallsign, $"SPD {commanded:F0}");
        Assert.True(reduction.Success, reduction.Message);
        SettleAt(engine, TurbopropCallsign, commanded);

        aircraft = engine.FindAircraft(TurbopropCallsign)!;
        double shortfall = ScheduledKts(aircraft, runway) - aircraft.IndicatedAirspeed;
        output.WriteLine($"before RNS: {aircraft.IndicatedAirspeed:F0} kt, {shortfall:F0} kt below the scheduled {scheduled:F0} kt");
        Assert.True(shortfall <= ArrivalSpacingManager.SpeedRestoreDeadbandKts, "premise: the arrival must be inside the deadband");

        CommandResult resume = engine.SendCommand(TurbopropCallsign, "RNS");
        Assert.True(resume.Success, resume.Message);

        Assert.Null(aircraft.Targets.TargetSpeed);
        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.Null(aircraft.Targets.SpeedCeiling);
    }

    /// <summary>
    /// Inside the restore gate the phase is about to start its own deceleration stages, so the command hands nothing
    /// back however slow the arrival is: speeding it up there only to slow it again moments later is the alternating
    /// decrease-and-increase §5-7-1 warns against. The arrival is the same 40 kt below its schedule that gets the
    /// speed back outside the gate, so the only difference between this arm and the first one is where it is.
    /// </summary>
    [Fact]
    public void Rns_InsideTheRestoreGate_WritesNoTarget()
    {
        double gate = RestoreGateNm(TurbopropCallsign, TurbopropType);
        (SimulationEngine Engine, RunwayInfo Runway)? setup = ArrivalOnLongFinal(TurbopropCallsign, TurbopropType, gate - InsideGateMarginNm);
        if (setup is null)
        {
            output.WriteLine("SKIP: scenario, navdata or the OAK layout is unavailable");
            return;
        }
        (SimulationEngine engine, RunwayInfo runway) = setup.Value;

        AircraftState aircraft = engine.FindAircraft(TurbopropCallsign)!;
        double commanded = Math.Round(Math.Min(ScheduledKts(aircraft, runway), RegulatoryCapKts(aircraft)) - ReductionKts);
        CommandResult reduction = engine.SendCommand(TurbopropCallsign, $"SPD {commanded:F0}");
        Assert.True(reduction.Success, reduction.Message);
        SettleAt(engine, TurbopropCallsign, commanded);

        aircraft = engine.FindAircraft(TurbopropCallsign)!;
        double distanceNm = AlongFinalNm(aircraft, runway);
        double shortfall = ScheduledKts(aircraft, runway) - aircraft.IndicatedAirspeed;
        output.WriteLine($"before RNS: {aircraft.IndicatedAirspeed:F0} kt at {distanceNm:F1} nm (restore gate {gate:F1} nm), {shortfall:F0} kt slow");
        Assert.True(distanceNm < gate, $"premise: the arrival must be inside the restore gate ({distanceNm:F1} nm vs {gate:F1} nm)");
        Assert.True(shortfall > ArrivalSpacingManager.SpeedRestoreDeadbandKts, "premise: outside the gate this shortfall would be restored");

        CommandResult resume = engine.SendCommand(TurbopropCallsign, "RNS");
        Assert.True(resume.Success, resume.Message);

        Assert.Null(aircraft.Targets.TargetSpeed);
        Assert.Null(aircraft.Targets.AssignedSpeed);
    }

    /// <summary>
    /// A <see cref="FinalApproachPhase"/> that has not ticked yet still carries its <c>double.MaxValue</c> distance
    /// sentinel, and a speed scheduled at "MaxValue nm" is the clean-profile speed for an aircraft whose position the
    /// command cannot yet read. Nothing is written until the phase has measured where it is.
    /// </summary>
    [Fact]
    public void Rns_BeforeTheFinalApproachPhaseHasTicked_WritesNoTarget()
    {
        (SimulationEngine Engine, RunwayInfo Runway)? setup = PlaceArrivalOnFinal(
            TurbopropCallsign,
            TurbopropType,
            RestoreGateNm(TurbopropCallsign, TurbopropType) + OutsideGateMarginNm
        );
        if (setup is null)
        {
            output.WriteLine("SKIP: scenario, navdata or the OAK layout is unavailable");
            return;
        }
        (SimulationEngine engine, RunwayInfo _) = setup.Value;

        AircraftState aircraft = engine.FindAircraft(TurbopropCallsign)!;
        FinalApproachPhase phase = Assert.IsType<FinalApproachPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(double.MaxValue, phase.DistanceToThresholdNm); // premise: the sentinel, never a measured distance

        // Slow enough that the sentinel is the only thing standing between the command and a target: at "MaxValue nm"
        // the schedule hands back the clean-profile speed, which this aircraft is now well below.
        aircraft.IndicatedAirspeed -= ReductionKts;
        AircraftCategory category = AircraftCategorization.Categorize(TurbopropType);
        double sentinelScheduled = ArrivalSpacingManager.ScheduledFinalSpeedKts(
            TurbopropType,
            category,
            AircraftPerformance.ApproachSpeed(TurbopropType, category),
            TurbopropCallsign,
            double.MaxValue
        );
        Assert.True(
            aircraft.IndicatedAirspeed < sentinelScheduled - ArrivalSpacingManager.SpeedRestoreDeadbandKts,
            $"premise: {aircraft.IndicatedAirspeed:F0} kt must be a deadband below the sentinel's {sentinelScheduled:F0} kt"
        );

        CommandResult resume = engine.SendCommand(TurbopropCallsign, "RNS");
        Assert.True(resume.Success, resume.Message);

        output.WriteLine($"target after RNS on an unticked phase: {aircraft.Targets.TargetSpeed?.ToString("F0") ?? "(none)"}");
        Assert.Null(aircraft.Targets.TargetSpeed);
    }

    /// <summary>How far (nm) inside its own restore gate the no-restore arm places the arrival.</summary>
    private const double InsideGateMarginNm = 2.0;

    /// <summary>
    /// The same arrival, ticked once so the phase has cached its distance to the threshold — which is the distance the
    /// restore reads. Null only when the scenario, navdata or the OAK layout is unavailable (silent skip): an aircraft
    /// that is placed and then turns out not to be flying the final approach is a broken fixture, not missing data, and
    /// fails here rather than skipping every arm that depends on it.
    /// </summary>
    private static (SimulationEngine Engine, RunwayInfo Runway)? ArrivalOnLongFinal(string callsign, string aircraftType, double distanceNm)
    {
        if (PlaceArrivalOnFinal(callsign, aircraftType, distanceNm) is not { } placed)
        {
            return null;
        }

        placed.Engine.TickOneSecond();
        AircraftState aircraft = placed.Engine.FindAircraft(callsign)!;
        Assert.IsType<FinalApproachPhase>(aircraft.Phases?.CurrentPhase);
        return (placed.Engine, placed.Runway);
    }

    /// <summary>
    /// An arrival established in <see cref="FinalApproachPhase"/> on the OAK runway 30 final, cleared to land,
    /// <paramref name="distanceNm"/> from the threshold, in a world that has not ticked yet. Null when the scenario,
    /// navdata or the OAK layout is unavailable (silent skip).
    /// </summary>
    private static (SimulationEngine Engine, RunwayInfo Runway)? PlaceArrivalOnFinal(string callsign, string aircraftType, double distanceNm)
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

        // The generators would drop their own arrivals onto this final; the arms are about one aircraft's speed.
        engine.Scenario.SoloArrivalGeneratorRatePercent = 0;
        engine.Scenario.FinalApproachSpeedVarietyEnabled = true;

        RunwayInfo runway = engine.Scenario.Generators.Single(g => g.Config.Runway == "30").Runway;
        AircraftCategory category = AircraftCategorization.Categorize(aircraftType);
        PhaseInitResult init = AircraftInitializer.InitializeOnFinal(
            runway,
            category,
            callsign,
            requestedDistanceNm: distanceNm,
            aircraftType: aircraftType
        );

        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = aircraftType,
            Position = init.Position,
            TrueHeading = init.TrueHeading,
            TrueTrack = init.TrueHeading,
            Altitude = init.Altitude,
            IndicatedAirspeed = init.Speed,
            IsOnGround = false,
            IsGeneratorArrival = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
            Phases = init.Phases,
        };
        aircraft.Phases!.LandingClearance = ClearanceType.ClearedToLand;
        engine.World.AddAircraft(aircraft);
        return (engine, runway);
    }

    /// <summary>The distance (nm) from the threshold inside which this aircraft's speed is no longer given back.</summary>
    private static double RestoreGateNm(string callsign, string aircraftType) =>
        ArrivalSpacingManager.SpeedRestoreGateNm(AircraftCategorization.Categorize(aircraftType), callsign);

    /// <summary>Ticks until the aircraft is flying <paramref name="commandedKts"/>, so the reduction is a settled fact and not a transient.</summary>
    private void SettleAt(SimulationEngine engine, string callsign, double commandedKts)
    {
        for (int t = 1; t <= SettleSeconds; t++)
        {
            engine.TickOneSecond();
            if (Math.Abs(engine.FindAircraft(callsign)!.IndicatedAirspeed - commandedKts) <= 2.0)
            {
                output.WriteLine($"settled on {commandedKts:F0} kt after {t}s");
                return;
            }
        }

        Assert.Fail($"premise: the arrival never settled on the commanded {commandedKts:F0} kt");
    }

    /// <summary>
    /// Ticks <paramref name="seconds"/> seconds, tracing the speed every ten, and returns the highest indicated
    /// airspeed reached with the tightest 14 CFR 91.117 cap that applied anywhere in the window — the aircraft flies
    /// down the final and under a Class B shelf, so the cap is not one number.
    /// </summary>
    private (double MaxIasKts, double MinCapKts) TickForSeconds(SimulationEngine engine, string callsign, int seconds, RunwayInfo runway)
    {
        AircraftState aircraft = engine.FindAircraft(callsign)!;
        double maxIas = aircraft.IndicatedAirspeed;
        double minCap = RegulatoryCapKts(aircraft);
        for (int t = 1; t <= seconds; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(callsign)!;
            maxIas = Math.Max(maxIas, aircraft.IndicatedAirspeed);
            minCap = Math.Min(minCap, RegulatoryCapKts(aircraft));
            if (t % 10 == 0)
            {
                output.WriteLine(
                    $"  t+{t, 3}s: {aircraft.IndicatedAirspeed:F0} kt at {AlongFinalNm(aircraft, runway):F1} nm, "
                        + $"target {aircraft.Targets.TargetSpeed?.ToString("F0") ?? "(none)"}, "
                        + $"cap {RegulatoryCapKts(aircraft):F0}, {aircraft.Altitude:F0} ft"
                );
            }
        }
        return (maxIas, minCap);
    }

    /// <summary>
    /// Flies the approach out and returns the indicated airspeed on reaching <see cref="LandingPhase"/>. Fails on a
    /// go-around: the restored speed must not leave the aircraft unable to make its own approach.
    /// </summary>
    private double FlyToLanding(SimulationEngine engine, string callsign, RunwayInfo runway)
    {
        for (int t = 1; t <= ApproachSeconds; t++)
        {
            engine.TickOneSecond();
            AircraftState? aircraft = engine.FindAircraft(callsign);
            Assert.NotNull(aircraft);
            Assert.False(
                aircraft.Phases?.CurrentPhase is GoAroundPhase,
                $"the arrival went around at t={t}s, {AlongFinalNm(aircraft, runway):F1} nm, {aircraft.IndicatedAirspeed:F0} kt"
            );
            if (aircraft.Phases?.CurrentPhase is LandingPhase)
            {
                output.WriteLine($"landing phase at t={t}s, {AlongFinalNm(aircraft, runway):F2} nm");
                return aircraft.IndicatedAirspeed;
            }
        }

        Assert.Fail($"the arrival never reached the landing phase within {ApproachSeconds}s");
        return double.NaN;
    }

    /// <summary>
    /// The fastest speed 14 CFR 91.117 lets this aircraft fly where it is — 200 kt under a Class B shelf, 250 kt
    /// otherwise below 10,000 ft. The same two figures <see cref="FlightPhysics"/> clamps the speed goal with, which is
    /// why an aircraft whose profile speed is above them never reaches it.
    /// </summary>
    private static double RegulatoryCapKts(AircraftState aircraft)
    {
        if (aircraft.Altitude >= 10000.0)
        {
            return double.MaxValue;
        }

        return AirspaceDatabase.Default.IsUnderClassBShelf(aircraft.Position, aircraft.Altitude) ? ClassBShelfSpeedLimitKts : Below10kSpeedLimitKts;
    }

    private static double ScheduledKts(AircraftState aircraft, RunwayInfo runway)
    {
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        double vref = AircraftPerformance.ApproachSpeed(aircraft.AircraftType, category);
        return ArrivalSpacingManager.ScheduledFinalSpeedKts(aircraft.AircraftType, category, vref, aircraft.Callsign, AlongFinalNm(aircraft, runway));
    }

    private static double FinalApproachSpeedKts(SimulationEngine engine, string callsign, RunwayInfo runway)
    {
        AircraftState aircraft = engine.FindAircraft(callsign)!;
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        return SameRunwayArrivalProtection.FinalApproachSpeedKts(
            AircraftPerformance.ApproachSpeed(aircraft.AircraftType, category),
            AircraftPerformance.WindApproachAdditive(engine.World.Weather, runway.TrueHeading.Degrees)
        );
    }

    private static double AlongFinalNm(AircraftState aircraft, RunwayInfo runway)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var outbound = new TrueHeading((runway.TrueHeading.Degrees + 180.0) % 360.0);
        return GeoMath.AlongTrackDistanceNm(aircraft.Position, threshold, outbound);
    }
}
