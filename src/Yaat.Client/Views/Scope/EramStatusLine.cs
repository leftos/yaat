using Yaat.Client.ContextMenus;
using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The ERAM FDB line-3 status text (CRC's Field E, <c>FdbRenderObject.GetFieldE</c>) as one text by strict priority, never
/// time-shared: special-purpose code, handoff <c>H</c>, recently accepted <c>O</c>/<c>K</c>, beacon mismatch, <c>NONE</c>,
/// <c>FRZN</c>, then <c>CST</c>. Pure: it reads only the aircraft and the viewer.
/// </summary>
internal static class EramStatusLine
{
    private const string UnknownPeer = "UNK";

    /// <summary>
    /// The status text and whether it blinks (<c>H</c>, <c>CST</c>, and a special-purpose code on a correlated track inside
    /// its blink window); empty when no link applies.
    /// </summary>
    internal static (string Text, bool Blinks) Build(AircraftModel ac, ScopeViewer viewer) =>
        Spc(ac) ?? Handoff(ac) ?? RecentlyAccepted(ac, viewer) ?? Beacon(ac) ?? FrozenOrCoasting(ac) ?? ("", false);

    /// <summary>
    /// The viewer's ERAM sector id and facility: the server's, else the digits of a <c>C</c>-prefixed acting TCP (a dropdown
    /// pick the server never answered) and the acting position's track facility.
    /// </summary>
    internal static (string? SectorId, string? FacilityId) ViewerSector(ScopeViewer viewer) =>
        (viewer.EramSectorId ?? SectorOfTcp(viewer.Acting?.TcpCode), viewer.EramFacilityId ?? viewer.Acting?.FacilityId);

    /// <summary>CRC <c>EramSpecialPurposeCodeExtensions.ToSpc</c>: the label of a special-purpose code, or null.</summary>
    internal static string? SpcLabel(uint code) =>
        code switch
        {
            7500 => "HIJK",
            7600 => "RDOF",
            7700 => "EMRG",
            1276 => "ADIZ",
            7777 => "AFIO",
            7400 => "LLNK",
            _ => null,
        };

    private static string? SectorOfTcp(string? tcp)
    {
        if ((tcp is not { Length: > 1 }) || (tcp[0] != 'C'))
        {
            return null;
        }
        string digits = tcp[1..];
        return digits.All(char.IsAsciiDigit) ? digits : null;
    }

    // A transponder on standby or off replies with no code.
    private static bool ReportsNoCode(AircraftModel ac) => ac.TransponderMode is "Standby" or "Off";

    /// <summary>The special-purpose-code label of a transponder that replies with a code (<see cref="SpcLabel"/>), or null.</summary>
    internal static string? SpcText(AircraftModel ac) => ReportsNoCode(ac) ? null : SpcLabel(ac.BeaconCode);

    private static (string, bool)? Spc(AircraftModel ac) => SpcText(ac) is { } label ? (label, ac.EramIsCorrelated && ac.EramSpcBlinking) : null;

    // The handoff peer exists under the condition AircraftModel.HandoffDisplay uses.
    private static (string, bool)? Handoff(AircraftModel ac) =>
        string.IsNullOrEmpty(ac.HandoffDisplay) ? null : (WithPeer("H", ac.EramHandoffPeerFieldE), true);

    private static (string, bool)? RecentlyAccepted(AircraftModel ac, ScopeViewer viewer)
    {
        if (ac.EramRecentHandoffFromSectorId is not { } sector)
        {
            return null;
        }
        (string? viewerSector, string? viewerFacility) = ViewerSector(viewer);
        bool fromViewer =
            (viewerSector == sector) && string.Equals(ac.EramRecentHandoffFromFacilityId, viewerFacility, StringComparison.OrdinalIgnoreCase);
        return fromViewer ? (WithPeer(ac.EramRecentHandoffWasForced ? "K" : "O", ac.EramRecentHandoffPeerFieldE), false) : null;
    }

    // CRC gates the mismatch and NONE on a correlated track; the mismatch is the judge the STARS block uses.
    private static (string, bool)? Beacon(AircraftModel ac)
    {
        if (!ac.EramIsCorrelated)
        {
            return null;
        }
        if (AircraftCommandApplicability.TryGetSquawkMismatch(ac, out string reported, out _))
        {
            return (reported, false);
        }
        return (ReportsNoCode(ac) && (ac.AssignedBeaconCode != 0)) ? ("NONE", false) : null;
    }

    private static (string, bool)? FrozenOrCoasting(AircraftModel ac)
    {
        if (ac.EramIsFrozen)
        {
            return ("FRZN", false);
        }
        return (ac.EramIsCoastTrack || ac.EramIsCoverageCoasting) ? (" CST", true) : null;
    }

    // CRC: a hyphen joins a peer id of two characters or fewer.
    private static string WithPeer(string mark, string? peer)
    {
        string token = peer ?? UnknownPeer;
        return mark + (token.Length > 2 ? "" : "-") + token;
    }
}
