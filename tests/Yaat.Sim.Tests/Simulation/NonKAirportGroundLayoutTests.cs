using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-2909: an aircraft at a field whose ICAO id is not "K" + its FAA id (PHNL / HNL) must get the scenario's
/// primary layout, which is keyed by the FAA id. A coordinate-placed aircraft filed PHNL asked the ground data for
/// "PHNL", found nothing, and refused PUSH with "No airport ground layout available".
/// </summary>
public class NonKAirportGroundLayoutTests(ITestOutputHelper output)
{
    [Fact]
    public void ResolveGroundLayout_IcaoFiledAircraft_GetsThePrimaryFaaLayout()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        using IDisposable _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        var groundData = new TestAirportGroundData();
        AirportGroundLayout? hnl = groundData.GetLayout("HNL");
        Assert.NotNull(hnl);

        // Ground data serving only the exact FAA id, as the server's FAA-keyed layouts do.
        var engine = new SimulationEngine(new ExactIdGroundData("HNL", hnl), PushPlanStore.CreateShipped());
        engine.World.GroundLayout = hnl;

        var aircraft = new AircraftState
        {
            Callsign = "MKU8876",
            AircraftType = "C208",
            Position = new LatLon(21.3245, -157.9251),
            TrueHeading = new TrueHeading(220),
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                Departure = "PHNL",
                Destination = "PHOG",
            },
            AirportId = "PHNL",
        };

        Assert.Same(hnl, engine.ResolveGroundLayout(aircraft));

        aircraft.Ground.Layout = hnl;
        SimulationEngine.DropLayoutOfAnotherField(aircraft);
        Assert.Same(hnl, aircraft.Ground.Layout);
    }

    private sealed class ExactIdGroundData(string airportId, AirportGroundLayout layout) : IAirportGroundData
    {
        public AirportGroundLayout? GetLayout(string id) => string.Equals(id, airportId, StringComparison.Ordinal) ? layout : null;

        public string? GetSourceGeoJson(string id) => null;
    }

    [Theory]
    [InlineData("PHNL", "HNL")]
    [InlineData("PHOG", "OGG")]
    [InlineData("KOAK", "OAK")]
    public void TestGroundData_IcaoId_FindsTheFaaIdFixture(string airportId, string expectedLayoutId)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airportId);

        Assert.NotNull(layout);
        Assert.Equal(expectedLayoutId, layout.AirportId, ignoreCase: true);
    }
}
