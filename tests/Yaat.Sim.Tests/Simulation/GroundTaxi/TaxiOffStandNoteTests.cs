using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A live TAXI / TAXIAUTO off a stand the aircraft normally pushes back from (KOAK gate 26) is still accepted and the
/// aircraft taxis, but the response carries an RPO note that the stand usually needs a push first — the command's own
/// response text, never the pilot's readback. A scenario preset gets no note, and neither does a piston, a taxi-out
/// stand or an either stand. KOAK, a B738 unless the test says otherwise.
/// </summary>
public class TaxiOffStandNoteTests(ITestOutputHelper output)
{
    private const string AircraftType = "B738";
    private const string PushBackStand = "26";
    private const string PushBackNote = "(26 normally needs a push first)";

    /// <summary>Gate 26's own connector (TE) to the T/U junction, then U and W to runway 30.</summary>
    private const string PushBackRoute = "TAXI TE U W RWY 30";

    /// <summary>GA20 stands off F, which meets C; a taxi-out stand's own power.</summary>
    private const string TaxiOutStand = "GA20";
    private const string TaxiOutRoute = "TAXI F C";

    /// <summary>MTN1 stands off B1, which meets R; an either stand takes a push or a taxi-out.</summary>
    private const string EitherStand = "MTN1";
    private const string EitherRoute = "TAXI B1 R";

    private const string AutoRoute = "TAXIAUTO 28R";

    /// <summary>A live TAXI off KOAK gate 26 is accepted, and its response ends with the note.</summary>
    [Fact]
    public void LiveTaxi_JetAtPushBackStand_NotesItNeedsAPush()
    {
        if (Live(PushBackStand, AircraftType, PushBackRoute) is not { } sent)
        {
            return;
        }

        Assert.EndsWith(PushBackNote, sent.Response);
        AssertTaxis(sent.Aircraft);
        AssertNotSpoken(sent.Spoken);
    }

    /// <summary>A live TAXIAUTO off KOAK gate 26 is accepted, and its response ends with the note.</summary>
    [Fact]
    public void LiveTaxiAuto_JetAtPushBackStand_NotesItNeedsAPush()
    {
        if (Live(PushBackStand, AircraftType, AutoRoute) is not { } sent)
        {
            return;
        }

        Assert.EndsWith(PushBackNote, sent.Response);
        AssertTaxis(sent.Aircraft);
        AssertNotSpoken(sent.Spoken);
    }

    /// <summary>A turboprop is as heavy as a jet here: a live TAXI off gate 26 carries the note and still taxis.</summary>
    [Fact]
    public void LiveTaxi_TurbopropAtPushBackStand_NotesIt()
    {
        if (Live(PushBackStand, "DH8D", PushBackRoute) is not { } sent)
        {
            return;
        }

        Assert.EndsWith(PushBackNote, sent.Response);
        AssertTaxis(sent.Aircraft);
        AssertNotSpoken(sent.Spoken);
    }

    /// <summary>
    /// A live TAXI to the very stand the aircraft is on (KOAK <c>TAXI @26</c> from 26) is the zero-segment shortcut:
    /// the aircraft stays parked, so there is no taxi to note and its response carries no push-back note.
    /// </summary>
    [Fact]
    public void LiveTaxi_SameStand_NoNote()
    {
        if (Live(PushBackStand, AircraftType, $"TAXI @{PushBackStand}") is not { } sent)
        {
            return;
        }

        Assert.DoesNotContain("needs a push first", sent.Response);
    }

    /// <summary>A scenario preset TAXI off gate 26 carries no note: no controller is reading the response.</summary>
    [Fact]
    public void PresetTaxi_JetAtPushBackStand_NoNote()
    {
        if (Preset(PushBackStand, AircraftType, PushBackRoute) is not { } response)
        {
            return;
        }

        Assert.DoesNotContain(PushBackNote, response);
    }

    /// <summary>A scenario preset TAXIAUTO off gate 26 carries no note, as the preset TAXI does.</summary>
    [Fact]
    public void PresetTaxiAuto_JetAtPushBackStand_NoNote()
    {
        if (Preset(PushBackStand, AircraftType, AutoRoute) is not { } response)
        {
            return;
        }

        Assert.DoesNotContain(PushBackNote, response);
    }

    /// <summary>GA20 is a taxi-out stand: a live TAXI off it needs no push, so no note.</summary>
    [Fact]
    public void LiveTaxi_JetAtTaxiOutStand_NoNote()
    {
        if (Live(TaxiOutStand, AircraftType, TaxiOutRoute) is not { } sent)
        {
            return;
        }

        Assert.DoesNotContain("needs a push first", sent.Response);
    }

    /// <summary>MTN1 is an either stand: a live TAXI off it is normal, so no note.</summary>
    [Fact]
    public void LiveTaxi_JetAtEitherStand_NoNote()
    {
        if (Live(EitherStand, AircraftType, EitherRoute) is not { } sent)
        {
            return;
        }

        Assert.DoesNotContain("needs a push first", sent.Response);
    }

    /// <summary>A piston is light enough to leave a push-back-geometry stand on its own: no note.</summary>
    [Fact]
    public void LiveTaxi_PistonAtPushBackStand_NoNote()
    {
        if (Live(PushBackStand, "C172", PushBackRoute) is not { } sent)
        {
            return;
        }

        Assert.DoesNotContain("needs a push first", sent.Response);
    }

    /// <summary>
    /// Parks <paramref name="type"/> on <paramref name="stand"/>, sends <paramref name="command"/> as the live
    /// controller, and returns the command's response text, the pilot's readback of it and the aircraft it left
    /// taxiing; null when the KOAK layout is missing. The command must be accepted.
    /// </summary>
    private (string Response, PilotSpeechText? Spoken, AircraftState Aircraft)? Live(string stand, string type, string command)
    {
        if (BuildOak(output) is not { } ground)
        {
            return null;
        }

        AircraftState aircraft = ParkedAt(ground, type, stand);
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, $"live {command} off {stand} was refused: {result.Message}");
        string response = Assert.IsType<string>(result.Message);
        PilotSpeechText? spoken = PilotResponder.BuildReadbackAsApplied(
            CommandParser.ParseCompound(command).Value!,
            result,
            aircraft,
            PilotPersonality.Verbatim,
            FrequencyActivityLevel.Moderate
        );
        output.WriteLine($"{stand} {type} live \"{command}\": response \"{response}\"; spoken \"{spoken?.Terminal}\" / \"{spoken?.Tts}\"");
        return (response, spoken, aircraft);
    }

    /// <summary>
    /// Parks <paramref name="type"/> on <paramref name="stand"/> as <see cref="Live"/> does and dispatches
    /// <paramref name="command"/> as a scenario preset (<see cref="DispatchContext.IsScenarioScripted"/>), returning
    /// the response text; null when the KOAK layout is missing. The command must be accepted.
    /// </summary>
    private string? Preset(string stand, string type, string command)
    {
        if (BuildOak(output) is not { } ground)
        {
            return null;
        }

        AircraftState aircraft = ParkedAt(ground, type, stand);
        DispatchContext ctx = TestDispatch.Context(new Random(42), groundLayout: ground.Layout, isScenarioScripted: true);
        CommandResult result = CommandDispatcher.DispatchCompound(CommandParser.ParseCompound(command).Value!, aircraft, ctx);
        Assert.True(result.Success, $"preset {command} off {stand} was refused: {result.Message}");
        string response = Assert.IsType<string>(result.Message);
        output.WriteLine($"{stand} {type} preset \"{command}\": response \"{response}\"");
        return response;
    }

    /// <summary>Parks <paramref name="type"/> on <paramref name="stand"/>, its parking spot set as a scenario spawn sets it.</summary>
    private static AircraftState ParkedAt(SfoGround ground, string type, string stand)
    {
        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "N123AB", type, stand);
        aircraft.Ground.ParkingSpot = stand;
        return aircraft;
    }

    /// <summary>The note is advisory: the clearance is still flown, so the aircraft is taxiing.</summary>
    private static void AssertTaxis(AircraftState aircraft) => Assert.IsType<TaxiingPhase>(aircraft.Phases!.CurrentPhase);

    /// <summary>The pilot never says the note: neither the terminal nor the TTS readback carries it.</summary>
    private static void AssertNotSpoken(PilotSpeechText? spoken)
    {
        if (spoken is null)
        {
            Assert.Fail("a TAXI clearance is read back, but the pilot said nothing");
            return;
        }

        foreach (string phrase in new[] { "normally needs a push", "push first" })
        {
            Assert.DoesNotContain(phrase, spoken.Terminal, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(phrase, spoken.Tts, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>An engine over the committed KOAK layout, as <see cref="SfoGroundHarness.Build"/> builds one over SFO's.</summary>
    private static SfoGround? BuildOak(ITestOutputHelper output)
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout("OAK") is not { } layout))
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-taxi-off-stand-note",
                ScenarioName = "OAK Taxi Off Stand Note",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunway = false,
            },
        };
        return new SfoGround(engine, layout);
    }
}
