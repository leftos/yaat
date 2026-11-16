using System.Collections.Frozen;

namespace Yaat.Sim.Commands;

/// <summary>
/// The ERAM entry verbs yaat-server dispatches (<c>CrcClientState.DispatchEramMessage</c>, through its
/// <c>EramVariants.Verbs</c>), in one place both the server and the YAAT Scope read: the server so a verb with no dispatch arm
/// fails loudly, the Scope so an entry with one of these first words reads as ERAM. Every other first token is an implied
/// command. <see cref="Readouts"/> are the verbs that only answer and never write, so they never take control of a tape the
/// room is playing back.
/// </summary>
public static class EramEntryVerbs
{
    /// <summary>Every verb the server dispatches, uppercase.</summary>
    public static FrozenSet<string> All { get; } =
        new[]
        {
            "QN",
            "QF",
            "QL",
            "RD",
            "QU",
            "QT",
            "QZ",
            "QQ",
            "QS",
            "QX",
            "QP",
            "QR",
            "QH",
            "LA",
            "LB",
            "LC",
            "LD",
            "LE",
            "AM",
            "LF",
            "DM",
            "VP",
            "QB",
            "CO",
            "HM",
            "RF",
            "QA",
            "QV",
            "DQ",
            "FR",
            "RS",
            "RM",
            "CA",
            "RK",
            "SM",
            "SW",
            "UR",
            "WX",
            "FP",
            "SP",
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The readouts: <c>QF</c>, <c>LA</c>, <c>LB</c>, <c>LC</c>, <c>LD</c>, <c>LE</c> and <c>UR</c> answer with a response and
    /// change no state.
    /// </summary>
    public static FrozenSet<string> Readouts { get; } = new[] { "QF", "LA", "LB", "LC", "LD", "LE", "UR" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The verbs that are readouts only when typed bare: <c>SM</c> and <c>RK</c> with no operands list what is set.</summary>
    public static FrozenSet<string> BareReadouts { get; } = new[] { "SM", "RK" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether the entry <paramref name="tokens"/> only answers and changes no state: its verb is one of the
    /// <see cref="Readouts"/>, or one of the <see cref="BareReadouts"/> typed with no operands.
    /// </summary>
    public static bool IsReadoutEntry(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return false;
        }

        string verb = tokens[0].ToUpperInvariant();
        return Readouts.Contains(verb) || ((tokens.Count == 1) && BareReadouts.Contains(verb));
    }
}
