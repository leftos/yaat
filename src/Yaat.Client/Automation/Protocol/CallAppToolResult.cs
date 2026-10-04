namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// The result of <c>call_app_tool</c>: when the tool ran, <paramref name="Message"/> says what it did (or how the client or the
/// room refused it); when it could not run now, <paramref name="Available"/> is false and <paramref name="Reason"/> says why.
/// </summary>
public sealed record CallAppToolResult(bool Available, string? Reason, string? Message);
