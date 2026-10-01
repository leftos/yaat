namespace Yaat.Client.Automation.Protocol;

/// <summary>The result of <c>ping</c>: the host's process id and protocol version.</summary>
public sealed record PingResult(int Pid, string ProtocolVersion);
