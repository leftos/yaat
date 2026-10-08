namespace Yaat.Client.ContextMenus;

/// <summary>What a row of a rich-row picker (<see cref="MenuRichList"/>) is, which sets its mark, its colour and whether it can be picked.</summary>
public enum MenuRichRowKind
{
    /// <summary>A value above the aircraft's own, marked ↑, sending a climb.</summary>
    Climb,

    /// <summary>A value below the aircraft's own, marked ↓, sending a descent.</summary>
    Descend,

    /// <summary>The value nearest the aircraft's own, marked ●.</summary>
    Now,

    /// <summary>The assigned value, marked ◆.</summary>
    Assigned,

    /// <summary>The value nearest the aircraft's own when it is also the assigned one, marked ●.</summary>
    NowAssigned,

    /// <summary>A value below the minimum vectoring altitude, greyed but still picked and sent.</summary>
    BelowMva,

    /// <summary>The minimum vectoring altitude line between the rows, which cannot be picked.</summary>
    MvaLine,
}
