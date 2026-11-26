namespace Yaat.Client.Views.Scope.Toolbar.Eram;

/// <summary>Where one Master Toolbar button sits in the bar.</summary>
/// <param name="Row">The 0-based row: 0 is the top row.</param>
/// <param name="Column">The 0-based column, left to right.</param>
/// <param name="Type">The button at that cell.</param>
public sealed record EramButtonPlacement(int Row, int Column, EramButtonType Type);

/// <summary>
/// CRC's 2x9 ERAM Master Toolbar as a table (CRC's <c>MasterMenu</c>), with the geometry CRC lays it out with
/// (CRC's <c>ViewToolbars</c> and <c>ButtonBase</c>): two button rows, 2 DIP apart, of a gold strip and a pick area
/// inside CRC's (3, 2) padding and its 1 DIP interior border line.
/// </summary>
public static class EramMasterLayout
{
    /// <summary>The bar's rows.</summary>
    public const int Rows = 2;

    /// <summary>The bar's columns; the second row's last cell is empty.</summary>
    public const int Columns = 9;

    /// <summary>The height of one button: its two text lines and its border.</summary>
    public const double ButtonHeight = 34;

    /// <summary>The width of the gold tear-off strip down a button's left edge.</summary>
    public const double StripWidth = 9;

    /// <summary>How many characters wide a button's pick area is at its narrowest, CRC's minimum (seven).</summary>
    public const int PickAreaChars = 7;

    /// <summary>What CRC adds to seven characters of the toolbar font for a pick area's width.</summary>
    public const double PickAreaExtraWidth = 9;

    /// <summary>How many characters wide the move-down pick area down the bar's left edge is.</summary>
    public const double MoveDownChars = 1.5;

    /// <summary>The 1 DIP border around the move-down pick area.</summary>
    public const double MoveDownBorderWidth = 1;

    /// <summary>The gap between the bar's two button rows.</summary>
    public const double RowGap = 2;

    /// <summary>The gap between two of the bar's columns.</summary>
    public const double ColumnGap = 2;

    /// <summary>CRC's padding down the bar's left and right edges.</summary>
    public const double HorizontalPadding = 3;

    /// <summary>CRC's padding down the bar's top and bottom edges.</summary>
    public const double VerticalPadding = 2;

    /// <summary>The 1 DIP line CRC draws under the bar's buttons.</summary>
    public const double InteriorBorder = 1;

    /// <summary>The bar's height in DIP: its two button rows, their gap, CRC's padding and its interior border line: 75.</summary>
    public const double BarHeight = (2 * ButtonHeight) + RowGap + (2 * VerticalPadding) + InteriorBorder;

    /// <summary>Every button in the bar, each row left to right, from CRC's <c>MasterMenu</c>.</summary>
    public static IReadOnlyList<EramButtonPlacement> Placements { get; } =
    [
        new(0, 0, EramButtonType.AnnotationMenu),
        new(0, 1, EramButtonType.AtcToolsMenu),
        new(0, 2, EramButtonType.SdSettingMenu),
        new(0, 3, EramButtonType.RangeView),
        new(0, 4, EramButtonType.CursorMenu),
        new(0, 5, EramButtonType.BrightnessMenu),
        new(0, 6, EramButtonType.FontSizeMenu),
        new(0, 7, EramButtonType.DbFieldsMenu),
        new(0, 8, EramButtonType.VectorLength),
        new(1, 0, EramButtonType.ViewsMenu),
        new(1, 1, EramButtonType.CheckListsMenu),
        new(1, 2, EramButtonType.CategoryMenus),
        new(1, 3, EramButtonType.GeomapMenu),
        new(1, 4, EramButtonType.AltitudeLimitsMenu),
        new(1, 5, EramButtonType.RadarFilterMenu),
        new(1, 6, EramButtonType.PrefsetMenu),
        new(1, 7, EramButtonType.DeleteTearoff),
    ];

    /// <summary>The button at <paramref name="row"/> and <paramref name="column"/>, or null for the bar's empty last cell.</summary>
    /// <param name="row">The 0-based row.</param>
    /// <param name="column">The 0-based column.</param>
    public static EramButtonType? At(int row, int column)
    {
        foreach (EramButtonPlacement placement in Placements)
        {
            if ((placement.Row == row) && (placement.Column == column))
            {
                return placement.Type;
            }
        }

        return null;
    }
}
