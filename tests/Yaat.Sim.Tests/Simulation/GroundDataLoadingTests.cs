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

    /// <summary>
    /// Regression guard: a pick made while one of its airports loads, read by a command and by a timed preset, caches
    /// nothing on the aircraft and reads the source no more than the admission's poll does. It passes on the base as well
    /// (no line of the change that added it is on its path); it pins the no-partial-pick rule for the paths that read it.
    /// </summary>
    [Fact]
    public void RegressionGuard_PickWhileLoading_ReadByACommandAndATimedPreset_CachesNothing()
    {
        var data = new PartlyLoadingGroundData("SMF");
        var engine = new SimulationEngine(data, PushPlanStore.CreateShipped());
        engine.LoadScenario(OakToSmfJson, 42, MagneticDeclination.EvaluationDateUtc);
        AircraftState aircraft = engine.World.FindAircraft("N1")!;
        aircraft.Ground.Layout = null;
        aircraft.Ground.LayoutAirportId = null;

        // OAK is ready and nearer, SMF loading: neither the command's pick nor the preset's takes OAK in SMF's place.
        Assert.True(engine.SendCommand("N1", "FH 260").Success);
        Assert.Contains("SMF", engine.Scenario!.DeferredLayoutAirports);
        int reads = data.ReadCount("SMF");
        for (int i = 0; i < 3; i++)
        {
            engine.TickOneSecond();
        }

        Assert.Empty(engine.Scenario.PresetQueue);
        Assert.Null(aircraft.Ground.Layout);
        Assert.Null(aircraft.Ground.PendingLayoutAirportIds);
        Assert.Null(engine.ResolveGroundLayout(aircraft));

        // The only reads while deferred are the admission's poll, one each second: no pick reads the source.
        Assert.Equal(reads + 3, data.ReadCount("SMF"));

        data.Release("SMF");
        engine.TickOneSecond();
        Assert.NotNull(engine.ResolveGroundLayout(aircraft));
    }

    private const string OakToSmfJson = """
        {
          "id": "pick-while-loading",
          "name": "Pick while loading",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N1",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 37.80, "lon": -122.30 },
                "altitude": 3500,
                "heading": 90,
                "speed": 100
              },
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KSMF",
                "cruiseAltitude": 3500,
                "cruiseSpeed": 100,
                "route": "",
                "remarks": "",
                "aircraftType": "C172"
              },
              "presetCommands": [ { "id": "p1", "command": "FH 270", "timeOffset": 2 } ]
            }
          ]
        }
        """;

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
