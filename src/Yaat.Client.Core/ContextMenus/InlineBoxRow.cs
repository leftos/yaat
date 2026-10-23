using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// What an <see cref="InlineBoxRow"/> makes of its box's text: the <see cref="Command"/> its command column shows, or the
/// <see cref="Refusal"/> naming why the text cannot be sent; neither while the box waits for text or is
/// <see cref="LeftOut"/> of the command. A <see cref="Kept"/> command is a present value the row puts in the command for a
/// blank box. The <see cref="Note"/> is the line under the box: amber for a kept or left-out box, else green, the reading of
/// what was typed; null for none.
/// </summary>
internal readonly record struct InlineBoxReading(string? Command, string? Refusal, bool Kept, bool LeftOut, string? Note)
{
    /// <summary>A box waiting for text: nothing to send and nothing to refuse.</summary>
    public static InlineBoxReading Blank { get; } = new(null, null, false, false, null);

    /// <summary>Text the row sends as <paramref name="command"/>.</summary>
    public static InlineBoxReading Sends(string command) => new(command, null, false, false, null);

    /// <summary>Text the row sends as <paramref name="command"/>, read back on the line under the box as <paramref name="note"/>.</summary>
    public static InlineBoxReading Reads(string command, string note) => new(command, null, false, false, note);

    /// <summary>
    /// A blank box whose row puts the present value <paramref name="command"/> in the command, shown as kept, with
    /// <paramref name="note"/> under the box.
    /// </summary>
    public static InlineBoxReading Keeps(string command, string note) => new(command, null, true, false, note);

    /// <summary>A blank box the command leaves out, <paramref name="note"/> under the box naming what the sim keeps.</summary>
    public static InlineBoxReading LeavesOut(string note) => new(null, null, false, true, note);

    /// <summary>Text the row cannot send, for <paramref name="reason"/>.</summary>
    public static InlineBoxReading Refuses(string reason) => new(null, reason, false, false, null);
}

/// <summary>What an <see cref="InlineBoxRow"/> shows and how it reads its box.</summary>
internal sealed record InlineBoxSpec
{
    /// <summary>The row's label, over the box: <c>Code</c>.</summary>
    public required string Label { get; init; }

    /// <summary>The glyph leading the row; null for none.</summary>
    public required QuickCommandGlyph? Glyph { get; init; }

    /// <summary>
    /// With no <see cref="Glyph"/>, whether the row keeps a blank slot as wide in the glyph column, so its label lines up with
    /// a glyph row's; false leaves the column out.
    /// </summary>
    public required bool BlankGlyphSlot { get; init; }

    /// <summary>The verb drawn inside the box before the text, in the monospace font: <c>SQ</c>; empty for none.</summary>
    public required string Verb { get; init; }

    /// <summary>What the empty box reads after the verb.</summary>
    public required string Placeholder { get; init; }

    /// <summary>The box's name.</summary>
    public required string BoxName { get; init; }

    /// <summary>The box's width.</summary>
    public required double BoxWidth { get; init; }

    /// <summary>The text the box opens holding, all of it selected whenever the box takes the focus still holding it; empty for none.</summary>
    public required string Seed { get; init; }

    /// <summary>
    /// The fixes the box suggests for a partial name (<see cref="IMenuHost.SuggestFixes"/>), which makes the box a fix box, an
    /// autocomplete (<see cref="TakeoffFixBox"/>); null for a plain text box.
    /// </summary>
    public required Func<string, IReadOnlyList<string>>? FixSuggestions { get; init; }

    /// <summary>Reads the box's text as typed.</summary>
    public required Func<string, InlineBoxReading> Read { get; init; }
}

/// <summary>
/// A free-text box row, always open: the glyph (or a blank slot as wide, <see cref="InlineBoxSpec.BlankGlyphSlot"/>), the
/// label with the box under it (its verb drawn inside it before the text) and the line under the box, and the command column.
/// The command column follows the box as it is typed in: the command its text reads as; a kept present value, in amber;
/// <c>—</c>, dimmed, for a box the command leaves out; the verb and <c>···</c>, dimmed, while the box waits for text; or, for
/// text the row cannot send, <c>can't send</c> (a <see cref="CreateSendingAll"/> row: <c>✕</c>) in red, with the box
/// underlined in red and the reason on the line under it. Else the line under the box carries the reading's note, amber for
/// a kept or left-out box, green for typed text. The box is a
/// plain text box, or a fix box offering fix suggestions (<see cref="InlineBoxSpec.FixSuggestions"/>).
/// </summary>
internal sealed class InlineBoxRow : Grid
{
    /// <summary>What a <see cref="Create"/> row's command column reads while the box holds text the row cannot send.</summary>
    public const string CantSend = "can't send";

    /// <summary>
    /// What a <see cref="CreateSendingAll"/> row's command column reads while its box holds text it refuses: the whole command's
    /// own send line says it can't send.
    /// </summary>
    public const string RefusedMark = "✕";

    /// <summary>What the command column reads for a box the command leaves out.</summary>
    public const string LeftOutMark = "—";

    private readonly InlineBoxSpec _spec;
    private readonly Func<InlineBoxRow, string?> _commandToSend;
    private readonly bool _sendsOnClick;

    private InlineBoxRow(InlineBoxSpec spec, Func<InlineBoxRow, string?> commandToSend, bool sendsOnClick, Func<string, Task> send)
    {
        _spec = spec;
        _commandToSend = commandToSend;
        _sendsOnClick = sendsOnClick;
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        Control? glyphCell =
            (spec.Glyph is { } glyph) ? QuickCommandStrip.GlyphIcon(glyph) : (spec.BlankGlyphSlot ? QuickCommandStrip.GlyphSlot() : null);
        if (glyphCell is not null)
        {
            glyphCell.Margin = new Thickness(0, 0, 8, 0);
            Children.Add(glyphCell);
        }

        Input = (spec.FixSuggestions is { } suggestions) ? FixBox(spec, suggestions) : PlainBox(spec);
        Input.Name = spec.BoxName;
        Input.Width = spec.BoxWidth;
        Input.Margin = new Thickness(0, 3, 0, 0);
        Input.HorizontalAlignment = HorizontalAlignment.Left;
        if (spec.Seed.Length > 0)
        {
            Text = spec.Seed;
            SelectTheSeedOnFocus();
        }

        AutomationProperties.SetName(Input, (spec.Verb.Length > 0) ? $"{spec.Label} after {spec.Verb}" : spec.Label);
        TakeoffFlyout.KeepClicksInside(Input);
        Refusal = new RefusableBox(Input);
        NoteLine = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            MaxWidth = 290,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        var name = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = spec.Label },
                Refusal.Host,
                Refusal.ReasonLine,
                NoteLine,
            },
        };
        SetColumn(name, 1);
        Children.Add(name);
        CommandColumn = FlyoutPanelParts.CommandText("");
        SetColumn(CommandColumn, 2);
        Children.Add(CommandColumn);
        Input.PropertyChanged += (_, e) =>
        {
            if ((e.Property == TextBox.TextProperty) || (e.Property == AutoCompleteBox.TextProperty))
            {
                Show();
            }
        };
        Show();
        Item = new MenuItem { Header = this };
        WireItem(send);
    }

    /// <summary>The box: a plain text box, or a fix box (an <see cref="AutoCompleteBox"/>).</summary>
    public TemplatedControl Input { get; }

    /// <summary>The box's text, either kind.</summary>
    public string Text
    {
        get => ((Input is AutoCompleteBox fix) ? fix.Text : ((TextBox)Input).Text) ?? "";
        set
        {
            if (Input is AutoCompleteBox fix)
            {
                fix.Text = value;
            }
            else
            {
                ((TextBox)Input).Text = value;
            }
        }
    }

    /// <summary>The box's refusal: the red bar over its bottom edge and the reason line under it, while its text cannot be sent.</summary>
    public RefusableBox Refusal { get; }

    /// <summary>The line under the box carrying the reading's note (<see cref="InlineBoxReading.Note"/>); hidden while there is none.</summary>
    public TextBlock NoteLine { get; }

    /// <summary>The command column.</summary>
    public TextBlock CommandColumn { get; }

    /// <summary>The command the box's text reads as (a kept present value included); null while the box is blank, left out or refused.</summary>
    public string? Command => Reading.Command;

    /// <summary>Whether the row refuses what its box holds now.</summary>
    public bool IsRefused => Reading.Refusal is not null;

    /// <summary>
    /// The command Enter sends now (a <see cref="Create"/> row's click too): null while the box's own text is refused, else what
    /// the row's command source gives (<see cref="Create"/>: the row's own <see cref="Command"/>; <see cref="CreateSendingAll"/>:
    /// the whole command).
    /// </summary>
    public string? SendsCommand => IsRefused ? null : _commandToSend(this);

    /// <summary>Whether the row sends a command now.</summary>
    public bool CanSend => SendsCommand is not null;

    /// <summary>The command column's text.</summary>
    public string ShownCommand =>
        Reading switch
        {
            { Command: { } command } => command,
            { Refusal: not null } => _sendsOnClick ? CantSend : RefusedMark,
            { LeftOut: true } => LeftOutMark,
            _ => (_spec.Verb.Length > 0) ? $"{_spec.Verb} {TakeoffFlyout.Unfilled}" : TakeoffFlyout.Unfilled,
        };

    private InlineBoxReading Reading => _spec.Read(Text);

    /// <summary>Raised when the box's text changed, after the row shows it.</summary>
    public event EventHandler? Changed;

    /// <summary>The menu item whose header the row is, what a flyout adds to its items (<see cref="Create"/>).</summary>
    public MenuItem Item { get; }

    /// <summary>
    /// A row for <paramref name="spec"/> in its menu item (<see cref="Item"/>), which stays open on its click while the row
    /// cannot send. Enter in the box, or a click on the row, hands the row's command to <paramref name="send"/> and closes the
    /// menu; while the row cannot send, both do nothing.
    /// </summary>
    public static InlineBoxRow Create(InlineBoxSpec spec, Func<string, Task> send) => new(spec, row => row.Command, true, send);

    /// <summary>
    /// A row for <paramref name="spec"/> that is one field of a larger command: its command column shows what its box puts in
    /// that command, while Enter in the box hands <paramref name="wholeCommand"/>'s command to <paramref name="send"/> and closes
    /// the menu, and does nothing while the box's own text is refused or the whole command is null. A click on the row never
    /// sends: it focuses the row's box, the menu staying open. The caller calls <see cref="RefollowSend"/> when another field
    /// changes the whole command or what this row's box reads as.
    /// </summary>
    public static InlineBoxRow CreateSendingAll(InlineBoxSpec spec, Func<string?> wholeCommand, Func<string, Task> send) =>
        new(spec, _ => wholeCommand(), false, send);

    /// <summary>Brings the row up to date with the fields beside it: re-reads its box, whose reading may hang on them, and what it sends.</summary>
    public void RefollowSend()
    {
        ShowReading();
        FollowItem();
    }

    /// <summary>
    /// Focuses the box once laid out and while <paramref name="isOpen"/> still holds: a plain box itself, a fix box the text
    /// box it holds once templated.
    /// </summary>
    public void FocusLater(Func<bool> isOpen) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (isOpen())
                {
                    EntryBox()?.Focus(NavigationMethod.Directional);
                }
            },
            DispatcherPriority.Loaded
        );

    private TextBox? EntryBox() => (Input as TextBox) ?? Input.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();

    private static TextBox PlainBox(InlineBoxSpec spec)
    {
        var box = new TextBox { PlaceholderText = spec.Placeholder };
        if (spec.Verb.Length > 0)
        {
            box.InnerLeftContent = Verb(spec.Verb);
        }

        return box;
    }

    private static TakeoffFixBox FixBox(InlineBoxSpec spec, Func<string, IReadOnlyList<string>> suggestions)
    {
        var box = new TakeoffFixBox
        {
            PlaceholderText = spec.Placeholder,
            MinimumPrefixLength = 1,
            FilterMode = AutoCompleteFilterMode.None,
            AsyncPopulator = (text, _) => Task.FromResult<IEnumerable<object>>([.. suggestions(text ?? "")]),
        };
        if (spec.Verb.Length > 0)
        {
            box.InnerLeftContent = Verb(spec.Verb);
        }

        return box;
    }

    private static TextBlock Verb(string verb)
    {
        var text = new TextBlock
        {
            Text = verb,
            Opacity = 0.7,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        FlyoutPanelParts.Mono(text);
        return text;
    }

    private void WireItem(Func<string, Task> send)
    {
        MenuItem item = Item;
        FollowItem();
        Changed += (_, _) => FollowItem();
        FlyoutBoxFocus.HandFocusToTheBox(item);
        item.Click += async (_, e) =>
        {
            if (!_sendsOnClick)
            {
                e.Handled = true;
                EntryBox()?.Focus(NavigationMethod.Pointer);
                return;
            }

            if (SendsCommand is not { } command)
            {
                e.Handled = true;
                return;
            }

            await send(command);
        };
        Input.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            if (SendsCommand is not { } command)
            {
                return;
            }

            if (_sendsOnClick)
            {
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                item.FindLogicalAncestorOfType<ContextMenu>()?.Close();
                return;
            }

            Task sending = send(command);
            item.FindLogicalAncestorOfType<ContextMenu>()?.Close();
            await sending;
        };
    }

    /// <summary>
    /// Selects the whole seed each time the box takes the focus while it still holds the seed untouched, so the first keystroke
    /// replaces it; a box losing the focus drops its selection, so a first focus alone would not keep it selected. A fix box's
    /// focus lands on the text box it holds.
    /// </summary>
    private void SelectTheSeedOnFocus()
    {
        Input.GotFocus += (_, e) =>
        {
            if (string.Equals(Text, _spec.Seed, StringComparison.Ordinal))
            {
                ((e.Source as TextBox) ?? EntryBox())?.SelectAll();
            }
        };
    }

    /// <summary>The row as one line, <c>Label — Command</c>.</summary>
    public override string ToString() => $"{_spec.Label} — {ShownCommand}";

    /// <summary>
    /// Brings the row's menu item up to date with what it sends now: a <see cref="Create"/> row's click sends the row's command,
    /// a <see cref="CreateSendingAll"/> row's click sends nothing and leaves the menu open.
    /// </summary>
    private void FollowItem()
    {
        MenuCommandText.SetCommand(Item, _sendsOnClick ? SendsCommand : null);
        AutomationProperties.SetName(Item, ToString());
        Item.StaysOpenOnClick = (!_sendsOnClick) || (!CanSend);
    }

    private void Show()
    {
        ShowReading();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ShowReading()
    {
        InlineBoxReading reading = Reading;
        Refusal.ShowRefusal(reading.Refusal);
        NoteLine.Text = reading.Note ?? "";
        NoteLine.IsVisible = reading.Note is not null;
        NoteLine.Foreground = (reading.Kept || reading.LeftOut) ? FlyoutPanelParts.KeptBrush : FlyoutPanelParts.ReadingBrush;
        CommandColumn.Text = ShownCommand;
        if (Refusal.IsRefused)
        {
            CommandColumn.Foreground = FlyoutPanelParts.RefusalBrush;
            CommandColumn.Opacity = 1;
        }
        else if (reading.Kept)
        {
            CommandColumn.Foreground = FlyoutPanelParts.KeptBrush;
            CommandColumn.Opacity = 0.9;
        }
        else
        {
            CommandColumn.ClearValue(TextBlock.ForegroundProperty);
            CommandColumn.Opacity = (reading.Command is null) ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 0.7;
        }
    }
}
