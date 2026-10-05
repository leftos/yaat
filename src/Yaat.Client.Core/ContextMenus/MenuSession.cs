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
public sealed record MenuSession(
    string Initials,
    bool SoloTrainingMode,
    VfrCommandsForIfr VfrCommandsForIfr,
    Func<AircraftSituation, IReadOnlyList<QuickCommandEntry>> QuickCommandLists
);
