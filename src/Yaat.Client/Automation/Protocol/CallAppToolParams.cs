using System.Text.Json;

namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// The params of <c>call_app_tool</c>: the tool's name, and its arguments as a JSON object keyed by parameter name. Every
/// parameter is required; an unknown tool, a missing or unknown argument, or a value of the wrong JSON kind is <c>INVALID_PARAM</c>.
/// </summary>
public sealed record CallAppToolParams(string Tool, JsonElement Arguments);
