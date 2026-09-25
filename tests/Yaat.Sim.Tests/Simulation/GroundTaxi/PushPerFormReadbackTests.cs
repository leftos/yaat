using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Every <c>PUSH</c> form reads back what the tug move will do, in the words of the approved per-form table: the response
/// text the RPO sees, with its RPO-only parenthetical where the form has one, and the pilot's readback — the same
/// sentence without the parenthetical, built from the goal the handler resolved. SFO, real stands and spots.
/// </summary>
public class PushPerFormReadbackTests(ITestOutputHelper output)
{
    private const string Narrowbody = "B738";

    [Fact]
    public void BarePush_ReadsStraightBack_NotesTheNoseForTheRpo()
    {
        if (Push("D15", Narrowbody, "PUSH") is not { } pushed)
        {
            return;
        }

        Assert.Equal("Push straight back (nose east)", pushed.Readback);
    }

    [Fact]
    public void PushFace_ReadsPushBackFaceTheCardinal()
    {
        if (Push("D15", Narrowbody, "PUSH FACE E") is not { } pushed)
        {
            return;
        }

        Assert.Equal("Push back, face east", pushed.Readback);
    }

    [Fact]
    public void PushTail_ReadsTheCardinalTyped_NeverItsReciprocal()
    {
        if (Push("D15", Narrowbody, "PUSH TAIL W") is not { } pushed)
        {
            return;
        }

        Assert.Equal("Push back, tail west", pushed.Readback);
    }

    /// <summary>
    /// B12's straight push crosses Y and stops on A
    /// (<c>TugMovePlannerTests.B12StraightBackToAlpha_AcceptedAcrossYankee_StopsOnAlpha</c>).
    /// </summary>
    [Fact]
    public void PushOntoTaxiwayAcross_ReadsStraightBackToTheTaxiway()
    {
        if (Push("B12", "B752", "PUSH A") is not { } pushed)
        {
            return;
        }

        Assert.Equal("Push straight back to taxiway A", pushed.Readback);
    }

    /// <summary>M4 runs alongside B2's push (<c>TugMovePlannerTests.B2StraightBackToM4Alongside_PushesOffThenCurvesOntoTheCentreline</c>).</summary>
    [Fact]
    public void PushOntoTaxiwayAlongside_ReadsOntoItNoseAlongIt()
    {
        if (Push("B2", "B737", "PUSH M4") is not { } pushed)
        {
            return;
        }

        Assert.Equal("Push onto M4, nose along M4", pushed.Readback);
    }

    [Fact]
    public void PushOntoTaxiwayFacingCardinal_ReadsOntoItFaceTheCardinal()
    {
        if (Push("B12", Narrowbody, "PUSH Y FACE N") is not { } pushed)
        {
            return;
        }

        Assert.Equal("Push onto Y, face north", pushed.Readback);
    }

    /// <summary>Issue #452's pose: C8's <c>PUSH Y TAIL S</c> reads the cardinal typed.</summary>
    [Fact]
    public void PushOntoTaxiwayTail_ReadsOntoItTailTheCardinal()
    {
        if (Push("C8", "B739", "PUSH Y TAIL S") is not { } pushed)
        {
            return;
        }

        Assert.Equal("Push onto Y, tail south", pushed.Readback);
    }

    [Fact]
    public void PushOntoTaxiwayFacingTaxiway_ReadsOntoItFaceTheTaxiway()
    {
        if (Push("D10", Narrowbody, "PUSH A F1") is not { } pushed)
        {
            return;
        }

        Assert.Equal("Push onto A, face taxiway F1", pushed.Readback);
    }

    [Fact]
    public void PushToNode_ReadsNodeHold()
    {
        if (PushToLaneNode("") is not { } pushed)
        {
            return;
        }

        Assert.Equal($"Push to node {pushed.NodeId}, hold", pushed.Readback);
    }

    [Fact]
    public void PushToNodeWithFacing_ReadsNodeFaceThenHold()
    {
        if (PushToLaneNode(" FACE E") is not { } pushed)
        {
            return;
        }

        Assert.Equal($"Push to node {pushed.NodeId}, face east, hold", pushed.Readback);
    }

    [Fact]
    public void PushToGateNode_ReadsGatePark()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode b13 = ground.Layout.FindParkingByName("B13") ?? throw new InvalidOperationException("SFO gate B13 missing");
        (string readback, _) = Send(ground, "B12", Narrowbody, $"PUSH #{b13.Id}");

        Assert.Equal("Push to gate B13, park (tail will foul taxiway A, coordinate with ground)", readback);
    }

    [Fact]
    public void PushToSpotNode_ReadsAsTheSpot()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode spot = ground.Layout.FindSpotNodeByName("7A") ?? throw new InvalidOperationException("SFO spot 7A missing");
        (string readback, _) = Send(ground, "F8", "CRJ7", $"PUSH #{spot.Id}");

        Assert.Equal("Push to spot 7A (nose out toward A)", readback);
    }

    [Fact]
    public void PushToGate_ReadsGateParkOnTheStand()
    {
        if (Push("B12", Narrowbody, "PUSH @B13") is not { } pushed)
        {
            return;
        }

        Assert.Equal("Push to gate B13, park on the stand (tail will foul taxiway A, coordinate with ground)", pushed.Readback);
    }

    [Fact]
    public void PushToSpot_ReadsTheSpot_NotesTheNoseOutTaxiwayForTheRpo()
    {
        if (Push("F8", "CRJ7", "PUSH $7A") is not { } pushed)
        {
            return;
        }

        Assert.Equal("Push to spot 7A (nose out toward A)", pushed.Readback);
    }

    [Fact]
    public void PushToSpotTail_ReadsTheSpotTailTheCardinal()
    {
        if (Push("F8", "CRJ7", "PUSH $7A TAIL W") is not { } pushed)
        {
            return;
        }

        Assert.Equal("Push to spot 7A, tail west", pushed.Readback);
    }

    [Fact]
    public void PushToSpotFace_ReadsTheSpotFaceTheCardinal()
    {
        if (Push("F8", "CRJ7", "PUSH $7A FACE E") is not { } pushed)
        {
            return;
        }

        Assert.Equal("Push to spot 7A, face east", pushed.Readback);
    }

    [Fact]
    public void PushToSpotFacingTaxiway_ReadsTheSpotFaceTheTaxiway()
    {
        if (Push("F8", "CRJ7", "PUSH $7A A") is not { } pushed)
        {
            return;
        }

        Assert.Equal("Push to spot 7A, face taxiway A", pushed.Readback);
    }

    [Fact]
    public void Pushm_ReadsTheLastPointViaTheOthers()
    {
        if (Push("D15", Narrowbody, "PUSHM $6A $6B") is not { } moved)
        {
            return;
        }

        Assert.Equal("Push to spot 6B via spot 6A", moved.Readback);
    }

    [Fact]
    public void PushmThroughThreePoints_ListsEveryPointOnTheWay()
    {
        if (Push("D15", Narrowbody, "PUSHM $6A $6 $6B") is not { } moved)
        {
            return;
        }

        Assert.Equal("Push to spot 6B via spot 6A and spot 6", moved.Readback);
    }

    [Fact]
    public void PushmWithFinalFacing_AppendsTheFacing()
    {
        if (Push("D15", Narrowbody, "PUSHM $6A $6B FACE E") is not { } moved)
        {
            return;
        }

        Assert.Equal("Push to spot 6B via spot 6A, face east", moved.Readback);
    }

    /// <summary>
    /// The forced-leg and marked-point forms, read back from the command in both of the pilot's forms (terminal text and
    /// spoken): the verb names a forced motion on a <c>PUSH</c>; a <c>PUSHM</c> reads its last point via the others, joined
    /// with "and", and never a target's <c>/PUSH</c> or <c>/PULL</c>; a marked point is never read by its coordinates.
    /// </summary>
    [Theory]
    [InlineData("PUSH $7A/PULL", "pull forward to spot 7A", "pull forward to spot seven alpha")]
    [InlineData("PUSH $7A/PUSH", "push back to spot 7A", "push back to spot seven alpha")]
    [InlineData("PUSH $7A/PULL FACE E", "pull forward to spot 7A, face east", "pull forward to spot seven alpha, face east")]
    [InlineData(
        "PUSHM @F8 $7A/PULL $7B FACE E",
        "push to spot 7B via gate F8 and spot 7A, face east",
        "push to spot seven bravo via gate foxtrot eight and spot seven alpha, face east"
    )]
    [InlineData(
        "PUSHM $6A $6 $5A $5B",
        "push to spot 5B via spot 6A, spot 6 and spot 5A",
        "push to spot five bravo via spot six alpha, spot six and spot five alpha"
    )]
    [InlineData("PUSHM $6A/PUSH $6B/PULL", "push to spot 6B via spot 6A", "push to spot six bravo via spot six alpha")]
    [InlineData("PUSHM ~37.61523/-122.38604 $7B", "push to spot 7B via the marked point", "push to spot seven bravo via the marked point")]
    [InlineData("PUSHM $6A $6B", "push to spot 6B via spot 6A", "push to spot six bravo via spot six alpha")]
    [InlineData("PUSH ~37.61523/-122.38604/045", "push to the marked point, face northeast", "push to the marked point, face northeast")]
    [InlineData(
        "PUSH ~37.61523/-122.38604/045/PULL",
        "pull forward to the marked point, face northeast",
        "pull forward to the marked point, face northeast"
    )]
    [InlineData("PUSH ~37.61523/-122.38604", "push to the marked point, hold", "push to the marked point, hold")]
    [InlineData(
        "PUSHM ~37.61523/-122.38604 ~37.6155/-122.3862 FACE E",
        "push to marked point 2 via marked point 1, face east",
        "push to marked point two via marked point one, face east"
    )]
    [InlineData(
        "PUSHM $6A ~37.6155/-122.3862/360",
        "push to the marked point via spot 6A, face north",
        "push to the marked point via spot six alpha, face north"
    )]
    public void ForcedAndMarkedForms_ReadBackFromTheCommand(string command, string terminal, string spoken)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        PushWords words = parsed.Value switch
        {
            PushbackCommand push => PushReadbackPhrases.FromCommand(push),
            PushbackMultiCommand move => PushReadbackPhrases.FromCommand(move),
            _ => throw new InvalidOperationException($"'{command}' is not a push"),
        };
        output.WriteLine($"{command} → \"{words.Terminal}\" / \"{words.Tts}\"");

        Assert.Equal(terminal, words.Terminal);
        Assert.Equal(spoken, words.Tts);
    }

    /// <summary>F8 → 7A forced <c>/PULL</c> through the handler: the RPO's readback and the pilot's say "pull forward".</summary>
    [Fact]
    public void PushToSpotForcedPull_ReadsPullForward_ThePilotToo()
    {
        if (Push("F8", Narrowbody, "PUSH $7A/PULL") is not { } pushed)
        {
            return;
        }

        Assert.StartsWith("Pull forward to spot 7A", pushed.Readback);
        Assert.Equal("pull forward to spot 7A", pushed.Spoken.Terminal);
        Assert.StartsWith("pull forward to spot seven alpha, ", pushed.Spoken.Tts);
    }

    /// <summary>A marked point on taxiway A, typed off D15: the handler refuses it naming the taxiway.</summary>
    [Fact]
    public void PushToMarkedPointOnATaxiway_RefusedNamingTheTaxiway()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode d15 = ground.Layout.FindParkingByName("D15") ?? throw new InvalidOperationException("SFO gate D15 missing");
        GroundEdge alpha = ground
            .Layout.AllEdges.OfType<GroundEdge>()
            .Where(e =>
                e.MatchesTaxiway("A") && !e.IsRunwayCenterline && (GeoMath.DistanceNm(e.Nodes[0].Position, d15.Position) * GeoMath.FeetPerNm < 1500.0)
            )
            .MaxBy(e => GeoMath.DistanceNm(e.Nodes[0].Position, e.Nodes[1].Position))!;
        double lat = (alpha.Nodes[0].Position.Lat + alpha.Nodes[1].Position.Lat) / 2.0;
        double lon = (alpha.Nodes[0].Position.Lon + alpha.Nodes[1].Position.Lon) / 2.0;
        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "UAL462", Narrowbody, "D15");

        CommandResult result = ground.Engine.SendCommand(
            aircraft.Callsign,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PUSH ~{lat:F6}/{lon:F6}")
        );

        Assert.False(result.Success, result.Message);
        Assert.Equal("Unable, the marked point is on taxiway A", result.Message);
    }

    /// <summary>
    /// A typed <c>PUSHM @F8 ~lat/lon/facing/PULL</c> from gate F7 travels the engine's command path whole — the comma
    /// splitter never sees a comma in it — and reaches the tug-move planner as a stand and a marked point forced to a
    /// pull: the planner's refusal names the marked point and the pull, which it could only do with both targets and the
    /// suffix intact.
    /// The point is spot 7A's stop, facing 7A's nose-out heading. <c>MarkedPointPushTests</c> proves an accepted one.
    /// </summary>
    [Fact]
    public void TypedPushmToAMarkedPoint_ReachesThePlannerThroughTheEngine_WhichRefusesItsForcedPull()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode spot = ground.Layout.FindSpotNodeByName("7A") ?? throw new InvalidOperationException("SFO spot 7A missing");
        Assert.True(ground.Layout.TryGetSpotOutboundHeading(spot, out double outbound));
        LatLon stop = TugMovePlanner.SpotStopGeometry(spot, outbound, Narrowbody).Stop;
        var facing = new MagneticHeading(MagneticDeclination.TrueToMagnetic(outbound, stop));
        string command = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"PUSHM @F8 ~{stop.Lat:F6}/{stop.Lon:F6}/{facing.ToDisplayString()}/PULL"
        );
        PushbackMultiCommand parsed = Assert.IsType<PushbackMultiCommand>(CommandParser.Parse(command).Value);
        Assert.Equal(command, CommandDescriber.DescribeCommand(parsed));
        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "UAL462", Narrowbody, "F7");

        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{command} → {result.Success}: {result.Message}");

        Assert.DoesNotContain("parse", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Unable, the marked point cannot be reached by a pull", result.Message);
    }

    /// <summary>
    /// A typed <c>PUSHM ~lat/lon/090 $7B</c> from gate F8, the marked point on spot 7A's rest point: the tow passes a point
    /// before the last without stopping there, so only the last point takes a facing, and the move is refused.
    /// </summary>
    [Fact]
    public void TypedPushmWithAFacingOnAPassedPoint_Refused()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        GroundNode spot = ground.Layout.FindSpotNodeByName("7A") ?? throw new InvalidOperationException("SFO spot 7A missing");
        Assert.True(ground.Layout.TryGetSpotOutboundHeading(spot, out double outbound));
        LatLon rest = TugMovePlanner.SpotStopGeometry(spot, outbound, Narrowbody).Stop;
        string command = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PUSHM ~{rest.Lat:F6}/{rest.Lon:F6}/090 $7B");
        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "UAL462", Narrowbody, "F8");

        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, command);
        output.WriteLine($"{command} → {result.Success}: {result.Message}");

        Assert.False(result.Success, result.Message);
        Assert.Equal("Unable, the marked point is passed through; only the last point takes a facing", result.Message);
    }

    [Fact]
    public void BarePush_PilotSaysPushStraightBack_WithoutTheNoseNote()
    {
        if (Push("D15", Narrowbody, "PUSH") is not { } pushed)
        {
            return;
        }

        Assert.Equal("push straight back", pushed.Spoken.Terminal);
        Assert.StartsWith("push straight back, ", pushed.Spoken.Tts);
        Assert.DoesNotContain("nose", pushed.Spoken.Tts, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PushTail_PilotSaysTailTheCardinalTyped()
    {
        if (Push("D15", Narrowbody, "PUSH TAIL W") is not { } pushed)
        {
            return;
        }

        Assert.Equal("push back, tail west", pushed.Spoken.Terminal);
        Assert.StartsWith("push back, tail west, ", pushed.Spoken.Tts);
    }

    [Fact]
    public void PushToSpot_PilotSpellsTheSpot_WithoutTheNoseOutNote()
    {
        if (Push("F8", "CRJ7", "PUSH $7A") is not { } pushed)
        {
            return;
        }

        Assert.Equal("push to spot 7A", pushed.Spoken.Terminal);
        Assert.StartsWith("push to spot seven alpha, ", pushed.Spoken.Tts);
        Assert.DoesNotContain("nose", pushed.Spoken.Tts, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>WAIT 5 PUSH TAIL W</c> is deferred, so no handler has read it back yet: the pilot reads the parsed push back in
    /// the table's words for the form.
    /// </summary>
    [Fact]
    public void DeferredPushTail_PilotReadsTheFormBackFromTheCommand()
    {
        if (Push("D15", Narrowbody, "WAIT 5 PUSH TAIL W") is not { } pushed)
        {
            return;
        }

        Assert.Contains("push back, tail west", pushed.Spoken.Terminal);
        Assert.Contains("push back, tail west", pushed.Spoken.Tts);
        Assert.DoesNotContain("pushback", pushed.Spoken.Tts, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>PUSH $7A</c> queued behind a give-way condition reads the spot back from the command — without the nose-out
    /// note, which only the layout decides.
    /// </summary>
    [Fact]
    public void PushToSpotBehindACondition_PilotReadsTheSpotBackFromTheCommand()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        SfoGroundHarness.SpawnParked(ground, "SKW5000", "CRJ7", "F4");
        (_, PilotSpeechText spoken) = Send(ground, "F8", "CRJ7", "GIVEWAY SKW5000 PUSH $7A");

        Assert.Contains("push to spot 7A", spoken.Terminal);
        Assert.Contains("push to spot seven alpha", spoken.Tts);
        Assert.DoesNotContain("nose", spoken.Tts, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>PUSH FACE E</c> off D15, amended a second into the push-off by <c>PUSH TAIL W</c>: the readback and the pilot
    /// both say the amended facing as typed, prefixed "push amended".
    /// </summary>
    [Fact]
    public void TailAmendmentMidPush_ReadsPushAmendedTailTheCardinalTyped()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        (string readback, PilotSpeechText spoken) = SendInTurn(ground, "D15", Narrowbody, ["PUSH FACE E", "PUSH TAIL W"]);

        Assert.StartsWith("Push amended, tail west", readback);
        Assert.Equal("push amended, tail west", spoken.Terminal);
        Assert.StartsWith("push amended, tail west, ", spoken.Tts);
    }

    /// <summary>
    /// The engine queues the solo pilot's readback of <c>PUSH TAIL W</c> in the table's words — the handler's readback
    /// reaches the transmission through the engine's dispatch.
    /// </summary>
    [Fact]
    public void PushTail_Answering_QueuesThePilotReadbackInTheTableWords()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        ground.Engine.Scenario!.SoloTrainingMode = true;
        Assert.True(ground.Engine.Scenario.PilotContacts.AnyAnswering);
        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "UAL462", Narrowbody, "D15");
        CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, "PUSH TAIL W");
        Assert.True(result.Success, result.Message);

        PilotTransmission readback = Assert.Single(aircraft.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        output.WriteLine($"queued \"{readback.Text}\" / \"{readback.SpeechText}\"");
        Assert.Contains("push back, tail west", readback.Text);
        Assert.Contains("push back, tail west", readback.SpeechText);
    }

    /// <summary>The taxiway spot 7A's nose-out facing points toward — the name the <c>PUSH $7A</c> note gives.</summary>
    [Fact]
    public void SpotOutboundTaxiway_Sfo7A_IsA()
    {
        if (new TestAirportGroundData().GetLayout("SFO") is not { } layout)
        {
            return;
        }

        GroundNode spot = layout.FindSpotNodeByName("7A") ?? throw new InvalidOperationException("SFO spot 7A missing");

        Assert.True(layout.TryGetSpotOutboundTaxiway(spot, out string taxiway), "spot 7A has no outbound taxiway");
        Assert.Equal("A", taxiway);
    }

    /// <summary>
    /// Parks <paramref name="aircraftType"/> on <paramref name="gate"/> and sends <paramref name="command"/>; null when SFO's
    /// layout is missing.
    /// </summary>
    private (string Readback, PilotSpeechText Spoken)? Push(string gate, string aircraftType, string command) =>
        SfoGroundHarness.Build(output, autoCross: false) is { } ground ? Send(ground, gate, aircraftType, command) : null;

    /// <summary>
    /// A push off D15 to the plain (not spot, stand or helipad) node next to spot 6A on its lane, with
    /// <paramref name="facing"/> appended to the command.
    /// </summary>
    private (string Readback, int NodeId)? PushToLaneNode(string facing)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return null;
        }

        GroundNode spot = ground.Layout.FindSpotNodeByName("6A") ?? throw new InvalidOperationException("SFO spot 6A missing");
        GroundNode node =
            spot.Edges.Select(e => e.OtherNode(spot))
                .FirstOrDefault(n => n.Type is not (GroundNodeType.Spot or GroundNodeType.Parking or GroundNodeType.Helipad))
            ?? throw new InvalidOperationException("spot 6A has no plain neighbour node");
        (string readback, _) = Send(ground, "D15", Narrowbody, $"PUSH #{node.Id}{facing}");
        return (readback, node.Id);
    }

    /// <summary>The command's response text and the pilot's readback of it, as the solo pilot would say it.</summary>
    private (string Readback, PilotSpeechText Spoken) Send(SfoGround ground, string gate, string aircraftType, string command) =>
        SendInTurn(ground, gate, aircraftType, [command]);

    /// <summary>Sends each command in turn, a second apart, each accepted; the last one's response text and pilot readback.</summary>
    private (string Readback, PilotSpeechText Spoken) SendInTurn(SfoGround ground, string gate, string aircraftType, IReadOnlyList<string> commands)
    {
        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "UAL462", aircraftType, gate);
        CommandResult? last = null;
        foreach (string sent in commands)
        {
            if (last is not null)
            {
                ground.Engine.TickOneSecond();
            }

            last = ground.Engine.SendCommand(aircraft.Callsign, sent);
            output.WriteLine($"{gate} {aircraftType} {sent}: success={last.Success} \"{last.Message}\"");
            Assert.True(last.Success, $"{sent} off {gate} was refused: {last.Message}");
        }

        CommandResult result = last!;
        string command = commands[^1];

        PilotSpeechText spoken = Assert.IsType<PilotSpeechText>(
            PilotResponder.BuildReadbackAsApplied(
                CommandParser.ParseCompound(command).Value!,
                result,
                aircraft,
                PilotPersonality.Verbatim,
                FrequencyActivityLevel.Moderate
            )
        );
        output.WriteLine($"  spoken \"{spoken.Terminal}\" / \"{spoken.Tts}\"");
        return (Assert.IsType<string>(result.Message), spoken);
    }
}
