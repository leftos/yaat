using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Threading;

namespace Yaat.Client.Views.Map;

/// <summary>
/// The small menu a map canvas opens at a right-click that hit two or more <see cref="RightClickTarget"/>s. Choosing an
/// entry hands that target back to the canvas, which raises the target's own right-click event so its normal menu opens.
/// </summary>
public sealed class RightClickPicker
{
    /// <summary>The picker currently open, or null when none is.</summary>
    public ContextMenu? Menu { get; private set; }

    /// <summary>
    /// Opens the picker at <paramref name="screenPos"/> on <paramref name="owner"/>, closing any picker still open. The
    /// choice reaches <paramref name="choose"/> after the picker has closed, so the menu it opens is not closed with it.
    /// </summary>
    public void Open(Control owner, Point screenPos, IReadOnlyList<RightClickTarget> targets, Action<RightClickTarget> choose)
    {
        Close();

        var menu = new ContextMenu
        {
            PlacementTarget = owner,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementRect = new Rect(screenPos, new Size(1, 1)),
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
        };
        foreach (RightClickTarget target in targets)
        {
            var item = new MenuItem { Header = target.Label };
            item.Click += (_, _) => Dispatcher.UIThread.Post(() => choose(target));
            menu.Items.Add(item);
        }

        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(Menu, menu))
            {
                Menu = null;
            }
        };
        Menu = menu;
        menu.Open(owner);
    }

    /// <summary>Closes the picker if one is open.</summary>
    public void Close() => Menu?.Close();
}
