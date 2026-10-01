namespace Yaat.Client.ContextMenus;

/// <summary>
/// The stable identity of every catalog action, so a stored quick-command list survives the menus being
/// rearranged. An identifier is <c>&lt;group&gt;.&lt;item&gt;</c> (<c>tower.cto</c>, <c>heading.fly</c>,
/// <c>ground.pushback</c>), one per action rather than one per placement: the All Commands group the action
/// belongs to is its prefix, and a list stores the action, so moving an action between groups is a deliberate
/// identifier change.
///
/// <para>Identifiers are append-only. An exported preference file carries them, so one is never renamed, reused
/// or deleted — a retired action keeps its constant, so an old file resolves to nothing rather than to a
/// different command.</para>
/// </summary>
public static class MenuIds { }
