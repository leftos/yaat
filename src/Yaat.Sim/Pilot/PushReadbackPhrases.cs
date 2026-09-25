using System.Globalization;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;

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

    /// <summary>
    /// The verb a leg is read with: <c>push to </c> when the planner chooses the motion, <c>push back to </c> on a leg forced
    /// to <c>/PUSH</c>, <c>pull forward to </c> on one forced to <c>/PULL</c>.
    /// </summary>
    internal static PushWords Verb(PushbackLegKind? forced) =>
        PushWords.Plain(
            forced switch
            {
                PushbackLegKind.Push => "push back to ",
                PushbackLegKind.Pull => "pull forward to ",
                _ => "push to ",
            }
        );

    /// <summary><c>PUSH @B13</c>: <c>push to gate B13, park on the stand</c>, the stand named by <paramref name="stand"/>.</summary>
    internal static PushWords ToStand(PushWords stand, PushbackLegKind? forced) => Verb(forced) + stand + PushWords.Plain(", park on the stand");

    /// <summary><c>PUSH #node</c> resolving to a stand: <c>push to gate B13, park</c>.</summary>
    internal static PushWords ToStandNode(PushWords stand, PushbackLegKind? forced) => Verb(forced) + stand + PushWords.Plain(", park");

    /// <summary>
    /// <c>PUSH $7A</c> with the push's facing: <c>push to spot 7A</c>, <c>push to spot 7A, tail west</c>,
    /// <c>pull forward to spot 7A, face east</c>.
    /// </summary>
    internal static PushWords ToSpot(PushbackCommand push, string spot) =>
        Verb(push.Destination?.ForcedKind) + PushWords.Named("spot", spot) + Facing(push);

    /// <summary><c>PUSH #1926</c> to a plain node: <c>push to node 1926, hold</c>, the push's facing before the hold.</summary>
    internal static PushWords ToNode(PushbackCommand push, int nodeId) =>
        Verb(push.Destination?.ForcedKind)
        + PushWords.Named("node", nodeId.ToString(CultureInfo.InvariantCulture))
        + Facing(push)
        + PushWords.Plain(", hold");

    /// <summary>
    /// <c>PUSH ~lat/lon[/facing]</c>: <c>push to the marked point, face northeast</c> with the point's facing or the push's
    /// <c>FACE</c>/<c>TAIL</c>, else <c>push to the marked point, hold</c>. The coordinates are never read.
    /// </summary>
    internal static PushWords ToMarkedPoint(PushbackCommand push)
    {
        PushWords to = Verb(push.Destination?.ForcedKind) + MarkedPoint(null);
        if (push.Destination?.FreePose?.Facing is { } facing)
        {
            return to + Orientation(facing, false);
        }

        return push.MagneticHeading is { } heading ? to + Orientation(heading, push.IsTail) : to + PushWords.Plain(", hold");
    }

    /// <summary>
    /// A marked point by its place among a <c>PUSHM</c>'s: <c>the marked point</c> alone, <c>marked point 2</c> /
    /// "marked point two" among several.
    /// </summary>
    internal static PushWords MarkedPoint(int? number) =>
        number is { } n ? PushWords.Named("marked point", n.ToString(CultureInfo.InvariantCulture)) : PushWords.Plain("the marked point");

    /// <summary>How a refusal names a marked point: <c>the marked point</c>, or <c>marked point 2</c> among several.</summary>
    internal static string MarkedPointLabel(int? number) => MarkedPoint(number).Terminal;

    /// <summary>
    /// A <c>PUSHM</c> leg's marked-point number: its place among the command's marked points when there are two or more,
    /// null when it is the only one.
    /// </summary>
    /// <param name="legs">The command's targets.</param>
    /// <param name="index">The leg, a marked point.</param>
    internal static int? MarkedPointNumber(IReadOnlyList<PushDestination> legs, int index) =>
        legs.Count(l => l.FreePose is not null) > 1 ? legs.Take(index + 1).Count(l => l.FreePose is not null) : null;

    /// <summary>
    /// <c>PUSHM</c>: the last point, then the points the tow passes on the way: <c>push to spot 6B via spot 6A</c>,
    /// <c>push to spot 6B via spot 6A and spot 6</c>, <c>push to spot 5B via spot 6A, spot 6 and spot 5A</c>. No target's
    /// <c>/PUSH</c> or <c>/PULL</c> is read. The last point's own facing (a marked point's) or the final facing comes last.
    /// </summary>
    /// <param name="move">The command: its last target's facing and its final facing.</param>
    /// <param name="points">Every point in the order the tug passes it, named; one per target.</param>
    internal static PushWords Multi(PushbackMultiCommand move, IReadOnlyList<PushWords> points)
    {
        PushWords words = PushWords.Plain("push to ") + points[^1] + PushWords.Plain(" via ") + JoinedWithAnd([.. points.Take(points.Count - 1)]);
        if (move.Legs[^1].FreePose?.Facing is { } own)
        {
            words += Orientation(own, false);
        }

        return move.FinalFacing is { } facing ? words + Orientation(facing, move.IsTail) : words;
    }

    /// <summary>A list read aloud: <c>spot 6A</c>, <c>spot 6A and spot 6</c>, <c>spot 6A, spot 6 and spot 5A</c>.</summary>
    private static PushWords JoinedWithAnd(IReadOnlyList<PushWords> items)
    {
        PushWords words = PushWords.None;
        for (int i = 0; i < items.Count; i++)
        {
            string separator = (i == 0) ? string.Empty : ((i == items.Count - 1) ? " and " : ", ");
            words += PushWords.Plain(separator) + items[i];
        }

        return words;
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

            if (destination.FreePose is not null)
            {
                return ToMarkedPoint(push);
            }

            return destination.Spot is { } spot ? ToSpot(push, spot) : ToStand(PushWords.Named("gate", destination.Parking!), destination.ForcedKind);
        }

        if (push.Taxiway is { } taxiway)
        {
            return (push.FacingTaxiway is null) && (push.MagneticHeading is null) ? ToTaxiway(taxiway) : OntoTaxiway(push, taxiway);
        }

        return push.MagneticHeading is { } heading ? Back(heading, push.IsTail) : StraightBack();
    }

    /// <summary>
    /// A <c>PUSHM</c> read back from its parsed targets: <c>$6A</c> a spot, <c>@D15</c> a gate, <c>#1926</c> a node,
    /// <c>~lat/lon</c> a marked point.
    /// </summary>
    internal static PushWords FromCommand(PushbackMultiCommand move) => Multi(move, [.. move.Legs.Select((leg, i) => TargetWords(move.Legs, i))]);

    private static PushWords TargetWords(IReadOnlyList<PushDestination> legs, int index) =>
        legs[index] switch
        {
            { Spot: { } spot } => PushWords.Named("spot", spot),
            { Parking: { } parking } => PushWords.Named("gate", parking),
            { NodeId: { } nodeId } => PushWords.Named("node", nodeId.ToString(CultureInfo.InvariantCulture)),
            _ => MarkedPoint(MarkedPointNumber(legs, index)),
        };
}
