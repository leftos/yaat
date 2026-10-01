// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// Stable machine-readable codes for <see cref="AutomationError.Code"/>.
/// New codes may be added; existing codes must not change spelling.
/// </summary>
public static class AutomationErrorCodes
{
    public const string MissingSelector = "MISSING_SELECTOR";
    public const string NoMatch = "NO_MATCH";
    public const string AmbiguousSelector = "AMBIGUOUS_SELECTOR";
    public const string StaleNode = "STALE_NODE";
    public const string InvalidParam = "INVALID_PARAM";
    public const string InvalidSelector = "INVALID_SELECTOR";
    public const string UnsupportedOperation = "UNSUPPORTED_OPERATION";
    public const string Timeout = "TIMEOUT";
    public const string Internal = "INTERNAL";
}
