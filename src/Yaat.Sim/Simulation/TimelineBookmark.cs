namespace Yaat.Sim.Simulation;

/// <summary>
/// A user-authored timeline bookmark — a position on the session timeline the
/// instructor flagged as a highlight to scrub back to. Held as server-authoritative
/// per-room state and synced to every connected RPO; persisted inside the recording
/// archive's <c>bookmarks.json</c> entry and read back on load.
/// </summary>
/// <param name="Id">Stable server-minted identifier (e.g. <c>bm-3</c>).</param>
/// <param name="TimeSeconds">Scenario elapsed seconds the bookmark points at.</param>
/// <param name="Name">Optional user-supplied label; null/empty means "unnamed".</param>
/// <param name="CreatorInitials">Initials of the RPO who created the bookmark; null when unknown.</param>
public sealed record TimelineBookmark(string Id, double TimeSeconds, string? Name, string? CreatorInitials);

/// <summary>
/// Envelope for the recording archive's <c>bookmarks.json</c> entry. The
/// <see cref="Version"/> guards against future format changes.
/// </summary>
public sealed record RecordingBookmarks(int Version, IReadOnlyList<TimelineBookmark> Bookmarks);
