namespace Yaat.Client.Services;

/// <summary>
/// One alias definition read from a CRC alias file — a line of the form
/// <c>.name replacement text</c>.
/// </summary>
/// <param name="Name">Alias name including the leading dot, e.g. <c>.C172</c>. Matched case-insensitively.</param>
/// <param name="ReplacementTokens">The replacement text, already split on spaces the way CRC tokenizes it.</param>
/// <param name="ArgumentCount">
/// How many <c>$1</c>..<c>$n</c> slots the replacement text consumes. CRC counts them consecutively from
/// <c>$1</c> and stops at the first gap, so a body using <c>$1</c> and <c>$3</c> consumes only one argument.
/// </param>
/// <param name="SourceFile">File the definition came from, for diagnostics.</param>
/// <param name="LineNumber">1-based line within <paramref name="SourceFile" />, for diagnostics.</param>
public sealed record CrcAlias(string Name, IReadOnlyList<string> ReplacementTokens, int ArgumentCount, string SourceFile, int LineNumber)
{
    /// <summary>Replacement text rejoined with single spaces — the form CRC executes.</summary>
    public string ReplacementText => string.Join(" ", ReplacementTokens);
}
