using Avalonia.Controls;
using Avalonia.LogicalTree;
using Yaat.Client.Services;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// A panel an advisory flyout (<see cref="AdvisoryFlyoutContent"/>) holds: the solo traffic advisory
/// (<see cref="SoloAdvisoryPanel"/>) or the field advisory (<see cref="FieldAdvisoryPanel"/>).
/// </summary>
internal interface IAdvisoryFlyoutPanel
{
    /// <summary>Restates the panel from the host and the aircraft, as the flyout opens and once a second while the strip's is open.</summary>
    void Refill();

    /// <summary>Holds the panel's list order while <paramref name="inside"/> (the pointer is over the flyout), or lets it go.</summary>
    void Hold(bool inside);

    /// <summary>Starts following the host's aircraft updates as the flyout opens.</summary>
    void Opened();

    /// <summary>Puts the panel back as it opens: no updates followed, no timers, nothing typed; a send in flight is answered to nobody.</summary>
    void Reset();

    /// <summary>Focuses the panel's box once laid out and while <paramref name="isOpen"/> still holds.</summary>
    void FocusBoxLater(Func<bool> isOpen);
}

/// <summary>
/// An advisory flyout's content on whichever surface shows it: one menu row holding the panel, refilled as the submenu or the
/// strip's flyout opens and, while the strip's flyout is open, once a second (<see cref="FlyoutLiveTick"/>). While open the panel
/// follows the host's aircraft updates. A sent advisory the sim takes closes the flyout and the menu; a refused one keeps it open.
/// </summary>
internal sealed class AdvisoryFlyoutContent : IStripFlyoutContent
{
    private readonly MenuItem _entry;
    private readonly MenuContext _context;
    private readonly IMenuHost _host;
    private readonly IAdvisoryFlyoutPanel _panel;
    private readonly FlyoutLiveTick _tick;
    private Action? _closeFlyout;
    private bool _open;

    private AdvisoryFlyoutContent(
        MenuItem entry,
        MenuContext context,
        IMenuHost host,
        Func<AdvisoryFlyoutContent, (FlyoutPanelHost Holder, IAdvisoryFlyoutPanel Panel)> make
    )
    {
        _entry = entry;
        _context = context;
        _host = host;
        (FlyoutPanelHost holder, IAdvisoryFlyoutPanel panel) = make(this);
        _panel = panel;
        _tick = new FlyoutLiveTick(host.Clock, _panel.Refill);
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
                _panel.Refill();
                _panel.Opened();
                _panel.FocusBoxLater(() => entry.IsSubMenuOpen);
            }
            else
            {
                _panel.Reset();
            }
        };
    }

    /// <summary>
    /// Gives <paramref name="entry"/> the panel <paramref name="make"/> builds (it sends and closes through the content it is
    /// handed), filled from the host, and has the strip open it as a flyout.
    /// </summary>
    public static void Attach(
        MenuItem entry,
        MenuContext context,
        IMenuHost host,
        Func<AdvisoryFlyoutContent, (FlyoutPanelHost Holder, IAdvisoryFlyoutPanel Panel)> make
    )
    {
        var content = new AdvisoryFlyoutContent(entry, context, host, make);
        content._panel.Refill();
        QuickCommandStrip.FollowInFlyout(entry, content);
    }

    /// <inheritdoc />
    public void Opened(ItemCollection items, Action close)
    {
        _closeFlyout = close;
        _open = true;
        _tick.Start();
        _panel.Refill();
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
            _panel.Refill();
        }
    }

    /// <inheritdoc />
    public void Closed()
    {
        _tick.Stop();
        _open = false;
        _closeFlyout = null;
        _panel.Reset();
        _panel.Refill();
    }

    /// <summary>
    /// Sends <paramref name="command"/> for the menu's aircraft and hands back the sim's answer
    /// (<see cref="IMenuHost.SendForResultAsync"/>).
    /// </summary>
    public Task<CommandResultDto?> SendAsync(string command) => _host.SendForResultAsync(_context.Callsign, command, _context.Initials);

    /// <summary>Closes the strip's flyout and the menu, or the submenu (which puts the panel back) and its menu.</summary>
    public void Close()
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

/// <summary>
/// An advisory panel's send: one at a time, its answer dropped once the panel has been put back (<see cref="Forget"/>), so a
/// reply never lands in a flyout opened afresh.
/// </summary>
internal sealed class AdvisorySend(Func<string, Task<CommandResultDto?>> send)
{
    /// <summary>The note under the advisory when the send never reached the sim; the advisory is kept.</summary>
    public const string NotSentNote = "Not sent: connection error (see status line)";

    private int _opening;

    /// <summary>Whether a send is in flight.</summary>
    public bool IsSending { get; private set; }

    /// <summary>The note under the advisory when the sim refuses it with <paramref name="message"/>; the values are kept.</summary>
    public static string RefusedNote(string? message) => $"Refused: “{message}”. The values are kept; fix a part and press Enter again.";

    /// <summary>The note for an answer that is not a take: <see cref="NotSentNote"/> or <see cref="RefusedNote"/>.</summary>
    public static string NoteFor(CommandResultDto? result) => (result is null) ? NotSentNote : RefusedNote(result.Message);

    /// <summary>Whether <paramref name="result"/> is the sim taking the advisory.</summary>
    public static bool IsTaken(CommandResultDto? result) => result is { Success: true };

    /// <summary>The note when Enter finds <paramref name="missing"/> parts unset: <c>Set the clock, miles first; Enter sends nothing.</c></summary>
    public static string MissingNote(IEnumerable<AdvisoryPart> missing) =>
        $"Set the {string.Join(", ", missing.Select(SoloAdvisoryText.KindWord))} first; Enter sends nothing.";

    /// <summary>Lets go of a send in flight: its answer goes to nobody.</summary>
    public void Forget()
    {
        _opening++;
        IsSending = false;
    }

    /// <summary>
    /// Sends <paramref name="command"/> and hands back the sim's answer (null when it never reached the sim); false in
    /// <c>Answered</c> when the panel was put back before the answer came.
    /// </summary>
    public async Task<(bool Answered, CommandResultDto? Result)> SendAsync(string command)
    {
        int opening = _opening;
        IsSending = true;
        CommandResultDto? result;
        try
        {
            result = await send(command);
        }
        finally
        {
            if (opening == _opening)
            {
                IsSending = false;
            }
        }

        return (opening == _opening, result);
    }
}
