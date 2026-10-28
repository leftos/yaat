using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1362: a speed the aircraft was able to accept but that becomes unflyable as it climbs or descends is capped at
/// the type's limit (the lower of Vmo and Mmo's IAS equivalent at the altitude), recomputed every tick. The assignment
/// stands behind the cap, as it does behind 14 CFR 91.117. The A320's figures are EASA TCDS A.064 III.10: VMO 350 kt,
/// MMO 0.82, crossover near FL240 on ISA.
/// </summary>
public sealed class SpeedEnvelopeCapTests
{
    private const double A320VmoKts = 350;
    private const double A320Mmo = 0.82;
    private const double SpeedSnapKts = 2;
    private const double MachTolerance = 0.005;

    private readonly ITestOutputHelper _output;

    public SpeedEnvelopeCapTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

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

    private static CommandResult Send(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new SerializableRandom(1)));
    }

    private static void FlySeconds(AircraftState aircraft, int seconds, Action<AircraftState> eachSecond)
    {
        for (int second = 0; second < seconds; second++)
        {
            for (int sub = 0; sub < 4; sub++)
            {
                FlightPhysics.Update(aircraft, 0.25);
            }

            eachSecond(aircraft);
        }
    }

    private static double CrossoverAltitudeFt()
    {
        double altitude = 20_000;
        while (WindInterpolator.MachToIas(A320Mmo, altitude) > A320VmoKts)
        {
            altitude += 100;
        }

        return altitude;
    }

    [Fact]
    public void AssignedSpeed_ClimbingPastCrossover_IsCappedAtMmo()
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPD 340").Success);
        Assert.True(Send(ac, "CM 350").Success);
        double crossover = CrossoverAltitudeFt();

        double worstMach = 0;
        FlySeconds(
            ac,
            900,
            a =>
            {
                if (a.Altitude > crossover)
                {
                    worstMach = Math.Max(worstMach, WindInterpolator.IasToMach(a.IndicatedAirspeed, a.Altitude));
                }
            }
        );

        Assert.InRange(ac.Altitude, 34_990, 35_010);
        Assert.True(worstMach <= A320Mmo + MachTolerance, $"climbing with SPD 340 above the crossover flew Mach {worstMach:F3}, past MMO {A320Mmo}");
        Assert.Equal(340, Assert.IsType<double>(ac.Targets.AssignedSpeed), 0.5);
        Assert.NotNull(ac.Targets.TargetSpeed);
    }

    [Fact]
    public void MachHoldThenDescent_StaysAtOrBelowVmo()
    {
        AircraftState ac = A320At(altitude: 35_000, ias: 260);
        Assert.True(Send(ac, "MACH .78").Success);
        Assert.True(Send(ac, "DM 110").Success);

        double peakIas = 0;
        FlySeconds(ac, 900, a => peakIas = Math.Max(peakIas, a.IndicatedAirspeed));

        Assert.True(ac.Altitude < 20_000, $"premise: the descent reaches well below the crossover, ended at {ac.Altitude:F0} ft");
        Assert.True(peakIas <= A320VmoKts + SpeedSnapKts, $"MACH .78 then DM 110 flew {peakIas:F1} KIAS, past VMO {A320VmoKts}");
    }

    [Fact]
    public void ChainedSpeedHeldAtCap_Completes()
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPD 340, CM 350; SPD 260").Success);

        double worstMach = 0;
        int firedAt = -1;
        int second = 0;
        FlySeconds(
            ac,
            900,
            a =>
            {
                second++;
                worstMach = Math.Max(worstMach, WindInterpolator.IasToMach(a.IndicatedAirspeed, a.Altitude));
                if ((firedAt < 0) && (a.Targets.AssignedSpeed is 260))
                {
                    firedAt = second;
                }
            }
        );

        Assert.True(worstMach <= A320Mmo + MachTolerance, $"SPD 340 flew Mach {worstMach:F3} before the chain, past MMO {A320Mmo}");
        Assert.True(firedAt > 0, $"the chained SPD 260 never fired: SPD 340 held at the cap at FL{ac.Altitude / 100:F0} did not complete");
        Assert.Equal(260, ac.IndicatedAirspeed, 1.0);
    }

    [Fact]
    public void LevelCruiseAboveTheProfileCruiseAltitude_IsCappedAtMmo()
    {
        double levelCruiseIas = AircraftPerformance.DefaultSpeed("A320", AircraftCategory.Jet, 41_000, targetAltitude: null);
        AircraftState ac = A320At(altitude: 41_000, ias: levelCruiseIas);

        double worstMach = 0;
        FlySeconds(ac, 120, a => worstMach = Math.Max(worstMach, WindInterpolator.IasToMach(a.IndicatedAirspeed, a.Altitude)));

        Assert.True(
            WindInterpolator.IasToMach(ac.IndicatedAirspeed, ac.Altitude) <= A320Mmo + MachTolerance,
            $"level at FL410 the A320 settled at Mach {WindInterpolator.IasToMach(ac.IndicatedAirspeed, ac.Altitude):F3}, past MMO {A320Mmo}"
        );
        Assert.True(levelCruiseIas > WindInterpolator.MachToIas(A320Mmo, 41_000), "premise: the level-cruise IAS is past MMO at FL410");
        Assert.True(worstMach > 0, "premise: the aircraft flew");
    }

    /// <summary>
    /// Every speed the profile's schedule flies is within the type's cap: the climb and descent schedules at every
    /// 100 ft up to the ceiling and at each altitude the schedule changes band at (10,000, FL150, FL240, the cruise
    /// altitude and the ceiling), and level cruise up to the profile's cruise altitude. The cap's own floor samples a
    /// coarser grid, so a peak between its points is caught here.
    /// </summary>
    [Fact]
    public void ProfileSchedule_NeverAboveTheCap()
    {
        string profilesPath = Path.Combine(AppContext.BaseDirectory, "Data", "AircraftProfiles.json");
        List<string> offenders = [];
        foreach (string type in Yaat.Sim.Data.AircraftProfileDatabase.LoadFromFile(profilesPath).Keys)
        {
            if ((Yaat.Sim.Data.AircraftProfileDatabase.Get(type) is not { } p) || (AircraftPerformance.MaxOperatingIasKts(type, 0) is null))
            {
                continue;
            }

            AircraftCategory cat = AircraftCategorization.Categorize(type);
            SortedSet<double> altitudes = [10_000, 15_000, 24_000, p.CruiseAltitude, p.Ceiling];
            for (double grid = 0; grid <= p.Ceiling; grid += 100)
            {
                altitudes.Add(grid);
            }

            foreach (double alt in altitudes.Where(a => a <= p.Ceiling))
            {
                double cap = AircraftPerformance.MaxOperatingIasKts(type, alt)!.Value + 0.5;
                List<double> flown = [AircraftPerformance.ClimbSpeed(type, cat, alt), AircraftPerformance.DescentSpeed(type, cat, alt)];
                if (alt <= p.CruiseAltitude)
                {
                    flown.Add(AircraftPerformance.DefaultSpeed(type, cat, alt, targetAltitude: null));
                }

                if (flown.Max() > cap)
                {
                    offenders.Add($"{type} at {alt:F0} ft flies {flown.Max():F1} KIAS, cap {cap - 0.5:F1}");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders.Take(30)) + $"\n({offenders.Count} in all)");
    }

    // ---- SPDN lifts every speed limit ----------------------------------------------------

    [Fact]
    public void ForceSpeed_PastVmo_IsFlownPastVmo()
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPDN 360").Success);

        double slowest = double.MaxValue;
        FlySeconds(ac, 120, a => slowest = Math.Min(slowest, a.IndicatedAirspeed));

        Assert.True(slowest >= 360 - SpeedSnapKts, $"SPDN 360 at FL200 slowed to {slowest:F1} KIAS, held to VMO {A320VmoKts}");
        Assert.Equal(360, ac.IndicatedAirspeed, SpeedSnapKts);
    }

    /// <summary>Near Needles, AZ: below 10,000 ft and under no Class B shelf, so 91.117(a)'s 250 kt is the only legal cap.</summary>
    [Fact]
    public void ForceSpeed_Below10k_IsFlownPast250()
    {
        AircraftState ac = A320At(altitude: 8_000, ias: 250);
        Assert.Equal(250, FlightPhysics.RegulatorySpeedLimit(ac));
        Assert.True(Send(ac, "SPDN 300").Success);

        FlySeconds(ac, 120, _ => { });

        Assert.Equal(300, ac.IndicatedAirspeed, SpeedSnapKts);
    }

    [Fact]
    public void ForceSpeedRfas_SetsTheFlag()
    {
        AircraftState ac = A320At(altitude: 5_000, ias: 220);

        CommandResult result = CommandDispatcher.Dispatch(new ForceFinalApproachSpeedCommand(), ac, TestDispatch.Context(new SerializableRandom(1)));

        Assert.True(result.Success, result.Message);
        Assert.True(ac.Targets.SpeedLimitsLifted);
    }

    [Theory]
    [InlineData("SPD 280")]
    [InlineData("SPEEDF 280")]
    [InlineData("MACH .60")]
    [InlineData("RFAS")]
    [InlineData("DSR")]
    [InlineData("RNS")]
    [InlineData("CFIX SUNOL 200 280")]
    [InlineData("WARP SUNOL 270 200 280")]
    public void SpeedActAfterForceSpeed_ClearsTheFlag(string act)
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPDN 360").Success);
        Assert.True(ac.Targets.SpeedLimitsLifted, "premise: SPDN sets the flag");

        CommandResult result = Send(ac, act);

        Assert.True(result.Success, $"{act}: {result.Message}");
        Assert.False(ac.Targets.SpeedLimitsLifted, $"{act} left the SPDN flag set");
    }

    [Fact]
    public void RefusedSpeed_AfterForceSpeed_KeepsTheFlag()
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPDN 360").Success);

        CommandResult result = Send(ac, "SPD 400");

        Assert.False(result.Success, $"premise: SPD 400 past VMO is refused, got '{result.Message}'");
        Assert.True(ac.Targets.SpeedLimitsLifted);
        Assert.Equal(360, ac.Targets.AssignedSpeed);
    }

    [Theory]
    [InlineData("CM 240")]
    [InlineData("DM 160")]
    public void AltitudeClearance_AfterForceSpeed_KeepsTheFlag(string clearance)
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPDN 360").Success);

        CommandResult result = Send(ac, clearance);

        Assert.True(result.Success, $"{clearance}: {result.Message}");
        Assert.True(ac.Targets.SpeedLimitsLifted, $"{clearance} cleared the SPDN flag");
    }

    [Fact]
    public void ConstrainedDirectWithASpeed_AfterForceSpeed_ClearsTheFlag()
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPDN 360").Success);

        CommandResult result = CommandDispatcher.Dispatch(
            SpeedEnvelopeRefusalTests.ConstrainedDirectToSunol(20_000, 300),
            ac,
            TestDispatch.Context(new SerializableRandom(1))
        );

        Assert.True(result.Success, result.Message);
        Assert.False(ac.Targets.SpeedLimitsLifted);
    }

    [Fact]
    public void AltitudeOnlyCrossFix_AfterForceSpeed_KeepsTheFlag()
    {
        AircraftState ac = A320At(altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SPDN 360").Success);

        CommandResult result = Send(ac, "CFIX SUNOL 200");

        Assert.True(result.Success, result.Message);
        Assert.True(ac.Targets.SpeedLimitsLifted);
    }

    /// <summary>
    /// SPDN lifts 14 CFR 91.117(c)'s 200 kt too: a jet at 3,000 ft under the San Francisco Class B shelf, the first point
    /// of a grid around KSFO that the airspace data puts under a shelf.
    /// </summary>
    [Fact]
    public void ForceSpeed_UnderAClassBShelf_IsFlownPast200()
    {
        const double altitude = 3_000;
        LatLon? underShelf = null;
        for (double lat = 37.2; (lat <= 37.9) && (underShelf is null); lat += 0.05)
        {
            for (double lon = -122.6; (lon <= -121.9) && (underShelf is null); lon += 0.05)
            {
                var candidate = new LatLon(lat, lon);
                if (Yaat.Sim.Data.Airspace.AirspaceDatabase.Default.IsUnderClassBShelf(candidate, altitude))
                {
                    underShelf = candidate;
                }
            }
        }

        Assert.True(underShelf is not null, "premise: some point around KSFO at 3,000 ft is under a Class B shelf");
        AircraftState ac = A320At(altitude, ias: 200);
        ac.Position = underShelf.Value;
        Assert.Equal(200, FlightPhysics.RegulatorySpeedLimit(ac));
        Assert.True(Send(ac, "SPDN 260").Success);

        FlySeconds(ac, 30, _ => { });

        Assert.Equal(260, ac.IndicatedAirspeed, SpeedSnapKts);
    }

    /// <summary>
    /// "SPDN 210, CAPP I30" in one block: the approach clearance cancels the speed and puts back the one restated with it,
    /// the SPDN flag included. A generated jet on Oakland's real OAKES3 STAR, past its entry fix.
    /// </summary>
    [Fact]
    public void ForceSpeedThenApproachClearanceInOneBlock_KeepsTheFlag()
    {
        if (Yaat.Sim.Tests.Simulation.DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output) is not { } harness)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
            return;
        }

        AircraftState ac = harness.Aircraft;
        string entryFix = ac.Targets.NavigationRoute[0].Name;
        for (int t = 0; (t < 1800) && ac.Targets.NavigationRoute.Exists(f => f.Name.Equals(entryFix, StringComparison.OrdinalIgnoreCase)); t++)
        {
            harness.Engine.TickOneSecond();
        }

        CommandResult result = harness.Engine.SendCommand(ac.Callsign, "SPDN 210, CAPP I30");

        Assert.True(result.Success, result.Message);
        Assert.Equal(210, ac.Targets.AssignedSpeed);
        Assert.True(ac.Targets.SpeedLimitsLifted);
    }
}
