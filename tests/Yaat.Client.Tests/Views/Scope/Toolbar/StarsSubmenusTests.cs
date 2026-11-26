using Xunit;
using Yaat.Client.Views.Scope;
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
            ["DCB", "BKC", "LST", "LDB", "CMP", "BCN", "PRI", "WX", "WXC"],
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
                ["RR"] = ScopeButtonId.BriteRings,
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
                ["SHIFT"] = StarsDcbPage.Aux,
                ["YAAT"] = StarsDcbPage.Yaat,
            },
            openers
        );
    }

    [Fact]
    public void EverySubmenuEntryWithAButton_NamesAButtonTheToolbarResolves()
    {
        StarsDcbPage[] pages =
        [
            StarsDcbPage.Maps,
            StarsDcbPage.Brite,
            StarsDcbPage.CharSize,
            StarsDcbPage.Pref,
            StarsDcbPage.Aux,
            StarsDcbPage.TpaAtpa,
            StarsDcbPage.Yaat,
            .. YaatMenus,
        ];
        StarsDcbEntry[] withButton = [.. pages.SelectMany(StarsSubmenus.EntriesOf).Where(e => e.Button is not null)];

        Assert.NotEmpty(withButton);
        Assert.All(withButton, e => Assert.NotNull(ScopeToolbarButtons.Find(e.Button!.Value)));
    }

    [Fact]
    public void Aux_IsCrcsAuxPage_WithYaatsLiveTiles()
    {
        StarsDcbEntry[] aux = [.. StarsSubmenus.EntriesOf(StarsDcbPage.Aux)];
        string[] crc =
        [
            "VOL",
            "HISTORY",
            "H_RATE",
            "CURSOR",
            "CSR SPD",
            "MAP",
            "UNCOR",
            "BEACON",
            "RTQC",
            "MCP",
            "DCB",
            "DCB",
            "DCB",
            "DCB",
            "PTL",
            "PTL OWN",
            "PTL ALL",
            "DWELL",
            "TPA/",
            "TSAS",
            "TIME",
            "SHIFT",
        ];

        Assert.Equal(0, StarsSubmenus.DimmedCountOf(StarsDcbPage.Aux));
        Assert.Equal(crc, aux.Select(FirstLine));
        Assert.Equal([F, H, H, F, F, F, F, F, F, F, H, H, H, H, F, H, H, F, F, H, H, F], aux.Select(e => e.Shape));
        Assert.Equal(
            ["VOL", "H_RATE", "CURSOR", "CSR SPD", "MAP", "UNCOR", "BEACON", "RTQC", "MCP", "DCB", "DCB", "DCB", "DCB", "TSAS", "TIME"],
            aux.Where(e => e.Kind == DcbTileKind.Disabled).Select(FirstLine)
        );
        Assert.Equal(("HISTORY\n%value%", DcbTileKind.Spinner, (ScopeButtonId?)ScopeButtonId.Hist), (aux[1].Label, aux[1].Kind, aux[1].Button));
        Assert.Equal(
            ("PTL\nLNTH\n%value%", DcbTileKind.Spinner, (ScopeButtonId?)ScopeButtonId.Vector),
            (aux[14].Label, aux[14].Kind, aux[14].Button)
        );
        Assert.Equal(
            (DcbTileKind.Toggle, (ScopeButtonId?)null, (ScopeVectorOwnership?)ScopeVectorOwnership.Own),
            (aux[15].Kind, aux[15].Button, aux[15].Ownership)
        );
        Assert.Equal(
            (DcbTileKind.Toggle, (ScopeButtonId?)null, (ScopeVectorOwnership?)ScopeVectorOwnership.All),
            (aux[16].Kind, aux[16].Button, aux[16].Ownership)
        );
        Assert.Equal(("DWELL\nON", DcbTileKind.Toggle, (ScopeButtonId?)ScopeButtonId.DbDwell), (aux[17].Label, aux[17].Kind, aux[17].Button));
        Assert.Equal((DcbTileKind.Normal, (StarsDcbPage?)StarsDcbPage.TpaAtpa), (aux[18].Kind, aux[18].Opens));
        Assert.Equal((DcbTileKind.Normal, (StarsDcbPage?)StarsDcbPage.Main), (aux[^1].Kind, aux[^1].Opens));
        Assert.Equal([18, 21], aux.Select((e, i) => (e, i)).Where(p => p.e.Opens is not null).Select(p => p.i));
    }

    [Fact]
    public void TpaAtpa_StartsWithCrcsDimmedAuxButtons_ThenItsItems()
    {
        StarsDcbEntry[] head = [.. StarsSubmenus.EntriesOf(StarsDcbPage.TpaAtpa).Take(19)];
        StarsDcbEntry[] own = [.. Own(StarsDcbPage.TpaAtpa)];

        Assert.Equal(19, StarsSubmenus.DimmedCountOf(StarsDcbPage.TpaAtpa));
        Assert.Equal(StarsSubmenus.Aux.Take(19).Select(e => e.Label), head.Select(e => e.Label));
        Assert.Equal(StarsSubmenus.Aux.Take(19).Select(e => e.Shape), head.Select(e => e.Shape));
        Assert.All(head, e => Assert.Equal((DcbTileKind.Disabled, (StarsDcbPage?)null, (ScopeVectorOwnership?)null), (e.Kind, e.Opens, e.Ownership)));
        Assert.Equal(["A/TPA", "INTRAIL", "ALERT", "MONITOR", "DONE"], own.Select(FirstLine));
        Assert.All(own, e => Assert.Equal(F, e.Shape));
        Assert.Equal(["A/TPA"], own.Where(e => e.Kind == DcbTileKind.Disabled).Select(FirstLine));
        Assert.Equal(
            [ScopeButtonId.DbAtpa, ScopeButtonId.DbAtpaAlertCones, ScopeButtonId.DbAtpaMonitorCones],
            own.Where(e => e.Kind == DcbTileKind.Toggle).Select(e => e.Button!.Value)
        );
        Assert.Equal((DcbTileKind.Normal, (StarsDcbPage?)StarsDcbPage.Aux), (own[^1].Kind, own[^1].Opens));
    }

    [Fact]
    public void OnlyTpaAtpa_OnTheAuxSide_IsASubmenu_AndItsParentIsAux()
    {
        Assert.False(StarsSubmenus.IsSubmenu(StarsDcbPage.Main));
        Assert.False(StarsSubmenus.IsSubmenu(StarsDcbPage.Aux));
        Assert.True(StarsSubmenus.IsSubmenu(StarsDcbPage.TpaAtpa));
        Assert.True(StarsSubmenus.IsSubmenu(StarsDcbPage.Maps));
        Assert.Equal(StarsDcbPage.Aux, StarsSubmenus.ParentOf(StarsDcbPage.TpaAtpa));
        Assert.Equal(StarsDcbPage.Main, StarsSubmenus.ParentOf(StarsDcbPage.Maps));
    }

    private static readonly StarsDcbPage[] YaatMenus =
    [
        StarsDcbPage.YaatProfile,
        StarsDcbPage.YaatTemplate,
        StarsDcbPage.YaatTools,
        StarsDcbPage.YaatDisplay,
        StarsDcbPage.YaatDb,
        StarsDcbPage.YaatViews,
        StarsDcbPage.YaatLens,
    ];

    private static readonly string[] YaatLabels =
    [
        "PROFILE\n%value%",
        "TEMPLATE",
        "OTHERS\n%value%",
        "AUTO",
        "TOOLS",
        "TDM",
        "MVA",
        "DISPLAY",
        "DB",
        "VIEWS",
        "PILOT\nLENS",
        "DONE",
    ];

    [Fact]
    public void Yaat_IsTheYaatPage_InTheMocksOrder()
    {
        StarsDcbEntry[] yaat = [.. StarsSubmenus.EntriesOf(StarsDcbPage.Yaat)];
        const DcbTileKind N = DcbTileKind.Normal;
        const DcbTileKind T = DcbTileKind.Toggle;

        Assert.Equal(0, StarsSubmenus.DimmedCountOf(StarsDcbPage.Yaat));
        Assert.Equal(YaatLabels, yaat.Select(e => e.Label));
        Assert.Equal([F, F, F, F, F, H, H, F, F, F, F, F], yaat.Select(e => e.Shape));
        Assert.Equal([N, N, N, N, N, T, T, N, N, N, N, N], yaat.Select(e => e.Kind));
        Assert.Equal(
            [
                ScopeButtonId.Profile,
                null,
                ScopeButtonId.Others,
                ScopeButtonId.Auto,
                null,
                ScopeButtonId.Tdm,
                ScopeButtonId.Mva,
                null,
                null,
                null,
                ScopeButtonId.Lens,
                null,
            ],
            yaat.Select(e => e.Button)
        );
        Assert.Equal(
            [
                StarsDcbPage.YaatProfile,
                StarsDcbPage.YaatTemplate,
                null,
                null,
                StarsDcbPage.YaatTools,
                null,
                null,
                StarsDcbPage.YaatDisplay,
                StarsDcbPage.YaatDb,
                StarsDcbPage.YaatViews,
                StarsDcbPage.YaatLens,
                StarsDcbPage.Main,
            ],
            yaat.Select(e => e.Opens)
        );
    }

    /// <summary>
    /// Asserts that <paramref name="page"/> starts with the YAAT page's tiles up to its opener, dimmed, and ends with DONE
    /// back to the YAAT page.
    /// </summary>
    private static void AssertYaatMenuFrame(StarsDcbPage page, int dimmed)
    {
        StarsDcbEntry[] all = [.. StarsSubmenus.EntriesOf(page)];

        Assert.Equal(dimmed, StarsSubmenus.DimmedCountOf(page));
        Assert.Equal(YaatLabels.Take(dimmed), all.Take(dimmed).Select(e => e.Label));
        Assert.Equal(StarsSubmenus.EntriesOf(StarsDcbPage.Yaat).Take(dimmed).Select(e => e.Shape), all.Take(dimmed).Select(e => e.Shape));
        Assert.All(all.Take(dimmed), e => Assert.Equal((DcbTileKind.Disabled, (StarsDcbPage?)null), (e.Kind, e.Opens)));
        Assert.Equal(("DONE", F, DcbTileKind.Normal, (StarsDcbPage?)StarsDcbPage.Yaat), (all[^1].Label, all[^1].Shape, all[^1].Kind, all[^1].Opens));
    }

    [Fact]
    public void YaatTools_IsTheToolsTray_AfterTheDimmedYaatTiles()
    {
        StarsDcbEntry[] own = [.. Own(StarsDcbPage.YaatTools).SkipLast(1)];

        AssertYaatMenuFrame(StarsDcbPage.YaatTools, 5);
        Assert.Equal(["J-RING", "CONE", "DRI 5", "DRI 3", "SIZE\n%value%"], own.Select(e => e.Label));
        Assert.Equal([H, H, H, H, F], own.Select(e => e.Shape));
        Assert.Equal([DcbTileKind.Toggle, DcbTileKind.Toggle, DcbTileKind.Toggle, DcbTileKind.Toggle, DcbTileKind.Spinner], own.Select(e => e.Kind));
        Assert.Equal(
            [ScopeButtonId.ToolJRing, ScopeButtonId.ToolCone, ScopeButtonId.ToolDri5, ScopeButtonId.ToolDri3, ScopeButtonId.ToolSize],
            own.Select(e => e.Button!.Value)
        );
    }

    [Fact]
    public void YaatDb_Keeps4thLineAndCaRing_ThenTheAlertsLine4AndDwellBrightness()
    {
        StarsDcbEntry[] own = [.. Own(StarsDcbPage.YaatDb).SkipLast(1)];
        const DcbTileKind T = DcbTileKind.Toggle;
        const DcbTileKind S = DcbTileKind.Spinner;

        AssertYaatMenuFrame(StarsDcbPage.YaatDb, 9);
        Assert.Equal(["4TH LINE", "CA RING", "ALERTS\n%value%", "LINE 4\n%value%", "DWELL\n%value%"], own.Select(e => e.Label));
        Assert.Equal([H, H, F, F, F], own.Select(e => e.Shape));
        Assert.Equal([T, T, S, S, S], own.Select(e => e.Kind));
        Assert.Equal(
            [ScopeButtonId.Db4thLine, ScopeButtonId.DbCaRing, ScopeButtonId.BriteAlerts, ScopeButtonId.BriteLine4, ScopeButtonId.BriteDwell],
            own.Select(e => e.Button!.Value)
        );
        ScopeButtonId[] dropped =
        [
            ScopeButtonId.DbChar,
            ScopeButtonId.DbDwell,
            ScopeButtonId.DbAtpa,
            ScopeButtonId.DbAtpaAlertCones,
            ScopeButtonId.DbAtpaMonitorCones,
        ];
        Assert.DoesNotContain(StarsSubmenus.EntriesOf(StarsDcbPage.YaatDb), e => (e.Button is { } id) && dropped.Contains(id));
    }

    [Fact]
    public void YaatDisplay_KeepsFixWxLockAndDcnf_WithoutOffCntr_ThenRrShow()
    {
        StarsDcbEntry[] own = [.. Own(StarsDcbPage.YaatDisplay).SkipLast(1)];

        AssertYaatMenuFrame(StarsDcbPage.YaatDisplay, 8);
        Assert.Equal(["FIX", "WX", "LOCK", "DCNF\n%value%", "RR\nSHOW"], own.Select(e => e.Label));
        Assert.Equal([H, H, H, H, H], own.Select(e => e.Shape));
        Assert.Equal([DcbTileKind.Toggle, DcbTileKind.Toggle, DcbTileKind.Toggle, DcbTileKind.Normal, DcbTileKind.Toggle], own.Select(e => e.Kind));
        Assert.Equal(
            [ScopeButtonId.DisplayFix, ScopeButtonId.DisplayWx, ScopeButtonId.DisplayLock, ScopeButtonId.DisplayDcnf, ScopeButtonId.RrShow],
            own.Select(e => e.Button!.Value)
        );
        Assert.DoesNotContain(StarsSubmenus.EntriesOf(StarsDcbPage.YaatDisplay), e => e.Label.Replace('\n', ' ') == "OFF CNTR");
    }

    [Fact]
    public void YaatLens_IsTheLensTray_ThenTheLensBrightness()
    {
        StarsDcbEntry[] own = [.. Own(StarsDcbPage.YaatLens).SkipLast(1)];

        AssertYaatMenuFrame(StarsDcbPage.YaatLens, 11);
        Assert.Equal(ScopeToolbarButtons.Find(ScopeButtonId.Lens)!.Children.Select(c => (ScopeButtonId?)c.Id), own.SkipLast(1).Select(e => e.Button));
        Assert.Equal(
            ("LENS\nBRITE\n%value%", F, DcbTileKind.Spinner, (ScopeButtonId?)ScopeButtonId.BriteLens),
            (own[^1].Label, own[^1].Shape, own[^1].Kind, own[^1].Button)
        );
    }

    [Fact]
    public void TheYaatPages_AreSubmenus_UnderMainAndTheYaatPage()
    {
        Assert.True(StarsSubmenus.IsSubmenu(StarsDcbPage.Yaat));
        Assert.Equal(StarsDcbPage.Main, StarsSubmenus.ParentOf(StarsDcbPage.Yaat));
        Assert.Equal(StarsDcbPage.Yaat, StarsSubmenus.ParentOf(StarsDcbPage.YaatTools));
        Assert.All(YaatMenus, page => Assert.True(StarsSubmenus.IsSubmenu(page)));
        Assert.All(YaatMenus, page => Assert.Equal(StarsDcbPage.Yaat, StarsSubmenus.ParentOf(page)));
    }
}
