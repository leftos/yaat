// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

namespace Yaat.Client.Automation.Protocol;

/// <summary>The method names a request's <c>method</c> field carries.</summary>
public static class ProtocolMethods
{
    public const string GetTree = "get_tree";
    public const string ListWindows = "list_windows";
    public const string Ping = "ping";
}
