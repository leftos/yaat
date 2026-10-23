using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// What an <see cref="InlineBoxRow"/> makes of its box's text: the <see cref="Command"/> Enter sends, or the
/// <see cref="Refusal"/> naming why the text cannot be sent; neither while the box waits for text.
/// </summary>
internal readonly record struct InlineBoxReading(string? Command, string? Refusal)
{
    /// <summary>A box waiting for text: nothing to send and nothing to refuse.</summary>
    public static InlineBoxReading Blank { get; } = new(null, null);

    /// <summary>Text the row sends as <paramref name="command"/>.</summary>
    public static InlineBoxReading Sends(string command) => new(command, null);

    /// <summary>Text the row cannot send, for <paramref name="reason"/>.</summary>
    public static InlineBoxReading Refuses(string reason) => new(null, reason);
}

/// <summary>What an <see cref="InlineBoxRow"/> shows and how it reads its box.</summary>
internal sealed record InlineBoxSpec
{
    /// <summary>The row's label, over the box: <c>Code</c>.</summary>
    public required string Label { get; init; }

    /// <summary>The glyph leading the row; null for none.</summary>
    public required QuickCommandGlyph? Glyph { get; init; }

    /// <summary>The verb drawn inside the box before the text, in the monospace font: <c>SQ</c>.</summary>
    public required string Verb { get; init; }

    /// <summary>What the empty box reads after the verb.</summary>
    public required string Placeholder { get; init; }

    /// <summary>The box's name.</summary>
    public required string BoxName { get; init; }

    /// <summary>The box's width.</summary>
    public required double BoxWidth { get; init; }

    /// <summary>The text the box opens holding, all of it selected whenever the box takes the focus still holding it; empty for none.</summary>
    public required string Seed { get; init; }

    /// <summary>Reads the box's text as typed.</summary>
    public required Func<string, InlineBoxReading> Read { get; init; }
}

/// <summary>
/// A free-text box row, always open: the glyph, the label with the box under it (its verb drawn inside
/// it before the text) and a reason line under the box, and the command column. The command column follows the box as it is
/// typed in: the command Enter sends; the verb and <c>···</c>, dimmed, while the box waits for text; or, for text the row
/// cannot send, <c>can't send</c> in red, with the box underlined in red and the reason on the line under it.
/// </summary>
internal sealed class InlineBoxRow : Grid
{
    /// <summary>What the command column reads while the box holds text the row cannot send.</summary>
    public const string CantSend = "can't send";

    private readonly InlineBoxSpec _spec;

    private InlineBoxRow(InlineBoxSpec spec, Func<string, Task> send)
    {
        _spec = spec;
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        if (spec.Glyph is { } glyph)
        {
            Viewbox icon = QuickCommandStrip.GlyphIcon(glyph);
            icon.Margin = new Thickness(0, 0, 8, 0);
            Children.Add(icon);
        }

        var verb = new TextBlock
        {
            Text = spec.Verb,
            Opacity = 0.7,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        FlyoutPanelParts.Mono(verb);
        Box = new TextBox
        {
            Name = spec.BoxName,
            PlaceholderText = spec.Placeholder,
            InnerLeftContent = verb,
            Width = spec.BoxWidth,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (spec.Seed.Length > 0)
        {
            Box.Text = spec.Seed;
            SelectTheSeedOnFocus();
        }

        AutomationProperties.SetName(Box, $"{spec.Label} after {spec.Verb}");
        TakeoffFlyout.KeepClicksInside(Box);
        Refusal = new RefusableBox(Box);
        var name = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = spec.Label },
                Refusal.Host,
                Refusal.ReasonLine,
            },
        };
        SetColumn(name, 1);
        Children.Add(name);
        CommandColumn = FlyoutPanelParts.CommandText("");
        SetColumn(CommandColumn, 2);
        Children.Add(CommandColumn);
        Box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                Show();
            }
        };
        Show();
        Item = BuildItem(send);
    }

    /// <summary>The box.</summary>
    public TextBox Box { get; }

    /// <summary>The box's refusal: the red bar over its bottom edge and the reason line under it, while its text cannot be sent.</summary>
    public RefusableBox Refusal { get; }

    /// <summary>The command column.</summary>
    public TextBlock CommandColumn { get; }

    /// <summary>The command Enter sends now; null while the box is blank or its text is refused.</summary>
    public string? Command => Reading.Command;

    /// <summary>Whether the row sends a command now.</summary>
    public bool CanSend => Command is not null;

    /// <summary>The command column's text.</summary>
    public string ShownCommand =>
        Reading switch
        {
            { Command: { } command } => command,
            { Refusal: not null } => CantSend,
            _ => $"{_spec.Verb} {TakeoffFlyout.Unfilled}",
        };

    private InlineBoxReading Reading => _spec.Read(Box.Text ?? "");

    /// <summary>Raised when the box's text changed, after the row shows it.</summary>
    public event EventHandler? Changed;

    /// <summary>The menu item whose header the row is, what a flyout adds to its items (<see cref="Create"/>).</summary>
    public MenuItem Item { get; }

    /// <summary>
    /// A row for <paramref name="spec"/> in its menu item (<see cref="Item"/>), which stays open on its click while the row
    /// cannot send. Enter in the box, or a click on the row, hands the row's command to <paramref name="send"/> and closes the
    /// menu; while the row cannot send, both do nothing.
    /// </summary>
    public static InlineBoxRow Create(InlineBoxSpec spec, Func<string, Task> send) => new(spec, send);

    private MenuItem BuildItem(Func<string, Task> send)
    {
        var item = new MenuItem { Header = this };

        void Follow()
        {
            MenuCommandText.SetCommand(item, Command);
            AutomationProperties.SetName(item, ToString());
            item.StaysOpenOnClick = !CanSend;
        }

        Follow();
        Changed += (_, _) => Follow();
        FlyoutBoxFocus.HandFocusToTheBox(item);
        item.Click += async (_, e) =>
        {
            if (Command is not { } command)
            {
                e.Handled = true;
                return;
            }

            await send(command);
        };
        Box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            if (!CanSend)
            {
                return;
            }

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            item.FindLogicalAncestorOfType<ContextMenu>()?.Close();
        };
        return item;
    }

    /// <summary>
    /// Selects the whole seed each time the box takes the focus while it still holds the seed untouched, so the first keystroke
    /// replaces it; a box losing the focus drops its selection, so a first focus alone would not keep it selected.
    /// </summary>
    private void SelectTheSeedOnFocus()
    {
        Box.GotFocus += (_, _) =>
        {
            if (string.Equals(Box.Text, _spec.Seed, StringComparison.Ordinal))
            {
                Box.SelectAll();
            }
        };
    }

    /// <summary>The row as one line, <c>Label — Command</c>.</summary>
    public override string ToString() => $"{_spec.Label} — {ShownCommand}";

    private void Show()
    {
        InlineBoxReading reading = Reading;
        Refusal.ShowRefusal(reading.Refusal);
        CommandColumn.Text = ShownCommand;
        if (Refusal.IsRefused)
        {
            CommandColumn.Foreground = FlyoutPanelParts.RefusalBrush;
            CommandColumn.Opacity = 1;
        }
        else
        {
            CommandColumn.ClearValue(TextBlock.ForegroundProperty);
            CommandColumn.Opacity = (reading.Command is null) ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 0.7;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
