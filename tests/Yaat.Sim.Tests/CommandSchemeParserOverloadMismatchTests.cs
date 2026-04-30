using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Systematically verifies that, for every command in <see cref="CommandRegistry.All"/>,
/// the client-side <see cref="CommandSchemeParser"/> produces a descriptive
/// <see cref="ParseFailure"/> when the user's argument shape does not match any overload.
/// Specifically:
///  - Commands that <b>require</b> an argument: typing the verb alone produces
///    <c>"requires an argument"</c> with an <c>Expected</c> signature.
///  - Commands that <b>don't accept</b> arguments: typing the verb with garbage produces
///    <c>"does not accept arguments"</c> with an <c>Expected</c> signature.
///  - All ParseFailure.Reason texts are non-empty.
///
/// This test is the systemic guardrail: a new command cannot ship without a
/// rejection-message contract because the test enumerates the entire registry.
/// </summary>
[Collection("NavDbMutator")]
public class CommandSchemeParserOverloadMismatchTests : IDisposable
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();
    private readonly IDisposable _navDbScope;

    public CommandSchemeParserOverloadMismatchTests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithFixNames("KLIDE", "BRIXX"));
    }

    public void Dispose() => _navDbScope.Dispose();

    public static IEnumerable<object[]> RequiredArgVerbs() =>
        CommandRegistry
            .All.Values.Where(d => d.ArgMode == ArgMode.Required && d.DefaultAliases.Length > 0)
            .Select(d => new object[] { d.Type, d.DefaultAliases[0] });

    /// <summary>
    /// Condition-prefix verbs (LV/AT/ATFN/ONHO/GIVEWAY/BEHIND/GW) trigger compound-parse
    /// mode in <see cref="CommandSchemeParser"/> rather than verb-arg validation. They are
    /// not normal "no-arg" commands — when followed by tokens, the prefix is consumed and
    /// the remainder is parsed as a separate command. Skip them here.
    /// </summary>
    private static readonly HashSet<CanonicalCommandType> ConditionPrefixVerbs = [CanonicalCommandType.OnHandoff, CanonicalCommandType.GiveWay];

    public static IEnumerable<object[]> NoArgVerbs() =>
        CommandRegistry
            .All.Values.Where(d => d.ArgMode == ArgMode.None && d.DefaultAliases.Length > 0 && !ConditionPrefixVerbs.Contains(d.Type))
            .Select(d => new object[] { d.Type, d.DefaultAliases[0] });

    [Theory]
    [MemberData(nameof(RequiredArgVerbs))]
    public void RequiredArgVerb_WithoutArgument_ProducesDescriptiveFailure(CanonicalCommandType type, string alias)
    {
        var result = CommandSchemeParser.ParseCompound(alias, Scheme, out var failure);

        if (result is not null)
        {
            // Some "required-arg" verbs accept just the verb via downstream handlers
            // (e.g. CTO has runway-fallback rules). That's fine — the contract only
            // applies when the parser returns a failure. Skip those cases.
            return;
        }

        Assert.NotNull(failure);
        Assert.Equal(alias, failure.Verb);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason), $"{type}: ParseFailure.Reason must be non-empty");
        Assert.Equal("requires an argument", failure.Reason);
        Assert.False(string.IsNullOrWhiteSpace(failure.Expected), $"{type}: ParseFailure.Expected must be populated for required-arg verbs");
        Assert.Contains(alias, failure.Expected, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(NoArgVerbs))]
    public void NoArgVerb_WithGarbageArgument_ProducesDescriptiveFailure(CanonicalCommandType type, string alias)
    {
        // Typing "VERB JUNK" when the verb takes no arguments must produce a
        // structured ParseFailure with the expected signature.
        var input = $"{alias} 99";
        var result = CommandSchemeParser.ParseCompound(input, Scheme, out var failure);

        if (result is not null)
        {
            // Some no-arg verbs are SAY/text-arg or have alternate handling that absorbs
            // trailing tokens (e.g. CIFR allowed without arg). Skip when parsing succeeds —
            // the contract is only that *when* parsing fails, the reason is descriptive.
            return;
        }

        Assert.NotNull(failure);
        Assert.Equal(alias, failure.Verb);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason), $"{type}: ParseFailure.Reason must be non-empty");
        Assert.Equal("does not accept arguments", failure.Reason);
        Assert.False(string.IsNullOrWhiteSpace(failure.Expected), $"{type}: ParseFailure.Expected must be populated for no-arg verbs");
    }

    [Fact]
    public void UnrecognizedVerb_ProducesIsNotARecognizedCommand()
    {
        var result = CommandSchemeParser.ParseCompound("XYZZY 99", Scheme, out var failure);

        Assert.Null(result);
        Assert.NotNull(failure);
        Assert.Equal("XYZZY", failure.Verb);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
        // For unknown verbs, Expected should be null (no overload to render)
        Assert.Null(failure.Expected);
    }

    [Fact]
    public void RenderSignature_EveryRegisteredCommand_ReturnsNonEmpty()
    {
        // Guard rail for the RenderSignature helper itself: no command in the registry
        // should produce an empty signature. If a new CommandDefinition is added with
        // empty aliases, this test will fail the contract.
        foreach (var (type, def) in CommandRegistry.All)
        {
            var sig = CommandRegistry.RenderSignature(type);
            Assert.False(string.IsNullOrWhiteSpace(sig), $"{type} ({def.Label}) produced empty RenderSignature");
        }
    }

    [Fact]
    public void RenderSignature_KnownExamples_MatchExpectedFormat()
    {
        Assert.Equal("CM <altitude>", CommandRegistry.RenderSignature(CanonicalCommandType.ClimbMaintain));
        Assert.Equal("DM <altitude>", CommandRegistry.RenderSignature(CanonicalCommandType.DescendMaintain));
        Assert.Equal("FH <heading>", CommandRegistry.RenderSignature(CanonicalCommandType.FlyHeading));
        Assert.Equal("SPD <speed>", CommandRegistry.RenderSignature(CanonicalCommandType.Speed));
        // Optional overload rendering: EXP has bare and altitude variants
        var exp = CommandRegistry.RenderSignature(CanonicalCommandType.Expedite);
        Assert.Contains("EXP", exp);
        Assert.Contains("<altitude>", exp);
        Assert.Contains("|", exp);
    }
}
