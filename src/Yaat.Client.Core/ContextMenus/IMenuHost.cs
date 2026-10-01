namespace Yaat.Client.ContextMenus;

/// <summary>
/// What a catalog entry's builder needs from the surface that owns the menu. The desktop client adapts its view models onto this.
/// </summary>
public interface IMenuHost
{
    /// <summary>For <paramref name="callsign"/>, sends <paramref name="command"/> on behalf of <paramref name="initials"/>.</summary>
    Task SendAsync(string callsign, string command, string initials);
}
