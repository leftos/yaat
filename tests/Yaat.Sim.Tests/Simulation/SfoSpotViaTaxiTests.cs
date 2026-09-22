using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// SFO's spot lanes T7A / T7 / T7B hang off taxiway A side by side, and only T7's ramp end joins gate E2.
/// A clearance that names a spot before a gate — <c>TAXI A $7B @E2</c> — is a route via that spot's lane to
/// the gate, so the spot is a waypoint and the gate is the destination. It used to be read the other way
/// round: the spot won as the destination, the route stopped on the spot, and the aircraft was parked there
/// with <c>ParkingSpot = "E2"</c> 500 ft from the gate it had been cleared to.
/// </summary>
public class SfoSpotViaTaxiTests(ITestOutputHelper output)
{
    /// <summary>Recorded session the live-dispatch case replays from: S1-SFO-2 Ground Control 28/01.</summary>
    private const string RecordingPath = "TestData/sfo-gc-28-01-spot-lanes-recording.zip";

    /// <summary>An aircraft this far from the gate node is not parked at it, whatever the route claimed.</summary>
    private const double NotAtGateFt = 100.0;

    /// <summary>How close the nose-in stop leaves the centroid to the gate node.</summary>
    private const double AtGateFt = 60.0;

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>
    /// Fails the moment an aircraft is parked somewhere that is not the gate it was cleared to — the defect
    /// this file covers, caught while it happens rather than at the end of the tick budget.
    /// </summary>
    private static void AssertNotParkedAwayFromGate(AircraftState aircraft, GroundNode gate, int second)
    {
        if (aircraft.Phases?.CurrentPhase is not AtParkingPhase)
        {
            return;
        }

        double ft = DistanceFt(aircraft.Position, gate.Position);
        Assert.True(ft <= NotAtGateFt, $"{aircraft.Callsign} parked at t={second}s {ft:F0} ft from gate {gate.Name}");
    }

    [Fact]
    public void TaxiViaSpot7_ToE2_EndsAtParkingE2()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot7 = ground.Layout.FindSpotNodeByName("7");
        GroundNode? gate = ground.Layout.FindParkingByName("E2");
        Assert.True(spot7 is not null, "SFO layout has no spot named '7'");
        Assert.True(gate is not null, "SFO layout has no parking named 'E2'");

        AircraftState aircraft = SfoGroundHarness.SpawnAtJunction(ground, "SKW1", "CRJ7", "A", "T7A");
        CommandResult result = ground.Engine.SendCommand("SKW1", "TAXI A $7 @E2");
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.Equal("E2", route.DestinationParking);
        Assert.Null(route.DestinationSpot);
        Assert.Contains(route.Segments, s => (s.FromNodeId == spot7.Id) || (s.ToNodeId == spot7.Id));

        int arrived = SfoGroundHarness.TickUntil(
            ground.Engine,
            () => aircraft.Phases?.CurrentPhase is AtParkingPhase,
            maxSeconds: 300,
            second => AssertNotParkedAwayFromGate(aircraft, gate, second)
        );

        double finalFt = DistanceFt(aircraft.Position, gate.Position);
        output.WriteLine($"arrived at t={arrived}s, {finalFt:F0} ft from E2, phase {aircraft.Phases?.CurrentPhase?.GetType().Name}");
        Assert.True(arrived > 0, $"SKW1 never reached E2 (last phase {aircraft.Phases?.CurrentPhase?.GetType().Name}, {finalFt:F0} ft away)");
        Assert.True(finalFt <= AtGateFt, $"parked {finalFt:F0} ft from E2; expected within {AtGateFt:F0} ft");
    }

    /// <summary>
    /// The graph has no ramp edge from T7B's end to E2. Either the clearance is refused by naming the via, or
    /// the aircraft gets to E2 some other way — but it is never parked on spot 7B under the gate's name.
    /// </summary>
    [Fact]
    public void TaxiViaSpot7B_ToE2_NeverParksAtSpot()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? gate = ground.Layout.FindParkingByName("E2");
        Assert.True(gate is not null, "SFO layout has no parking named 'E2'");

        AircraftState aircraft = SfoGroundHarness.SpawnAtJunction(ground, "SKW2", "CRJ7", "A", "T7A");
        CommandResult result = ground.Engine.SendCommand("SKW2", "TAXI A $7B @E2");
        output.WriteLine($"result: {result.Success} — {result.Message}");

        if (!result.Success)
        {
            Assert.Contains("via 7B", result.Message);
            return;
        }

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(output, route);

        SfoGroundHarness.TickUntil(
            ground.Engine,
            () => aircraft.Phases?.CurrentPhase is AtParkingPhase,
            maxSeconds: 300,
            second => AssertNotParkedAwayFromGate(aircraft, gate, second)
        );

        double finalFt = DistanceFt(aircraft.Position, gate.Position);
        output.WriteLine($"final: {finalFt:F0} ft from E2, phase {aircraft.Phases?.CurrentPhase?.GetType().Name}");
        Assert.True(finalFt <= AtGateFt, $"the clearance was accepted but left SKW2 {finalFt:F0} ft from E2");
    }

    /// <summary>
    /// The live case from the recorded session: SKW5416 on taxiway A is cleared <c>TAXI A $7B @E2</c> and must
    /// not end up parked on spot 7B.
    /// </summary>
    [Fact]
    public void Replay_Skw5416_TaxiViaSpot7BToE2_NeverParksAtSpot()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);
        engine.Replay(recording, 1390);

        AircraftState? aircraft = engine.FindAircraft("SKW5416");
        Assert.NotNull(aircraft);

        AirportGroundLayout? layout = engine.World.GroundLayout;
        Assert.NotNull(layout);
        GroundNode? gate = layout.FindParkingByName("E2");
        Assert.True(gate is not null, "SFO layout has no parking named 'E2'");

        CommandResult result = engine.SendCommand("SKW5416", "TAXI A $7B @E2");
        output.WriteLine($"result: {result.Success} — {result.Message}");
        if (!result.Success)
        {
            Assert.Contains("via 7B", result.Message);
            return;
        }

        for (int second = 1; second <= 60; second++)
        {
            engine.TickOneSecond();
            AssertNotParkedAwayFromGate(aircraft, gate, second);
        }

        output.WriteLine(
            $"after 60s: {DistanceFt(aircraft.Position, gate.Position):F0} ft from E2, phase {aircraft.Phases?.CurrentPhase?.GetType().Name}"
        );
    }
}
