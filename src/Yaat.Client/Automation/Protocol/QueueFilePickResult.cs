namespace Yaat.Client.Automation.Protocol;

/// <summary>The result of <c>queue_file_pick</c>: the number of picks waiting after this one was enqueued.</summary>
public sealed record QueueFilePickResult(int Queued);
