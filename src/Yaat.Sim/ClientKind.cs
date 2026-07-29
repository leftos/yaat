namespace Yaat.Sim;

/// <summary>
/// Identifies which YAAT app a connected SignalR client is running. Sent on
/// CreateRoom/JoinRoom so the server can label terminal-broadcast messages
/// (e.g. "joined the room (Flight Strips)") and other RPOs in the room can
/// tell at a glance whether a participant has the full trainer or just the
/// standalone vStrips client.
///
/// Stored verbatim in <c>RoomMember.Kind</c> server-side.
/// </summary>
public static class ClientKind
{
    public const string Main = "main";
    public const string VStrips = "vstrips";
    public const string VTdls = "vtdls";

    /// <summary>
    /// Suffix to append to terminal-broadcast verbs (e.g. "joined the room")
    /// for the given client kind. Returns an empty string for the default
    /// main-client case so existing messages stay unchanged.
    /// </summary>
    public static string DisplaySuffix(string kind) =>
        kind switch
        {
            VStrips => " (Flight Strips)",
            VTdls => " (vTDLS)",
            _ => "",
        };

    /// <summary>
    /// Standalone label for the app a client is running, for surfaces that show the kind on its own
    /// rather than appended to a sentence — the Room Members panel badge. Unlike
    /// <see cref="DisplaySuffix" /> this always names something, so a member list can state which
    /// connections are browser tabs rather than leaving the main-client case blank.
    /// </summary>
    public static string DisplayName(string kind) =>
        kind switch
        {
            VStrips => "Flight Strips",
            VTdls => "vTDLS",
            Main => "YAAT Client",
            _ => kind,
        };
}
