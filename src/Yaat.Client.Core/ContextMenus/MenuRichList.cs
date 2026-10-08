namespace Yaat.Client.ContextMenus;

/// <summary>
/// A titled picker of rich rows: a bold <paramref name="Title"/>, a dim <paramref name="Subtitle"/>, the
/// <paramref name="Rows"/> top to bottom, and the row the popup opens on, selected and centred (<paramref name="SelectedIndex"/>).
/// </summary>
public sealed record MenuRichList(string Title, string Subtitle, IReadOnlyList<MenuRichRow> Rows, int SelectedIndex);
