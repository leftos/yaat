using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Simulation;

/// <summary>
/// Rewrites recorded CTO and LUAW text written before snapshot schema
/// <see cref="RunwayGrammarSchemaVersion"/>, when a 1-2 digit CTO argument was a heading and
/// neither verb took a runway, so it replays as it was recorded under the current grammar: <c>CTO 28</c> (heading 028)
/// becomes <c>CTO 028</c>, and the runway forms the parser then refused (<c>CTO 28R</c>, <c>LUAW 28L</c>) become
/// <c>CTO RWY 28R</c> / <c>LUAW RWY 28L</c>, which it still refuses. Every chained unit is rewritten, after any
/// <c>AS</c>, wait or condition prefix.
///
/// <para>Not safe on text written from that schema on, where <c>CTO 28</c> names runway 28: callers gate it on
/// <see cref="PredatesRunwayGrammar"/> of the schema the tape's snapshot was written at. Its own output is left unchanged, so a second pass over a rewritten pre-42 recording
/// is a no-op.</para>
/// </summary>
public static partial class DepartureGrammarCanonicalRewriter
{
    /// <summary>
    /// The snapshot schema from which CTO and LUAW take a runway (<c>CTO 28L</c>, and <c>CTO 28</c> names runway 28).
    /// </summary>
    private const int RunwayGrammarSchemaVersion = 42;

    private static readonly ILogger Log = SimLog.CreateLogger("DepartureGrammarCanonicalRewriter");

    /// <summary>
    /// Whether text recorded beside a snapshot written at <paramref name="schemaVersion"/> predates the CTO/LUAW runway
    /// grammar, so it must go through <see cref="RewriteRecordedCommands"/> before it replays.
    /// </summary>
    public static bool PredatesRunwayGrammar(int schemaVersion) => schemaVersion < RunwayGrammarSchemaVersion;

    /// <summary>Prefixes that take one argument before the command they qualify (<c>AS 2B</c>, <c>WAIT 10</c>, <c>AT SUNOL</c>).</summary>
    private static readonly string[] OneArgumentPrefixes = ["AS", "WAIT", "DELAY", "WAITD", "AT", "LV", "ATFN", "GIVEWAY", "BEHIND", "GW"];

    /// <summary>Condition prefixes that take no argument.</summary>
    private static readonly string[] BarePrefixes = ["ONHO", "ONH", "ONHS", "OTG"];

    /// <summary>Rewrites every pre-schema-42 CTO and LUAW unit of a possibly-compound canonical.</summary>
    public static string Rewrite(string canonical) => CompoundCanonical.RewriteUnits(canonical, RewriteUnit);

    /// <summary>
    /// Rewrites each <see cref="RecordedCommand"/> of a pre-schema-42 recording in place, logging every change. Returns
    /// whether any command changed.
    /// </summary>
    public static bool RewriteRecordedCommands(List<RecordedAction> actions)
    {
        bool changed = false;
        for (int i = 0; i < actions.Count; i++)
        {
            if (actions[i] is not RecordedCommand command)
            {
                continue;
            }

            string rewritten = Rewrite(command.Command);
            if (string.Equals(rewritten, command.Command, StringComparison.Ordinal))
            {
                continue;
            }

            Log.LogDebug("Pre-runway-grammar recorded command '{Old}' rewritten as '{New}'", command.Command, rewritten);
            actions[i] = command with { Command = rewritten };
            changed = true;
        }

        return changed;
    }

    private static string RewriteUnit(string unit)
    {
        string[] tokens = unit.Split(' ');
        int[] words = [.. Enumerable.Range(0, tokens.Length).Where(i => tokens[i].Length > 0)];
        int verbWord = SkipPrefixes(tokens, words);
        if (verbWord + 1 >= words.Length)
        {
            return unit;
        }

        int argumentToken = words[verbWord + 1];
        if (RewriteFirstArgument(tokens[words[verbWord]], tokens[argumentToken]) is not { } rewritten)
        {
            return unit;
        }

        tokens[argumentToken] = rewritten;
        return string.Join(' ', tokens);
    }

    /// <summary>The index into <paramref name="words"/> of the unit's verb, past any prefixes.</summary>
    private static int SkipPrefixes(string[] tokens, int[] words)
    {
        int word = 0;
        while (word < words.Length)
        {
            string token = tokens[words[word]];
            if (BarePrefixes.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                word += 1;
            }
            else if (OneArgumentPrefixes.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                word += 2;
            }
            else
            {
                break;
            }
        }

        return word;
    }

    /// <summary>The pre-schema-42 first argument as the current grammar reads it the same way, or null when it needs no rewrite.</summary>
    private static string? RewriteFirstArgument(string verb, string argument)
    {
        if (verb.Equals("CTO", StringComparison.OrdinalIgnoreCase))
        {
            if (BareHeading().IsMatch(argument))
            {
                return argument.PadLeft(3, '0');
            }

            return SuffixedRunway().IsMatch(argument) ? $"RWY {argument}" : null;
        }

        if (verb.Equals("LUAW", StringComparison.OrdinalIgnoreCase) && Runway().IsMatch(argument))
        {
            return $"RWY {argument}";
        }

        return null;
    }

    [GeneratedRegex("^[0-9]{1,2}$")]
    private static partial Regex BareHeading();

    [GeneratedRegex("^[0-9]{1,2}[LCR]$", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixedRunway();

    [GeneratedRegex("^[0-9]{1,2}[LCR]?$", RegexOptions.IgnoreCase)]
    private static partial Regex Runway();
}
