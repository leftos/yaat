using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

public sealed class AirspaceRespectTests
{
    [Fact]
    public void FaaTrainingPrimaryFixture_LoadsSfoBravoAndOakCharlie()
    {
        var db = AirspaceDatabase.Default;

        Assert.Contains(db.Volumes, v => v.Ident == "SFO" && v.Class == AirspaceClass.Bravo);
        Assert.Contains(db.Volumes, v => v.Ident == "OAK" && v.Class == AirspaceClass.Charlie);
        Assert.Contains(db.FindContaining(new LatLon(37.6213, -122.3790), 1000), v => v.Ident == "SFO" && v.Class == AirspaceClass.Bravo);
        Assert.Contains(db.FindContaining(new LatLon(37.7213, -122.2208), 1000), v => v.Ident == "OAK" && v.Class == AirspaceClass.Charlie);
    }

    [Fact]
    public void ProjectedEntry_FindsOakCharlieForInboundVfrAircraft()
    {
        var ac = CreateAirborneVfr(new LatLon(37.7213, -122.4200), heading: 90, altitude: 2000, speed: 600);

        var crossing = AirspaceDatabase.Default.FindFirstProjectedEntry(ac, lookaheadSeconds: 180);

        Assert.NotNull(crossing);
        Assert.Equal(AirspaceClass.Charlie, crossing.Volume.Class);
        Assert.Equal("OAK", crossing.Volume.Ident);
    }

    [Fact]
    public void PilotProactive_InsertsCharlieHoldUntilControllerAcknowledges()
    {
        var ac = CreateAirborneVfr(new LatLon(37.7213, -122.4200), heading: 90, altitude: 2000, speed: 600);
        ac.HasMadeInitialContact = true;
        var scenario = new SimScenarioState
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = true,
            PrimaryAirportId = "OAK",
            StudentPositionType = "APP",
        };

        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);

        var phase = Assert.IsType<AirspaceBoundaryHoldPhase>(Assert.Single(ac.Phases!.Phases));
        Assert.Equal(AirspaceClass.Charlie, phase.AirspaceClass);
    }

    [Fact]
    public void PilotProactive_DoesNotHoldCharlieAfterTwoWayCommsGate()
    {
        var ac = CreateAirborneVfr(new LatLon(37.7213, -122.4200), heading: 90, altitude: 2000, speed: 180);
        ac.HasMadeInitialContact = true;
        ac.HasControllerAcknowledgedInitialContact = true;
        var scenario = new SimScenarioState
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = true,
            PrimaryAirportId = "OAK",
            StudentPositionType = "APP",
        };

        PilotProactive.TickAirspaceBoundaryRespect(ac, scenario, AirspaceDatabase.Default, LookupAirport);

        Assert.Null(ac.Phases);
    }

    [Fact]
    public void ClearedBravoCommand_SetsBravoGate()
    {
        var ac = CreateAirborneVfr(new LatLon(37.60, -122.55), heading: 90, altitude: 3500, speed: 160);
        var parsed = CommandParser.Parse("CLBRV");
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);

        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(Random.Shared, soloTrainingMode: true));

        Assert.True(result.Success);
        Assert.True(ac.IsClearedIntoBravo);
    }

    [Theory]
    [InlineData("STBY")]
    [InlineData("ROGER")]
    public void PilotContactAckCommand_SetsTwoWayCommsGate(string command)
    {
        var ac = CreateAirborneVfr(new LatLon(37.7213, -122.4200), heading: 90, altitude: 2000, speed: 160);
        var parsed = CommandParser.Parse(command);
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);

        var result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(Random.Shared, soloTrainingMode: true));

        Assert.True(result.Success);
        Assert.True(ac.HasControllerAcknowledgedInitialContact);
    }

    private static AircraftState CreateAirborneVfr(LatLon position, double heading, double altitude, double speed) =>
        new()
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Position = position,
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = speed,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" },
        };

    private static LatLon? LookupAirport(string ident) =>
        ident switch
        {
            "OAK" or "KOAK" => new LatLon(37.7213, -122.2208),
            "SFO" or "KSFO" => new LatLon(37.6213, -122.3790),
            _ => null,
        };
}
