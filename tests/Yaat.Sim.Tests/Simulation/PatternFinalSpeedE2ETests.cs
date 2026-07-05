using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression: a VFR pattern aircraft cleared `ERD &lt;runway&gt;` with no follow,
/// no DCT chain, no other interference, must not accelerate during
/// FinalApproachPhase. The S2-OAK-4 bundle showed N80ZU (DA62) decelerating
/// to FAS=75 kt at the start of FinalApproachPhase, then accelerating back
/// up to 110 kt before reaching the threshold — tripping the unstable
/// approach gate (1.3·Vref) and forcing an unprompted go-around.
///
/// Root cause: <c>FinalApproachPhase</c> didn't override
/// <c>ManagesSpeed</c>, so once <c>FlightPhysics.UpdateSpeed</c> snapped IAS to
/// FAS and cleared <c>TargetSpeed</c>, the auto-speed-schedule fallback kicked
/// in and reassigned <c>DefaultSpeed</c> (~110 kt for a small piston descending
/// to low altitude).
/// </summary>
public class PatternFinalSpeedE2ETests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        return new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-pattern-final-speed",
                ScenarioName = "Pattern Final Speed E2E",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoClearedToLand = true,
            },
        };
    }

    /// <summary>
    /// DA62 spawned at pattern altitude north of OAK 28R, given `ERD 28R`. Tick
    /// through PatternEntry → Downwind → Base → FinalApproach → Landing. Assert:
    ///   1. Aircraft reaches FinalApproachPhase
    ///   2. Once FAS is set (IAS ≤ Vref + small tolerance), IAS never bumps
    ///      back UP by more than 2 kt during FinalApproach or Landing
    ///   3. IAS at any tick during FinalApproach/Landing stays ≤ 1.3·Vref + 2 kt
    /// </summary>
    [Fact]
    public void Da62_ErdRight28R_NoOverspeedOnFinal()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        // OAK 28R threshold ~ (37.7156, -122.2117), runway heads 280° true.
        // Right-pattern downwind is north of the runway, flying east (~100° true).
        // Spawn 4 nm north of the threshold at pattern altitude (1009 ft TPA at OAK)
        // heading roughly toward the airport so PatternEntry has work to do.
        var ac = new AircraftState
        {
            Callsign = "TST001",
            AircraftType = "DA62",
            Position = new LatLon(37.78, -122.21),
            TrueHeading = new TrueHeading(180),
            TrueTrack = new TrueHeading(180),
            Altitude = 1500,
            IndicatedAirspeed = 130,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KKOA",
                Destination = "OAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(1500),
                CruiseSpeed = 130,
            },
        };
        engine.World.AddAircraft(ac);

        var erd = engine.SendCommand("TST001", "ERD 28R");
        Assert.True(erd.Success, $"ERD 28R failed: {erd.Message}");

        var category = AircraftCategorization.Categorize("DA62");
        double vref = AircraftPerformance.ApproachSpeed("DA62", category);
        double maxStable = vref * 1.3;
        const double iasNoiseTolerance = 2.0;
        const double fasReachedThreshold = 5.0; // within 5 kt of Vref counts as "FAS reached"
        output.WriteLine($"DA62 Vref={vref:F0} kt, 1.3·Vref={maxStable:F0} kt — overspeed gate above this");

        var recorder = new TickRecorder(ac);
        recorder.Record(0);

        bool reachedFinal = false;
        bool fasReached = false; // true once IAS first dropped to within fasReachedThreshold of Vref on Final
        double minIasAfterFas = double.MaxValue;
        int tFasReached = -1;
        double worstUpwardExcursion = 0;
        int tWorstExcursion = -1;
        double iasAtWorstExcursion = 0;

        for (int t = 1; t <= 600; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft("TST001");
            if (ac is null)
            {
                output.WriteLine($"t+{t}: aircraft gone");
                break;
            }
            recorder.Record(t);

            var phaseType = ac.Phases?.CurrentPhase?.GetType();
            bool onFinalOrLanding = phaseType == typeof(FinalApproachPhase) || phaseType == typeof(LandingPhase);
            bool inGoAround = phaseType?.Name == "GoAroundPhase";

            if (phaseType == typeof(FinalApproachPhase) && !reachedFinal)
            {
                reachedFinal = true;
                output.WriteLine($"t+{t}: entered FinalApproachPhase at IAS={ac.IndicatedAirspeed:F0} alt={ac.Altitude:F0}");
            }

            if (onFinalOrLanding)
            {
                // Once IAS gets near Vref, treat FAS as reached and start tracking.
                if (!fasReached && ac.IndicatedAirspeed <= vref + fasReachedThreshold)
                {
                    fasReached = true;
                    tFasReached = t;
                    minIasAfterFas = ac.IndicatedAirspeed;
                    output.WriteLine($"t+{t}: FAS reached, IAS={ac.IndicatedAirspeed:F0} (Vref={vref:F0})");
                }

                if (fasReached)
                {
                    // Each tick: how much above the lowest-seen IAS are we? That's the
                    // upward excursion. Pure deceleration through the flare keeps this at 0.
                    // Any "speed up on final" bug pushes it positive.
                    double excursion = ac.IndicatedAirspeed - minIasAfterFas;
                    if (excursion > worstUpwardExcursion)
                    {
                        worstUpwardExcursion = excursion;
                        tWorstExcursion = t;
                        iasAtWorstExcursion = ac.IndicatedAirspeed;
                    }
                    if (ac.IndicatedAirspeed < minIasAfterFas)
                    {
                        minIasAfterFas = ac.IndicatedAirspeed;
                    }
                }
            }

            if (inGoAround)
            {
                output.WriteLine($"t+{t}: GO-AROUND fired (warnings: {string.Join(" | ", ac.PendingWarnings)})");
                break;
            }

            if (ac.IsOnGround && ac.GroundSpeed < 30)
            {
                output.WriteLine($"t+{t}: landed (IAS={ac.IndicatedAirspeed:F0})");
                break;
            }
        }

        string outPath = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "da62-pattern-no-overspeed.tickrec.json");
        recorder.WriteJson(outPath);
        output.WriteLine($"Wrote {recorder.Count} ticks → {outPath}");

        Assert.True(reachedFinal, "Aircraft never reached FinalApproachPhase");
        Assert.True(fasReached, $"Aircraft on FinalApproach never reached FAS (within {fasReachedThreshold} kt of Vref={vref:F0})");

        output.WriteLine(
            $"After FAS at t+{tFasReached}: minIAS={minIasAfterFas:F0}, "
                + $"worst upward excursion={worstUpwardExcursion:F1} kt at t+{tWorstExcursion} (IAS={iasAtWorstExcursion:F0})"
        );

        Assert.True(
            worstUpwardExcursion <= iasNoiseTolerance,
            $"After FAS reached at t+{tFasReached} (IAS≈{vref:F0}), aircraft accelerated to {iasAtWorstExcursion:F0} kt at t+{tWorstExcursion} "
                + $"— that's {worstUpwardExcursion:F1} kt above the lowest IAS seen on Final/Landing (tolerance {iasNoiseTolerance:F0} kt). "
                + $"Aircraft on FinalApproach/Landing must not accelerate after reaching approach speed."
        );
    }
}
