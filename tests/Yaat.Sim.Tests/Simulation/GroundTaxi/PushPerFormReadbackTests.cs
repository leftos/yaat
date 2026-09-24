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

        Assert.Equal("Push to spot 6B via spot 6A, spot 6", moved.Readback);
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
