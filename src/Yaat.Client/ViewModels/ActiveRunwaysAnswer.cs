namespace Yaat.Client.ViewModels;

/// <summary>What one row's text answers: the <c>ARWY</c> command to send, the refusal to show under it, or neither.</summary>
public sealed record ActiveRunwaysAnswer(string? Command, string? Error);
