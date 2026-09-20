namespace Yaat.Client.Services.Discord;

/// <summary>
/// What the scenario lifecycle sees of Discord rich presence: the activity it wants shown, or
/// nothing. Both calls are fire-and-forget — they never block the caller and never throw, so a
/// missing or wedged Discord cannot affect the client.
/// </summary>
public interface IRichPresencePublisher
{
    /// <summary>Asks for <paramref name="activity"/> to be what Discord shows. Latest call wins.</summary>
    void Publish(DiscordActivity activity);

    /// <summary>Asks for nothing to be shown, which drops the YAAT entry from the user's profile.</summary>
    void Clear();
}
