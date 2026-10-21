using Avalonia.Controls;
using Avalonia.LogicalTree;
using Yaat.Client.Services;

namespace Yaat.Client.ContextMenus;

/// <summary>The solo advisory flyout's menu row header, holding its <see cref="SoloAdvisoryPanel"/>.</summary>
public sealed class SoloAdvisoryFlyoutHost : FlyoutPanelHost
{
    internal SoloAdvisoryFlyoutHost(SoloAdvisoryPanel panel)
        : base("report traffic in sight flyout", "solo-advisory-flyout-panel")
    {
        Panel = panel;
        Child = panel;
    }

    /// <summary>The panel the row holds.</summary>
    public SoloAdvisoryPanel Panel { get; }
}

/// <summary>
/// The solo advisory flyout's content on whichever surface shows it: one menu row holding the panel, refilled as the submenu or
/// the strip's flyout opens and, while the strip's flyout is open, once a second (<see cref="FlyoutLiveTick"/>). While open it
/// follows the host's aircraft updates, which re-run the live check. A sent call that the sim takes closes the flyout and the
/// menu; a refused one keeps it open.
/// </summary>
internal sealed class SoloAdvisoryContent : IStripFlyoutContent
{
    private readonly MenuItem _entry;
    private readonly MenuContext _context;
    private readonly IMenuHost _host;
    private readonly SoloAdvisoryPanel _panel;
    private readonly FlyoutLiveTick _tick;
    private Action? _closeFlyout;
    private bool _open;

    private SoloAdvisoryContent(MenuItem entry, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        _entry = entry;
        _context = context;
        _host = host;
        _panel = new SoloAdvisoryPanel(aircraft, context, host, SendAsync, Close);
        _tick = new FlyoutLiveTick(host.Clock, Refill);
        var holder = new SoloAdvisoryFlyoutHost(_panel);
        var panelRow = new MenuItem
        {
            Header = holder,
            StaysOpenOnClick = true,
            Focusable = false,
        };
        panelRow.Classes.Add(holder.PanelRowClass);
        panelRow.Styles.Add(FlyoutPanelParts.PlainPanelRowStyle(holder.PanelRowClass));
        panelRow.Click += (_, e) => e.Handled = true;
        entry.Items.Add(panelRow);
        entry.PropertyChanged += (_, e) =>
        {
            if (e.Property != MenuItem.IsSubMenuOpenProperty)
            {
                return;
            }

            if (entry.IsSubMenuOpen)
            {
                Refill();
                _panel.Opened();
                _panel.FocusBoxLater(() => entry.IsSubMenuOpen);
            }
            else
            {
                _panel.Reset();
            }
        };
    }

    /// <summary>Gives <paramref name="entry"/> the solo advisory flyout, filled from the host, and has the strip open it as a flyout.</summary>
    public static void Attach(MenuItem entry, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var content = new SoloAdvisoryContent(entry, aircraft, context, host);
        content.Refill();
        QuickCommandStrip.FollowInFlyout(entry, content);
    }

    /// <inheritdoc />
    public void Opened(ItemCollection items, Action close)
    {
        _closeFlyout = close;
        _open = true;
        _tick.Start();
        Refill();
        _panel.Opened();
        _panel.FocusBoxLater(() => _open);
    }

    /// <inheritdoc />
    public void PointerInside(bool inside)
    {
        if (!_tick.IsRunning)
        {
            return;
        }

        _panel.Hold(inside);
        if (!inside)
        {
            Refill();
        }
    }

    /// <inheritdoc />
    public void Closed()
    {
        _tick.Stop();
        _open = false;
        _closeFlyout = null;
        _panel.Reset();
        Refill();
    }

    private void Refill() => _panel.Update(_host.GetAirborneTraffic(_context.Callsign));

    private Task<CommandResultDto?> SendAsync(string command) => _host.SendForResultAsync(_context.Callsign, command, _context.Initials);

    /// <summary>Closes the strip's flyout and the menu, or the submenu (which puts the panel back) and its menu.</summary>
    private void Close()
    {
        if (_closeFlyout is { } close)
        {
            close();
            return;
        }

        _entry.IsSubMenuOpen = false;
        _entry.FindLogicalAncestorOfType<ContextMenu>()?.Close();
    }
}
