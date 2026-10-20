using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Yaat.Client.ContextMenus;

/// <summary>What a panel flyout's content asks of its panel as it opens and on each beat.</summary>
internal interface IFlyoutPanel
{
    /// <summary>The panel itself, which the flyout's menu row holds.</summary>
    Control View { get; }

    /// <summary>Puts the panel back as it opens.</summary>
    void Reset();

    /// <summary>Follows the aircraft, in place.</summary>
    void Refresh();

    /// <summary>Focuses where the panel opens, once laid out and while <paramref name="isOpen"/> still holds.</summary>
    void FocusOpeningLater(Func<bool> isOpen);
}

/// <summary>A panel flyout's menu row header, which holds its panel once the flyout first opens.</summary>
/// <param name="outline">The header as one line, for a menu outline, the same before and after the first opening.</param>
/// <param name="panelRowClass">The class of the menu row holding the header, kept at its resting background.</param>
public abstract class FlyoutPanelHost(string outline, string panelRowClass) : Border
{
    /// <summary>The class of the menu row holding the header, kept at its resting background.</summary>
    public string PanelRowClass { get; } = panelRowClass;

    /// <inheritdoc />
    public override string ToString() => outline;
}

/// <summary>
/// A live panel flyout's content on whichever surface shows it, the strip's flyout or All Commands' submenu: one menu row whose
/// header (<see cref="FlyoutPanelHost"/>) holds the panel, built at the first opening, reset and refreshed at each opening, and
/// refreshed once a second while open (<see cref="FlyoutLiveTick"/>). A choice sends and closes the strip's flyout and the
/// menu, or the submenu's menu.
/// </summary>
internal sealed class PanelFlyoutContent : IStripFlyoutContent
{
    private readonly MenuItem _entry;
    private readonly MenuContext _context;
    private readonly IMenuHost _host;
    private readonly Func<Func<string, Task>, Action, IFlyoutPanel> _build;
    private readonly FlyoutPanelHost _holder;
    private IFlyoutPanel? _panel;
    private FlyoutLiveTick? _tick;
    private Action? _closeFlyout;
    private bool _open;

    private PanelFlyoutContent(
        MenuItem entry,
        FlyoutPanelHost holder,
        MenuContext context,
        IMenuHost host,
        Func<Func<string, Task>, Action, IFlyoutPanel> build
    )
    {
        _entry = entry;
        _holder = holder;
        _context = context;
        _host = host;
        _build = build;
        entry.Items.Add(PanelRow(holder));
        entry.PropertyChanged += (_, e) =>
        {
            if (e.Property != MenuItem.IsSubMenuOpenProperty)
            {
                return;
            }

            if (entry.IsSubMenuOpen)
            {
                Open(null);
            }
            else
            {
                Closed();
            }
        };
    }

    /// <summary>
    /// Gives <paramref name="entry"/> its one panel row, held by <paramref name="holder"/>, and has the strip open it as a flyout.
    /// </summary>
    /// <param name="entry">The catalog entry's menu item.</param>
    /// <param name="holder">The panel row's header, which holds the panel once built and names the row's class.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the flyout sends through and whose clock it refreshes on.</param>
    /// <param name="build">Builds the panel from the choose (send and close) and close (close only) actions.</param>
    public static void Attach(
        MenuItem entry,
        FlyoutPanelHost holder,
        MenuContext context,
        IMenuHost host,
        Func<Func<string, Task>, Action, IFlyoutPanel> build
    ) => QuickCommandStrip.FollowInFlyout(entry, new PanelFlyoutContent(entry, holder, context, host, build));

    /// <inheritdoc />
    public void Opened(ItemCollection items, Action close) => Open(close);

    /// <inheritdoc />
    public void PointerInside(bool inside) { }

    /// <inheritdoc />
    public void Closed()
    {
        _tick?.Stop();
        _open = false;
        _closeFlyout = null;
    }

    /// <summary>The menu row holding the panel, which no hover or selection repaints.</summary>
    private static MenuItem PanelRow(FlyoutPanelHost holder)
    {
        // A menu row takes keyboard focus back from whatever inside it gets it, so the row holding the panel is not focusable.
        var panelRow = new MenuItem
        {
            Header = holder,
            StaysOpenOnClick = true,
            Focusable = false,
        };
        panelRow.Classes.Add(holder.PanelRowClass);
        panelRow.Styles.Add(FlyoutPanelParts.PlainPanelRowStyle(holder.PanelRowClass));
        panelRow.Click += (_, e) => e.Handled = true;
        return panelRow;
    }

    private void Open(Action? close)
    {
        _open = true;
        _closeFlyout = close;
        if (_panel is null)
        {
            _panel = _build(ChooseAsync, Close);
            _tick = new FlyoutLiveTick(_host.Clock, _panel.Refresh);
            _holder.Child = _panel.View;
        }

        _panel.Reset();
        _panel.Refresh();
        _tick?.Start();
        _panel.FocusOpeningLater(() => _open);
    }

    /// <summary>Sends <paramref name="command"/> and closes the strip's flyout and the menu, or the submenu's menu.</summary>
    private async Task ChooseAsync(string command)
    {
        Task sending = _host.SendAsync(_context.Callsign, command, _context.Initials);
        Close();
        await sending;
    }

    /// <summary>Closes the strip's flyout and the menu, or the submenu's menu, sending nothing.</summary>
    private void Close()
    {
        if (_closeFlyout is { } close)
        {
            close();
        }
        else
        {
            _entry.FindLogicalAncestorOfType<ContextMenu>()?.Close();
        }
    }
}
