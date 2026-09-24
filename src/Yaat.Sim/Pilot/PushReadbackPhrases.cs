using System.Globalization;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Pilot;

/// <summary>
/// A piece of a push readback in the pilot's two forms: the terminal text, identifiers as the RPO typed them, and the
/// spoken text, identifiers spelled the way <see cref="PhraseologyVerbalizer"/> speaks taxiways, gates and spots.
/// </summary>
/// <param name="Terminal">The terminal text.</param>
/// <param name="Tts">The spoken text.</param>
internal readonly record struct PushWords(string Terminal, string Tts)
{
    internal static readonly PushWords None = new(string.Empty, string.Empty);

    internal static PushWords Plain(string text) => new(text, text);

    internal static PushWords Taxiway(string name) => new(name, PhraseologyVerbalizer.SpellTaxiway(name));

    /// <summary>A named point: <c>spot 7A</c> / "spot seven alpha", <c>gate 4A</c>, <c>node 1926</c>.</summary>
    internal static PushWords Named(string noun, string name) =>
        new($"{noun} {name}", $"{noun} {PhraseologyVerbalizer.SpellDestinationName(name, noun)}");

    internal PilotSpeechText ToSpeech() => new(Terminal, Tts);

    public static PushWords operator +(PushWords a, PushWords b) => new(a.Terminal + b.Terminal, a.Tts + b.Tts);
}

/// <summary>
/// The sentences a <c>PUSH</c> / <c>PUSHM</c> readback is built from, one per form, lower-case as a pilot readback clause.
/// The handler reads back the goal it resolved with these; a push not dispatched yet (behind a <c>WAIT</c>, a
/// <c>GIVEWAY</c>, a condition or a later <c>;</c> block) reads back its parsed command through
/// <see cref="FromCommand(PushbackCommand)"/>, which words the same forms without the parts only a plan decides.
/// </summary>
internal static class PushReadbackPhrases
{
    /// <summary>A facing named by cardinal: <c>, face east</c>, or <c>, tail west</c> naming the cardinal the RPO typed.</summary>
    internal static PushWords Orientation(MagneticHeading facing, bool isTail) =>
        PushWords.Plain(
            isTail ? $", tail {GroundCommandParser.CardinalWord(facing.ToReciprocal())}" : $", face {GroundCommandParser.CardinalWord(facing)}"
        );

    /// <summary>The facing a <c>PUSH</c> names: <c>, face taxiway T</c>, <c>, face east</c>, <c>, tail west</c>, or nothing.</summary>
    internal static PushWords Facing(PushbackCommand push)
    {
        if (push.FacingTaxiway is { } facingTaxiway)
        {
            return PushWords.Plain(", face taxiway ") + PushWords.Taxiway(facingTaxiway);
        }

        return push.MagneticHeading is { } heading ? Orientation(heading, push.IsTail) : PushWords.None;
    }

    /// <summary>A bare <c>PUSH</c>: <c>push straight back</c>.</summary>
    internal static PushWords StraightBack() => PushWords.Plain("push straight back");

    /// <summary><c>PUSH FACE E</c> / <c>PUSH TAIL W</c>: <c>push back, face east</c> / <c>push back, tail west</c>.</summary>
    internal static PushWords Back(MagneticHeading facing, bool isTail) => PushWords.Plain("push back") + Orientation(facing, isTail);

    /// <summary>A bare <c>PUSH &lt;taxiway&gt;</c> planned straight back across it: <c>push straight back to taxiway A</c>.</summary>
    internal static PushWords StraightBackToTaxiway(string taxiway) => PushWords.Plain("push straight back to taxiway ") + PushWords.Taxiway(taxiway);

    /// <summary>A bare <c>PUSH &lt;taxiway&gt;</c> planned onto a taxiway alongside: <c>push onto M4, nose along M4</c>.</summary>
    internal static PushWords AlongsideTaxiway(string taxiway)
    {
        var name = PushWords.Taxiway(taxiway);
        return PushWords.Plain("push onto ") + name + PushWords.Plain(", nose along ") + name;
    }

    /// <summary>A bare <c>PUSH &lt;taxiway&gt;</c> not planned yet, so neither across nor alongside: <c>push to taxiway A</c>.</summary>
    internal static PushWords ToTaxiway(string taxiway) => PushWords.Plain("push to taxiway ") + PushWords.Taxiway(taxiway);

    /// <summary><c>PUSH &lt;taxiway&gt;</c> with a facing: <c>push onto Y, face north</c>, <c>push onto A, face taxiway F1</c>.</summary>
    internal static PushWords OntoTaxiway(PushbackCommand push, string taxiway) =>
        PushWords.Plain("push onto ") + PushWords.Taxiway(taxiway) + Facing(push);

    /// <summary><c>PUSH @B13</c>: <c>push to gate B13, park on the stand</c>, the stand named by <paramref name="stand"/>.</summary>
    internal static PushWords ToStand(PushWords stand) => PushWords.Plain("push to ") + stand + PushWords.Plain(", park on the stand");

    /// <summary><c>PUSH #node</c> resolving to a stand: <c>push to gate B13, park</c>.</summary>
    internal static PushWords ToStandNode(PushWords stand) => PushWords.Plain("push to ") + stand + PushWords.Plain(", park");

    /// <summary><c>PUSH $7A</c> with the push's facing: <c>push to spot 7A</c>, <c>push to spot 7A, tail west</c>.</summary>
    internal static PushWords ToSpot(PushbackCommand push, string spot) => PushWords.Plain("push to ") + PushWords.Named("spot", spot) + Facing(push);

    /// <summary><c>PUSH #1926</c> to a plain node: <c>push to node 1926, hold</c>, the push's facing before the hold.</summary>
    internal static PushWords ToNode(PushbackCommand push, int nodeId) =>
        PushWords.Plain("push to ")
        + PushWords.Named("node", nodeId.ToString(CultureInfo.InvariantCulture))
        + Facing(push)
        + PushWords.Plain(", hold");

    /// <summary>
    /// <c>PUSHM</c>: <c>push to spot 6B via spot 6A, spot 6</c> — the last point, then every point on the way — with the
    /// final facing appended.
    /// </summary>
    /// <param name="points">Every point in the order the tug reaches it; at least two.</param>
    /// <param name="finalFacing">The final facing, or null.</param>
    /// <param name="isTail">Whether the facing was named by the tail.</param>
    internal static PushWords Multi(IReadOnlyList<PushWords> points, MagneticHeading? finalFacing, bool isTail)
    {
        PushWords words = PushWords.Plain("push to ") + points[^1] + PushWords.Plain(" via ");
        for (int i = 0; i < points.Count - 1; i++)
        {
            words += (i == 0 ? PushWords.None : PushWords.Plain(", ")) + points[i];
        }

        return finalFacing is { } facing ? words + Orientation(facing, isTail) : words;
    }

    /// <summary>A mid-push facing amendment: <c>push amended, tail west</c>.</summary>
    internal static PushWords Amended(PushbackCommand push) => PushWords.Plain("push amended") + Facing(push);

    /// <summary>
    /// A <c>PUSH</c> read back from its parsed form, before any plan: the handler's sentence for the form, less the parts
    /// only the plan or the layout decides. A bare push onto a taxiway reads <c>push to taxiway A</c>, a stand is a gate,
    /// and a <c>#node</c> is a node.
    /// </summary>
    internal static PushWords FromCommand(PushbackCommand push)
    {
        if (push.Destination is { } destination)
        {
            if (destination.NodeId is { } nodeId)
            {
                return ToNode(push, nodeId);
            }

            return destination.Spot is { } spot ? ToSpot(push, spot) : ToStand(PushWords.Named("gate", destination.Parking!));
        }

        if (push.Taxiway is { } taxiway)
        {
            return (push.FacingTaxiway is null) && (push.MagneticHeading is null) ? ToTaxiway(taxiway) : OntoTaxiway(push, taxiway);
        }

        return push.MagneticHeading is { } heading ? Back(heading, push.IsTail) : StraightBack();
    }

    /// <summary>A <c>PUSHM</c> read back from its parsed targets: <c>$6A</c> a spot, <c>@D15</c> a gate, <c>#1926</c> a node.</summary>
    internal static PushWords FromCommand(PushbackMultiCommand move) => Multi([.. move.Targets.Select(TargetWords)], move.FinalFacing, move.IsTail);

    private static PushWords TargetWords(string target) =>
        target[0] switch
        {
            '$' => PushWords.Named("spot", target[1..]),
            '@' => PushWords.Named("gate", target[1..]),
            '#' => PushWords.Named("node", target[1..]),
            _ => PushWords.Plain(target),
        };
}
