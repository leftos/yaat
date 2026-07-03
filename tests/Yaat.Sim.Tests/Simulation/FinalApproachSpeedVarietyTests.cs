using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Tests for the per-aircraft final-approach-speed variety: the deterministic distribution of
/// FAS settle distances (<see cref="FinalApproachSpeedVariety"/>), snapshot durability of the
/// stored value, and the resulting behavior — an aircraft with a larger reach gate settles at
/// Vref farther from the threshold.
/// </summary>
public class FinalApproachSpeedVarietyTests(ITestOutputHelper output)
{
    [Fact]
    public void ComputeReachGateNm_IsDeterministicPerCallsign()
    {
        double a1 = FinalApproachSpeedVariety.ComputeReachGateNm("UAL123");
        double a2 = FinalApproachSpeedVariety.ComputeReachGateNm("UAL123");
        double b = FinalApproachSpeedVariety.ComputeReachGateNm("DAL456");

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
    }

    [Fact]
    public void ComputeReachGateNm_AlwaysWithinFloorAndCap()
    {
        foreach (var callsign in SampleCallsigns())
        {
            double gate = FinalApproachSpeedVariety.ComputeReachGateNm(callsign);
            Assert.InRange(gate, FinalApproachSpeedVariety.FloorNm, FinalApproachSpeedVariety.CapNm);
        }
    }

    /// <summary>
    /// The distribution is right-skewed toward the competent floor: most aircraft settle inside
    /// ~3.5 NM, the median sits near ~3.0 NM (≈1000 ft AGL), and a non-empty tail reaches toward
    /// the 5.0 NM cap. Bands are wide enough to be robust to the hash while still catching a
    /// regression that flattens or inverts the skew.
    /// </summary>
    [Fact]
    public void ComputeReachGateNm_IsRightSkewedTowardFloor()
    {
        var gates = SampleCallsigns().Select(FinalApproachSpeedVariety.ComputeReachGateNm).OrderBy(g => g).ToList();

        double median = gates[gates.Count / 2];
        double fractionWithin35 = gates.Count(g => g <= 3.5) / (double)gates.Count;
        double fractionBeyond40 = gates.Count(g => g > 4.0) / (double)gates.Count;
        double fractionNearFloor = gates.Count(g => g <= 2.5) / (double)gates.Count;

        output.WriteLine($"n={gates.Count} min={gates[0]:F2} median={median:F2} max={gates[^1]:F2}");
        output.WriteLine($"<=2.5nm: {fractionNearFloor:P0}  <=3.5nm: {fractionWithin35:P0}  >4.0nm: {fractionBeyond40:P0}");

        Assert.InRange(median, 2.8, 3.3);
        Assert.InRange(fractionWithin35, 0.55, 0.72);
        Assert.InRange(fractionBeyond40, 0.15, 0.35);
        // A meaningful chunk clusters near the tight competent floor.
        Assert.True(fractionNearFloor > 0.15, $"Expected a floor cluster; near-floor fraction was {fractionNearFloor:P0}");
        // The tail genuinely reaches the cap region.
        Assert.True(gates[^1] > 4.5, $"Expected a tail toward the cap; max was {gates[^1]:F2}");
    }

    [Fact]
    public void ReachGate_SurvivesSnapshotRoundTrip()
    {
        double gate = FinalApproachSpeedVariety.ComputeReachGateNm("SWA789");
        var ac = new AircraftState
        {
            Callsign = "SWA789",
            AircraftType = "B738",
            Approach = new AircraftApproachState { FinalApproachFasReachGateNm = gate },
        };

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), null);

        Assert.Equal(gate, restored.Approach.FinalApproachFasReachGateNm);
    }

    [Fact]
    public void NullReachGate_RoundTripsAsNull()
    {
        var ac = new AircraftState { Callsign = "TEST1", AircraftType = "B738" };
        Assert.Null(ac.Approach.FinalApproachFasReachGateNm);

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), null);

        Assert.Null(restored.Approach.FinalApproachFasReachGateNm);
    }

    /// <summary>
    /// An aircraft with a larger reach gate settles at Vref farther from the threshold. Two
    /// otherwise-identical B738s on final — one on the tight 2.0 NM floor, one at 4.0 NM — must
    /// reach FAS at visibly different distances, with the early-slower settling well outside the
    /// tight one and near its own gate.
    /// </summary>
    [Fact]
    public void LargerReachGate_SettlesAtFasFartherOut()
    {
        TestVnasData.EnsureInitialized();

        double floorDist = DistanceAtWhichFasReached(explicitGate: FinalApproachSpeedVariety.FloorNm, varietyEnabled: false);
        double earlyDist = DistanceAtWhichFasReached(explicitGate: 4.0, varietyEnabled: false);

        output.WriteLine($"floor(2.0) settled FAS at {floorDist:F2}nm; early(4.0) settled FAS at {earlyDist:F2}nm");

        // Each settles near its own reach gate (kinematic trigger settles AT the gate).
        Assert.InRange(floorDist, 1.7, 2.6);
        Assert.InRange(earlyDist, 3.6, 4.6);
        // The core property: the larger gate settles meaningfully farther out.
        Assert.True(earlyDist > floorDist + 1.0, $"Early-slower ({earlyDist:F2}nm) should settle well outside the floor aircraft ({floorDist:F2}nm)");
    }

    /// <summary>
    /// Replay-safety contract: when the scenario has variety disabled (pre-feature recordings) and
    /// the aircraft has no stored gate, it settles at the tight competent floor — reproducing the
    /// original uniform behavior so those recordings replay unchanged.
    /// </summary>
    [Fact]
    public void VarietyDisabled_UsesTightFloor()
    {
        TestVnasData.EnsureInitialized();

        double dist = DistanceAtWhichFasReached(explicitGate: null, varietyEnabled: false);

        Assert.InRange(dist, 1.7, 2.6);
    }

    /// <summary>
    /// When variety is enabled and the aircraft has no stored gate, the phase lazily assigns the
    /// deterministic per-callsign distance and settles Vref there. Uses a callsign whose distribution
    /// draw is comfortably outside the floor so the effect is distinguishable.
    /// </summary>
    [Fact]
    public void VarietyEnabled_LazilyAssignsPerCallsignGate()
    {
        TestVnasData.EnsureInitialized();

        string callsign = Enumerable.Range(1, 2000).Select(i => $"AAL{i}").First(c => FinalApproachSpeedVariety.ComputeReachGateNm(c) > 3.5);
        double expectedGate = FinalApproachSpeedVariety.ComputeReachGateNm(callsign);

        double dist = DistanceAtWhichFasReached(explicitGate: null, varietyEnabled: true, callsign: callsign);

        output.WriteLine($"{callsign}: expected gate {expectedGate:F2}nm, settled FAS at {dist:F2}nm");
        Assert.InRange(dist, expectedGate - 0.6, expectedGate + 0.6);
    }

    /// <summary>
    /// Drives a B738 down a stabilized final from 8 NM at 1.5·Vref and returns the distance (NM) at
    /// which its IAS first settles to Vref. <paramref name="explicitGate"/> pre-sets the stored reach
    /// gate; when null, the phase resolves it from <paramref name="varietyEnabled"/>.
    /// </summary>
    private double DistanceAtWhichFasReached(double? explicitGate, bool varietyEnabled, string callsign = "UAL999")
    {
        var rwy = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            heading: 280,
            elevationFt: 9
        );

        const double startDistNm = 8.0;
        double vref = AircraftPerformance.ApproachSpeed("B738", AircraftCategory.Jet);
        double startSpeed = vref * 1.5;
        var startPos = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading.ToReciprocal(), startDistNm);

        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(startPos.Lat, startPos.Lon),
            TrueHeading = rwy.TrueHeading,
            Altitude = GlideSlopeGeometry.AltitudeAtDistance(startDistNm, rwy.ElevationFt),
            IndicatedAirspeed = startSpeed,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
            Approach = new AircraftApproachState { FinalApproachFasReachGateNm = explicitGate },
        };

        var clearance = new ApproachClearance
        {
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = rwy.TrueHeading,
        };
        ac.Phases = new PhaseList
        {
            AssignedRunway = rwy,
            ActiveApproach = clearance,
            LandingClearance = ClearanceType.ClearedToLand,
        };
        ac.Targets.TargetSpeed = startSpeed;

        var phase = new FinalApproachPhase { SkipInterceptCheck = true };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
            AutoClearedToLand = true,
            FinalApproachSpeedVarietyEnabled = varietyEnabled,
        };

        phase.OnStart(ctx);

        for (int tick = 0; tick < 600; tick++)
        {
            phase.OnTick(ctx);
            FlightPhysics.Update(ac, 1.0);

            double dist = GeoMath.DistanceNm(ac.Position, new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude));
            if (ac.IndicatedAirspeed <= vref + 2.0)
            {
                return dist;
            }

            if (dist < 0.3)
            {
                break;
            }
        }

        Assert.Fail(
            $"Aircraft never settled at Vref ({vref:F0}kts) [explicitGate={explicitGate?.ToString("F1") ?? "null"}, variety={varietyEnabled}]"
        );
        return 0;
    }

    private static IEnumerable<string> SampleCallsigns()
    {
        string[] prefixes = ["AAL", "UAL", "DAL", "SWA", "JBU", "ASA", "FFT", "NKS", "N", "SKW"];
        foreach (var prefix in prefixes)
        {
            for (int n = 1; n <= 500; n++)
            {
                yield return $"{prefix}{n}";
            }
        }
    }
}
