using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// An airport whose ground data is not loaded yet reads as no layout and is never stood in for by another airport's:
/// <see cref="SimulationEngine.ResolveGroundLayout"/> picks nothing while an airport it looks at is loading, and a site
/// that needs a layout already loaded reports a missing one once, not on every read.
/// </summary>
public class GroundDataLoadingTests
{
    public GroundDataLoadingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void ResolveGroundLayout_OneOfTheAirportsItLooksAtLoading_ReadsNone()
    {
        AircraftState airborne = new()
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Altitude = 3000,
            Position = new LatLon(37.9, -121.9),
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK", Destination = "KSMF" },
        };

        // With both airports loaded the aircraft gets a layout, so the null below is the loading airport's doing.
        var loaded = new SimulationEngine(new PartlyLoadingGroundData(), PushPlanStore.CreateShipped());
        Assert.NotNull(loaded.ResolveGroundLayout(airborne));

        var smfLoading = new SimulationEngine(new PartlyLoadingGroundData("SMF"), PushPlanStore.CreateShipped());
        Assert.Null(smfLoading.ResolveGroundLayout(airborne));
    }

    [Fact]
    public void ReadyLayoutReader_RepeatedReadsOfAnAirportNotLoaded_LogOneError()
    {
        var log = new WarningLogCapture();
        var reader = new ReadyLayoutReader(new PartlyLoadingGroundData("SMF"), log);

        for (int i = 0; i < 5; i++)
        {
            Assert.Null(reader.Read("SMF", "tick"));
            Assert.Null(reader.Read("KSMF", "tick"));
        }

        string error = Assert.Single(log.Warnings);
        Assert.Contains("is not loaded at tick", error, StringComparison.Ordinal);
        Assert.NotNull(reader.Read("OAK", "tick"));
        Assert.Single(log.Warnings);
    }
}
