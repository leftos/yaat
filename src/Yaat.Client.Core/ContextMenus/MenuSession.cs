using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The controller's session settings an aircraft menu reads, the same for every menu the session opens.
/// </summary>
/// <param name="Initials">The controller's initials, sent with every command.</param>
/// <param name="SoloTrainingMode">True when the surface is driving a solo-training session.</param>
/// <param name="VfrCommandsForIfr">The controller's "VFR commands for IFR aircraft" setting.</param>
/// <param name="QuickCommandLists">
/// Each situation's effective quick-command list: the controller's stored list, else <see cref="QuickCommandDefaults.For"/>.
/// </param>
/// <param name="SoloAdvisoryPrefill">
/// The session's "Pre-fill traffic advisories" setting: whether the solo advisory flyout offers the true clock, miles and
/// direction, or leaves them for the student.
/// </param>
/// <param name="SoloAdvisoryLiveCheck">
/// The session's "Live advisory check" setting: whether the solo advisory flyout grades the call as it is typed.
/// </param>
/// <param name="Scheme">
/// The controller's command scheme, whose primary verbs the rows' command aliases show (<see cref="MenuAliases.Display"/>).
/// </param>
/// <param name="ShowQuickCommandIcons">
/// The controller's "Show quick command icons" setting: whether the menus draw their quick commands as icon strips, or as
/// text rows with the icons off.
/// </param>
/// <param name="ActiveTcp">
/// The TCP code the client acts as (<c>ActiveTcp</c>, which follows <c>AS</c>); null before a scenario loads, when the
/// direction-aware quick rows (Accept, Cancel, Acknowledge) show for no aircraft.
/// </param>
/// <param name="ActivePositionCallsign">
/// The callsign of the position the client acts as; null while the server has not sent it (after a dropdown pick).
/// </param>
/// <param name="ActiveTrackFacilityId">
/// The facility the acting position's tracks are owned under (<c>ActivePositionDto.TrackFacilityId</c>: the parent TRACON for
/// a tower cab), kept across a dropdown pick; null while unknown.
/// </param>
public sealed record MenuSession(
    string Initials,
    bool SoloTrainingMode,
    VfrCommandsForIfr VfrCommandsForIfr,
    Func<AircraftSituation, IReadOnlyList<QuickCommandEntry>> QuickCommandLists,
    SoloAdvisoryPrefill SoloAdvisoryPrefill,
    bool SoloAdvisoryLiveCheck,
    CommandScheme Scheme,
    bool ShowQuickCommandIcons,
    string? ActiveTcp,
    string? ActivePositionCallsign,
    string? ActiveTrackFacilityId
)
{
    /// <summary>The position the client acts as, for <see cref="TrackCoordinationView"/>; null when <see cref="ActiveTcp"/> is unknown.</summary>
    public ActingPosition? Acting =>
        string.IsNullOrEmpty(ActiveTcp) ? null : new ActingPosition(ActivePositionCallsign, ActiveTrackFacilityId, ActiveTcp);
}
