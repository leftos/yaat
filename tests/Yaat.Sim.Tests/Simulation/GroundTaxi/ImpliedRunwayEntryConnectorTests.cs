using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Unit-level cover for the runway-entry connector a runway clearance implies. The connector is the numbered
/// variant of the clearance's last taxiway the resolver extends onto to reach the cleared runway's bar; it is
/// implied (driven silently) only at the approach end of the assigned runway direction (7110.65 §3-7-2.b NOTE).
/// BZN C3 is a mid-runway intersection bar on 12/30, so neither clearance implies it: it is driven, but warned
/// as an uncleared taxiway. OAK's W1, the full-length 30 entry, stays implied (<see cref="TaxiApproachLegTests"/>).
/// </summary>
public class ImpliedRunwayEntryConnectorTests(ITestOutputHelper output)
{
    private const string Callsign = "DAL2150";
    private const string AircraftType = "A319";

    /// <summary>The BZN starting node on C the scan used.</summary>
    private const int StartNodeId = 253;

    private const double StartHeadingDeg = 320.0;

    private AirportGroundLayout? LoadBznLayout()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new TestAirportGroundData().GetLayout("BZN");
    }

    private static AircraftState MakeAircraft(AirportGroundLayout layout, LatLon position, double headingDeg)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            Declination = MagneticDeclination.GetDeclination(position),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "BZN", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingAfterPushbackPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    private void AssertWarnsC3(string runway)
    {
        AirportGroundLayout? layout = LoadBznLayout();
        if (layout is null)
        {
            return;
        }

        AircraftState aircraft = MakeAircraft(layout, layout.Nodes[StartNodeId].Position, StartHeadingDeg);
        var taxi = new TaxiCommand(Path: ["C"], HoldShorts: [], Expedite: false, DestinationRunway: runway);
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"route: {route.FormatTaxiwaySequence()}");
        output.WriteLine($"result: {result.Message}");

        Assert.EndsWith("C3", route.FormatTaxiwaySequence());
        Assert.Contains("taxiing via C3 — not in the route issued", result.Message);
    }

    [Fact]
    public void TaxiCRwy12_EndsOnMidRunwayC3_WarnsC3AsUncleared() => AssertWarnsC3("12");

    [Fact]
    public void TaxiCRwy30_EndsOnMidRunwayC3_WarnsC3AsUncleared() => AssertWarnsC3("30");
}
