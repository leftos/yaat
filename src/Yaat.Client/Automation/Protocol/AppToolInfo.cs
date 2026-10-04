namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// One entry of <c>list_app_tools</c>: an app tool's name, what it does, its parameters in order, and whether it can run now;
/// <paramref name="Reason"/> says why not, and is null when <paramref name="Available"/>.
/// </summary>
public sealed record AppToolInfo(string Name, string Description, IReadOnlyList<AppToolParameterInfo> Parameters, bool Available, string? Reason);

/// <summary>
/// One parameter of an app tool: its argument name, its type (<c>string</c>, <c>int</c>, <c>double</c> or <c>bool</c>) and
/// what it means.
/// </summary>
public sealed record AppToolParameterInfo(string Name, string Type, string Description);
