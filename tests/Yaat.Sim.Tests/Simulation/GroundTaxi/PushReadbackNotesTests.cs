using System.Text.RegularExpressions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// What the tug planner could not avoid, or the RPO may not have meant, reaches the RPO as parentheticals on the push
/// readback — the command's own response text — and never the pilot's spoken readback, which is verbalized from the
/// command. SFO, a B738, no neighbours.
/// </summary>
public partial class PushReadbackNotesTests(ITestOutputHelper output)
{
    private const string AircraftType = "B738";

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
        Assert.StartsWith("Pushing back onto A facing F1 (", pushed.Readback);
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

        Assert.Equal("Tug move to 5, 2 legs (right wing will foul taxiway A, coordinate with ground)", moved.Readback);
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

        Assert.StartsWith("Pushback amended, face heading 360 (", amended.Readback);
        Assert.Matches(LongPushNote(), amended.Readback);
        Assert.DoesNotMatch(FarFacingNote(), amended.Readback);
        AssertNotSpoken(amended.Spoken, "long push", "ft away");
    }

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
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return null;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "UAL462", AircraftType, gate);
        string readback = "";
        for (int i = 0; i < commands.Count; i++)
        {
            if (i > 0)
            {
                ground.Engine.TickOneSecond();
            }

            CommandResult result = ground.Engine.SendCommand(aircraft.Callsign, commands[i]);
            Assert.True(result.Success, $"{commands[i]} off {gate} was refused: {result.Message}");
            readback = Assert.IsType<string>(result.Message);
        }

        string last = commands[^1];
        PilotSpeechText? spoken = PilotResponder.BuildReadback(CommandParser.ParseCompound(last).Value!, aircraft);
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
