using Yaat.Client.Services;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope's half of the shared ERAM conflict display rule, docs/crc/eram.md, Conflict Alert Processing: "Alerts are only
/// generated if one of the two targets is owned by a controller in your ERAM facility." CRC applies the whole rule per
/// subscriber in yaat-server's <c>CrcBroadcastService.IsEramConflictShown</c>:
/// <code>
/// ((conflict.OwnerFacilityA == facilityId) || (conflict.OwnerFacilityB == facilityId))
///     &amp;&amp; (settings?.ShowsConflict(facilityId, sectorId, conflict.IntruderCallsign is not null) ?? true);
/// </code>
/// The training hub broadcasts to the whole room, so it lists on each pair the owner facilities whose CA settings show it
/// (<see cref="ConflictAlertDto.ShownFacilities"/>, from <c>TrainingBroadcastService.ShownFacilities</c>); this passes the pair
/// when the Scope's own viewer is in one of them (<see cref="ScopeViewer.EramFacilityId"/>). A viewer with no ERAM facility
/// is shown every pair, as every track counts as its own.
/// </summary>
internal static class ScopeEramConflictGate
{
    private static readonly IReadOnlySet<string> NoCallsigns = new HashSet<string>();

    /// <summary>Whether <paramref name="pair"/> is an unsuppressed ERAM pair the §377 rule shows <paramref name="viewer"/>.</summary>
    public static bool IsShown(ConflictAlertDto pair, ScopeViewer viewer) =>
        (pair.Kind == ConflictAlertKind.Eram)
        && !pair.Suppressed
        && ((viewer.EramFacilityId is not { } facility) || pair.ShownFacilities.Contains(facility, StringComparer.Ordinal));

    /// <summary>
    /// The callsigns whose ERAM block flashes for <paramref name="viewer"/>: both members of every pair it is shown
    /// (<see cref="IsShown"/>) but the pair's Mode C intruder, the untracked side CRC draws a conflict data block for instead.
    /// </summary>
    public static IReadOnlySet<string> FlashingCallsigns(IReadOnlyList<ConflictAlertDto>? pairs, ScopeViewer viewer)
    {
        if ((pairs is null) || (pairs.Count == 0))
        {
            return NoCallsigns;
        }

        var flashing = new HashSet<string>(StringComparer.Ordinal);
        foreach (ConflictAlertDto pair in pairs)
        {
            if (IsShown(pair, viewer))
            {
                AddUnlessIntruder(flashing, pair.CallsignA, pair);
                AddUnlessIntruder(flashing, pair.CallsignB, pair);
            }
        }
        return flashing;
    }

    private static void AddUnlessIntruder(HashSet<string> flashing, string callsign, ConflictAlertDto pair)
    {
        if (!string.Equals(callsign, pair.IntruderCallsign, StringComparison.Ordinal))
        {
            flashing.Add(callsign);
        }
    }
}
