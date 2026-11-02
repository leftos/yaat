using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1411: a descending aircraft crosses 10,000 ft at or below 250 KIAS (14 CFR 91.117(a)). The 250 kt cap leads the
/// descent: above 10,000 ft it applies once the time left to 10,000 ft is within 1.1 times the time the type needs to
/// slow to 250, and an aircraft too fast to make it even at full deceleration levels at 10,000 ft until it has slowed.
/// The A320 is the DAL334 fixture of <see cref="SpeedEnvelopeCapTests"/> near Needles, AZ, under no Class B shelf.
/// </summary>
public sealed class DescentSpeedLimitLeadTests
{
    private const double CapKts = 250;
    private const double CrossingLimitKts = CapKts + FlightPhysics.SpeedSnapMarginKts;

    private readonly ITestOutputHelper _output;

    public DescentSpeedLimitLeadTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private sealed record Harness(SimulationEngine Engine, AircraftState Aircraft);

    private static AircraftState A320At(double altitude, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "DAL334",
            AircraftType = "A320",
            Position = new LatLon(34.771654, -114.542825),
            TrueHeading = new TrueHeading(270),
            TrueTrack = new TrueHeading(270),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                HasFlightPlan = true,
                Departure = "KPHX",
                Destination = "KLAX",
            },
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(270);
        ac.Targets.TargetAltitude = altitude;
        return ac;
    }

    private static void Send(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new SerializableRandom(1)));
        Assert.True(result.Success, $"{text}: {result.Message}");
    }

    /// <summary>Steps sub-tick by sub-tick until <paramref name="done"/> holds, failing after <paramref name="maxSeconds"/>.</summary>
    private static void FlyUntil(AircraftState aircraft, Func<AircraftState, bool> done, double maxSeconds, Action<AircraftState> eachSubTick)
    {
        for (int i = 0; i < (int)(maxSeconds / 0.25); i++)
        {
            FlightPhysics.Update(aircraft, 0.25);
            eachSubTick(aircraft);
            if (done(aircraft))
            {
                return;
            }
        }

        Assert.Fail($"Not done within {maxSeconds} s: at {aircraft.Altitude:F0} ft, {aircraft.IndicatedAirspeed:F1} KIAS");
    }

    /// <summary>The IAS on the first sub-tick below 10,000 ft of a descent flown from the aircraft's present state.</summary>
    private static double IasFirstBelowTenThousand(AircraftState aircraft, Action<AircraftState> eachSubTick)
    {
        double? crossing = null;
        FlyUntil(
            aircraft,
            ac => crossing is not null,
            900,
            ac =>
            {
                eachSubTick(ac);
                if ((crossing is null) && (ac.Altitude < 10_000))
                {
                    crossing = ac.IndicatedAirspeed;
                }
            }
        );
        return crossing!.Value;
    }

    [Fact]
    public void SpdAndDmFromFl150_CrossesTenThousandAt252OrLess_NeverLevels()
    {
        AircraftState ac = A320At(altitude: 15_000, ias: 280);
        Send(ac, "SPD 280");
        Send(ac, "DM 080");

        double? crossing = null;
        FlyUntil(
            ac,
            a => a.Altitude <= 8_500,
            900,
            a =>
            {
                Assert.True(a.VerticalSpeed != 0, $"Levelled at {a.Altitude:F0} ft, {a.IndicatedAirspeed:F1} KIAS");
                Assert.Equal(280, Assert.IsType<double>(a.Targets.AssignedSpeed), 0.5);
                if ((crossing is null) && (a.Altitude < 10_000))
                {
                    crossing = a.IndicatedAirspeed;
                }
            }
        );

        _output.WriteLine($"Crossed 10,000 ft at {crossing:F1} KIAS");
        Assert.True(crossing <= CrossingLimitKts, $"Crossed 10,000 ft at {crossing:F1} KIAS");
    }

    /// <summary>Once the cap starts slowing the aircraft above 10,000 ft, it slows steadily: no sawtooth back up toward 280.</summary>
    [Fact]
    public void SpdAndDmFromFl150_OnceTheBandStarts_IasNeverRisesAboveTenThousand()
    {
        AircraftState ac = A320At(altitude: 15_000, ias: 280);
        Send(ac, "SPD 280");
        Send(ac, "DM 080");

        double previous = ac.IndicatedAirspeed;
        double? bandStartFt = null;
        IasFirstBelowTenThousand(
            ac,
            a =>
            {
                if (a.Altitude >= 10_000)
                {
                    if ((bandStartFt is null) && (a.IndicatedAirspeed < previous))
                    {
                        bandStartFt = a.Altitude;
                    }
                    else if (bandStartFt is not null)
                    {
                        Assert.True(
                            a.IndicatedAirspeed <= previous,
                            $"IAS rose from {previous:F2} to {a.IndicatedAirspeed:F2} at {a.Altitude:F0} ft after slowing from {bandStartFt:F0} ft"
                        );
                    }
                }

                previous = a.IndicatedAirspeed;
            }
        );

        _output.WriteLine($"Began slowing at {bandStartFt:F0} ft");
        Assert.True(bandStartFt is not null, "The aircraft did not begin slowing above 10,000 ft");
    }

    [Fact]
    public void ScheduleOnlyDmFromFl150_CrossesTenThousandAt252OrLess()
    {
        AircraftCategory cat = AircraftCategorization.Categorize("A320");
        double scheduleIas = AircraftPerformance.DescentSpeed("A320", cat, 15_000);
        AircraftState ac = A320At(altitude: 15_000, ias: scheduleIas);
        Send(ac, "DM 080");

        double crossing = IasFirstBelowTenThousand(ac, _ => { });

        _output.WriteLine($"Schedule {scheduleIas:F0} KIAS at 15,000 ft; crossed 10,000 ft at {crossing:F1} KIAS");
        Assert.True(crossing <= CrossingLimitKts, $"Crossed 10,000 ft at {crossing:F1} KIAS");
    }

    /// <summary>Fly time 300 s: about 20 s to slow from 290 to 250 at the A320's 2 kt/s, then 2,600 ft of descent.</summary>
    [Fact]
    public void TooFastJustAboveTenThousand_HoldsAtTenThousandUntilSlow_ThenContinues()
    {
        AircraftState ac = A320At(altitude: 10_600, ias: 290);
        Send(ac, "DM 080");

        FlyUntil(
            ac,
            a => Math.Abs(a.Altitude - 8_000) <= 10,
            300,
            a =>
            {
                Assert.Equal(8_000, Assert.IsType<double>(a.Targets.AssignedAltitude));
                if (a.IndicatedAirspeed > CrossingLimitKts)
                {
                    Assert.True(a.Altitude >= 9_990, $"Below 10,000 ft at {a.Altitude:F0} ft, {a.IndicatedAirspeed:F1} KIAS");
                }
            }
        );
    }

    /// <summary>
    /// A crossing speed above 250 at a fix crossed at or above 10,000 ft wins until that fix: "CFIX GRUDG 110 280, DVIA 080"
    /// on KOAK's OAKES3 RW12 holds 280 down to GRUDG at 11,000 ft. Past GRUDG the aircraft is below 10,000 ft only at or
    /// below 252 KIAS, slowed by the lead or held at 10,000 ft by the backstop.
    /// </summary>
    [Fact]
    public void CrossingSpeedAboveTwoFiftyAtOrAboveTenThousand_WinsUntilTheFix()
    {
        Harness harness = SpawnOnOakes3Rw12(altitudeFt: 16000);
        AircraftState aircraft = harness.Aircraft;
        TickUntilPassed(harness, "OAKES", _ => { });
        SendOk(harness, "CFIX GRUDG 110 280, DVIA 080");

        TickUntilPassed(
            harness,
            "GRUDG",
            a => Assert.True(a.IndicatedAirspeed > CrossingLimitKts, $"Slowed to {a.IndicatedAirspeed:F1} KIAS at {a.Altitude:F0} ft before GRUDG")
        );

        for (int t = 1; (t <= 900) && (aircraft.Altitude > 9_000); t++)
        {
            harness.Engine.TickOneSecond();
            if (aircraft.Altitude < 9_990)
            {
                Assert.True(
                    aircraft.IndicatedAirspeed <= CrossingLimitKts,
                    $"Below 10,000 ft at {aircraft.Altitude:F0} ft, {aircraft.IndicatedAirspeed:F1} KIAS, {t} s past GRUDG"
                );
            }
        }

        Assert.True(aircraft.Altitude <= 9_000, $"Still at {aircraft.Altitude:F0} ft 900 s past GRUDG");
    }

    /// <summary>
    /// The cap and the 10,000 ft hold judge the time to 10,000 ft at one rate, so the hold's level-off taper does not drop
    /// the cap: the band applies from the first sub-tick of this descent, too fast to make 10,000 ft at 250 even at full
    /// deceleration, and the cap holds on every sub-tick down to 10,000 ft.
    /// </summary>
    [Fact]
    public void TooFastJustAboveTenThousand_CapHoldsOnEverySubTickUntilBelowTenThousand()
    {
        AircraftState ac = A320At(altitude: 10_600, ias: 290);
        Send(ac, "DM 080");

        FlyUntil(
            ac,
            a => a.Altitude < 10_000,
            300,
            a =>
            {
                if (a.Altitude < 10_000)
                {
                    return;
                }

                double limit = FlightPhysics.RegulatorySpeedLimit(a);
                Assert.True(limit == CapKts, $"No cap ({limit}) at {a.Altitude:F0} ft, {a.IndicatedAirspeed:F1} KIAS, {a.VerticalSpeed:F0} fpm");
            }
        );
    }

    /// <summary>
    /// "CFIX GRUDG B110 280" is a ceiling, not a floor: the aircraft may be below 10,000 ft before GRUDG, so its 280 does not
    /// hold the cap off, and after "DM 080" it crosses 10,000 ft at the cap.
    /// </summary>
    [Fact]
    public void CfixAtOrBelowElevenThousandAt280ThenDm_CrossesTenThousandAtTheCap()
    {
        Harness harness = SpawnOnOakes3Rw12(altitudeFt: 15000);
        AircraftState aircraft = harness.Aircraft;
        TickUntilPassed(harness, "OAKES", _ => { });
        SendOk(harness, "CFIX GRUDG B110 280");
        SendOk(harness, "DM 080");

        double? crossing = null;
        for (int t = 1; (t <= 900) && (crossing is null); t++)
        {
            harness.Engine.TickOneSecond();
            if (aircraft.Altitude < 10_000)
            {
                crossing = aircraft.IndicatedAirspeed;
            }
        }

        bool grudgAhead = aircraft.Targets.NavigationRoute.Exists(f => f.Name.Equals("GRUDG", StringComparison.OrdinalIgnoreCase));
        _output.WriteLine($"Crossed 10,000 ft at {crossing:F1} KIAS, GRUDG still ahead: {grudgAhead}");
        Assert.True(crossing <= CrossingLimitKts, $"Crossed 10,000 ft at {crossing:F1} KIAS");
    }

    [Fact]
    public void SpdnDescent_IsNotCapped()
    {
        AircraftState ac = A320At(altitude: 15_000, ias: 300);
        Send(ac, "SPDN 300");
        Send(ac, "DM 080");

        double crossing = IasFirstBelowTenThousand(ac, _ => { });

        Assert.True(crossing > 290, $"SPDN 300 crossed 10,000 ft at {crossing:F1} KIAS");
    }

    [Fact]
    public void SmaxDescent_IsNotCapped()
    {
        AircraftState ac = A320At(altitude: 15_000, ias: 300);
        Send(ac, "SMAX");
        Send(ac, "DM 080");

        double crossing = IasFirstBelowTenThousand(ac, _ => { });

        Assert.True(crossing > 290, $"SMAX crossed 10,000 ft at {crossing:F1} KIAS");
    }

    /// <summary>A generated heavy jet level at <paramref name="altitudeFt"/> at OAKES on KOAK's OAKES3 RW12, the STAR filed.</summary>
    private Harness SpawnOnOakes3Rw12(int altitudeFt)
    {
        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Ifr,
            Weight = WeightClass.Heavy,
            Engine = EngineKind.Jet,
            PositionType = SpawnPositionType.OnStar,
            StarEntryFix = "OAKES",
            StarId = "OAKES3",
            StarRunway = "12",
            DescendVia = false,
            StarAltitude = altitudeFt,
            DestinationAirportId = "KOAK",
        };
        (AircraftState? aircraft, string? error) = AircraftGenerator.Generate(
            request,
            "KOAK",
            [],
            groundLayout: null,
            new Random(1),
            new BeaconCodePool()
        );
        Assert.Null(error);
        Assert.NotNull(aircraft);

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "descent-speed-limit-lead",
                ScenarioName = "The 250 kt cap leads a descent through 10,000 ft",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        engine.World.AddAircraft(aircraft);
        _output.WriteLine($"Spawned {aircraft.Callsign} {aircraft.AircraftType} at {aircraft.Altitude:F0}, IAS {aircraft.IndicatedAirspeed:F0}");
        return new Harness(engine, aircraft);
    }

    private void TickUntilPassed(Harness harness, string fixName, Action<AircraftState> eachSecond)
    {
        AircraftState aircraft = harness.Aircraft;
        for (int t = 1; t <= 1800; t++)
        {
            harness.Engine.TickOneSecond();
            if (!aircraft.Targets.NavigationRoute.Exists(f => f.Name.Equals(fixName, StringComparison.OrdinalIgnoreCase)))
            {
                _output.WriteLine($"Passed {fixName} after {t} s at {aircraft.IndicatedAirspeed:F0} kt, {aircraft.Altitude:F0} ft");
                return;
            }

            eachSecond(aircraft);
        }

        Assert.Fail($"The aircraft did not pass {fixName} within 1800 s");
    }

    private void SendOk(Harness harness, string command)
    {
        CommandResult result = harness.Engine.SendCommand(harness.Aircraft.Callsign, command);
        _output.WriteLine($"{command} -> {result.Message} (IAS {harness.Aircraft.IndicatedAirspeed:F0})");
        Assert.True(result.Success, $"{command}: {result.Message}");
    }
}
