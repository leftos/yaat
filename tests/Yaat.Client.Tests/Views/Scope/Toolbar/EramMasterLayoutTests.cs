using Xunit;
using Yaat.Client.Views.Scope.Toolbar.Eram;

namespace Yaat.Client.Tests.Views.Scope.Toolbar;

/// <summary>
/// CRC's ERAM Master Toolbar as a table: the order of its two rows, each button's two text lines, and the buttons YAAT
/// has no feature for yet.
/// </summary>
public class EramMasterLayoutTests
{
    [Fact]
    public void Row0_InCrcOrder()
    {
        EramButtonType[] expected =
        [
            EramButtonType.AnnotationMenu,
            EramButtonType.AtcToolsMenu,
            EramButtonType.SdSettingMenu,
            EramButtonType.RangeView,
            EramButtonType.CursorMenu,
            EramButtonType.BrightnessMenu,
            EramButtonType.FontSizeMenu,
            EramButtonType.DbFieldsMenu,
            EramButtonType.VectorLength,
            EramButtonType.HideToolbar,
        ];

        Assert.Equal(expected, Row(0));
    }

    [Fact]
    public void Row1_InCrcOrder()
    {
        EramButtonType[] expected =
        [
            EramButtonType.ViewsMenu,
            EramButtonType.CheckListsMenu,
            EramButtonType.CategoryMenus,
            EramButtonType.GeomapMenu,
            EramButtonType.AltitudeLimitsMenu,
            EramButtonType.RadarFilterMenu,
            EramButtonType.PrefsetMenu,
            EramButtonType.YaatMenu,
        ];

        Assert.Equal(expected, Row(1));
        Assert.Equal(EramButtonType.YaatMenu, EramMasterLayout.At(1, 8));
    }

    [Fact]
    public void Cell1_7_IsEmpty() => Assert.Null(EramMasterLayout.At(1, 7));

    [Fact]
    public void TenthColumn_HoldsHideToolbarOverAnEmptyCell()
    {
        Assert.Equal(10, EramMasterLayout.Columns);
        Assert.Equal(EramButtonType.HideToolbar, EramMasterLayout.At(0, 9));
        Assert.Null(EramMasterLayout.At(1, 9));
    }

    [Theory]
    [InlineData(EramButtonType.AnnotationMenu, "DRAW", "")]
    [InlineData(EramButtonType.AtcToolsMenu, "ATC", "TOOLS")]
    [InlineData(EramButtonType.SdSettingMenu, "AB", "SETTING")]
    [InlineData(EramButtonType.RangeView, "RANGE", "")]
    [InlineData(EramButtonType.CursorMenu, "CURSOR", "")]
    [InlineData(EramButtonType.BrightnessMenu, "BRIGHT", "")]
    [InlineData(EramButtonType.FontSizeMenu, "FONT", "")]
    [InlineData(EramButtonType.DbFieldsMenu, "DB", "FIELDS")]
    [InlineData(EramButtonType.VectorLength, "VECTOR", "")]
    [InlineData(EramButtonType.ViewsMenu, "VIEWS", "")]
    [InlineData(EramButtonType.CheckListsMenu, "CHECK", "LISTS")]
    [InlineData(EramButtonType.CategoryMenus, "COMMAND", "MENUS")]
    [InlineData(EramButtonType.GeomapMenu, "GEOMAP", "")]
    [InlineData(EramButtonType.AltitudeLimitsMenu, "ALT LIM", "")]
    [InlineData(EramButtonType.RadarFilterMenu, "RADAR", "FILTER")]
    [InlineData(EramButtonType.PrefsetMenu, "PREFSET", "")]
    [InlineData(EramButtonType.YaatMenu, "YAAT", "")]
    [InlineData(EramButtonType.HideToolbar, "HIDE", "TOOLBAR")]
    [InlineData(EramButtonType.ShowToolbar, "SHOW", "TOOLBAR")]
    public void EachButton_HasCrcsTwoLines(EramButtonType type, string line1, string line2) => Assert.Equal((line1, line2), type.Lines());

    [Theory]
    [InlineData(EramButtonType.AnnotationMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.AtcToolsMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.SdSettingMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.RangeView, EramButtonKind.Toggle)]
    [InlineData(EramButtonType.CursorMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.BrightnessMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.FontSizeMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.DbFieldsMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.VectorLength, EramButtonKind.IncDec)]
    [InlineData(EramButtonType.ViewsMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.CheckListsMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.CategoryMenus, EramButtonKind.Menu)]
    [InlineData(EramButtonType.GeomapMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.AltitudeLimitsMenu, EramButtonKind.Toggle)]
    [InlineData(EramButtonType.RadarFilterMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.PrefsetMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.YaatMenu, EramButtonKind.Menu)]
    [InlineData(EramButtonType.HideToolbar, EramButtonKind.Command)]
    [InlineData(EramButtonType.ShowToolbar, EramButtonKind.Command)]
    public void EachButton_HasCrcsKind(EramButtonType type, EramButtonKind kind) => Assert.Equal(kind, type.Kind());

    [Fact]
    public void UnbuiltButtons_AreDisabled()
    {
        EramButtonType[] expected =
        [
            EramButtonType.AnnotationMenu,
            EramButtonType.SdSettingMenu,
            EramButtonType.CursorMenu,
            EramButtonType.CheckListsMenu,
            EramButtonType.CategoryMenus,
            EramButtonType.AltitudeLimitsMenu,
        ];

        EramButtonType[] unbuilt = [.. All().Where(type => !type.IsBuilt()).OrderBy(type => type)];

        Assert.Equal([.. expected.OrderBy(type => type)], unbuilt);
    }

    private static EramButtonType[] Row(int row) =>
        [
            .. EramMasterLayout
                .Placements.Where(placement => placement.Row == row)
                .OrderBy(placement => placement.Column)
                .Select(placement => placement.Type),
        ];

    private static IEnumerable<EramButtonType> All() => EramMasterLayout.Placements.Select(placement => placement.Type);
}
