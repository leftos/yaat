// Derived from Zafiro.Avalonia.Mcp (https://github.com/SuperJMN/Zafiro.Avalonia.Mcp), MIT License,
// Copyright (c) 2026 José Manuel Nieto. Modified for YAAT.

using System.Text.Json.Serialization;

namespace Yaat.Client.Automation.Protocol;

/// <summary>The content of a discovery file, <c>&lt;discovery dir&gt;/&lt;pid&gt;.json</c>: where a client finds a running host's pipe.</summary>
public sealed class DiscoveryInfo
{
    [JsonPropertyName("pid")]
    public required int Pid { get; init; }

    [JsonPropertyName("pipeName")]
    public required string PipeName { get; init; }

    [JsonPropertyName("processName")]
    public required string ProcessName { get; init; }

    [JsonPropertyName("startTime")]
    public required DateTimeOffset StartTime { get; init; }

    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }
}
