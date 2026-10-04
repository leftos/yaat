namespace Yaat.Client.ContextMenus;

/// <summary>The text a free-text input opens with, and where its caret starts.</summary>
/// <param name="Text">The initial text.</param>
/// <param name="Caret">The caret index into <paramref name="Text"/>.</param>
public sealed record MenuTextSeed(string Text, int Caret);
