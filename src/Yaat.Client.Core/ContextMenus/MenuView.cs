namespace Yaat.Client.ContextMenus;

/// <summary>The surface a context menu is built for, so a shared group can keep that surface's variant of its items.</summary>
public enum MenuView
{
    /// <summary>The radar scope.</summary>
    Radar,

    /// <summary>The ground view.</summary>
    Ground,

    /// <summary>The aircraft list.</summary>
    List,
}
