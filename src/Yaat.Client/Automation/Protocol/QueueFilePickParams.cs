namespace Yaat.Client.Automation.Protocol;

/// <summary>
/// The params of <c>queue_file_pick</c>: exactly one of <c>path</c> (the file the next dialog answers with) or
/// <c>cancel</c> (<c>true</c>, to answer it as a cancel). Both, neither, a blank path or a false cancel is an error.
/// </summary>
public sealed record QueueFilePickParams(string? Path, bool? Cancel);
