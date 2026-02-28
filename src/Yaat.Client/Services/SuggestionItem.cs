namespace Yaat.Client.Services;

public enum SuggestionKind
{
    Callsign,
    Command,
}

public sealed class SuggestionItem
{
    public required SuggestionKind Kind { get; init; }
    public required string Text { get; init; }
    public required string Description { get; init; }
    public required string InsertText { get; init; }
}
