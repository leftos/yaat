namespace Yaat.Client.Services.Discord;

/// <summary>
/// Opens the local IPC channel to a running Discord client. Abstracted so tests can script
/// Discord's side of the conversation over an in-memory duplex stream instead of a real pipe.
/// </summary>
internal interface IDiscordIpcConnector
{
    /// <summary>
    /// Connects to the first available Discord IPC endpoint, or returns null when Discord is not
    /// running. Never throws for the "not running" case — that is the ordinary state on most
    /// machines and must cost nothing.
    /// </summary>
    Task<Stream?> ConnectAsync(CancellationToken cancellationToken);
}
