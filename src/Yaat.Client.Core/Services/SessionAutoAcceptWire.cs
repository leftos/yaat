namespace Yaat.Client.Services;

/// <summary>
/// The room's auto-accept setting on the wire: one delay in seconds, where any negative value means off. The session
/// flyout shows it as a checkbox and a delay, and keeps the delay while auto-accept is off.
/// </summary>
public static class SessionAutoAcceptWire
{
    /// <summary>The delay to send: the delay when auto-accept is on, -1 when it is off.</summary>
    public static int ToWire(bool enabled, int delaySeconds) => enabled ? delaySeconds : -1;

    /// <summary>The flyout state for a received delay: off with the previous delay kept when negative, else on with it.</summary>
    public static (bool Enabled, int DelaySeconds) FromWire(int wireDelaySeconds, int previousDelaySeconds) =>
        wireDelaySeconds < 0 ? (false, previousDelaySeconds) : (true, wireDelaySeconds);
}
