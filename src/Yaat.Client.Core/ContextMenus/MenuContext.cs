using Yaat.Sim.Commands;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Everything beside the aircraft itself that a catalog entry's applicability predicate and builder need: who is
/// asking, what was selected before, and which mode the surface is in.
/// </summary>
/// <param name="Callsign">The aircraft the menu was opened on.</param>
/// <param name="Initials">The controller's initials, sent with every command.</param>
/// <param name="PreviousSelection">The aircraft selected before this one, for relative commands; null when there is none.</param>
/// <param name="SoloTrainingMode">True when the surface is driving a solo-training session.</param>
/// <param name="VfrCommandsForIfr">The controller's "VFR commands for IFR aircraft" setting.</param>
public sealed record MenuContext(
    string Callsign,
    string Initials,
    IMenuAircraft? PreviousSelection,
    bool SoloTrainingMode,
    VfrCommandsForIfr VfrCommandsForIfr
);
