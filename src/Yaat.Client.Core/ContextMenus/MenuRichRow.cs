namespace Yaat.Client.ContextMenus;

/// <summary>
/// One row of a rich-row picker: its mark (<paramref name="Glyph"/>, <c>↑</c>, <c>↓</c>, <c>●</c>, <c>◆</c> or empty), the
/// value as the controller reads it (<paramref name="Label"/>), the grey text at its right (<paramref name="Hint"/>), what
/// it is (<paramref name="Kind"/>), the command a pick sends (<paramref name="Command"/>, null for a row that cannot be
/// picked) and the number typing jumps by (<paramref name="Value"/>, null for a row typing never lands on).
/// </summary>
public sealed record MenuRichRow(string Glyph, string Label, string Hint, MenuRichRowKind Kind, string? Command, int? Value);
