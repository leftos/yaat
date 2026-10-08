namespace Yaat.Client.ContextMenus;

/// <summary>
/// One row of a rich-row picker: its mark (<paramref name="Glyph"/>, <c>↑</c>, <c>↓</c>, <c>●</c>, <c>◆</c> or empty), the
/// value as the controller reads it (<paramref name="Label"/>), the grey text at its right (<paramref name="Hint"/>), what
/// it is (<paramref name="Kind"/>), the command a pick sends (<paramref name="Command"/>, null for a row that sends
/// nothing itself), the number typing jumps by (<paramref name="Value"/>, null for a row typing never lands on), and the
/// columns between the value and the hint (<paramref name="Columns"/>: a traffic row's clock position, distance and
/// altitude difference; empty for every other row).
/// </summary>
public sealed record MenuRichRow(
    string Glyph,
    string Label,
    string Hint,
    MenuRichRowKind Kind,
    string? Command,
    int? Value,
    IReadOnlyList<string> Columns
)
{
    /// <summary>Whether the controller can pick the row: it sends a command, or it opens a text box (<see cref="MenuRichRowKind.Prompt"/>).</summary>
    public bool IsPickable => (Command is not null) || (Kind == MenuRichRowKind.Prompt);

    /// <summary>Every field compared by value, with <paramref name="other"/>'s columns compared element by element.</summary>
    public bool Equals(MenuRichRow? other) =>
        (other is not null)
        && (Glyph == other.Glyph)
        && (Label == other.Label)
        && (Hint == other.Hint)
        && (Kind == other.Kind)
        && (Command == other.Command)
        && (Value == other.Value)
        && Columns.SequenceEqual(other.Columns);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Glyph);
        hash.Add(Label);
        hash.Add(Hint);
        hash.Add(Kind);
        hash.Add(Command);
        hash.Add(Value);
        foreach (string column in Columns)
        {
            hash.Add(column);
        }

        return hash.ToHashCode();
    }
}
