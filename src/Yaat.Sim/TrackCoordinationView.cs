using Microsoft.Extensions.Logging;

namespace Yaat.Sim;

/// <summary>
/// The position a client acts as: its <c>ActivePosition</c> callsign and facility, and its <c>ActiveTcp</c> code
/// (<see cref="Commands.TrackResolver.AsPrefixCode"/>: <c>{subset}{sector}</c> for STARS, <c>C{sector}</c> for ERAM, else the
/// callsign). A null <see cref="Callsign"/> means the client does not know its callsign yet, so nothing matches by callsign.
/// </summary>
public sealed record ActingPosition(string? Callsign, string? FacilityId, string TcpCode);

/// <summary>
/// One position a track names, as <c>AircraftUpdated</c> carries it: the callsign, the facility id and the sector code
/// (<c>{subset}{sector}</c> for STARS, <c>C{sector}</c> for ERAM, null when none resolves). All null when the track names none.
/// </summary>
public readonly record struct TrackPositionIdentity(string? Callsign, string? FacilityId, string? SectorCode);

/// <summary>
/// Compares the handoff and pointout identities a client receives with the position it acts as, so each client tells a handoff
/// to it from one from it and from one between two others. <see cref="MatchesActing"/> re-expresses
/// <see cref="TrackOwner.MatchesPosition"/> over the identity values and must judge the same pairs.
/// </summary>
public static class TrackCoordinationView
{
    private static readonly ILogger Log = SimLog.CreateLogger("TrackCoordinationView");

    /// <summary>
    /// True when the position named by <paramref name="callsign"/>, <paramref name="facilityId"/> and
    /// <paramref name="sectorCode"/> is the one <paramref name="acting"/> names: the callsigns are equal and the facilities do
    /// not conflict, else the facility and the STARS sector code are equal (OAK_TWR and OAK_GND both use NCT 3O). An ERAM
    /// sector code never matches on its own, as <see cref="TrackOwner.MatchesPosition"/> needs a subset for the TCP match.
    /// </summary>
    public static bool MatchesActing(string? callsign, string? facilityId, string? sectorCode, ActingPosition acting)
    {
        bool sameCallsign =
            (callsign is not null) && (acting.Callsign is not null) && string.Equals(callsign, acting.Callsign, StringComparison.Ordinal);
        if (sameCallsign && FacilitiesAgree(facilityId, acting.FacilityId))
        {
            return true;
        }

        if (sameCallsign && Log.IsEnabled(LogLevel.Trace))
        {
            Log.LogTrace("{Callsign}: facility {Facility} is not the acting facility {ActingFacility}", callsign, facilityId, acting.FacilityId);
        }

        return (facilityId is not null)
            && string.Equals(facilityId, acting.FacilityId, StringComparison.Ordinal)
            && IsStarsSectorCode(sectorCode)
            && string.Equals(sectorCode, acting.TcpCode, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A handoff is in progress to the acting position: the handoff peer, or the position that redirected it, is it.</summary>
    public static bool IsHandoffToActing(TrackPositionIdentity handoffPeer, TrackPositionIdentity redirectedBy, ActingPosition acting) =>
        Matches(handoffPeer, acting) || Matches(redirectedBy, acting);

    /// <summary>
    /// A handoff is in progress from the acting position: a handoff peer is set (by callsign, or by sector code for an
    /// unstaffed sector) and the track's owner is the acting position.
    /// </summary>
    public static bool IsHandoffFromActing(TrackPositionIdentity owner, TrackPositionIdentity handoffPeer, ActingPosition acting) =>
        IsSet(handoffPeer) && Matches(owner, acting);

    /// <summary>
    /// A pointout is pending to the acting position: the pending pointout's recipient code (null unless the pointout is pending)
    /// is the acting TCP code, compared ignoring case as the sector match is.
    /// </summary>
    public static bool IsPointoutToActing(string? pointoutRecipientCode, ActingPosition acting) =>
        (pointoutRecipientCode is not null) && string.Equals(pointoutRecipientCode, acting.TcpCode, StringComparison.OrdinalIgnoreCase);

    private static bool Matches(TrackPositionIdentity position, ActingPosition acting) =>
        MatchesActing(position.Callsign, position.FacilityId, position.SectorCode, acting);

    private static bool IsSet(TrackPositionIdentity position) =>
        !string.IsNullOrEmpty(position.Callsign) || !string.IsNullOrEmpty(position.SectorCode);

    private static bool FacilitiesAgree(string? facilityId, string? actingFacilityId) =>
        (facilityId is null) || (actingFacilityId is null) || string.Equals(facilityId, actingFacilityId, StringComparison.OrdinalIgnoreCase);

    // A STARS code is "{subset}{sector}" and opens with the subset digit; an ERAM code opens with "C".
    private static bool IsStarsSectorCode(string? sectorCode) => !string.IsNullOrEmpty(sectorCode) && char.IsAsciiDigit(sectorCode[0]);
}
