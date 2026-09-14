using System.Text.Json;
using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests;

/// <summary>
/// The shareable command-verb file (<c>*.yaat-verbs.json</c>) that Settings → Commands imports and exports.
/// It carries the full scheme, so a round trip must reproduce every command's alias list exactly, and a file
/// from another build must import the commands it shares instead of failing whole.
/// </summary>
public class CommandSchemeFileTests
{
    [Fact]
    public void Serialize_ThenDeserialize_RoundTripsEveryCommand()
    {
        var scheme = CommandScheme.Default();
        scheme.Patterns[CanonicalCommandType.FlyHeading].Aliases = ["HDG", "TURN"];

        var import = CommandSchemeFile.Deserialize(CommandSchemeFile.Serialize(scheme));

        Assert.Empty(import.UnknownCommands);
        Assert.Equal(scheme.Patterns.Count, import.Verbs.Count);
        foreach (var (type, pattern) in scheme.Patterns)
        {
            Assert.Equal(pattern.Aliases, import.Verbs[type]);
        }
    }

    [Fact]
    public void Deserialize_UnknownCommand_ReportedAndSkipped()
    {
        const string json = """{ "verbs": { "FlyHeading": ["HDG"], "NotACommandAtAll": ["XX"] } }""";

        var import = CommandSchemeFile.Deserialize(json);

        Assert.Equal(["HDG"], import.Verbs[CanonicalCommandType.FlyHeading]);
        Assert.Single(import.Verbs);
        Assert.Equal(["NotACommandAtAll"], import.UnknownCommands);
    }

    [Fact]
    public void Deserialize_EmptyAliasList_Skipped()
    {
        // A blank-only list would leave the command with no verb at all, which makes it unreachable —
        // so the entry is dropped and the user keeps whatever verb they already had.
        const string json = """{ "verbs": { "FlyHeading": [], "ClimbMaintain": ["  ", ""], "Speed": [" SPD ", ""] } }""";

        var import = CommandSchemeFile.Deserialize(json);

        Assert.Empty(import.UnknownCommands);
        Assert.False(import.Verbs.ContainsKey(CanonicalCommandType.FlyHeading));
        Assert.False(import.Verbs.ContainsKey(CanonicalCommandType.ClimbMaintain));
        Assert.Equal(["SPD"], import.Verbs[CanonicalCommandType.Speed]);
    }

    [Fact]
    public void Deserialize_MalformedJson_Throws()
    {
        Assert.Throws<JsonException>(() => CommandSchemeFile.Deserialize("{ \"verbs\": "));
    }

    [Fact]
    public void Deserialize_MissingVerbsObject_Throws()
    {
        Assert.Throws<JsonException>(() => CommandSchemeFile.Deserialize("""{ "macros": [] }"""));
    }
}
