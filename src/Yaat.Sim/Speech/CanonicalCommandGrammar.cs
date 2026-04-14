using System.Text;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Speech;

/// <summary>
/// GBNF grammar that constrains <see cref="LocalLlmCommandMapper"/>'s LLM output to syntactically
/// valid YAAT canonical commands. Generated at runtime from <see cref="CommandRegistry.AliasToCanonicType"/>
/// so new commands automatically expand the grammar — there is no second list to keep in sync.
///
/// The grammar mirrors the surface that <c>LocalLlmCommandMapper.NormalizeOutput</c> validates:
/// <list type="bullet">
///   <item><description>One or more comma-separated clauses ("CM 5000, FH 270").</description></item>
///   <item><description>Each clause is an optional condition prefix ("AT FIX " / "LV ALT ") followed
///     by a verb (one of <see cref="CommandRegistry.AliasToCanonicType"/>'s keys) and zero or more
///     args.</description></item>
///   <item><description>Args are non-empty tokens of <c>[A-Z0-9.+/-]</c> — exactly the set
///     <c>LocalLlmCommandMapper.IsCanonicalToken</c> permits.</description></item>
/// </list>
///
/// Verb literals are sorted longest-first so the pushdown automaton matches multi-character verbs
/// before single-letter prefixes (e.g. "RELL" beats "R", "FPH" beats "F"). The condition-prefix
/// keywords <c>AT</c> and <c>LV</c> are not aliases in the registry — they are encoded directly.
/// Confirmed by inspecting the alias surface that no canonical alias collides with "AT" or "LV".
///
/// Pair this string with <c>LLama.Sampling.Grammar(gbnf, "root")</c> on a <c>GreedySamplingPipeline</c>
/// inside <see cref="LocalLlmService"/>.
/// </summary>
public static class CanonicalCommandGrammar
{
    private static readonly Lazy<string> DefaultLazy = new(BuildGbnf);

    /// <summary>
    /// The default GBNF for the production command-mapper path. Cached on first access so the
    /// pipeline doesn't rebuild the alias enumeration on every PTT.
    /// </summary>
    public static string Default => DefaultLazy.Value;

    /// <summary>
    /// Builds the GBNF from <see cref="CommandRegistry.AliasToCanonicType"/>. Exposed so unit
    /// tests can assert structural properties of the freshly-generated grammar instead of comparing
    /// against a snapshot.
    /// </summary>
    public static string BuildGbnf()
    {
        // Sort aliases by length descending then by ordinal so the alternation is deterministic and
        // the longest match wins when prefixes overlap (e.g. "FPH" before "FCH" before "F" — the
        // single-letter "F" alias doesn't actually exist today but the ordering makes the grammar
        // future-proof against new short aliases that prefix-collide with existing ones).
        var aliases = CommandRegistry.AliasToCanonicType.Keys.OrderByDescending(a => a.Length).ThenBy(a => a, StringComparer.Ordinal).ToList();

        if (aliases.Count == 0)
        {
            // Defensive: registry should never be empty in production. Returning a grammar that
            // matches nothing is preferable to an invalid GBNF that crashes llama.cpp's parser.
            return "root ::= \"\"\n";
        }

        var verbAlternation = string.Join(" | ", aliases.Select(a => $"\"{a}\""));

        // Notes on the GBNF charset for `arg`:
        // - `-` MUST go last inside the character class to be a literal hyphen (otherwise it forms
        //   a range). `+`, `.`, and `/` are literal in any position.
        // - `[A-Z0-9.+/-]+` is the same set IsCanonicalToken permits: uppercase letters, digits,
        //   plus, minus, dot, and slash.
        //
        // `root` is `clauses?` (NOT `clauses`) so the model can emit end-of-generation at
        // position zero when no command is appropriate. We previously tried this with qwen3.5:4b
        // under greedy sampling and the model defaulted to EOG for every input including valid
        // commands — the model wasn't well enough calibrated to balance "respect the grammar
        // structure" vs "stop early when uncertain". gemma4:e4b validated 2026-04-14 picks the
        // right side of that trade-off: emits commands for valid inputs, EOG for garbage.
        var sb = new StringBuilder();
        sb.AppendLine("root ::= clauses?");
        sb.AppendLine("clauses ::= clause (\", \" clause)*");
        sb.AppendLine("clause ::= condition? verb (\" \" arg)*");
        sb.AppendLine("condition ::= (\"AT \" fixname | \"LV \" altnum) \" \"");
        sb.Append("verb ::= ").AppendLine(verbAlternation);
        sb.AppendLine("arg ::= [A-Z0-9.+/-]+");
        sb.AppendLine("fixname ::= [A-Z]+");
        sb.AppendLine("altnum ::= [0-9]+");
        return sb.ToString();
    }
}
