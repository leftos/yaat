using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #396: a TAXI issued at a gate whose FIRST cleared taxiway is a neighbouring ramp lane the graph
/// cannot reach from the gate (SFO B20S → M4: M4 only joins M1, ~404 ft away across the M3 lane — the lane a
/// tug would push onto) now drops that leading taxiway, routes the rest, and warns "unable via M4 — … taxiing
/// via M3". The drop is guarded: it only applies to a parking start, only to the first taxiway, only when the
/// resolver reported it unreachable, only within gate-adjacent range, and only when no runway centerline lies
/// between the gate and that lane — a taxiway across the field (SFO 41-15 → A, ~3400 ft, across active
/// runways) must still be rejected outright.
/// </summary>
public class Issue396GateAdjacentTaxiwayDropTests
{
    private readonly ITestOutputHelper _output;

    public Issue396GateAdjacentTaxiwayDropTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? BuildEngine(out AirportGroundLayout? layout) => BuildEngine(out layout, soloTraining: false);

    private SimulationEngine? BuildEngine(out AirportGroundLayout? layout, bool soloTraining)
    {
        layout = null;
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).EnableCategory("GroundCommandHandler", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 0,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "SFO",
                SoloTrainingMode = soloTraining,
            },
        };
    }

    private static AircraftState AddParkedAt(SimulationEngine engine, AirportGroundLayout layout, string callsign, string type, string parking)
    {
        var gate = layout.FindParkingByName(parking);
        Assert.True(gate is not null, $"parking {parking} not found in the SFO layout");

        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = gate.Position,
            TrueHeading = gate.TrueHeading ?? new TrueHeading(0),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            AirportId = "SFO",
            FlightPlan = new AircraftFlightPlan { Departure = "KSFO", Destination = "KLAX" },
        };
        // A scenario spawn at a gate installs AtParkingPhase; without it a TAXI never reaches the tower path.
        var init = AircraftInitializer.InitializeAtParking(gate, 13);
        aircraft.Phases = init.Phases;
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    private static bool Traverses(TaxiRoute route, string twy) =>
        route.Segments.Any(s =>
            s.TaxiwayName.Split([' ', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries)
                .Any(tok => string.Equals(tok, twy, StringComparison.OrdinalIgnoreCase))
        );

    [Fact]
    public void TaxiM4FromB20S_DropsM4AndWarns()
    {
        var engine = BuildEngine(out var layout);
        if (engine is null || layout is null)
        {
            return;
        }

        var aircraft = AddParkedAt(engine, layout, "AAL436", "B77W", "B20S");
        var result = engine.SendCommand("AAL436", "TAXI M4 M1 A H GL L LF F 28L HS 1L");
        _output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        _output.WriteLine("route: " + string.Join(" ", route.Segments.Select(s => s.TaxiwayName)));
        Assert.True(Traverses(route, "M1"), "route must reach M1");
        Assert.True(Traverses(route, "A"), "route must reach A");
        Assert.False(Traverses(route, "M4"), "M4 is unreachable from the gate and must not appear in the route");
        Assert.Contains(route.Warnings, w => w.Contains("unable via M4", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("unable via M4", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("taxiing via M3", result.Message, StringComparison.OrdinalIgnoreCase);

        // The rest of the clearance is honored: hold short of 1L en route, line-up stop at 28L.
        Assert.Contains(
            route.HoldShortPoints,
            h => h.Reason == HoldShortReason.ExplicitHoldShort && h.TargetName is { } name && RunwayIdentifier.Parse(name).Contains("1L")
        );
        Assert.Contains(
            route.HoldShortPoints,
            h => h.Reason == HoldShortReason.DestinationRunway && h.TargetName is { } name && RunwayIdentifier.Parse(name).Contains("28L")
        );
    }

    [Fact]
    public void TaxiM4FromB20S_SoloReadbackSaysUnableM4AndTheLaneUsed()
    {
        var engine = BuildEngine(out var layout, soloTraining: true);
        if (engine is null || layout is null)
        {
            return;
        }

        var aircraft = AddParkedAt(engine, layout, "AAL436", "B77W", "B20S");
        var result = engine.SendCommand("AAL436", "TAXI M4 M1 A A1 1R");
        Assert.True(result.Success, result.Message);

        // Solo transmissions queue on the aircraft; the host's frequency scheduler drains them, so the
        // queued readback is the observable here. It must be the route the aircraft will actually taxi.
        var readback = aircraft.PendingPilotTransmissions.FirstOrDefault(t => t.Kind == Yaat.Sim.Pilot.PilotTransmissionKind.Readback);
        Assert.True(readback is not null, "the solo pilot must read the taxi clearance back");
        _output.WriteLine($"readback: {readback.Text} / {readback.SpeechText}");
        Assert.StartsWith("unable M4, ", readback.Text);
        Assert.Contains("M1 A A1", readback.Text);
        Assert.StartsWith("unable mike four, ", readback.SpeechText);
        Assert.DoesNotContain("mike four", readback.SpeechText["unable mike four, ".Length..]);
    }

    [Fact]
    public void TaxiAcrossRunwaysFromGate_StillRejected()
    {
        var engine = BuildEngine(out var layout);
        if (engine is null || layout is null)
        {
            return;
        }

        AddParkedAt(engine, layout, "N70234", "C182", "41-15");
        var result = engine.SendCommand("N70234", "TAXI A E 28R HS E");
        _output.WriteLine($"result: {result.Success} — {result.Message}");

        Assert.False(
            result.Success,
            $"taxiway A lies across active runways from 41-15 — the clearance must be rejected, not rerouted: {result.Message}"
        );
        Assert.Contains("via A", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
