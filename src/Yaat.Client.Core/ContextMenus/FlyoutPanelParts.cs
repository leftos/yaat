using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The parts the panel flyouts (Taxi to runway, Air taxi, Cleared / Expect approach, heading, altitude, speed, Direct to) share:
/// the style that keeps the one menu row holding a panel at its resting background, the panel's separator line, the title line,
/// the monospace font, the box's opening focus, the verb toggle, the box row, the chip, the command column, and the brushes of
/// a refusal and of the present value's mark.
/// </summary>
internal static class FlyoutPanelParts
{
    private static readonly IImmutableSolidColorBrush SeparatorBrush = new ImmutableSolidColorBrush(Color.Parse("#3D3D3D"));
    private static readonly IImmutableSolidColorBrush ToggleLine = new ImmutableSolidColorBrush(Color.Parse("#3A414B"));
    private static readonly IImmutableSolidColorBrush ToggleFill = new ImmutableSolidColorBrush(Color.Parse("#23272C"));
    private static readonly IImmutableSolidColorBrush ToggleOnFill = new ImmutableSolidColorBrush(Color.Parse("#24406A"));
    private static readonly IImmutableSolidColorBrush ChipLine = new ImmutableSolidColorBrush(Color.Parse("#33383F"));
    private static readonly IImmutableSolidColorBrush CodeBrush = new ImmutableSolidColorBrush(Color.Parse("#9AA3AD"));

    /// <summary>The grey of a panel flyout's title line and its other quiet labels.</summary>
    public static IImmutableSolidColorBrush TitleBrush { get; } = new ImmutableSolidColorBrush(Color.Parse("#A4ADB8"));

    /// <summary>The red of a box's refusal (<c>Not a valid altitude</c>).</summary>
    public static IImmutableSolidColorBrush RefusalBrush { get; } = new ImmutableSolidColorBrush(Color.Parse("#E08A8A"));

    /// <summary>The fill of the present value's row or chip.</summary>
    public static IImmutableSolidColorBrush NowFill { get; } = new ImmutableSolidColorBrush(Color.Parse("#2F4A6E"));

    /// <summary>The edge of the present value's row or chip, and the assigned altitude's glyph.</summary>
    public static IImmutableSolidColorBrush NowEdge { get; } = new ImmutableSolidColorBrush(Color.Parse("#5B8DEF"));

    /// <summary>
    /// Keeps a menu row of class <paramref name="panelClass"/> at its resting background while it is selected or under the
    /// pointer.
    /// </summary>
    public static Style PlainPanelRowStyle(string panelClass)
    {
        Selector LayoutRoot(Selector? x, string state) =>
            x.OfType<MenuItem>().Class(panelClass).Class(state).Template().OfType<Border>().Name("PART_LayoutRoot");

        return new Style(x => Selectors.Or(LayoutRoot(x, ":selected"), LayoutRoot(x, ":pointerover")))
        {
            Setters = { new Setter(Border.BackgroundProperty, Brushes.Transparent) },
        };
    }

    /// <summary>The title line: <paramref name="title"/> left, <paramref name="now"/> right in the monospace font.</summary>
    public static Grid TitleLine(TextBlock title, TextBlock now)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(11, 6, 11, 2) };
        grid.Children.Add(title);
        now.Margin = new Thickness(12, 0, 0, 0);
        Mono(now);
        Grid.SetColumn(now, 1);
        grid.Children.Add(now);
        return grid;
    }

    /// <summary>Draws <paramref name="text"/> in the monospace font.</summary>
    public static void Mono(TextBlock text) => text.Bind(TextBlock.FontFamilyProperty, text.GetResourceObservable(QuickCommandStrip.MonoFontKey));

    /// <summary>
    /// Focuses <paramref name="box"/> once laid out and while <paramref name="isOpen"/> still holds, then runs
    /// <paramref name="after"/>.
    /// </summary>
    public static void FocusLater(TextBox box, Func<bool> isOpen, Action after) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!isOpen())
                {
                    return;
                }

                box.Focus(NavigationMethod.Directional);
                after();
            },
            DispatcherPriority.Loaded
        );

    /// <summary>A one-pixel separator line across the panel.</summary>
    public static Border SeparatorLine() =>
        new()
        {
            Height = 1,
            Background = SeparatorBrush,
            Margin = new Thickness(2, 4),
        };

    /// <summary>
    /// One button of a verb toggle strip (<see cref="ToggleStrip"/>): <paramref name="label"/> with the verb's <paramref name="code"/>
    /// beside it (none when it is empty), not focusable, running <paramref name="onClick"/> when clicked. Every button but the <paramref name="first"/>
    /// has a line on its left.
    /// </summary>
    public static ToggleButton VerbToggle(string label, string code, bool first, Action onClick)
    {
        var codeText = new TextBlock
        {
            Text = code,
            FontSize = 11,
            Foreground = CodeBrush,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Mono(codeText);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        if (code.Length > 0)
        {
            content.Children.Add(codeText);
        }

        var toggle = new ToggleButton
        {
            Content = content,
            FontSize = 12,
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(first ? 0 : 1, 0, 0, 0),
            BorderBrush = ToggleLine,
            Background = ToggleFill,
            Focusable = false,
        };
        toggle.Resources["ToggleButtonBackgroundChecked"] = ToggleOnFill;
        toggle.Resources["ToggleButtonBackgroundCheckedPointerOver"] = ToggleOnFill;
        toggle.Resources["ToggleButtonBackgroundCheckedPressed"] = ToggleOnFill;
        toggle.Resources["ToggleButtonForegroundChecked"] = Brushes.White;
        toggle.Resources["ToggleButtonForegroundCheckedPointerOver"] = Brushes.White;
        AutomationProperties.SetName(toggle, label);
        toggle.Click += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return toggle;
    }

    /// <summary>A verb toggle: <paramref name="toggles"/> side by side in one rounded outline.</summary>
    public static Border ToggleStrip(IEnumerable<ToggleButton> toggles)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.AddRange(toggles);
        return new Border
        {
            BorderBrush = ToggleLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Margin = new Thickness(11, 4, 11, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = buttons,
        };
    }

    /// <summary>
    /// A box's row: <paramref name="glyph"/>, then <paramref name="label"/> with <paramref name="box"/> under it, then
    /// <paramref name="commandText"/>, the command the box sends, right-aligned in the monospace font.
    /// </summary>
    public static Grid BoxRow(QuickCommandGlyph glyph, string label, Control box, TextBlock commandText)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(11, 4) };
        Viewbox icon = QuickCommandStrip.GlyphIcon(glyph);
        icon.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(icon);
        var name = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = label },
                box,
            },
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        commandText.FontSize = 12;
        commandText.Margin = new Thickness(16, 0, 0, 0);
        commandText.HorizontalAlignment = HorizontalAlignment.Right;
        commandText.VerticalAlignment = VerticalAlignment.Center;
        Mono(commandText);
        Grid.SetColumn(commandText, 2);
        grid.Children.Add(commandText);
        return grid;
    }

    /// <summary>
    /// A chip showing <paramref name="content"/> in the monospace font, named <paramref name="automationName"/>, sending
    /// <paramref name="command"/> through <paramref name="choose"/> when clicked.
    /// </summary>
    public static Button Chip(string content, string command, string automationName, Func<string, Task> choose)
    {
        var chip = new Button
        {
            Content = content,
            FontSize = 12,
            Height = 24,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ToggleFill,
            BorderBrush = ChipLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
        };
        chip.Bind(TemplatedControl.FontFamilyProperty, chip.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        MenuCommandText.SetCommand(chip, command);
        AutomationProperties.SetName(chip, automationName);
        chip.Click += async (_, e) =>
        {
            e.Handled = true;
            await choose(command);
        };
        return chip;
    }

    /// <summary>A row's command column: <paramref name="text"/>, faded, right-aligned in the monospace font.</summary>
    public static TextBlock CommandText(string text)
    {
        var command = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Mono(command);
        return command;
    }
}
