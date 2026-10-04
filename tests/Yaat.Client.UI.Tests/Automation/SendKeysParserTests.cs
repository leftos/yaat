using Xunit;
using Yaat.Client.Automation.Handlers;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// The <c>send_keys</c> syntax as a pure function: what each string parses to, and where a malformed one faults. A typed
/// stroke reads <c>'text'</c>, a key reads <c>Modifiers+Key</c> (Enter prints as its alias Return).
/// </summary>
public sealed class SendKeysParserTests
{
    [Theory]
    [InlineData("{}}", "'}'")]
    [InlineData("{{}", "'{'")]
    [InlineData("{+}", "'+'")]
    [InlineData("~", "None+Return")]
    [InlineData("^~", "Control+Return")]
    [InlineData("^{F8}", "Control+F8")]
    [InlineData("^^a", "Control+A")]
    [InlineData("^+a", "Control, Shift+A")]
    [InlineData("%^1", "Alt, Control+D1")]
    [InlineData("+{TAB}", "Shift+Tab")]
    [InlineData("{enter}", "None+Return")]
    [InlineData("+a", "'A'")]
    [InlineData("+1", "'!'")]
    [InlineData("ab", "'a' 'b'")]
    [InlineData("😀x", "'😀' 'x'")]
    public void TryParse_WellFormed_ReturnsStrokes(string keys, string expected)
    {
        Assert.True(SendKeysParser.TryParse(keys, out List<KeyStroke>? strokes, out KeysFault? fault), fault?.Message);

        Assert.Equal(expected, string.Join(' ', strokes.Select(Describe)));
    }

    [Theory]
    [InlineData("{}", 0)]
    [InlineData("a^", 1)]
    [InlineData("^{+}", 0)]
    [InlineData("x^ ", 1)]
    [InlineData("a(", 1)]
    [InlineData("ab{FOO}", 2)]
    [InlineData("ab{ENTER", 2)]
    public void TryParse_Malformed_FaultsAtItsPosition(string keys, int position) => AssertFaultsAt(keys, position);

    [Fact]
    public void TryParse_ControlCharacter_FaultsWithAKeyHint()
    {
        KeysFault fault = AssertFaultsAt("ab\ncd", 2);

        Assert.Contains("{ENTER}", fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_UnpairedSurrogates_Fault()
    {
        AssertFaultsAt("a" + '\uD83D', 1);
        AssertFaultsAt("ab" + '\uDE00' + "c", 2);
    }

    private static KeysFault AssertFaultsAt(string keys, int position)
    {
        Assert.False(SendKeysParser.TryParse(keys, out _, out KeysFault? fault));
        Assert.Equal(position, fault.Position);
        Assert.Contains($"position {position}", fault.Message, StringComparison.Ordinal);
        return fault;
    }

    private static string Describe(KeyStroke stroke) => (stroke.Text is not null) ? $"'{stroke.Text}'" : $"{stroke.Modifiers}+{stroke.Key}";
}
