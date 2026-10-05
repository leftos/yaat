using Yaat.Sim.Commands;
using Yaat.Sim.Situation;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Everything beside the aircraft itself that a catalog entry's applicability predicate and builder need: the click
/// that opened the menu and the controller's session settings.
/// </summary>
/// <param name="Click">The right-clicked aircraft, the previous selection and the list's selected rows.</param>
/// <param name="Session">
/// The controller's initials, solo-training flag, "VFR commands for IFR aircraft" setting and quick-command lists.
/// </param>
public sealed record MenuContext(MenuClick Click, MenuSession Session)
{
    /// <summary>The aircraft the menu was opened on.</summary>
    public string Callsign => Click.Callsign;

    /// <summary>The aircraft selected before this one, for relative commands; null when there is none.</summary>
    public IMenuAircraft? PreviousSelection => Click.PreviousSelection;

    /// <summary>The controller's initials, sent with every command.</summary>
    public string Initials => Session.Initials;

    /// <summary>True when the surface is driving a solo-training session.</summary>
    public bool SoloTrainingMode => Session.SoloTrainingMode;

    /// <summary>The controller's "VFR commands for IFR aircraft" setting.</summary>
    public VfrCommandsForIfr VfrCommandsForIfr => Session.VfrCommandsForIfr;

    /// <summary>The effective quick-command list for <paramref name="situation"/>: the controller's stored list, else the default.</summary>
    public IReadOnlyList<QuickCommandEntry> QuickCommandListFor(AircraftSituation situation) => Session.QuickCommandLists(situation);
}
