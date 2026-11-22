using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-2727 (GitHub #1395 follow-up): an aircraft on a STAR sped up or slowed down to its first published speed from far
/// out. A published speed is met at its fix: the speed change toward it starts only when the time to the fix's sequencing
/// point at the present ground speed is within 1.1 times the change's own time at the type's acceleration or deceleration
/// rate (AIM 5-4-1.a.2(a) NOTE 2; 7110.65 5-7-1.g NOTE 3), and until then the present speed holds. An "at or above" minimum
/// the aircraft is below is met the same way. Once at a published speed the aircraft holds it until the next restriction
/// needs a change. Real navdata: a B738 on KOAK OAKES3 (OAKES at 280, between FL320 and FL240) and on KIAH DOOBI3 from
/// AEX (HHART at 230 or above).
/// </summary>
public class StarSpeedTimingTests
{
    private const string Oakes = "OAKES";
    private const double OakesKts = 280;
    private const string Hhart = "HHART";
    private const string Doobi = "DOOBI";
    private const double HhartFloorKts = 230;
    private const string Aryel = "ARYEL";
    private const double AryelMaxKts = 280;
    private const string Cokt = "COKTL";
    private const double CoktlKts = 250;
    private const string Sulrr = "SULRR";
    private const double SulrrKts = 250;
    private const string Lohgn = "LOHGN";
    private const double LohgnKts = 200;

    /// <summary>How far the IAS may drift from the speed it holds.</summary>
    private const double HoldToleranceKts = 1.0;

    /// <summary>How far under a published speed the aircraft may cross it (the type's Mmo at FL350 is 279.5 KIAS).</summary>
    private const double ReachToleranceKts = 2.0;

    /// <summary>The window margin over the change's own time.</summary>
    private const double WindowMargin = 1.1;

    private readonly ITestOutputHelper _output;

    public StarSpeedTimingTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>250 KIAS level at FL350, 150 NM before OAKES (280): the speed holds until the acceleration window.</summary>
    [Fact]
    public void FasterPublishedSpeed_FarOut_AcceleratesOnlyInItsWindow()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150, 35000);
        SetSpeed(aircraft, 250);
        AssertChangeInWindow(engine, aircraft, Oakes, OakesKts);
    }

    /// <summary>
    /// The same with a descend via issued first: level before the top of descent the speed holds; from the top of descent the
    /// descent schedule flies, and OAKES is crossed at its 280.
    /// </summary>
    [Fact]
    public void FasterPublishedSpeed_UnderDescendVia_HoldsWhileLevelAndCrossesAtIt()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150, 35000);
        SetSpeed(aircraft, 250);
        SendOk(engine, aircraft, "DVIA");
        int level = 0;
        for (int t = 1; (t <= 3600) && aircraft.Targets.NavigationRoute.Exists(f => f.Name == Oakes); t++)
        {
            engine.TickOneSecond();
            if ((level == t - 1) && (aircraft.VerticalSpeed == 0))
            {
                level = t;
                Assert.InRange(aircraft.IndicatedAirspeed, 250 - HoldToleranceKts, 250 + HoldToleranceKts);
            }
        }

        Assert.True(level > 120, $"setup: level for {level} s");
        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, f => f.Name == Oakes);
        Assert.InRange(aircraft.IndicatedAirspeed, OakesKts - ReachToleranceKts, OakesKts + ReachToleranceKts);
    }

    /// <summary>
    /// 20 kt over the type's Mach .82 at FL300, where Mmo's IAS is well above OAKES's 280: the cap slows it to Mmo at once,
    /// and no slowdown toward 280 starts early.
    /// </summary>
    [Fact]
    public void PastMmo_TheMachCapStillAppliesAtOnce()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150, 30000);
        double capKts = WindInterpolator.MachToIas(0.82, aircraft.Altitude);
        Assert.True(capKts > OakesKts + 20, $"setup: Mmo's IAS {capKts:F1}");
        SetSpeed(aircraft, capKts + 20);
        engine.TickOneSecond();
        Assert.True(aircraft.IndicatedAirspeed < capKts + 19, $"IAS {aircraft.IndicatedAirspeed:F1} after one second");
        for (int t = 2; t <= 90; t++)
        {
            engine.TickOneSecond();
            Assert.True(
                aircraft.IndicatedAirspeed >= capKts - ReachToleranceKts,
                $"+{t} s: IAS {aircraft.IndicatedAirspeed:F1} below Mmo's {capKts:F1}"
            );
        }

        Assert.InRange(aircraft.IndicatedAirspeed, capKts - ReachToleranceKts, capKts + ReachToleranceKts);
    }

    /// <summary>
    /// 205 KIAS on KIAH DOOBI3 past DOOBI, before HHART's 230-or-above minimum: the minimum is met at its acceleration point,
    /// not at once when "resume published speeds" makes it the next published speed.
    /// </summary>
    [Fact]
    public void AtOrAboveMinimum_AboveTheSpeed_AcceleratesOnlyInItsWindow()
    {
        (SimulationEngine engine, AircraftState aircraft) = PastDoobi("B738", EngineKind.Jet, 24000, 205);
        SendOk(engine, aircraft, "RPS");
        AssertChangeInWindow(engine, aircraft, Hhart, HhartFloorKts);
    }

    /// <summary>A turboprop at 10,000 ft, 170 KIAS, resumed toward HHART's 230-or-above: the large speed-up still crosses at it.</summary>
    [Fact]
    public void LargeSpeedUp_Turboprop_CrossesAtTheSpeed()
    {
        (SimulationEngine engine, AircraftState aircraft) = PastDoobi("DH8D", EngineKind.Turboprop, 10000, 170);
        SendOk(engine, aircraft, "RPS");
        AssertChangeInWindow(engine, aircraft, Hhart, HhartFloorKts);
        Assert.True(aircraft.IndicatedAirspeed >= HhartFloorKts - 1, $"Crossed {Hhart} at {aircraft.IndicatedAirspeed:F1}");
    }

    /// <summary>
    /// An "at or above" minimum the aircraft is below when the fix before it sequences is held back there: no floor until its
    /// acceleration window, and the floor is set when HHART is crossed.
    /// </summary>
    [Fact]
    public void AtOrAboveMinimum_HeldBackAtSequencing()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnStar("KIAH", "AEX", "DOOBI3", null, 24000, "B738", EngineKind.Jet);
        for (int t = 0; (t < 3600) && aircraft.Targets.NavigationRoute.Exists(f => f.Name == Doobi); t++)
        {
            if ((SequencingDistanceTo(aircraft, Doobi) ?? 0) < (1.5 * aircraft.GroundSpeed / 3600.0))
            {
                SetSpeed(aircraft, 205);
            }

            engine.TickOneSecond();
        }

        Assert.Equal(Hhart, aircraft.Targets.NavigationRoute[0].Name);
        Assert.True((aircraft.Targets.SpeedFloor ?? 0) < HhartFloorKts, $"floor {aircraft.Targets.SpeedFloor} set at once");
        SetSpeed(aircraft, 205);
        AssertChangeInWindow(engine, aircraft, Hhart, HhartFloorKts);
        Assert.Equal(HhartFloorKts, aircraft.Targets.SpeedFloor);
    }

    /// <summary>
    /// A climb via off KSFO toward WESLA's published "at or below 230" above the present 200 KIAS: the climb flies its
    /// schedule from the start (the present speed is not held in a climb), never past the 230 maximum, which is not flown
    /// toward.
    /// </summary>
    [Fact]
    public void ClimbTowardAHigherSidSpeed_FliesItsSchedule()
    {
        (SimulationEngine engine, AircraftState aircraft) = WeslaClimbVia(["WESLA"]);
        NavigationTarget wesla = aircraft.Targets.NavigationRoute.First(f => f.SpeedRestriction is not null);
        double maxKts = wesla.SpeedRestriction!.SpeedKts;
        _output.WriteLine($"{wesla.Name}: {wesla.SpeedRestriction.Type} {maxKts}");
        Assert.True(maxKts > 200, $"setup: {wesla.Name} publishes {maxKts}");
        for (int t = 1; (t <= 600) && aircraft.Targets.NavigationRoute.Contains(wesla); t++)
        {
            engine.TickOneSecond();
            Assert.True(aircraft.IndicatedAirspeed <= maxKts + ReachToleranceKts, $"+{t} s: IAS {aircraft.IndicatedAirspeed:F1} past {maxKts}");
            bool moved = Math.Abs(aircraft.IndicatedAirspeed - 200) > HoldToleranceKts;
            Assert.True((t != 10) || moved, $"IAS {aircraft.IndicatedAirspeed:F1} after 10 s: the present speed was held, not the climb schedule");
        }
    }

    /// <summary>A B738 off KSFO runway 28R at 1,500 ft, 200 KIAS, on WESLA5 with <paramref name="fixes"/> as its route, cleared CVIA.</summary>
    private (SimulationEngine Engine, AircraftState Aircraft) WeslaClimbVia(string[] fixes)
    {
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        var aircraft = new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Position = new LatLon(37.625, -122.40),
            TrueHeading = new TrueHeading(284),
            TrueTrack = new TrueHeading(284),
            Altitude = 1500,
            IndicatedAirspeed = 200,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSFO",
                Destination = "KLAX",
                Route = "WESLA5",
                Altitude = PlannedAltitude.Ifr(35000),
                FlightRules = "IFR",
            },
        };
        aircraft.Procedure.DepartureRunway = "28R";
        foreach (string name in fixes)
        {
            (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition(name) ?? throw new InvalidOperationException($"no {name}");
            aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = name, Position = new LatLon(lat, lon) });
        }

        SimulationEngine engine = NewEngine(aircraft, "SFO");
        SendOk(engine, aircraft, "CVIA");
        return (engine, aircraft);
    }

    /// <summary>
    /// A B738 or turboprop level at <paramref name="altitudeFt"/> on KIAH DOOBI3 from AEX, just past DOOBI with HHART next, slowed
    /// to <paramref name="kts"/> by an assigned speed more than 5 NM before HHART.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft) PastDoobi(string type, EngineKind engineKind, double altitudeFt, double kts)
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnStar("KIAH", "AEX", "DOOBI3", null, altitudeFt, type, engineKind);
        for (int t = 0; (t < 3600) && (aircraft.Targets.NavigationRoute.FindIndex(f => f.Name == Hhart) > 0); t++)
        {
            engine.TickOneSecond();
        }

        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        Assert.True((route.Count > 0) && (route[0].Name == Hhart), $"setup: route {string.Join(" ", route.Select(f => f.Name))}");
        Assert.Equal(new CifpSpeedRestriction((int)HhartFloorKts, CifpSpeedRestrictionType.AtOrAbove), route[0].SpeedRestriction);
        SendOk(engine, aircraft, $"SPD {kts}");
        for (int t = 0; (t < 60) && (Math.Abs(aircraft.IndicatedAirspeed - kts) > HoldToleranceKts); t++)
        {
            engine.TickOneSecond();
        }

        Assert.InRange(aircraft.IndicatedAirspeed, kts - HoldToleranceKts, kts + HoldToleranceKts);
        Assert.True((SequencingDistanceTo(aircraft, Hhart) ?? 0) > 5, $"setup: {SequencingDistanceTo(aircraft, Hhart):F1} NM to {Hhart}");
        return (engine, aircraft);
    }

    /// <summary>300 KIAS at FL240, 150 NM before OAKES (280): the slowdown starts in its window, as before.</summary>
    [Fact]
    public void SlowerPublishedSpeed_SlowsOnlyInItsWindow()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150, 24000);
        SetSpeed(aircraft, 300);
        AssertChangeInWindow(engine, aircraft, Oakes, OakesKts);
    }

    /// <summary>
    /// the whole KIAH DOOBI3 descend via from AEX at FL240: past each published speed crossed below 10,000 ft, the
    /// aircraft holds that speed (the descent schedule does not slow it) until its slowdown window for the next lower speed.
    /// </summary>
    [Fact]
    public void PublishedSpeedCrossedBelowTenThousand_HoldsUntilTheNextRestriction()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnStar("KIAH", "AEX", "DOOBI3", null, 24000, "B738", EngineKind.Jet);
        SendOk(engine, aircraft, "DVIA");
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        double decel = AircraftPerformance.DecelRate(aircraft.AircraftType, cat);
        double? heldKts = null;
        int checkedTicks = 0;
        for (int t = 1; (t <= 3600) && (aircraft.Targets.NavigationRoute.Count > 0); t++)
        {
            NavigationTarget? crossing = aircraft.Targets.NavigationRoute[0];
            engine.TickOneSecond();
            if (
                !aircraft.Targets.NavigationRoute.Contains(crossing)
                && (crossing.SpeedRestriction is { Type: not CifpSpeedRestrictionType.AtOrAbove } s)
            )
            {
                bool atIt = (aircraft.Altitude < 10000) && (Math.Abs(aircraft.IndicatedAirspeed - s.SpeedKts) <= ReachToleranceKts);
                heldKts = atIt ? s.SpeedKts : null;
                _output.WriteLine($"+{t} s crossed {crossing.Name} ({s.SpeedKts}) at {aircraft.Altitude:F0} ft, IAS {aircraft.IndicatedAirspeed:F1}");
            }

            if ((heldKts is not { } held) || ((NextSpeedFix(aircraft) is { } ahead) && (ahead.Kts > held)))
            {
                continue;
            }

            (double Kts, double SeqDistNm) next = NextSpeedFix(aircraft) ?? (held, double.MaxValue);
            double windowNm = aircraft.GroundSpeed * WindowMargin * Math.Max(0, aircraft.IndicatedAirspeed - next.Kts) / decel / 3600.0;
            if (next.SeqDistNm > windowNm + (2 * aircraft.GroundSpeed / 3600.0))
            {
                checkedTicks++;
                Assert.True(
                    Math.Abs(aircraft.IndicatedAirspeed - held) <= ReachToleranceKts,
                    $"+{t} s at {aircraft.Altitude:F0} ft: IAS {aircraft.IndicatedAirspeed:F1} off the {held} kt held, "
                        + $"{next.SeqDistNm:F1} NM before the next {next.Kts} kt fix (window {windowNm:F1} NM)"
                );
            }
        }

        Assert.True(checkedTicks > 0, "setup: no published speed was crossed and held below 10,000 ft");
        _output.WriteLine($"{checkedTicks} s held below 10,000 ft");
    }

    /// <summary>
    /// T-A1: 305 KIAS level at FL260 with a descend via, 150 NM before OAKES (280, FL320 to FL240): level in the
    /// top-of-descent hold toward FL240, where the descent schedule would fly 290, the speed holds, and the slowdown starts
    /// in OAKES's window.
    /// </summary>
    [Fact]
    public void SlowerPublishedSpeed_UnderDescendVia_HoldsWhileLevelAndSlowsInItsWindow()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150, 26000);
        SetSpeed(aircraft, 305);
        SendOk(engine, aircraft, "DVIA");
        engine.TickOneSecond();
        Assert.Equal(24000, aircraft.Targets.TargetAltitude);
        Assert.Equal(0, aircraft.Targets.PlannedVerticalRate);
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        double scheduleKts = AircraftPerformance.DefaultSpeed(aircraft.AircraftType, cat, aircraft.Altitude, aircraft.Targets.TargetAltitude);
        Assert.True(Math.Abs(scheduleKts - 305) > 10, $"setup: the schedule's {scheduleKts:F0} is the held speed");
        Assert.InRange(aircraft.IndicatedAirspeed, 305 - HoldToleranceKts, 305 + HoldToleranceKts);
        AssertChangeInWindow(engine, aircraft, Oakes, OakesKts);
    }

    /// <summary>
    /// T-A2: KLAS COKTL4 from FLCHR, level at FL300 at 260 KIAS, no descend via: ARYEL's "at or below 280" is a maximum
    /// only, so 260 holds through ARYEL, and the slowdown for COKTL's 250 starts in its window.
    /// </summary>
    [Fact]
    public void HigherAtOrBelow_Level_HoldsThroughItAndSlowsForTheNextInItsWindow()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnStar("KLAS", "FLCHR", "COKTL4", null, 30000, "B738", EngineKind.Jet);
        SetSpeed(aircraft, 260);
        AssertChangeInWindow(engine, aircraft, Cokt, CoktlKts);
    }

    /// <summary>
    /// T-A2 with a descend via: while level in the top-of-descent hold 260 holds, and past the top of descent the speed
    /// never runs over ARYEL's 280 maximum before ARYEL.
    /// </summary>
    [Fact]
    public void HigherAtOrBelow_UnderDescendVia_HoldsWhileLevelAndNeverPassesTheMaximum()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnStar("KLAS", "FLCHR", "COKTL4", null, 30000, "B738", EngineKind.Jet);
        SetSpeed(aircraft, 260);
        SendOk(engine, aircraft, "DVIA");
        int level = 0;
        for (int t = 1; (t <= 3600) && aircraft.Targets.NavigationRoute.Exists(f => f.Name == Aryel); t++)
        {
            engine.TickOneSecond();
            if ((level == t - 1) && (aircraft.VerticalSpeed == 0))
            {
                level = t;
                Assert.InRange(aircraft.IndicatedAirspeed, 260 - HoldToleranceKts, 260 + HoldToleranceKts);
                continue;
            }

            Assert.True(
                aircraft.IndicatedAirspeed <= AryelMaxKts + FlightPhysics.SpeedSnapMarginKts,
                $"+{t} s at {aircraft.Altitude:F0} ft: IAS {aircraft.IndicatedAirspeed:F1} over {Aryel}'s {AryelMaxKts}"
            );
        }

        Assert.True(level > 60, $"setup: level for {level} s");
        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, f => f.Name == Aryel);
    }

    /// <summary>
    /// T-B1: a CRJ9 on KOAK OAKES3 for runway 12 under a descend via, whose descent schedule below 10,000 ft is under 250:
    /// past SULRR's 250 it holds 250 down through 10,000 ft (KEENR at 7,000) until LOHGN's (200) slowdown window.
    /// </summary>
    [Fact]
    public void PublishedSpeedReached_ScheduleBelowIt_HoldsItUntilTheNextWindow()
    {
        (SimulationEngine engine, AircraftState aircraft) = CrjPastSulrr();
        AssertChangeInWindow(engine, aircraft, Lohgn, LohgnKts);
        Assert.True(aircraft.Altitude < 10000, $"setup: {Lohgn} sequenced at {aircraft.Altitude:F0} ft");

        // A lower ceiling set after the crossing still caps the held speed
        (engine, aircraft) = CrjPastSulrr();
        const double ceilingKts = 235;
        aircraft.Targets.SpeedCeiling = ceilingKts;
        for (int t = 1; t <= 30; t++)
        {
            engine.TickOneSecond();
        }

        Assert.True(
            aircraft.IndicatedAirspeed <= ceilingKts + HoldToleranceKts,
            $"IAS {aircraft.IndicatedAirspeed:F1} over the {ceilingKts} ceiling"
        );
    }

    /// <summary>T-B1's setup: a CRJ9 on KOAK OAKES3 runway 12 from BANND under a descend via, just past SULRR at 250.</summary>
    private (SimulationEngine Engine, AircraftState Aircraft) CrjPastSulrr()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnStar("KOAK", "BANND", "OAKES3", "12", 14000, "CRJ9", EngineKind.Jet);
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        double scheduleKts = AircraftPerformance.DefaultSpeed(aircraft.AircraftType, cat, 8000, 6000);
        Assert.True(scheduleKts < SulrrKts - 5, $"setup: the CRJ9 schedule at 8,000 ft is {scheduleKts:F0}");
        SetSpeed(aircraft, SulrrKts);
        SendOk(engine, aircraft, "DVIA");
        for (int t = 0; (t < 3600) && aircraft.Targets.NavigationRoute.Exists(f => f.Name == Sulrr); t++)
        {
            engine.TickOneSecond();
        }

        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, f => f.Name == Sulrr);
        Assert.InRange(aircraft.IndicatedAirspeed, SulrrKts - HoldToleranceKts, SulrrKts + HoldToleranceKts);
        return (engine, aircraft);
    }

    /// <summary>
    /// T-C1: level at FL350 at 250 KIAS with a descend via, short of OAKES's (280) acceleration window, the planner pins the
    /// present speed; a speed floor of 265 in force lifts the pin to it, so the pin never holds the aircraft under a floor.
    /// </summary>
    [Fact]
    public void Pin_UnderASpeedFloor_TargetsTheFloor()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150, 35000);
        SetSpeed(aircraft, 250);
        SendOk(engine, aircraft, "DVIA");
        engine.TickOneSecond();
        Assert.InRange(aircraft.IndicatedAirspeed, 250 - HoldToleranceKts, 250 + HoldToleranceKts);
        aircraft.Targets.SpeedFloor = 265;
        engine.TickOneSecond();
        Assert.True((aircraft.Targets.TargetSpeed ?? 0) >= 265, $"target {aircraft.Targets.TargetSpeed} under the 265 floor");
        Assert.True(aircraft.IndicatedAirspeed > 250 + HoldToleranceKts, $"IAS {aircraft.IndicatedAirspeed:F1} under the floor");
    }

    /// <summary>"SPD 250, DVIA" in one transmission at FL350: the assigned speed wins over OAKES's 280.</summary>
    [Fact]
    public void SpeedWithDescendVia_TheAssignedSpeedWins()
    {
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnOakes(150, 35000);
        SendOk(engine, aircraft, "SPD 250, DVIA");
        for (int t = 1; (t <= 3600) && aircraft.Targets.NavigationRoute.Exists(f => f.Name == Oakes); t++)
        {
            engine.TickOneSecond();
            Assert.Equal(250, aircraft.Targets.AssignedSpeed);
            if (t > 30)
            {
                Assert.InRange(aircraft.IndicatedAirspeed, 250 - ReachToleranceKts, 250 + ReachToleranceKts);
            }
        }

        Assert.DoesNotContain(aircraft.Targets.NavigationRoute, f => f.Name == Oakes);
    }

    /// <summary>
    /// Ticks until <paramref name="fixName"/> sequences. The IAS holds within <see cref="HoldToleranceKts"/> of its start until
    /// the change toward <paramref name="toKts"/> begins, which happens when the sequencing distance is within two ticks'
    /// travel of the window (the ground speed times 1.1 times the change's time at the type's rate; for a speed-up the mean
    /// ground speed over the change, the present one plus half the true airspeed gained). A speed-up, once begun, gains speed
    /// every tick until within the snap of <paramref name="toKts"/>; the fix is crossed at <paramref name="toKts"/>.
    /// </summary>
    private void AssertChangeInWindow(SimulationEngine engine, AircraftState aircraft, string fixName, double toKts)
    {
        AircraftCategory cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        double fromKts = aircraft.IndicatedAirspeed;
        bool accelerating = toKts > fromKts;
        double rate = accelerating
            ? AircraftPerformance.AccelRate(aircraft.AircraftType, cat)
            : AircraftPerformance.DecelRate(aircraft.AircraftType, cat);
        double gainedTas = WindInterpolator.IasToTas(toKts, aircraft.Altitude) - WindInterpolator.IasToTas(fromKts, aircraft.Altitude);
        bool started = false;
        for (int t = 1; t <= 3600; t++)
        {
            double seqDistNm = SequencingDistanceTo(aircraft, fixName) ?? 0;
            double gs = aircraft.GroundSpeed + (accelerating ? gainedTas / 2.0 : 0);
            double before = aircraft.IndicatedAirspeed;
            engine.TickOneSecond();
            bool rising = aircraft.IndicatedAirspeed > before;
            Assert.True(
                !started || !accelerating || rising || (toKts - before < FlightPhysics.SpeedSnapMarginKts),
                $"+{t} s: IAS {aircraft.IndicatedAirspeed:F1} stopped rising toward {toKts} (was {before:F1})"
            );
            if (!aircraft.Targets.NavigationRoute.Exists(f => f.Name == fixName))
            {
                _output.WriteLine($"+{t} s sequenced {fixName} at IAS {aircraft.IndicatedAirspeed:F1}, {aircraft.Altitude:F0} ft");
                Assert.True(started, $"The change toward {toKts} never started before {fixName}");
                Assert.InRange(aircraft.IndicatedAirspeed, toKts - ReachToleranceKts, toKts + ReachToleranceKts);
                return;
            }

            if (!started && (Math.Abs(aircraft.IndicatedAirspeed - fromKts) > HoldToleranceKts))
            {
                started = true;
                double windowNm = gs * WindowMargin * Math.Abs(toKts - fromKts) / rate / 3600.0;
                _output.WriteLine($"+{t} s change began {seqDistNm:F2} NM before {fixName}'s sequencing point; window {windowNm:F2} NM, GS {gs:F0}");
                Assert.InRange(seqDistNm, windowNm - (gs / 3600.0), windowNm + (2 * gs / 3600.0));
            }
        }

        Assert.Fail($"{fixName} did not sequence within 3600 s");
    }

    /// <summary>The along-route NM to where <paramref name="fixName"/> sequences, or null when it is not on the route.</summary>
    private static double? SequencingDistanceTo(AircraftState aircraft, string fixName)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        double along = 0;
        for (int i = 0; i < route.Count; i++)
        {
            along += GeoMath.DistanceNm((i == 0) ? aircraft.Position : route[i - 1].Position, route[i].Position);
            if (route[i].Name == fixName)
            {
                return FlightPhysics.SequencingDistanceNm(aircraft, route, i, along);
            }
        }

        return null;
    }

    /// <summary>The first route fix with a speed that is not a minimum: its speed and the NM to where it sequences.</summary>
    private static (double Kts, double SeqDistNm)? NextSpeedFix(AircraftState aircraft)
    {
        NavigationTarget? fix = aircraft.Targets.NavigationRoute.Find(f => f.SpeedRestriction is { Type: not CifpSpeedRestrictionType.AtOrAbove });
        double cappedKts = (fix?.SpeedRestriction?.SpeedKts is { } kts) ? Math.Min(kts, (aircraft.Altitude < 10000) ? 250 : kts) : 0;
        return fix is null ? null : (cappedKts, SequencingDistanceTo(aircraft, fix.Name) ?? 0);
    }

    private static void SetSpeed(AircraftState aircraft, double kts)
    {
        aircraft.IndicatedAirspeed = kts;
        aircraft.Targets.TargetSpeed = null;
    }

    private static void SendOk(SimulationEngine engine, AircraftState aircraft, string command)
    {
        CommandResult result = engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, result.Message);
    }

    /// <summary>A B738 on KOAK OAKES3 at <paramref name="altitudeFt"/>, <paramref name="distanceBeforeFixNm"/> before OAKES.</summary>
    private (SimulationEngine Engine, AircraftState Aircraft) SpawnOnOakes(double distanceBeforeFixNm, double altitudeFt)
    {
        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KOAK", "OAKES3"));
        string entryFix = DepartureClearanceHandler.ResolveLegsToTargets([.. star.CommonLegs])[0].Name;
        string runwayKey = star.RunwayTransitions.Keys.First();
        string runway = runwayKey.StartsWith("RW", StringComparison.Ordinal) ? runwayKey[2..] : runwayKey;
        (SimulationEngine engine, AircraftState aircraft) = SpawnOnStar("KOAK", entryFix, "OAKES3", runway, altitudeFt, "B738", EngineKind.Jet);
        PlaceBeforeFix(aircraft, distanceBeforeFixNm);
        return (engine, aircraft);
    }

    /// <summary>A fresh engine at <paramref name="airportId"/> holding <paramref name="aircraft"/> alone.</summary>
    private static SimulationEngine NewEngine(AircraftState aircraft, string airportId)
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout(airportId) is AirportGroundLayout layout)
        {
            aircraft.Ground.Layout = layout;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "star-speed-timing",
                ScenarioName = "STAR speed timing",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = airportId,
            },
        };
        engine.World.AddAircraft(aircraft);
        return engine;
    }

    /// <summary>A <paramref name="type"/> spawned on <paramref name="starId"/> at <paramref name="entryFix"/>, level.</summary>
    private (SimulationEngine Engine, AircraftState Aircraft) SpawnOnStar(
        string airport,
        string entryFix,
        string starId,
        string? runway,
        double altitudeFt,
        string type,
        EngineKind engineKind
    )
    {
        SimLogBuilder.CreateForTest(_output).InitializeSimLog();
        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Ifr,
            Weight = WeightClass.Large,
            Engine = engineKind,
            PositionType = SpawnPositionType.OnStar,
            StarEntryFix = entryFix,
            StarId = starId,
            StarRunway = runway,
            DescendVia = false,
            StarAltitude = altitudeFt,
            DestinationAirportId = airport,
            ExplicitType = type,
        };
        (AircraftState? aircraft, string? error) = AircraftGenerator.Generate(
            request,
            airport,
            [],
            groundLayout: null,
            new Random(1),
            new BeaconCodePool()
        );
        Assert.Null(error);
        Assert.NotNull(aircraft);
        SimulationEngine engine = NewEngine(aircraft, airport[1..]);
        _output.WriteLine(
            $"Spawned at {aircraft.Altitude:F0}, IAS {aircraft.IndicatedAirspeed:F0}, route "
                + string.Join(
                    " ",
                    aircraft.Targets.NavigationRoute.Select(f => $"{f.Name}({f.SpeedRestriction?.Type} {f.SpeedRestriction?.SpeedKts})")
                )
        );
        return (engine, aircraft);
    }

    /// <summary>Puts the aircraft on the inbound course to the STAR's first fix, <paramref name="distanceBeforeFixNm"/> before OAKES.</summary>
    private static void PlaceBeforeFix(AircraftState aircraft, double distanceBeforeFixNm)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        int k = route.FindIndex(f => f.Name == Oakes);
        Assert.True(k >= 0, $"{Oakes} is not on the route");
        double along = 0;
        for (int i = 1; i <= k; i++)
        {
            along += GeoMath.DistanceNm(route[i - 1].Position, route[i].Position);
        }

        double inbound = GeoMath.BearingTo(route[0].Position, route[1].Position);
        aircraft.Position = GeoMath.ProjectPoint(route[0].Position, new TrueHeading(inbound + 180), distanceBeforeFixNm - along);
        var heading = new TrueHeading(GeoMath.BearingTo(aircraft.Position, route[0].Position));
        aircraft.TrueHeading = heading;
        aircraft.TrueTrack = heading;
    }
}
