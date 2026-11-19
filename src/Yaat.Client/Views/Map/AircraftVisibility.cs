using Yaat.Client.Models;

namespace Yaat.Client.Views.Map;

/// <summary>
/// Which aircraft a radar-style view draws: the visibility rule the Scope and the Scope's minimum-separation
/// readout share.
/// </summary>
public static class AircraftVisibility
{
    /// <summary>
    /// Selects which aircraft the radar draws. Delayed aircraft are always hidden. On-ground
    /// aircraft are hidden in the normal (non-top-down) view, except when an aircraft has an
    /// active speech bubble whose airport isn't currently shown on the user's ground view — those
    /// are surfaced so a SAY / pilot / WARN prompt for a taxiing aircraft isn't missed (#169).
    /// Pure and static so it can be unit-tested without an Avalonia control instance.
    /// </summary>
    public static IReadOnlyList<AircraftModel> FilterAircraft(
        IReadOnlyList<AircraftModel>? aircraft,
        bool showTopDown,
        bool showSpeechBubbles,
        bool alwaysShowGroundBubbles,
        string? groundShownAirportId,
        DateTime nowUtc
    )
    {
        if (aircraft is null || aircraft.Count == 0)
        {
            return [];
        }

        var result = new List<AircraftModel>(aircraft.Count);
        foreach (AircraftModel ac in aircraft)
        {
            if (IsDrawable(ac, showTopDown, showSpeechBubbles, alwaysShowGroundBubbles, groundShownAirportId, nowUtc))
            {
                result.Add(ac);
            }
        }

        return result;
    }

    /// <summary>The per-aircraft predicate behind <see cref="FilterAircraft"/>: true when the aircraft is drawn.</summary>
    private static bool IsDrawable(
        AircraftModel ac,
        bool showTopDown,
        bool showSpeechBubbles,
        bool alwaysShowGroundBubbles,
        string? groundShownAirportId,
        DateTime nowUtc
    )
    {
        if (ac.IsDelayed)
        {
            return false;
        }

        if (ac.IsOnGround && !showTopDown && !ShouldSurfaceGroundBubble(ac, showSpeechBubbles, alwaysShowGroundBubbles, groundShownAirportId, nowUtc))
        {
            return false;
        }

        // Airborne but the displayed altitude still rounds to 000 (below the acquisition floor):
        // withhold the target from the radar so it matches CRC STARS' coast/skip. The aircraft
        // remains on the ground view until it climbs above the floor. Top-down (ground) mode keeps
        // showing low traffic.
        if (!ac.IsOnGround && ac.BelowDisplayFloor && !showTopDown)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when a ground aircraft carries an active speech bubble the radar should surface. With
    /// <paramref name="alwaysShowGroundBubbles"/> on, any active bubble surfaces; otherwise only
    /// when no open ground view is already showing the aircraft's airport — i.e. its airport
    /// differs from <paramref name="groundShownAirportId"/> (or is unknown).
    /// </summary>
    private static bool ShouldSurfaceGroundBubble(
        AircraftModel ac,
        bool showSpeechBubbles,
        bool alwaysShowGroundBubbles,
        string? groundShownAirportId,
        DateTime nowUtc
    )
    {
        if (!showSpeechBubbles || ac.SpeechBubble is not { } bubble || bubble.ExpiresAt <= nowUtc)
        {
            return false;
        }

        if (alwaysShowGroundBubbles)
        {
            return true;
        }

        return string.IsNullOrEmpty(ac.GroundAirportId)
            || !string.Equals(ac.GroundAirportId, groundShownAirportId, StringComparison.OrdinalIgnoreCase);
    }
}
