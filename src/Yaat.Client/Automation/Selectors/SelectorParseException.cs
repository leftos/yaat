// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

namespace Yaat.Client.Automation.Selectors;

/// <summary>A selector that does not parse; <see cref="Position"/> is the zero-based character offset of the fault.</summary>
public sealed class SelectorParseException(string message, int position) : Exception($"Selector parse error at position {position}: {message}")
{
    public int Position { get; } = position;
}
