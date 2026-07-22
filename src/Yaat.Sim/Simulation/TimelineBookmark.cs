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
public sealed record TimelineBookmark(string Id, double TimeSeconds, string? Name, string? CreatorInitials)
{
    /// <summary>Prefix of every minted <see cref="Id"/>.</summary>
    public const string IdPrefix = "bm-";

    /// <summary>
    /// Normalizes an id typed into the command terminal. Accepts the bare ordinal (<c>3</c>) or the
    /// full id (<c>bm-3</c>, case-insensitive) and produces the canonical <c>bm-3</c> form. Returns
    /// false for anything that isn't a non-negative integer in either shape.
    /// </summary>
    public static bool TryNormalizeId(string token, out string id)
    {
        id = "";
        var trimmed = token.Trim();
        if (trimmed.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[IdPrefix.Length..];
        }

        if (!int.TryParse(trimmed, out var n) || n < 0)
        {
            return false;
        }

        id = $"{IdPrefix}{n}";
        return true;
    }
}

/// <summary>
/// Envelope for the recording archive's <c>bookmarks.json</c> entry. The
/// <see cref="Version"/> guards against future format changes.
/// </summary>
public sealed record RecordingBookmarks(int Version, IReadOnlyList<TimelineBookmark> Bookmarks);
