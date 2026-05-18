using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Solo-training pilot-decision go-around (single roll on <see cref="FinalApproachPhase.OnStart"/>).
/// Verifies the RNG-gated branch fires/skips correctly under the documented gates and is
/// deterministic across replays (same RNG seed → same outcome).
/// </summary>
[Collection("NavDbMutator")]
public class FinalApproachSoloGoAroundTests : IDisposable
{
    private readonly IDisposable _navDbScope;

    public FinalApproachSoloGoAroundTests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(DefaultRunway()));
    }

    public void Dispose() => _navDbScope.Dispose();

    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100);

    private static AircraftState MakeAircraftOnFinal(RunwayInfo rwy)
    {
        var threshold = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude);
        var startPos = GeoMath.ProjectPoint(threshold, rwy.TrueHeading.ToReciprocal(), 3.0);
        return new AircraftState
        {
            Callsign = "TEST",
            AircraftType = "B738",
            Position = startPos,
            TrueHeading = rwy.TrueHeading,
            TrueTrack = rwy.TrueHeading,
            Altitude = rwy.ElevationFt + 1000,
            IndicatedAirspeed = 140,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KTEST" },
        };
    }

    private static PhaseContext BuildCtx(AircraftState ac, RunwayInfo rwy, bool soloMode, int probability, SerializableRandom? rng) =>
        new()
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
            AutoClearedToLand = true,
            SoloTrainingMode = soloMode,
            SoloGoAroundProbabilityPercent = probability,
            Rng = rng,
        };

    [Fact]
    public void SoloGoAround_AtFullProbability_TriggersOnStart()
    {
        var rwy = DefaultRunway();
        var ac = MakeAircraftOnFinal(rwy);
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });

        ac.Phases.Start(BuildCtx(ac, rwy, soloMode: true, probability: 100, rng: new SerializableRandom(0)));

        Assert.IsType<GoAroundPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void SoloGoAround_AtZeroProbability_DoesNotTrigger()
    {
        var rwy = DefaultRunway();
        var ac = MakeAircraftOnFinal(rwy);
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });

        ac.Phases.Start(BuildCtx(ac, rwy, soloMode: true, probability: 0, rng: new SerializableRandom(0)));

        Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void SoloGoAround_NotInSoloMode_DoesNotTrigger()
    {
        var rwy = DefaultRunway();
        var ac = MakeAircraftOnFinal(rwy);
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });

        // Probability 100% but solo mode off → branch is bypassed entirely.
        ac.Phases.Start(BuildCtx(ac, rwy, soloMode: false, probability: 100, rng: new SerializableRandom(0)));

        Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void SoloGoAround_NoRng_DoesNotTrigger()
    {
        // Defensive: tests without RNG (rare) must not crash; the OnStart guard
        // also protects against null Rng in case a future PhaseContext caller forgets it.
        var rwy = DefaultRunway();
        var ac = MakeAircraftOnFinal(rwy);
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });

        ac.Phases.Start(BuildCtx(ac, rwy, soloMode: true, probability: 100, rng: null));

        Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);
    }

    [Fact]
    public void SoloGoAround_SameSeed_SameOutcome()
    {
        // Determinism guarantee: snapshot/replay restores RNG state from
        // StateSnapshotDto.Rng, so the roll fires (or doesn't) at the same tick.
        var firstOutcome = RunRoll(probability: 50, seed: 4242);
        var secondOutcome = RunRoll(probability: 50, seed: 4242);

        Assert.Equal(firstOutcome, secondOutcome);

        static bool RunRoll(int probability, int seed)
        {
            var rwy = DefaultRunway();
            var ac = MakeAircraftOnFinal(rwy);
            ac.Phases = new PhaseList { AssignedRunway = rwy };
            ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
            ac.Phases.Start(BuildCtx(ac, rwy, soloMode: true, probability: probability, rng: new SerializableRandom(seed)));
            return ac.Phases.CurrentPhase is GoAroundPhase;
        }
    }

    [Fact]
    public void SoloGoAround_DifferentSeeds_DistinctOutcomes()
    {
        // Sanity: 50% probability across enough varied seeds yields both outcomes.
        // If this ever fails the implementation has silently stopped consuming the RNG.
        bool sawTrigger = false;
        bool sawSkip = false;
        for (int seed = 0; seed < 100 && !(sawTrigger && sawSkip); seed++)
        {
            var rwy = DefaultRunway();
            var ac = MakeAircraftOnFinal(rwy);
            ac.Phases = new PhaseList { AssignedRunway = rwy };
            ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
            ac.Phases.Start(BuildCtx(ac, rwy, soloMode: true, probability: 50, rng: new SerializableRandom(seed)));

            if (ac.Phases.CurrentPhase is GoAroundPhase)
            {
                sawTrigger = true;
            }
            else
            {
                sawSkip = true;
            }
        }

        Assert.True(sawTrigger, "50% probability never triggered across 100 seeds — RNG branch likely broken.");
        Assert.True(sawSkip, "50% probability always triggered across 100 seeds — RNG branch likely broken.");
    }
}
