using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

/// <summary>
/// The command alias a menu row shows beside its label: every catalog id either sends verbs of its own or is named as
/// sending none, and the alias is the controller's primary verb under their command scheme.
/// </summary>
public class MenuAliasesTests
{
    [Fact]
    public void EveryCatalogId_IsInExactlyOneOfVerbsAndNoVerb()
    {
        foreach (MenuCatalogEntry entry in MenuCatalog.All)
        {
            bool hasVerbs = MenuAliases.CanonicalVerbs.ContainsKey(entry.Id);
            bool hasNone = MenuAliases.NoVerb.Contains(entry.Id);
            Assert.True(hasVerbs != hasNone, $"'{entry.Id}' must be in exactly one of CanonicalVerbs and NoVerb.");
        }

        foreach (IReadOnlyList<string> verbs in MenuAliases.CanonicalVerbs.Values)
        {
            Assert.NotEmpty(verbs);
        }

        HashSet<string> catalogIds = [.. MenuCatalog.All.Select(e => e.Id)];
        Assert.DoesNotContain(MenuAliases.CanonicalVerbs.Keys, id => !catalogIds.Contains(id));
        Assert.DoesNotContain(MenuAliases.NoVerb, id => !catalogIds.Contains(id));
    }

    [Theory]
    [InlineData(MenuIds.GroundTaxiToRunway, "TAXI")]
    [InlineData(MenuIds.AltitudeMaintain, "CM / DM")]
    [InlineData(MenuIds.SquawkCode, "SQ …")]
    [InlineData(MenuIds.SimControlWarp, "WARP")]
    [InlineData(MenuIds.SimControlDelete, "DEL")]
    [InlineData(MenuIds.TowerClearedToLand, "CLAND")]
    public void Display_UnderTheDefaultScheme(string id, string expected) => Assert.Equal(expected, MenuAliases.Display(id, CommandScheme.Default()));

    [Theory]
    [InlineData(MenuIds.AircraftCommand)]
    [InlineData(MenuIds.AircraftNote)]
    public void Display_ForAnIdWithNoVerb_IsNull(string id) => Assert.Null(MenuAliases.Display(id, CommandScheme.Default()));

    [Fact]
    public void Display_ShowsTheSchemesPrimaryVerb_NotTheCanonicalOne()
    {
        var scheme = CommandScheme.Default();
        CommandPattern flyHeading = scheme.Patterns[CommandRegistry.AliasToCanonicType["FH"]];
        Assert.Equal(["FH", "H"], flyHeading.Aliases);
        flyHeading.Aliases = ["H", "FH"];

        Assert.Equal("H", MenuAliases.Display(MenuIds.HeadingFly, scheme));
    }
}
