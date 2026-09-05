using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E for a helicopter told to land at a parking spot from miles out.
///
/// Recording: S2-OAK-5 (2) "Practical Exam Preparation / Advanced Concepts" (ZOA, OAK). N20662 (R22)
/// is hovering at 500 ft MSL about 9.6 nm west-northwest of the north-field ramp spot SIG1 when the
/// instructor issues <c>LAND @SIG1</c> at t=2298.
///
/// Observed: the air-taxi profile was installed for the whole transit, so the R22 dropped to 100 ft
/// AGL within 25 s and crossed nine miles of bay at 40 kt and 100 ft.
///
/// Expected: air taxi is a ground movement on the airport (AIM §4-3-17.b; 7110.65 §3-11-1.c NOTE);
/// a landing clearance to a spot from off-field is flown as an approach (§3-11-6) — hold
/// the present altitude, then descend on a 6° final to the spot, slowing through 90 and 60 kt. The
/// R22 is already at the rotorcraft pattern altitude (AIM §4-3-3.a.3), so it holds 500 ft until final.
///
/// The recording's later instructor <c>DEL</c> (t=2572) is outside the replayed range; the assertion
/// ticks physics-only after the LAND.
/// </summary>
public class HelicopterLandSpotFromDistanceTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s2-oak5-follow-heli-recording.zip";
    private const string Callsign = "N20662";
    private const string Spot = "SIG1";

    // Restore at the HPP hover (t=2295) and replay through the LAND @SIG1 (t=2298).
    private const int RestoreAtSeconds = 2295;
    private const int ReplayStopSeconds = 2300;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("HelicopterApproachPhase", LogLevel.Trace).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    private static LatLon? SpotPosition()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        var node = layout?.FindSpotByName(Spot);
        return node?.Position;
    }

    [Fact]
    public void N20662_LandAtSig1FromNineMiles_HoldsAltitudeUntilFinal()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        if (archive is null)
        {
            return;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            var engine = BuildEngine();
            var spot = SpotPosition();
            if (engine is null || spot is null)
            {
                return;
            }

            engine.Replay(recording, 0);
            var snapshot = archive.ReadSnapshotAt(RestoreAtSeconds);
            if (snapshot is null)
            {
                output.WriteLine($"No snapshot near t={RestoreAtSeconds} — skipping");
                return;
            }

            engine.RestoreFromSnapshot(snapshot.State);
            int t0 = (int)snapshot.ElapsedSeconds;

            var pre = engine.FindAircraft(Callsign);
            Assert.NotNull(pre);
            Assert.IsType<VfrHoldPhase>(pre.Phases?.CurrentPhase);
            double startAltitude = pre.Altitude;
            double fieldElevation = TestVnasData.NavigationDb!.GetAirportElevation("OAK") ?? 0;

            for (int t = t0 + 1; t <= ReplayStopSeconds; t++)
            {
                engine.ReplayOneSecond();
            }

            var afterLand = engine.FindAircraft(Callsign);
            Assert.NotNull(afterLand);
            Assert.IsType<HelicopterApproachPhase>(afterLand.Phases?.CurrentPhase);

            double minAltitudeBeyondOneNm = double.PositiveInfinity;
            double minSpeedBeyondThreeNm = double.PositiveInfinity;
            bool underWay = false;
            double minAltitudeBeyondTenthNm = double.PositiveInfinity;
            bool reachedLanding = false;
            double minDist = double.PositiveInfinity;

            for (int t = ReplayStopSeconds + 1; t <= ReplayStopSeconds + 900; t++)
            {
                engine.TickOneSecond();
                var ac = engine.FindAircraft(Callsign);
                if (ac is null)
                {
                    break;
                }

                double dist = GeoMath.DistanceNm(ac.Position, spot.Value);
                minDist = Math.Min(minDist, dist);
                if (dist > 1.0)
                {
                    minAltitudeBeyondOneNm = Math.Min(minAltitudeBeyondOneNm, ac.Altitude);
                }

                // The R22 starts from a hover, so judge the en-route speed only once it has accelerated through 60 kt.
                underWay |= ac.IndicatedAirspeed >= 60;
                if (underWay && (dist > 3.0))
                {
                    minSpeedBeyondThreeNm = Math.Min(minSpeedBeyondThreeNm, ac.IndicatedAirspeed);
                }

                if (dist > 0.1)
                {
                    minAltitudeBeyondTenthNm = Math.Min(minAltitudeBeyondTenthNm, ac.Altitude);
                }

                if (ac.Phases?.CurrentPhase is HelicopterLandingPhase or AtParkingPhase)
                {
                    reachedLanding = true;
                    output.WriteLine(
                        $"t={t}: {Callsign} handed off to {ac.Phases.CurrentPhase.GetType().Name} at {dist:F3} nm, alt={ac.Altitude:F0}"
                    );
                    break;
                }
            }

            output.WriteLine(
                $"start alt={startAltitude:F0}; min alt beyond 1 nm={minAltitudeBeyondOneNm:F0}; min IAS beyond 3 nm={minSpeedBeyondThreeNm:F0}; "
                    + $"min alt beyond 0.1 nm={minAltitudeBeyondTenthNm:F0}; min dist={minDist:F3}"
            );

            Assert.True(
                minAltitudeBeyondOneNm >= startAltitude - 50,
                $"{Callsign} descended to {minAltitudeBeyondOneNm:F0} ft while still more than 1 nm from {Spot} — an inbound helicopter holds its "
                    + "altitude until final (7110.65 §3-11-6; air taxi is a ground movement, AIM §4-3-17.b)."
            );
            Assert.True(
                minSpeedBeyondThreeNm >= 60,
                $"{Callsign} slowed to {minSpeedBeyondThreeNm:F0} kt more than 3 nm out — "
                    + "the 40 kt air-taxi speed belongs on the field, not en route."
            );
            Assert.True(
                minAltitudeBeyondTenthNm >= fieldElevation + 90,
                $"{Callsign} was below 100 ft AGL ({minAltitudeBeyondTenthNm:F0} ft) before the last 0.1 nm — "
                    + "the final descends on a 6° path to the spot."
            );
            Assert.True(reachedLanding, $"{Callsign} never reached {Spot} within the tick window (min distance {minDist:F3} nm).");
            Assert.True(minDist < 0.05, $"{Callsign} should arrive over {Spot} (within 0.05 nm) but got no closer than {minDist:F3} nm.");
        }
    }
}
