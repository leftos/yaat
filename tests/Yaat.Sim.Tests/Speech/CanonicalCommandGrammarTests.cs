using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Speech;

namespace Yaat.Sim.Tests.Speech;

/// <summary>
/// Structural tests on the GBNF emitted by <see cref="CanonicalCommandGrammar"/>. Tests the
/// generator output rather than running the grammar through llama.cpp — running the grammar
/// belongs to <c>LocalLlmPipelineIntegrationTests</c> on the client side, which exercises the
/// full LLamaSharp path.
/// </summary>
public class CanonicalCommandGrammarTests
{
    [Fact]
    public void BuildGbnf_HasRootProduction()
    {
        var gbnf = CanonicalCommandGrammar.BuildGbnf();
        Assert.StartsWith("root ::=", gbnf, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGbnf_DefinesAllRequiredNonTerminals()
    {
        var gbnf = CanonicalCommandGrammar.BuildGbnf();
        // Every non-terminal referenced by `root` and its descendants must have a production,
        // otherwise llama.cpp's parser will reject the grammar at load time. We assert by
        // substring match because the productions are written on their own lines.
        Assert.Contains("clauses ::=", gbnf, StringComparison.Ordinal);
        Assert.Contains("clause ::=", gbnf, StringComparison.Ordinal);
        Assert.Contains("condition ::=", gbnf, StringComparison.Ordinal);
        Assert.Contains("verb ::=", gbnf, StringComparison.Ordinal);
        Assert.Contains("arg ::=", gbnf, StringComparison.Ordinal);
        Assert.Contains("fixname ::=", gbnf, StringComparison.Ordinal);
        Assert.Contains("altnum ::=", gbnf, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGbnf_RootIsOptional()
    {
        var gbnf = CanonicalCommandGrammar.BuildGbnf();
        // The `?` suffix on root's clauses production lets the model emit end-of-generation at
        // position zero when the transcript isn't a recognizable command. Validated against
        // gemma4:e4b — the model correctly picks EOG for garbled / chitchat inputs and
        // commands for valid ones.
        Assert.Contains("root ::= clauses?", gbnf, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGbnf_IncludesEveryAliasFromCommandRegistry()
    {
        var gbnf = CanonicalCommandGrammar.BuildGbnf();
        // Every alias the registry knows about must appear as a quoted literal in the verb
        // alternation. If a new command is added to CommandRegistry but the grammar generator
        // somehow filters it out, this test fails.
        foreach (var alias in CommandRegistry.AliasToCanonicType.Keys)
        {
            Assert.Contains($"\"{alias}\"", gbnf, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildGbnf_VerbAlternationIsSortedLongestFirst()
    {
        var gbnf = CanonicalCommandGrammar.BuildGbnf();
        // Find the verb production line and check the alternatives are in non-increasing length
        // order. This matters because llama.cpp's pushdown automaton wants longer literals to be
        // tried first when prefixes overlap (e.g. "RELL" before "R", "FPH" before "F"). Without
        // length-descending order the parser can lock in a short prefix and reject the longer
        // verb the model actually wanted to emit.
        //
        // Split on \n and TrimEnd carriage-return so CRLF line endings on Windows don't smuggle a
        // \r into the last alternative.
        var verbLine = gbnf.Split('\n').First(line => line.StartsWith("verb ::=", StringComparison.Ordinal)).TrimEnd('\r');
        var alternatives = verbLine
            .Substring("verb ::= ".Length)
            .Split(" | ", StringSplitOptions.RemoveEmptyEntries)
            .Select(literal => literal.Trim('"'))
            .ToList();

        for (var i = 1; i < alternatives.Count; i++)
        {
            Assert.True(
                alternatives[i].Length <= alternatives[i - 1].Length,
                $"verb alternation not sorted longest-first at index {i}: '{alternatives[i - 1]}' ({alternatives[i - 1].Length}) "
                    + $"before '{alternatives[i]}' ({alternatives[i].Length})"
            );
        }
    }

    [Fact]
    public void BuildGbnf_ConditionPrefixesAreEncodedDirectly()
    {
        var gbnf = CanonicalCommandGrammar.BuildGbnf();
        // AT and LV are not registry aliases — they're condition prefixes. They must be encoded
        // in the condition production, not the verb alternation, otherwise the grammar can't
        // accept "AT CEPIN CAPP" or "LV 5000 FH 270" style outputs that PhraseologyMapper produces.
        Assert.Contains("\"AT \"", gbnf, StringComparison.Ordinal);
        Assert.Contains("\"LV \"", gbnf, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGbnf_ArgCharsetIsCanonicalTokenSet()
    {
        var gbnf = CanonicalCommandGrammar.BuildGbnf();
        // The arg charset must match LocalLlmCommandMapper.IsCanonicalToken: uppercase letters,
        // digits, plus, minus, dot, slash. Hyphen MUST be last inside the character class to be
        // a literal (otherwise it forms a range and can change the accepted set).
        Assert.Contains("arg ::= [A-Z0-9.+/-]+", gbnf, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_IsCachedAcrossAccesses()
    {
        // Lazy<T> contract — the same string instance comes back on repeated access. This isn't
        // about correctness so much as about not re-enumerating CommandRegistry per PTT press.
        Assert.Same(CanonicalCommandGrammar.Default, CanonicalCommandGrammar.Default);
    }
}
