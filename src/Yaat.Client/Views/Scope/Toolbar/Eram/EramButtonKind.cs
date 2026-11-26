namespace Yaat.Client.Views.Scope.Toolbar.Eram;

/// <summary>What a press on an ERAM toolbar button does, as CRC's button classes do it.</summary>
public enum EramButtonKind
{
    /// <summary>Opens or closes its menu on the left or the middle button.</summary>
    Menu,

    /// <summary>Toggles its feature on the left or the middle button.</summary>
    Toggle,

    /// <summary>Decrements on the left button and increments on the middle one, with no wheel.</summary>
    IncDec,

    /// <summary>Runs its command on the left or the middle button.</summary>
    Command,
}

/// <summary>The kind CRC's <c>ButtonFactory</c> gives each ERAM toolbar button.</summary>
public static class EramButtonKindExtensions
{
    /// <summary>
    /// <paramref name="type"/>'s kind, as CRC's <c>ButtonFactory</c> builds it: its menu buttons open a menu, its brightness,
    /// font-size, HISTORY, FDB LDR and VECTOR buttons go up and down, DELETE TEAROFF and YAAT's HIDE TOOLBAR command, and every type it names
    /// nowhere falls through to its toggle button (RANGE, ALT LIM, a view, a radar filter or a DB field among them). Two
    /// departures: ATC TOOLS' WX toggles, as YAAT has one weather display and no Weather menu, and CRC's press-and-hold
    /// overrides (VRI, CODE, SPEED, DELAY FORMAT) command, as YAAT has no press-and-hold kind; the PREFSET items command. The
    /// YAAT menu's own buttons take their classic counterpart's kind: its menus open a menu, OTHERS, AUTO, the profile and
    /// template picks, DCNF and OFF CNTR command, SIZE goes up and down, and the rest toggle.
    /// </summary>
    /// <param name="type">The button whose kind is asked for.</param>
    public static EramButtonKind Kind(this EramButtonType type) =>
        type switch
        {
            EramButtonType.AnnotationMenu
            or EramButtonType.AtcToolsMenu
            or EramButtonType.BrightnessMenu
            or EramButtonType.CheckListsMenu
            or EramButtonType.CategoryMenus
            or EramButtonType.CursorMenu
            or EramButtonType.DbFieldsMenu
            or EramButtonType.FontSizeMenu
            or EramButtonType.GeomapMenu
            or EramButtonType.PrefsetMenu
            or EramButtonType.RadarFilterMenu
            or EramButtonType.SdSettingMenu
            or EramButtonType.ViewsMenu
            or EramButtonType.MapBrightnessMenu
            or EramButtonType.CpdlcBrightnessMenu => EramButtonKind.Menu,
            EramButtonType.DeleteTearoff
            or EramButtonType.HideToolbar
            or EramButtonType.VriOverride
            or EramButtonType.BeaconCodeDisplayOverride
            or EramButtonType.SpeedDisplayOverride
            or EramButtonType.DelayFormatOverride => EramButtonKind.Command,
            >= EramButtonType.PrefsetSet and <= EramButtonType.PrefsetDelete => EramButtonKind.Command,
            >= EramButtonType.YaatMenu and <= EramButtonType.YaatLensMenu => EramButtonKind.Menu,
            >= EramButtonType.YaatOthers and <= EramButtonType.YaatDisplayOffCntr => EramButtonKind.Command,
            EramButtonType.YaatToolSize => EramButtonKind.IncDec,
            >= EramButtonType.BackgroundBrightness and <= EramButtonType.DwellBrightness => EramButtonKind.IncDec,
            EramButtonType.MapBcg1 or EramButtonType.MapBcg2 or EramButtonType.NonAdsbBrightness => EramButtonKind.IncDec,
            >= EramButtonType.FdbFontSize and <= EramButtonType.FdbPortalFontSize => EramButtonKind.IncDec,
            EramButtonType.VectorLength or EramButtonType.HistoryFilter or EramButtonType.FdbLeaderLength => EramButtonKind.IncDec,
            _ => EramButtonKind.Toggle,
        };
}
