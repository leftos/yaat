// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Text.Json.Serialization;

namespace Yaat.Client.Automation.Protocol;

/// <summary>One element of the visual tree as the automation host reports it.</summary>
public sealed class NodeInfo
{
    [JsonPropertyName("nodeId")]
    public int NodeId { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("bounds")]
    public BoundsInfo? Bounds { get; init; }

    [JsonPropertyName("isVisible")]
    public bool IsVisible { get; init; } = true;

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("isEnabled")]
    public bool? IsEnabled { get; init; }

    [JsonPropertyName("isFocused")]
    public bool? IsFocused { get; init; }

    [JsonPropertyName("isInteractive")]
    public bool? IsInteractive { get; init; }

    [JsonPropertyName("automationId")]
    public string? AutomationId { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("className")]
    public string? ClassName { get; init; }

    [JsonPropertyName("parentId")]
    public int? ParentId { get; init; }

    /// <summary>The owning window's node id: an owned window's owner, or the window an overlay popup opens in; null otherwise.</summary>
    [JsonPropertyName("ownerId")]
    public int? OwnerId { get; init; }

    [JsonPropertyName("children")]
    public List<NodeInfo>? Children { get; init; }
}

/// <summary>A rectangle in device-independent pixels.</summary>
public sealed class BoundsInfo
{
    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("width")]
    public double Width { get; init; }

    [JsonPropertyName("height")]
    public double Height { get; init; }
}
