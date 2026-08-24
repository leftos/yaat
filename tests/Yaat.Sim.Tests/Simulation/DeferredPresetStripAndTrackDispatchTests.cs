using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
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

    /// <summary>
    /// A strip move is host-owned bookkeeping with no effect on the aircraft, so a phase that rejects
    /// unrecognised commands (parked, taxiing, holding short) must let it through to the strip queue.
    /// The S2-SFO-3 scenario's <c>WAIT 30 STRIP Local</c> preset fired while every departure was taxiing
    /// and was refused with "aircraft is taxiing; only HOLD/RES, CROSS, HS, SPD, or FOLLOWG apply".
    /// </summary>
    [Theory]
    [InlineData("STRIP Local", typeof(StripMoveCommand))]
    [InlineData("SCAN NCT/NCT", typeof(StripScanCommand))]
    [InlineData("HSM 2", typeof(HalfStripMoveCommand))]
    public void DeferredStripPreset_WhilePhaseGated_QueuesStripDispatch_NoWarning(string command, Type expectedType)
    {
        var engine = BuildEngine();
        var ac = AddParked(engine, "DAL2272");
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var stripDispatches = new List<(string Callsign, ParsedCommand Command)>();
        engine.StripDispatchRequested += (cs, cmd) => stripDispatches.Add((cs, cmd));
        var warnings = new List<(string Callsign, string Warning)>();
        engine.WarningEmitted += (cs, w) => warnings.Add((cs, w));

        var loaded = new LoadedAircraft { State = ac, PresetCommands = [new PresetCommand { Command = $"WAIT 2 {command}", TimeOffset = 0 }] };
        engine.DispatchPresetCommands(loaded);
        Assert.Single(ac.DeferredDispatches);

        for (int t = 0; t < 4; t++)
        {
            engine.TickOneSecond();
        }

        var fired = Assert.Single(stripDispatches);
        Assert.IsType(expectedType, fired.Command);
        Assert.DoesNotContain(warnings, w => w.Warning.Contains("could not apply", StringComparison.OrdinalIgnoreCase));
        Assert.IsType<AtParkingPhase>(ac.Phases.CurrentPhase);
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

    // --- Issue #396: STRIP <bay> (and the rest of the strip family) must be phase-transparent ---

    private static AircraftState AddParkedAtGate(SimulationEngine engine, string callsign, string parking)
    {
        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        var gate = layout.FindParkingByName(parking);
        Assert.True(gate is not null, $"parking {parking} not found in the SFO layout");

        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
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
        ac.Phases = init.Phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, layout));
        engine.World.AddAircraft(ac);
        return ac;
    }

    private static void AssertStripMoveDispatched(
        List<(string Callsign, ParsedCommand Command)> stripDispatches,
        List<TerminalEntry> terminal,
        string callsign
    )
    {
        var move = Assert.IsType<StripMoveCommand>(Assert.Single(stripDispatches).Command);
        Assert.Contains("Local", move.Tokens);
        Assert.DoesNotContain(terminal, e => e.Callsign == callsign && e.Message.Contains("could not apply", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeferredStripMovePreset_AtParking_QueuesStripDispatch_NoWarning()
    {
        if (TestVnasData.NavigationDb is null || new TestAirportGroundData().GetLayout("SFO") is null)
        {
            return;
        }

        // At a real gate with AtParkingPhase installed — the phase whose gate rejected the deferred STRIP.
        var engine = BuildEngine();
        var ac = AddParkedAtGate(engine, "DAL2272", "B4");

        var stripDispatches = new List<(string Callsign, ParsedCommand Command)>();
        engine.StripDispatchRequested += (cs, cmd) => stripDispatches.Add((cs, cmd));
        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += terminal.Add;

        var loaded = new LoadedAircraft { State = ac, PresetCommands = [new PresetCommand { Command = "WAIT 2 STRIP Local", TimeOffset = 0 }] };
        engine.DispatchPresetCommands(loaded);
        Assert.Single(ac.DeferredDispatches);

        for (int t = 0; t < 4; t++)
        {
            engine.TickOneSecond();
        }

        AssertStripMoveDispatched(stripDispatches, terminal, "DAL2272");
    }

    [Fact]
    public void DeferredStripMovePreset_WhileTaxiing_QueuesStripDispatch_NoWarning()
    {
        if (TestVnasData.NavigationDb is null || new TestAirportGroundData().GetLayout("SFO") is null)
        {
            return;
        }

        var engine = BuildEngine();
        var ac = AddParkedAtGate(engine, "SWA162", "B13");
        var taxi = engine.SendCommand("SWA162", "TAXI Y H B M1 1L");
        Assert.True(taxi.Success, taxi.Message);

        var stripDispatches = new List<(string Callsign, ParsedCommand Command)>();
        engine.StripDispatchRequested += (cs, cmd) => stripDispatches.Add((cs, cmd));
        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += terminal.Add;

        var loaded = new LoadedAircraft { State = ac, PresetCommands = [new PresetCommand { Command = "WAIT 2 STRIP Local", TimeOffset = 0 }] };
        engine.DispatchPresetCommands(loaded);
        Assert.Single(ac.DeferredDispatches);

        for (int t = 0; t < 4; t++)
        {
            engine.TickOneSecond();
        }

        AssertStripMoveDispatched(stripDispatches, terminal, "SWA162");
    }

    /// <summary>
    /// Every canonical type in the strip family — picked up by enum name so a newly added
    /// <c>Strip*</c> / <c>HalfStrip*</c> / <c>Separator*</c> / <c>Blank*</c> member is checked without
    /// anyone remembering to list it here.
    /// </summary>
    public static TheoryData<CanonicalCommandType> StripFamilyTypes()
    {
        var data = new TheoryData<CanonicalCommandType> { CanonicalCommandType.Annotate };
        foreach (var type in Enum.GetValues<CanonicalCommandType>())
        {
            string name = type.ToString();
            if (
                name.StartsWith("Strip", StringComparison.Ordinal)
                || name.StartsWith("HalfStrip", StringComparison.Ordinal)
                || name.StartsWith("Separator", StringComparison.Ordinal)
                || name.StartsWith("Blank", StringComparison.Ordinal)
            )
            {
                data.Add(type);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StripFamilyTypes))]
    public void EveryStripCommandTypeIsPhaseTransparent(CanonicalCommandType type)
    {
        // Strip state is host-owned; no phase has any business gating a strip command.
        Assert.True(
            CommandDescriber.IsPhaseTransparent(type),
            $"{type} must be phase-transparent so preset/deferred strip commands reach the strip arm"
        );
    }

    // --- Issue #396: a preset that fails when dispatched must tell the instructor ---

    [Fact]
    public void ImmediateTaxiPreset_Unresolvable_EmitsCouldNotApplyWarning()
    {
        if (TestVnasData.NavigationDb is null || new TestAirportGroundData().GetLayout("SFO") is null)
        {
            return;
        }

        var engine = BuildEngine();
        var ac = AddParkedAtGate(engine, "UAL123", "B13");
        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += terminal.Add;

        var loaded = new LoadedAircraft { State = ac, PresetCommands = [new PresetCommand { Command = "TAXI ZZ9 1L", TimeOffset = 0 }] };
        engine.DispatchPresetCommands(loaded);

        var warning = terminal.FirstOrDefault(e => e.Callsign == "UAL123" && e.Kind == "Warning");
        Assert.True(warning is not null, "a preset TAXI naming a taxiway that does not exist must surface a terminal warning");
        Assert.Contains("[Preset] could not apply", warning.Message);
        Assert.Contains("ZZ9", warning.Message);
    }
}
