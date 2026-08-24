using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #396 ("Planes not pushing back"): two gate departures in
/// S2-SFO-3 | High Intensity never left their gates, and every departure's <c>WAIT 30 STRIP Local</c>
/// preset failed with "[Deferred] could not apply".
///
/// Recording: S2-SFO-3 | High Intensity (ZOA), trimmed to t ≤ 700 s. The scenario scripts no PUSH — each
/// gate departure is <c>WAIT 2 ANNOTATE 10 ✓; WAIT 1 ANNOTATE 11 S; TAXI …; WAIT 30 STRIP Local</c>.
/// <list type="bullet">
/// <item>ASA811 (A332, gate B4, <c>TAXI M3 M2 A H B M1 1L</c>, spawns t=645): the start bridge's 3-hop BFS only
/// reached an M3 corner-arc node whose sole M3 edge was a U-turn, so resolution failed with "No valid path
/// from M3 to M2 — transition infeasible" and the aircraft stayed at the gate.</item>
/// <item>AAL436 (B77W, gate B20S, <c>TAXI M4 M1 A H GL L LF F 28L HS 1L</c>, spawns t=620): ramp lane M4 has no
/// RAMP edge in the layout (it only joins M1), so resolution failed "Cannot taxi via M4". M3/M4/M5 are parallel
/// taxilanes on one ramp, so the pilot cuts across the apron onto M4 and taxis the clearance as issued.</item>
/// <item>Every departure's deferred <c>STRIP Local</c> hit the phase gate ("aircraft is parked with engines
/// off …" / "aircraft is taxiing …") because <c>StripMove</c> was not phase-transparent.</item>
/// </list>
/// The preset TAXI failures themselves never reached the terminal — only the server log carried them.
/// </summary>
public class Issue396GateTaxiPresetTests
{
    private const string RecordingPath = "TestData/issue396-gate-taxi-presets-recording.zip";

    private readonly ITestOutputHelper _output;

    public Issue396GateTaxiPresetTests(ITestOutputHelper output)
    {
        _output = output;
        // Pin singletons before any [Fact] body runs (parallel-class race guard).
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? BuildEngine()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).EnableCategory("GroundCommandHandler", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private static bool Traverses(TaxiRoute route, string twy) =>
        route.Segments.Any(s =>
            s.TaxiwayName.Split([' ', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries)
                .Any(tok => string.Equals(tok, twy, StringComparison.OrdinalIgnoreCase))
        );

    private static void AssertTaxiing(AircraftState aircraft, List<TerminalEntry> terminal)
    {
        var warnings = string.Join(" | ", terminal.Where(e => e.Callsign == aircraft.Callsign && e.Kind == "Warning").Select(e => e.Message));
        string phase = aircraft.Phases?.CurrentPhase?.Name ?? "(no phase)";
        Assert.True(
            aircraft.Phases?.CurrentPhase is TaxiingPhase,
            $"{aircraft.Callsign} should be taxiing after its preset TAXI but is {phase}; warnings: {warnings}"
        );
    }

    [Fact]
    public void Asa811_TaxiPresetFromGateB4_Taxis()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += terminal.Add;

        engine.Replay(recording, 646);

        var aircraft = engine.FindAircraft("ASA811");
        Assert.NotNull(aircraft);
        AssertTaxiing(aircraft, terminal);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        _output.WriteLine("ASA811 route: " + string.Join(" ", route.Segments.Select(s => s.TaxiwayName)));
        Assert.True(Traverses(route, "M3"), "route must leave the gate along M3");
        Assert.True(Traverses(route, "M2"), "route must continue onto M2");
        Assert.DoesNotContain(terminal, e => e.Callsign == "ASA811" && e.Message.Contains("could not apply", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Aal436_TaxiPresetFromGateB20S_RepositionsOntoM4()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += terminal.Add;

        engine.Replay(recording, 621);

        var aircraft = engine.FindAircraft("AAL436");
        Assert.NotNull(aircraft);
        AssertTaxiing(aircraft, terminal);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        _output.WriteLine("AAL436 route: " + string.Join(" ", route.Segments.Select(s => s.TaxiwayName)));
        Assert.True(route.Segments[0].FromNodeId < 0, "the scripted TAXI M4 must start with a free-space cut across the ramp onto M4");
        Assert.True(Traverses(route, "M4"), "route must taxi along M4 as cleared");
        Assert.True(Traverses(route, "M1"), "route must reach M1 after leaving the gate");
        Assert.True(Traverses(route, "A"), "route must reach A");
        Assert.DoesNotContain(route.Warnings, w => w.Contains("unable", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(terminal, e => e.Callsign == "AAL436" && e.Message.Contains("unable via M4", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(terminal, e => e.Callsign == "AAL436" && e.Message.Contains("could not apply", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StripLocalPresets_ApplyAfterWait_NoWarning()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += terminal.Add;
        var strips = new List<(string Callsign, ParsedCommand Command)>();
        engine.StripDispatchRequested += (cs, cmd) => strips.Add((cs, cmd));

        // AAL436's WAIT 30 fires at ≈650, ASA811's at ≈675.
        engine.Replay(recording, 621);
        for (int t = 622; t <= 680; t++)
        {
            engine.ReplayOneSecond();
        }

        foreach (var callsign in new[] { "AAL436", "ASA811" })
        {
            var move = strips.FirstOrDefault(s => s.Callsign == callsign && s.Command is StripMoveCommand).Command as StripMoveCommand;
            Assert.True(move is not null, $"{callsign}: deferred STRIP Local never reached the strip dispatch queue");
            Assert.Contains("Local", move.Tokens);
            Assert.DoesNotContain(terminal, e => e.Callsign == callsign && e.Message.Contains("could not apply", StringComparison.OrdinalIgnoreCase));
        }
    }
}
