using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Preset/deferred strip and transparent-track commands must not silently fail.
///
/// Repro from the "S1-SFO-2 | Ground Control 28/01" bug bundle: every departure carries the preset
/// <c>WAIT 2 ANNOTATE 10 ✓</c> (auto-check the printed strip). When the WAIT expired the controller saw
/// <c>[Deferred] could not apply: Unable to Annotate strip box 1: ✓</c> — because strip state is
/// host-owned and <see cref="CommandDispatcher.ApplyCommand"/> had no arm for strip commands, so
/// preset/deferred strip commands hit the "no dispatcher arm" default. The same
/// <see cref="SimulationEngine"/> deferred path lacked the <c>TryDispatchImmediateTrackPreset</c> guard
/// that immediate presets use, so a deferred transparent-track command (e.g. <c>SP1</c>) failed
/// identically.
///
/// The fix queues strip commands onto <see cref="AircraftState.PendingStripDispatches"/> (drained by the
/// host into <c>StripCommandHandler</c>) and routes deferred all-track payloads through the track engine.
/// </summary>
public class DeferredPresetStripAndTrackDispatchTests
{
    private const string Checkmark = "✓";

    public DeferredPresetStripAndTrackDispatchTests()
    {
        // Physics/dispatch read data-backed singletons once the aircraft is ticked.
        TestVnasData.EnsureInitialized();
    }

    private static SimulationEngine BuildEngine() =>
        new(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 0,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "SFO",
            },
        };

    private static AircraftState AddParked(SimulationEngine engine, string callsign)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "A319",
            Position = new LatLon(37.6189, -122.3750),
            TrueHeading = new TrueHeading(277),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan(),
        };
        engine.World.AddAircraft(ac);
        return ac;
    }

    [Fact]
    public void ImmediateAnnotatePreset_QueuesStripDispatch_NoWarning()
    {
        var engine = BuildEngine();
        var ac = new AircraftState
        {
            Callsign = "DAL2272",
            AircraftType = "A319",
            Position = new LatLon(37.6189, -122.3750),
            FlightPlan = new AircraftFlightPlan(),
        };

        var loaded = new LoadedAircraft { State = ac, PresetCommands = [new PresetCommand { Command = $"ANNOTATE 10 {Checkmark}", TimeOffset = 0 }] };
        engine.DispatchPresetCommands(loaded);

        var queued = Assert.Single(ac.PendingStripDispatches);
        var annotate = Assert.IsType<StripAnnotateCommand>(queued);
        Assert.Equal("1", annotate.Box); // ANNOTATE 10 aliases to box 1
        Assert.Equal(Checkmark, annotate.Text);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void DeferredAnnotatePreset_QueuesStripDispatch_NoWarning()
    {
        var engine = BuildEngine();
        var ac = AddParked(engine, "DAL2272");

        var stripDispatches = new List<(string Callsign, ParsedCommand Command)>();
        engine.StripDispatchRequested += (cs, cmd) => stripDispatches.Add((cs, cmd));
        var warnings = new List<(string Callsign, string Warning)>();
        engine.WarningEmitted += (cs, w) => warnings.Add((cs, w));

        var loaded = new LoadedAircraft
        {
            State = ac,
            PresetCommands = [new PresetCommand { Command = $"WAIT 2 ANNOTATE 10 {Checkmark}", TimeOffset = 0 }],
        };
        engine.DispatchPresetCommands(loaded);

        // Deferred behind the WAIT — nothing dispatched yet.
        Assert.Single(ac.DeferredDispatches);
        Assert.Empty(stripDispatches);

        for (int t = 0; t < 4; t++)
        {
            engine.TickOneSecond();
        }

        var fired = Assert.Single(stripDispatches);
        Assert.Equal("DAL2272", fired.Callsign);
        var annotate = Assert.IsType<StripAnnotateCommand>(fired.Command);
        Assert.Equal("1", annotate.Box);
        Assert.Equal(Checkmark, annotate.Text);
        Assert.DoesNotContain(warnings, w => w.Warning.Contains("could not apply", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeferredTransparentTrackPreset_AppliesToTrack_NoWarning()
    {
        var engine = BuildEngine();
        var ac = AddParked(engine, "DAL2272");

        var warnings = new List<(string Callsign, string Warning)>();
        engine.WarningEmitted += (cs, w) => warnings.Add((cs, w));

        var loaded = new LoadedAircraft { State = ac, PresetCommands = [new PresetCommand { Command = "WAIT 2 SP1 AB", TimeOffset = 0 }] };
        engine.DispatchPresetCommands(loaded);
        Assert.Single(ac.DeferredDispatches);

        for (int t = 0; t < 4; t++)
        {
            engine.TickOneSecond();
        }

        Assert.Equal("AB", ac.Stars.Scratchpad1);
        Assert.DoesNotContain(warnings, w => w.Warning.Contains("could not apply", StringComparison.OrdinalIgnoreCase));
    }
}
