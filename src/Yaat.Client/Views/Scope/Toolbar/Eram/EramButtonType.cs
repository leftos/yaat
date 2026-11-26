namespace Yaat.Client.Views.Scope.Toolbar.Eram;

/// <summary>
/// One button of CRC's ERAM Master Toolbar, named after the CRC <c>ButtonType</c> it stands for; where it sits in the
/// bar is <see cref="EramMasterLayout.Placements"/>.
/// </summary>
public enum EramButtonType
{
    /// <summary>CRC's DRAW menu for annotations; YAAT has no feature behind it yet.</summary>
    AnnotationMenu,

    /// <summary>CRC's ATC TOOLS menu.</summary>
    AtcToolsMenu,

    /// <summary>CRC's altitude-block setting menu (AB SETTING); YAAT has no feature behind it yet.</summary>
    SdSettingMenu,

    /// <summary>CRC's range toggle, which steps the display's range.</summary>
    RangeView,

    /// <summary>CRC's CURSOR menu; YAAT has no feature behind it yet.</summary>
    CursorMenu,

    /// <summary>CRC's BRIGHT menu of brightness controls.</summary>
    BrightnessMenu,

    /// <summary>CRC's FONT menu of font sizes.</summary>
    FontSizeMenu,

    /// <summary>CRC's DB FIELDS menu of data-block fields.</summary>
    DbFieldsMenu,

    /// <summary>CRC's increment/decrement button for the length of data-block velocity vectors.</summary>
    VectorLength,

    /// <summary>CRC's VIEWS menu.</summary>
    ViewsMenu,

    /// <summary>CRC's CHECK LISTS menu; YAAT has no feature behind it yet.</summary>
    CheckListsMenu,

    /// <summary>CRC's COMMAND MENUS menu; YAAT has no feature behind it yet.</summary>
    CategoryMenus,

    /// <summary>CRC's GeoMap menu, labelled GEOMAP until the active geomap's two lines replace it.</summary>
    GeomapMenu,

    /// <summary>CRC's altitude limits menu; YAAT has no feature behind it yet.</summary>
    AltitudeLimitsMenu,

    /// <summary>CRC's RADAR FILTER menu.</summary>
    RadarFilterMenu,

    /// <summary>CRC's PREFSET menu.</summary>
    PrefsetMenu,

    /// <summary>CRC's DELETE TEAROFF command.</summary>
    DeleteTearoff,
}

/// <summary>The two text lines CRC draws on a Master Toolbar button, and the buttons YAAT has no feature for.</summary>
public static class EramButtonTypeExtensions
{
    /// <summary>
    /// The button's two text lines as CRC draws them, line 1 over line 2, copied from CRC's <c>ButtonTypeExtensions.GetLines</c>;
    /// an empty line draws blank, and GeoMap's is "GEOMAP" until the active geomap's two lines replace it.
    /// </summary>
    /// <param name="type">The button whose label is asked for.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not a Master Toolbar button.</exception>
    public static (string Line1, string Line2) Lines(this EramButtonType type) =>
        type switch
        {
            EramButtonType.AnnotationMenu => ("DRAW", ""),
            EramButtonType.AtcToolsMenu => ("ATC", "TOOLS"),
            EramButtonType.SdSettingMenu => ("AB", "SETTING"),
            EramButtonType.RangeView => ("RANGE", ""),
            EramButtonType.CursorMenu => ("CURSOR", ""),
            EramButtonType.BrightnessMenu => ("BRIGHT", ""),
            EramButtonType.FontSizeMenu => ("FONT", ""),
            EramButtonType.DbFieldsMenu => ("DB", "FIELDS"),
            EramButtonType.VectorLength => ("VECTOR", ""),
            EramButtonType.ViewsMenu => ("VIEWS", ""),
            EramButtonType.CheckListsMenu => ("CHECK", "LISTS"),
            EramButtonType.CategoryMenus => ("COMMAND", "MENUS"),
            EramButtonType.GeomapMenu => ("GEOMAP", ""),
            EramButtonType.AltitudeLimitsMenu => ("ALT LIM", ""),
            EramButtonType.RadarFilterMenu => ("RADAR", "FILTER"),
            EramButtonType.PrefsetMenu => ("PREFSET", ""),
            EramButtonType.DeleteTearoff => ("DELETE", "TEAROFF"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not an ERAM Master Toolbar button"),
        };

    /// <summary>Whether YAAT has a feature behind the button; one it has not is drawn dimmed and takes no input.</summary>
    /// <param name="type">The button asked about.</param>
    public static bool IsBuilt(this EramButtonType type) =>
        type
            is not (
                EramButtonType.AnnotationMenu
                or EramButtonType.SdSettingMenu
                or EramButtonType.CursorMenu
                or EramButtonType.CheckListsMenu
                or EramButtonType.CategoryMenus
                or EramButtonType.AltitudeLimitsMenu
            );
}
