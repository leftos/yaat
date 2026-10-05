using Yaat.Client.ContextMenus;
using Yaat.Sim.Commands;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>The one place the UI tests build a <see cref="MenuContext"/>: a canvas click with no multi-selection.</summary>
internal static class TestMenuContext
{
    /// <summary>A context for a right-click on <paramref name="callsign"/>, with an empty <see cref="MenuClick.Selection"/>.</summary>
    public static MenuContext Create(
        string callsign,
        string initials,
        IMenuAircraft? previousSelection,
        bool soloTrainingMode,
        VfrCommandsForIfr vfrCommandsForIfr
    ) =>
        new(
            new MenuClick(callsign, previousSelection, null, []),
            new MenuSession(initials, soloTrainingMode, vfrCommandsForIfr, QuickCommandDefaults.For)
        );
}
