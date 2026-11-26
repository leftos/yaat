using Xunit;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Client.Views.Scope.Toolbar.Dcb;

namespace Yaat.Client.Tests.Views.Scope.Toolbar;

/// <summary>
/// The STARS DCB submenus MAPS, BRITE, CHAR SIZE and PREF: CRC's buttons in CRC's order and shapes, the main buttons CRC
/// dims while each is open, the buttons with no YAAT feature disabled, and the Main page tiles that open them.
/// </summary>
public class StarsSubmenusTests
{
    private const DcbTileShape F = DcbTileShape.Full;
    private const DcbTileShape H = DcbTileShape.Half;

    private static string FirstLine(StarsDcbEntry entry) => entry.Label.Split('\n')[0];

    private static IEnumerable<StarsDcbEntry> Own(StarsDcbPage page) => StarsSubmenus.EntriesOf(page).Skip(StarsSubmenus.DimmedCountOf(page));

    private static string[] Numbers(int count) =>
        [.. Enumerable.Range(1, count).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture))];

    [Theory]
    [InlineData(StarsDcbPage.Maps, 7)]
    [InlineData(StarsDcbPage.Brite, 20)]
    [InlineData(StarsDcbPage.CharSize, 23)]
    [InlineData(StarsDcbPage.Pref, 0)]
    public void EachSubmenu_StartsWithCrcsDimmedMainButtons(StarsDcbPage page, int dimmed)
    {
        StarsDcbEntry[] head = [.. StarsSubmenus.EntriesOf(page).Take(dimmed)];

        Assert.Equal(dimmed, StarsSubmenus.DimmedCountOf(page));
        Assert.Equal(StarsMainPage.Entries.Take(dimmed).Select(e => e.Label), head.Select(e => e.Label));
        Assert.Equal(StarsMainPage.Entries.Take(dimmed).Select(e => e.Shape), head.Select(e => e.Shape));
        Assert.Equal(StarsMainPage.Entries.Take(dimmed).Select(e => e.MapSlot), head.Select(e => e.MapSlot));
        Assert.All(head, e => Assert.Equal(DcbTileKind.Disabled, e.Kind));
        Assert.All(head, e => Assert.Null(e.Opens));
    }

    [Fact]
    public void Maps_IsCrcsMapsMenu()
    {
        StarsDcbEntry[] own = [.. Own(StarsDcbPage.Maps)];

        Assert.Equal(["DONE", "CLR ALL", .. Enumerable.Repeat("", StarsSubmenus.MapSlots), "GEO", "CURRENT"], own.Select(FirstLine));
        Assert.All(own, e => Assert.Equal(H, e.Shape));
        Assert.Equal(StarsDcbPage.Main, own[0].Opens);
        Assert.Equal(ScopeButtonId.MapClear, own[1].Button);
        Assert.Equal(DcbTileKind.Normal, own[1].Kind);
        Assert.Equal(Enumerable.Range(0, StarsSubmenus.MapSlots), own.Skip(2).Take(StarsSubmenus.MapSlots).Select(e => e.MapSlot));
        Assert.All(own.Skip(2).Take(StarsSubmenus.MapSlots), e => Assert.Equal(DcbTileKind.Toggle, e.Kind));
        Assert.Equal(["GEO\nMAPS", "CURRENT"], own.Where(e => e.Kind == DcbTileKind.Disabled).Select(e => e.Label));
    }

    [Fact]
    public void Brite_IsCrcsBriteMenu_OverYaatsGroups()
    {
        StarsDcbEntry[] own = [.. Own(StarsDcbPage.Brite)];
        string[] crc = ["DCB", "BKC", "MPA", "MPB", "FDB", "LST", "POS", "LDB", "OTH", "TLS", "RR", "CMP", "BCN", "PRI", "HST", "WX", "WXC", "DONE"];

        Assert.Equal(crc, own.Select(FirstLine));
        Assert.Equal([.. Enumerable.Repeat(H, 17), F], own.Select(e => e.Shape));
        Assert.Equal(StarsDcbPage.Main, own[^1].Opens);
        Assert.Equal(
            ["DCB", "BKC", "LST", "LDB", "RR", "CMP", "BCN", "PRI", "WX", "WXC"],
            own.Where(e => e.Kind == DcbTileKind.Disabled).Select(FirstLine)
        );
        var live = own.Where(e => e.Kind == DcbTileKind.Spinner).ToDictionary(FirstLine, e => e.Button);
        Assert.Equal(
            new Dictionary<string, ScopeButtonId?>
            {
                ["MPA"] = ScopeButtonId.BriteMapA,
                ["MPB"] = ScopeButtonId.BriteMapB,
                ["FDB"] = ScopeButtonId.BriteFdb,
                ["POS"] = ScopeButtonId.BriteTargets,
                ["OTH"] = ScopeButtonId.BriteTargets,
                ["TLS"] = ScopeButtonId.BriteVector,
                ["HST"] = ScopeButtonId.BriteHist,
            },
            live
        );
        Assert.All(own.Where(e => e.Kind == DcbTileKind.Spinner), e => Assert.EndsWith("\n%value%", e.Label, StringComparison.Ordinal));
    }

    [Fact]
    public void CharSize_IsCrcsCharSizeMenu_WithDataBlocksLive()
    {
        StarsDcbEntry[] own = [.. Own(StarsDcbPage.CharSize)];

        Assert.Equal(["DATA", "LISTS", "DCB", "TOOLS", "POS", "DONE"], own.Select(FirstLine));
        Assert.All(own, e => Assert.Equal(F, e.Shape));
        Assert.Equal(DcbTileKind.Spinner, own[0].Kind);
        Assert.Equal(ScopeButtonId.DbChar, own[0].Button);
        Assert.Equal("DATA\nBLOCKS\n%value%", own[0].Label);
        Assert.All(own.Skip(1).Take(4), e => Assert.Equal(DcbTileKind.Disabled, e.Kind));
        Assert.Equal(StarsDcbPage.Main, own[^1].Opens);
    }

    [Fact]
    public void Pref_IsCrcsPrefMenu()
    {
        StarsDcbEntry[] own = [.. Own(StarsDcbPage.Pref)];

        Assert.Equal(
            [.. Numbers(StarsSubmenus.PrefSlots), "DEFAULT", "FSSTARS", "RESTORE", "SAVE", "CHG PIN", "SAVE AS", "DELETE", "DONE"],
            own.Select(e => e.Label)
        );
        Assert.All(own, e => Assert.Equal(H, e.Shape));
        Assert.Equal(Enumerable.Range(0, StarsSubmenus.PrefSlots), own.Take(StarsSubmenus.PrefSlots).Select(e => e.PrefSlot));
        Assert.Equal(["DEFAULT", "FSSTARS", "RESTORE", "CHG PIN"], own.Where(e => e.Kind == DcbTileKind.Disabled).Select(e => e.Label));
        Assert.Equal(
            [ScopeButtonId.PrefSave, ScopeButtonId.PrefSaveAs, ScopeButtonId.PrefDelete],
            own.Where(e => e.Button is not null).Select(e => e.Button!.Value)
        );
        Assert.Equal(StarsDcbPage.Main, own[^1].Opens);
    }

    [Fact]
    public void TheMainPage_OpensEachSubmenuFromItsButton()
    {
        var openers = StarsMainPage.Entries.Where(e => e.Opens is not null).ToDictionary(FirstLine, e => e.Opens!.Value);

        Assert.Equal(
            new Dictionary<string, StarsDcbPage>
            {
                ["MAPS"] = StarsDcbPage.Maps,
                ["BRITE"] = StarsDcbPage.Brite,
                ["CHAR"] = StarsDcbPage.CharSize,
                ["PREF"] = StarsDcbPage.Pref,
            },
            openers
        );
    }

    [Fact]
    public void EverySubmenuEntryWithAButton_NamesAButtonTheToolbarResolves()
    {
        StarsDcbPage[] pages = [StarsDcbPage.Maps, StarsDcbPage.Brite, StarsDcbPage.CharSize, StarsDcbPage.Pref];
        StarsDcbEntry[] withButton = [.. pages.SelectMany(StarsSubmenus.EntriesOf).Where(e => e.Button is not null)];

        Assert.NotEmpty(withButton);
        Assert.All(withButton, e => Assert.NotNull(ScopeToolbarButtons.Find(e.Button!.Value)));
    }
}
