using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// What the tug planner could not avoid, or the RPO may not have meant, reaches the RPO as parentheticals on the push
/// readback — the command's own response text — and never the pilot's readback, which is the same sentence without
/// them. The SFO cases fly a B738 with no neighbours; the OAK cases stage a parked neighbour on the push line.
/// </summary>
public partial class PushReadbackNotesTests(ITestOutputHelper output)
{
    private const string AircraftType = "B738";
    private const string NeighbourCallsign = "SWA2436";

    /// <summary>The RPO note a plain push that breaks a parked neighbour's floor carries.</summary>
    private const string NeighbourNote = "(holds short of SWA2436)";

    /// <summary>
    /// F3 <c>PUSH A F1</c>: A's line lies ~1,285 ft across the ramp from F3 and the A/F1 junction far along A beyond it,
    /// so the readback notes both the long push and that F1 only chose the direction.
    /// </summary>
    [Fact]
    public void PushAF1_FromF3_ReadbackNotesTheLongPushAndTheFarFacing_ThePilotSaysNeither()
    {
        if (Push("F3", "PUSH A F1") is not { } pushed)
        {
            return;
        }

        Assert.Matches(LongPushNote(), pushed.Readback);
        Assert.Matches(FarFacingNote(), pushed.Readback);
        Assert.StartsWith("Push onto A, face taxiway F1 (", pushed.Readback);
        AssertNotSpoken(pushed.Spoken, "long push", "facing only", "ft away");
    }

    /// <summary>D10 <c>PUSH A F1</c>: the A/F1 junction ~629 ft ahead of the nose is not far, so there is no far-facing note.</summary>
    [Fact]
    public void PushAF1_FromD10_ReadbackCarriesNoFarFacingNote()
    {
        if (Push("D10", "PUSH A F1") is not { } pushed)
        {
            return;
        }

        Assert.DoesNotMatch(FarFacingNote(), pushed.Readback);
    }

    /// <summary>
    /// D5 <c>PUSH $5</c>: no shape the planner tries keeps a B738 off stand D5 and onto spot 5 outside taxiway A's
    /// object-free area — the least-fouling one still reaches ~10 ft in with the right wing — so the readback tells the
    /// RPO to coordinate.
    /// </summary>
    [Fact]
    public void PushToSpot5_FromD5_ReadbackNotesTheFoul_ThePilotDoesNotSayIt()
    {
        if (Push("D5", "PUSH $5") is not { } pushed)
        {
            return;
        }

        Assert.Contains("(right wing will foul taxiway A, coordinate with ground)", pushed.Readback);
        AssertNotSpoken(pushed.Spoken, "foul", "coordinate");
    }

    /// <summary>
    /// D5 <c>PUSHM $5A $5</c>: a tug move ends on spot 5 as the <c>PUSH $5</c> does, with the right wing still inside
    /// taxiway A's object-free area, so its readback carries the same foul note.
    /// </summary>
    [Fact]
    public void PushmToSpot5_FromD5_ReadbackNotesTheFoul_ThePilotDoesNotSayIt()
    {
        if (Push("D5", "PUSHM $5A $5") is not { } moved)
        {
            return;
        }

        Assert.Equal("Push to spot 5 via spot 5A (right wing will foul taxiway A, coordinate with ground)", moved.Readback);
        AssertNotSpoken(moved.Spoken, "foul", "coordinate");
    }

    /// <summary>
    /// F3 <c>PUSH A F1</c>, amended mid push-off by <c>PUSH FACE N</c>: the re-plan is still the long push onto A, so
    /// the amendment's readback notes it again; the amendment names no facing taxiway, so there is no far-facing note.
    /// </summary>
    [Fact]
    public void FaceAmendment_OfTheLongPushFromF3_ReadbackNotesTheLongPush_ThePilotDoesNotSayIt()
    {
        if (PushThenAmend("F3", "PUSH A F1", "PUSH FACE N") is not { } amended)
        {
            return;
        }

        Assert.StartsWith("Push amended, face north (", amended.Readback);
        Assert.Matches(LongPushNote(), amended.Readback);
        Assert.DoesNotMatch(FarFacingNote(), amended.Readback);
        AssertNotSpoken(amended.Spoken, "long push", "ft away");
    }

    /// <summary>
    /// KOAK GA20 is a taxi-out stand: a <c>PUSH</c> off it is still accepted, and its readback tells the RPO the stand is
    /// one; the pilot does not say it.
    /// </summary>
    [Fact]
    public void Push_FromTaxiOutStandGa20_ReadbackNotesTheStand_ThePilotDoesNotSayIt()
    {
        if (PushAtStand(BuildOak, "GA20", "C172", "PUSH") is not { } pushed)
        {
            return;
        }

        Assert.Contains(Ga20Note, pushed.Readback);
        AssertNotSpoken(pushed.Spoken, "taxi-out", "taxi out");
    }

    /// <summary>A <c>PUSHM</c> off GA20, to two marked points behind it, carries the same note.</summary>
    [Fact]
    public void Pushm_FromTaxiOutStandGa20_ReadbackNotesTheStand_ThePilotDoesNotSayIt()
    {
        if (PushAtStand(BuildOak, "GA20", "C172", Ga20BackMove("PUSHM")) is not { } moved)
        {
            return;
        }

        Assert.Contains(Ga20Note, moved.Readback);
        AssertNotSpoken(moved.Spoken, "taxi-out", "taxi out");
    }

    /// <summary>The forced forms are the RPO's explicit override: <c>PUSHF</c> and <c>PUSHMF</c> off GA20 carry no stand note.</summary>
    [Fact]
    public void ForcedPushes_FromTaxiOutStandGa20_ReadbackCarriesNoStandNote()
    {
        if (
            (PushAtStand(BuildOak, "GA20", "C172", "PUSHF") is not { } forced)
            || (PushAtStand(BuildOak, "GA20", "C172", Ga20BackMove("PUSHMF")) is not { } moved)
        )
        {
            return;
        }

        Assert.DoesNotContain("taxi-out stand", forced.Readback);
        Assert.DoesNotContain("taxi-out stand", moved.Readback);
    }

    /// <summary>KOAK gate 26 and KSFO F8 are pushed back from, so a <c>PUSH</c> off either carries no stand note.</summary>
    [Fact]
    public void Push_FromPushBackStands_ReadbackCarriesNoStandNote()
    {
        if ((PushAtStand(BuildOak, "26", AircraftType, "PUSH") is not { } oak) || (PushAtStand(BuildSfo, "F8", AircraftType, "PUSH") is not { } sfo))
        {
            return;
        }

        Assert.DoesNotContain("taxi-out stand", oak.Readback);
        Assert.DoesNotContain("taxi-out stand", sfo.Readback);
    }

    /// <summary>KOAK MTN1 is an either stand: a push off it is normal, so its readback carries no stand note.</summary>
    [Fact]
    public void Push_OffEitherStand_CarriesNoTaxiOutNote()
    {
        if (PushAtStand(BuildOak, "MTN1", "C172", "PUSH") is not { } pushed)
        {
            return;
        }

        Assert.DoesNotContain("taxi-out stand", pushed.Readback);
    }

    /// <summary>
    /// KOAK OLD1 reads push-back by its geometry, but the North Field area rule makes it a taxi-out stand, so a push off it
    /// carries the stand note.
    /// </summary>
    [Fact]
    public void Push_OffNorthFieldStand_CarriesTaxiOutNote()
    {
        if (PushAtStand(BuildOak, "OLD1", "C172", "PUSH") is not { } pushed)
        {
            return;
        }

        Assert.Contains("(OLD1 is a taxi-out stand)", pushed.Readback);
    }

    /// <summary>
    /// OAK gate 25, <c>PUSH TE</c>, with a B738 parked crossways on the push line 230 ft behind the stand: the push is
    /// accepted (the tug creeps up and stops short of the neighbour) and the RPO is told it will hold short, once. The
    /// pilot says none of it.
    /// </summary>
    [Fact]
    public void PushTE_FromGate25_PastANeighbourOnThePushLine_ReadbackNotesTheHoldShort_ThePilotDoesNotSayIt()
    {
        if (PushPastTheNeighbour("PUSH TE", rightFt: 0.0) is not { } pushed)
        {
            return;
        }

        Assert.Contains(NeighbourNote, pushed.Readback, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(pushed.Readback, NeighbourCallsign));
        AssertNotSpoken(pushed.Spoken, NeighbourCallsign, "holds short");
    }

    /// <summary>
    /// The same push with the neighbour only just inside its clearance floor: the planned path passes it, a few feet
    /// clear rather than overlapping (the planner pins that side beside <c>TugMovePlannerTests</c>), and the RPO reads the
    /// same hold-short note.
    /// </summary>
    [Fact]
    public void PushTE_FromGate25_PassingANeighbourInsideItsFloor_ReadbackNotesTheHoldShort()
    {
        if (PushPastTheNeighbour("PUSH TE", rightFt: 185.0) is not { } pushed)
        {
            return;
        }

        Assert.Contains(NeighbourNote, pushed.Readback, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(pushed.Readback, NeighbourCallsign));
    }

    /// <summary>
    /// The same push with the neighbour 260 ft clear of the line: about 347 ft from the aircraft, inside the 400 ft
    /// <see cref="TugParkedNeighbours.RangeFt"/> the planner is fed within, so it really does see the neighbour — and the
    /// plan keeps its floor, so there is no note.
    /// </summary>
    [Fact]
    public void PushTE_FromGate25_WithANeighbourClearOfThePushLine_ReadbackCarriesNoNote()
    {
        if (PushPastTheNeighbour("PUSH TE", rightFt: 260.0) is not { } pushed)
        {
            return;
        }

        Assert.DoesNotContain(NeighbourNote, pushed.Readback, StringComparison.Ordinal);
        Assert.DoesNotContain(NeighbourCallsign, pushed.Readback, StringComparison.Ordinal);
    }

    /// <summary>
    /// The forced form: <c>PUSHF TE</c> past the same neighbour says it once, as its forced note, and never as the plain
    /// note — the two would say the same thing twice.
    /// </summary>
    [Fact]
    public void PushfTE_FromGate25_PastANeighbourOnThePushLine_ReadbackCarriesOneForcedNote()
    {
        if (PushPastTheNeighbour("PUSHF TE", rightFt: 0.0) is not { } pushed)
        {
            return;
        }

        Assert.Contains("(forced: ", pushed.Readback, StringComparison.Ordinal);
        Assert.DoesNotContain(NeighbourNote, pushed.Readback, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(pushed.Readback, NeighbourCallsign));
        string beforeForced = pushed.Readback.Split("(forced: ", StringSplitOptions.None)[0];
        Assert.DoesNotContain(NeighbourCallsign, beforeForced, StringComparison.Ordinal);
        AssertNotSpoken(pushed.Spoken, NeighbourCallsign, "forced", "holds short");
    }

    /// <summary>
    /// Parks a B738 on OAK gate 25, parks <see cref="NeighbourCallsign"/> crossways on its push line 230 ft behind it
    /// (<paramref name="rightFt"/> clear of the line), sends <paramref name="command"/> (which must be accepted), and
    /// returns its readback and the pilot's; null when OAK's layout is missing.
    /// </summary>
    private (string Readback, PilotSpeechText? Spoken)? PushPastTheNeighbour(string command, double rightFt)
    {
        if (BuildOak(output) is not { } ground)
        {
            return null;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "N123AB", AircraftType, "25");
        aircraft.Ground.ParkingSpot = "25";
        SpawnAcrossThePushLine(ground, aircraft, NeighbourCallsign, AircraftType, behindFt: 230.0, rightFt: rightFt);
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        Assert.True(result.Success, $"{command} off gate 25 was refused: {result.Message}");
        string readback = Assert.IsType<string>(result.Message);
        PilotSpeechText? spoken = PilotResponder.BuildReadbackAsApplied(
            CommandParser.ParseCompound(command).Value!,
            result,
            aircraft,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );
        output.WriteLine($"{command} off gate 25: readback \"{readback}\"; spoken \"{spoken?.Terminal}\" / \"{spoken?.Tts}\"");
        return (readback, spoken);
    }

    /// <summary>
    /// Parks <paramref name="type"/> crossways on <paramref name="pusher"/>'s push line, <paramref name="behindFt"/>
    /// behind it and <paramref name="rightFt"/> clear of the line: nose 90° off the push, so its fuselage lies across it.
    /// </summary>
    private static void SpawnAcrossThePushLine(SfoGround ground, AircraftState pusher, string callsign, string type, double behindFt, double rightFt)
    {
        double pushDeg = new TrueHeading(pusher.TrueHeading.Degrees + 180.0).Degrees;
        LatLon behind = GeoMath.ProjectPoint(pusher.Position, new TrueHeading(pushDeg), behindFt / GeoMath.FeetPerNm);
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = GeoMath.ProjectPoint(behind, new TrueHeading(pushDeg + 90.0), rightFt / GeoMath.FeetPerNm),
            TrueHeading = new TrueHeading(pushDeg + 90.0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = ground.Layout.AirportId,
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, ground.Layout));
        aircraft.Ground.Layout = ground.Layout;
        ground.Engine.World.AddAircraft(aircraft);
    }

    /// <summary>
    /// Parks <paramref name="type"/> on <paramref name="stand"/> of the built airport, its parking spot set as a scenario
    /// spawn sets it, sends <paramref name="command"/> (which must be accepted), and returns its readback and the pilot's.
    /// </summary>
    private (string Readback, PilotSpeechText? Spoken)? PushAtStand(
        Func<ITestOutputHelper, SfoGround?> build,
        string stand,
        string type,
        Func<AirportGroundLayout, string> command
    )
    {
        if (build(output) is not { } ground)
        {
            return null;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "N123AB", type, stand);
        aircraft.Ground.ParkingSpot = stand;
        string sent = command(ground.Layout);
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, sent);
        Assert.True(result.Success, $"{sent} off {stand} was refused: {result.Message}");
        string readback = Assert.IsType<string>(result.Message);
        PilotSpeechText? spoken = PilotResponder.BuildReadbackAsApplied(
            CommandParser.ParseCompound(sent).Value!,
            result,
            aircraft,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );
        output.WriteLine($"{stand} {sent}: readback \"{readback}\"; spoken \"{spoken?.Terminal}\" / \"{spoken?.Tts}\"");
        return (readback, spoken);
    }

    private (string Readback, PilotSpeechText? Spoken)? PushAtStand(
        Func<ITestOutputHelper, SfoGround?> build,
        string stand,
        string type,
        string command
    ) => PushAtStand(build, stand, type, _ => command);

    /// <summary>
    /// <paramref name="verb"/> with two marked points straight behind GA20, 120 ft and 240 ft off the stand: the first move
    /// off a stand is always a push-off, so the points lie behind the tail.
    /// </summary>
    private static Func<AirportGroundLayout, string> Ga20BackMove(string verb) =>
        layout =>
        {
            GroundNode ga20 = layout.FindParkingByName("GA20") ?? throw new InvalidOperationException("KOAK has no stand GA20");
            TrueHeading back = (ga20.TrueHeading ?? throw new InvalidOperationException("GA20 has no heading")).ToReciprocal();
            return $"{verb} {MarkedPoint(ga20.Position, back, 120)} {MarkedPoint(ga20.Position, back, 240)}";
        };

    /// <summary>The marked-point token <paramref name="feet"/> from <paramref name="from"/> along <paramref name="bearing"/>.</summary>
    private static string MarkedPoint(LatLon from, TrueHeading bearing, double feet)
    {
        LatLon point = GeoMath.ProjectPoint(from, bearing, feet / GeoMath.FeetPerNm);
        return string.Create(CultureInfo.InvariantCulture, $"~{point.Lat:F6}/{point.Lon:F6}");
    }

    private static SfoGround? BuildSfo(ITestOutputHelper output) => SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off);

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
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-push-notes",
                ScenarioName = "OAK Push Notes",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        return new SfoGround(engine, layout);
    }

    /// <summary>The RPO note a push off GA20 carries.</summary>
    private const string Ga20Note = "(GA20 is a taxi-out stand)";

    /// <summary>
    /// Parks a B738 on <paramref name="gate"/>, sends <paramref name="command"/>, and returns the command's readback
    /// with the pilot's spoken readback of the same command (terminal and TTS forms); null when SFO's layout is missing.
    /// </summary>
    private (string Readback, PilotSpeechText? Spoken)? Push(string gate, string command) => SendInTurn(gate, [command]);

    /// <summary>
    /// <see cref="Push"/> <paramref name="command"/>, one second of the push-off, then <paramref name="amendment"/>; the
    /// amendment's readback and spoken readback.
    /// </summary>
    private (string Readback, PilotSpeechText? Spoken)? PushThenAmend(string gate, string command, string amendment) =>
        SendInTurn(gate, [command, amendment]);

    /// <summary>Sends each command in turn, a second apart, each accepted; the last one's readback and spoken readback.</summary>
    private (string Readback, PilotSpeechText? Spoken)? SendInTurn(string gate, IReadOnlyList<string> commands)
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return null;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "UAL462", AircraftType, gate);
        CommandResult? result = null;
        for (int i = 0; i < commands.Count; i++)
        {
            if (i > 0)
            {
                ground.Engine.TickOneSecond();
            }

            result = ground.Engine.SendCommand(aircraft.Callsign, commands[i]);
            Assert.True(result.Success, $"{commands[i]} off {gate} was refused: {result.Message}");
        }

        string readback = Assert.IsType<string>(result!.Message);
        string last = commands[^1];
        PilotSpeechText? spoken = PilotResponder.BuildReadbackAsApplied(
            CommandParser.ParseCompound(last).Value!,
            result!,
            aircraft,
            PilotSpeechContext.Canonical,
            ActiveRunways.Empty
        );
        output.WriteLine(
            $"{gate} {last}: readback \"{readback}\"; spoken \"{spoken?.Terminal}\" / \"{spoken?.Tts}\" / rpo \"{spoken?.TerminalForRpo}\""
        );
        return (readback, spoken);
    }

    /// <summary>The pilot says none of <paramref name="phrases"/>; a command the pilot does not read back (null) says nothing at all.</summary>
    private static void AssertNotSpoken(PilotSpeechText? spoken, params string[] phrases)
    {
        if (spoken is null)
        {
            return;
        }

        foreach (string phrase in phrases)
        {
            Assert.DoesNotContain(phrase, spoken.Tts, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(phrase, spoken.Terminal, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The long-push note, its distance a multiple of 50 ft.</summary>
    [GeneratedRegex(@"\(taxiway A is \d*[05]0 ft away; long push\)")]
    private static partial Regex LongPushNote();

    /// <summary>The far-facing note, its distance a multiple of 50 ft.</summary>
    [GeneratedRegex(@"\(F1 is \d*[05]0 ft away; facing only\)")]
    private static partial Regex FarFacingNote();
}
