namespace Yaat.Sim;

/// <summary>
/// STARS datablock color as seen on a controller's scope. Maps to CRC's datablock palette:
/// Owned=white, Unowned=green (LimeGreen), Pointout=yellow, Highlighted=cyan.
/// </summary>
public enum StarsDatablockColor
{
    Unowned,
    Owned,
    Pointout,
    Highlighted,
}

/// <summary>
/// STARS datablock detail level as seen on a controller's scope, mirroring CRC's
/// <c>DataBlockFormat</c>: Limited (LDB, unassociated), Partial (PDB, associated but owned by
/// another), Full (FDB).
/// </summary>
public enum StarsDatablockLevel
{
    Limited,
    Partial,
    Full,
}

/// <summary>
/// The student-scope view of a single track: the datablock color, detail level, and leader-line
/// direction a given controller position would see in STARS. <see cref="LeaderDirection"/> is the
/// STARS <c>LeaderDirection</c> enum value (5 = Default).
/// </summary>
public readonly record struct StarsScopeView(StarsDatablockColor Color, StarsDatablockLevel Level, int LeaderDirection);

/// <summary>
/// Computes the STARS datablock view (color / FDB-PDB-LDB level / leader direction) a controller
/// position would see for a track. Mirrors CRC's <c>DisplayElementTracks</c> color and
/// <c>DataBlockFormat</c> logic, dropping only the CRC-client-local inputs the server does not have
/// (QuickLook preferences, beacon-readout mode, force-FDB-for-overflights).
/// </summary>
public static class StarsDatablockClassifier
{
    /// <summary>STARS <c>LeaderDirection.Default</c> sentinel — viewer falls back to its own placement.</summary>
    public const int DefaultLeaderDirection = 5;

    /// <summary>
    /// Classifies how <paramref name="studentPosition"/> (identified by <paramref name="studentTcp"/>)
    /// would see <paramref name="aircraft"/> on a STARS scope. When either student argument is null
    /// (scenario without a student position) returns a neutral default so callers render unchanged.
    /// </summary>
    public static StarsScopeView Classify(AircraftState aircraft, Tcp? studentTcp, TrackOwner? studentPosition)
    {
        if (studentTcp is null || studentPosition is null)
        {
            return new StarsScopeView(StarsDatablockColor.Unowned, StarsDatablockLevel.Full, DefaultLeaderDirection);
        }

        var track = aircraft.Track;
        // SharedState is keyed by Tcp.Id (the ULID) — matching every writer (CRC handler,
        // TickProcessor). ToString() yields the "{Subset}{SectorId}" code, which never matches.
        aircraft.Stars.SharedState.TryGetValue(studentTcp.Id, out var shared);

        bool isOwnedByStudent = track.Owner is not null && studentPosition.MatchesPosition(track.Owner);
        bool isHandoffIn =
            (track.HandoffPeer is not null && studentPosition.MatchesPosition(track.HandoffPeer))
            || (track.HandoffRedirectedBy is not null && studentPosition.MatchesPosition(track.HandoffRedirectedBy));
        bool wasPreviouslyOwned = shared?.WasPreviouslyOwned ?? false;
        bool isHighlighted = shared?.IsHighlighted ?? false;
        bool forceFdb = shared?.ForceFdb ?? false;
        // CRC's DisplayElementTracks colors the recipient's track yellow only while the pointout is
        // pending OR while its recently-accepted "flag" is set — the transient yellow the recipient
        // sees after slewing to accept, forced to a full data block until they slew it to clear. An
        // *accepted* StarsPointout is the persistent owner-side record (CRC keys the sender's 5-second
        // "PO" indicator off it, not the recipient's color), so it must not yellow the recipient: once
        // the recipient clears the flag, sim state still carries the accepted pointout and would
        // otherwise pin the track yellow forever.
        bool isRecentlyAcceptedPointout = shared?.IsRecentlyAcceptedIncomingPointout ?? false;
        bool isPointoutToStudent =
            isRecentlyAcceptedPointout || (track.Pointout is not null && track.Pointout.Recipient.Equals(studentTcp) && track.Pointout.IsPending);

        // "Involved" = the student controls or is directly receiving the track (CRC: owned / previously
        // owned / incoming handoff). These show the owned (white) color and a full data block.
        bool isInvolved = isOwnedByStudent || wasPreviouslyOwned || isHandoffIn;

        // Pointout (yellow) is evaluated only when the student is not otherwise involved. CRC shows
        // yellow over white only in the forced-pointout-to-an-owned-track corner, which YAAT does not
        // model (single-recipient StarsPointout, no forced pointouts) — so involved-wins is exact here.
        StarsDatablockColor color;
        if (isHighlighted)
        {
            color = StarsDatablockColor.Highlighted;
        }
        else if (isInvolved)
        {
            color = StarsDatablockColor.Owned;
        }
        else if (isPointoutToStudent)
        {
            color = StarsDatablockColor.Pointout;
        }
        else
        {
            color = StarsDatablockColor.Unowned;
        }

        StarsDatablockLevel level;
        if (track.Owner is null)
        {
            level = StarsDatablockLevel.Limited;
        }
        else if (isInvolved || forceFdb || isPointoutToStudent)
        {
            level = StarsDatablockLevel.Full;
        }
        else
        {
            level = StarsDatablockLevel.Partial;
        }

        int leaderDirection;
        if (aircraft.Stars.GlobalLeaderDirection is { } global)
        {
            leaderDirection = global;
        }
        else if (shared is not null && shared.LeaderDirection != DefaultLeaderDirection)
        {
            leaderDirection = shared.LeaderDirection;
        }
        else
        {
            leaderDirection = DefaultLeaderDirection;
        }

        return new StarsScopeView(color, level, leaderDirection);
    }
}
