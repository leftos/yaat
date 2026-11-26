using Xunit;
using Yaat.Client.Views.Scope.Toolbar;
using Yaat.Client.Views.Scope.Toolbar.Dcb;

namespace Yaat.Client.Tests.Views.Scope.Toolbar;

/// <summary>The STARS DCB Main page: CRC's buttons in CRC's order, the ones with no YAAT feature disabled, the rest over a Scope button.</summary>
public class StarsMainPageTests
{
    [Fact]
    public void Entries_AreCrcsMainPage_InCrcsOrder()
    {
        string[] expected =
        [
            "RANGE",
            "PLACE",
            "OFF",
            "RR",
            "PLACE",
            "RR",
            "MAPS",
            "",
            "",
            "",
            "",
            "",
            "",
            "WX1",
            "WX2",
            "WX3",
            "WX4",
            "WX5",
            "WX6",
            "BRITE",
            "LDR DIR",
            "LDR",
            "CHAR",
            "MODE",
            "PREF",
            "SITE",
            "SSA",
            "GI TEXT",
            "SHIFT",
            "YAAT",
        ];

        Assert.Equal(expected, StarsMainPage.Entries.Select(e => e.Label.Split('\n')[0]));
    }

    [Fact]
    public void Entries_TakeCrcsShapes()
    {
        const DcbTileShape F = DcbTileShape.Full;
        const DcbTileShape H = DcbTileShape.Half;
        const DcbTileShape W = DcbTileShape.Weather;
        DcbTileShape[] expected =
        [
            F, // RANGE
            H, // PLACE CNTR
            H, // OFF CNTR
            F, // RR
            H, // PLACE RR
            H, // RR CNTR
            F, // MAPS
            H,
            H,
            H,
            H,
            H,
            H, // map 1-6
            W,
            W,
            W,
            W,
            W,
            W, // WX1-WX6
            F, // BRITE
            H, // LDR DIR
            H, // LDR
            F, // CHAR SIZE
            F, // MODE
            F, // PREF
            F, // SITE
            H, // SSA FILTER
            H, // GI TEXT FILTER
            F, // SHIFT
            F, // YAAT
        ];

        Assert.Equal(expected, StarsMainPage.Entries.Select(e => e.Shape));
    }

    [Fact]
    public void LastEntry_IsTheYaatOpener()
    {
        StarsDcbEntry last = StarsMainPage.Entries[^1];

        Assert.Equal(
            ("YAAT", DcbTileShape.Full, DcbTileKind.Normal, (ScopeButtonId?)null, (StarsDcbPage?)StarsDcbPage.Yaat),
            (last.Label, last.Shape, last.Kind, last.Button, last.Opens)
        );
    }

    [Fact]
    public void DisabledEntries_AreTheButtonsWithNoYaatFeature()
    {
        string[] expected =
        [
            "PLACE\nCNTR",
            "WX1\n",
            "WX2\n",
            "WX3\n",
            "WX4\n",
            "WX5\n",
            "WX6\n",
            "LDR DIR\n%value%",
            "LDR\n%value%",
            "MODE\nFSL",
            "SITE\nFUSED",
            "SSA\nFILTER",
            "GI TEXT\nFILTER",
        ];

        Assert.Equal(expected, StarsMainPage.Entries.Where(e => e.Kind == DcbTileKind.Disabled).Select(e => e.Label));
    }

    [Fact]
    public void EveryEntryWithAButton_NamesAButtonTheToolbarResolves()
    {
        StarsDcbEntry[] withButton = [.. StarsMainPage.Entries.Where(e => e.Button is not null)];

        Assert.NotEmpty(withButton);
        Assert.All(withButton, e => Assert.NotNull(ScopeToolbarButtons.Find(e.Button!.Value)));
    }
}
