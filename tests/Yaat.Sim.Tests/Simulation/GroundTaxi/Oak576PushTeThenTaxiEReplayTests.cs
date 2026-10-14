using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-576, replayed from the OAK S1-OAK-5 bundle: SWA919's <c>PUSH TE</c> off stand 20 stopped beside SWA2436, parked at
/// stand 23, and the <c>TAXI $E</c> at t=815 routed through stand 23's parking node, where the conflict detector held SWA919
/// for the rest of the session with no reason on its datablock. The taxi now starts clear of the occupied stand and gets going.
/// </summary>
public class Oak576PushTeThenTaxiEReplayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak576-push-te-parked-neighbour-recording.yaat-bug-report-bundle.zip";
    private const string Callsign = "SWA919";
    private const string ParkedCallsign = "SWA2436";
    private const string OccupiedStand = "23";

    /// <summary>One second after the recorded <c>TAXI $E</c> (t=815.0).</summary>
    private const double AfterTaxiSecond = 816;

    /// <summary>The last second checked: the recording stalled SWA919 from about t=840 for good.</summary>
    private const int LastSecond = 960;

    /// <summary>How far SWA919 must get from where the TAXI found it: well past the 64 ft to stand 23 it stalled inside.</summary>
    private const double MinDistanceCoveredFt = 300.0;

    [Fact]
    public void PushTeThenTaxiE_NeverStallsAgainstParkedNeighbour()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        TestVnasData.EnsureInitialized();
        var groundData = RecordingGroundData.Evergreen("TAXI $E starting clear of the occupied stand 23");
        if ((recording is null) || (TestVnasData.NavigationDb is null) || (groundData.GetLayout("OAK") is not { } layout))
        {
            return;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.Replay(recording, AfterTaxiSecond);

        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        GroundNode stand = layout.FindParkingByName(OccupiedStand) ?? throw new InvalidOperationException($"OAK has no stand {OccupiedStand}");
        output.WriteLine($"route at t={AfterTaxiSecond}: {route.ToSummary()}");
        Assert.DoesNotContain(route.Segments, s => (s.FromNodeId == stand.Id) || (s.ToNodeId == stand.Id));

        LatLon taxiFrom = aircraft.Position;
        double farthestFt = 0;
        for (int t = (int)AfterTaxiSecond + 1; t <= LastSecond; t++)
        {
            engine.ReplayOneSecond();
            aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
            farthestFt = Math.Max(farthestFt, GeoMath.DistanceNm(taxiFrom, aircraft.Position) * GeoMath.FeetPerNm);
            if ((t % 10) == 0)
            {
                output.WriteLine(
                    $"t={t} {aircraft.Phases?.CurrentPhase?.Name} gs={aircraft.GroundSpeed:F1} lim={aircraft.Ground.SpeedLimit} "
                        + $"yield={aircraft.Ground.AutoYieldTarget} from={farthestFt:F0}ft"
                );
            }
        }

        Assert.True(
            farthestFt >= MinDistanceCoveredFt,
            $"{Callsign} got only {farthestFt:F0} ft from where TAXI $E found it by t={LastSecond}: stalled beside {ParkedCallsign}"
        );
    }
}
