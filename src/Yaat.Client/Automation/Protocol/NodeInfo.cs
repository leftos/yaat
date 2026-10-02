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

    /// <summary>
    /// The element's bounds translated into its top-level's coordinates: a window's own, or for an overlay popup's content
    /// the window the popup opens in. Null when the element is detached or the translation fails.
    /// </summary>
    [JsonPropertyName("windowBounds")]
    public BoundsInfo? WindowBounds { get; init; }

    [JsonPropertyName("isVisible")]
    public bool IsVisible { get; init; } = true;

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>
    /// A text box's own text, empty when it has none; null for any other element. Unlike <see cref="Text"/>, it never falls
    /// back to the automation name, so an emptied text box reads empty.
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

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
