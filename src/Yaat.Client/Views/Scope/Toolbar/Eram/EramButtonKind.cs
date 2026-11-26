namespace Yaat.Client.Views.Scope.Toolbar.Eram;

/// <summary>What a press on an ERAM Master Toolbar button does, as CRC's button classes do it.</summary>
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

/// <summary>The kind CRC's <c>ButtonFactory</c> gives each Master Toolbar button.</summary>
public static class EramButtonKindExtensions
{
    /// <summary>
    /// <paramref name="type"/>'s kind: RANGE and ALT LIM toggle (CRC's <c>ButtonFactory</c> names neither, so both fall
    /// through to its toggle button), VECTOR goes up and down, DELETE TEAROFF commands, and every other Master Toolbar
    /// button opens a menu.
    /// </summary>
    /// <param name="type">The button whose kind is asked for.</param>
    public static EramButtonKind Kind(this EramButtonType type) =>
        type switch
        {
            EramButtonType.RangeView => EramButtonKind.Toggle,
            EramButtonType.AltitudeLimitsMenu => EramButtonKind.Toggle,
            EramButtonType.VectorLength => EramButtonKind.IncDec,
            EramButtonType.DeleteTearoff => EramButtonKind.Command,
            _ => EramButtonKind.Menu,
        };
}
