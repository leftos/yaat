using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Yaat.Client.ContextMenus;
using Yaat.Client.Logging;
using Yaat.Client.Views.Radar.Flyouts;

namespace Yaat.Client.Views;

/// <summary>
/// The popups every view's menu host opens its pickers through: free-text input, a list, a type-to-filter list, a
/// titled list of marked rows with type to jump, the warp popup, and the Command… and Note… flyouts. Each popup is
/// code-built into its anchor's overlay layer with <see cref="PlacementMode.Pointer"/>, so it opens at the pointer on the
/// radar, the ground view and the aircraft list alike. Every popup light-dismisses on a click outside it; the input,
/// filtered-list, rich-list and warp popups also close on Escape, while the list popup light-dismisses only. A popup
/// opens on the next dispatcher turn so the context menu closing in the same message does not dismiss it at once.
/// </summary>
internal static class MenuPopups
{
    private static readonly ILogger Log = AppLog.CreateLogger("MenuPopups");

    /// <summary>The most names the filtered list shows for one prefix.</summary>
    private const int MaxFilteredMatches = 50;

    private static readonly IBrush InputBackground = new SolidColorBrush(Color.FromArgb(240, 24, 24, 24));
    private static readonly IBrush InputBorderBrush = new SolidColorBrush(Color.FromArgb(255, 80, 80, 80));
    private static readonly IBrush PickerBackground = new SolidColorBrush(Color.Parse("#222"));
    private static readonly IBrush PickerBorderBrush = new SolidColorBrush(Color.Parse("#555"));
    private static readonly IBrush PickerForeground = new SolidColorBrush(Color.Parse("#CCC"));
    private static readonly IBrush WarpHeaderForeground = new SolidColorBrush(Color.Parse("#DDD"));
    private static readonly IBrush WarpLabelForeground = new SolidColorBrush(Color.Parse("#999"));
    private static readonly IBrush RichHintForeground = new SolidColorBrush(Color.Parse("#a8adb4"));
    private static readonly IBrush RichClimbForeground = new SolidColorBrush(Color.Parse("#7fd1b9"));
    private static readonly IBrush RichDescendForeground = new SolidColorBrush(Color.Parse("#f2b880"));
    private static readonly IBrush RichGreyedForeground = new SolidColorBrush(Color.Parse("#7d838b"));
    private static readonly IBrush RichNowBackground = new SolidColorBrush(Color.Parse("#2f4a6e"));
    private static readonly IBrush RichNowBorderBrush = new SolidColorBrush(Color.Parse("#5b8def"));
    private static readonly IBrush RichMvaForeground = new SolidColorBrush(Color.Parse("#e0a84a"));

    /// <summary>The rich list's width, as the mock draws it.</summary>
    private const double RichListWidth = 300;

    /// <summary>How many rows PageUp and PageDown move the rich list's selection.</summary>
    private const int RichListPageRows = 10;

    /// <summary>The tallest the rich list's rows grow before they scroll.</summary>
    private const double RichListMaxHeight = 320;

    /// <summary>The values the warp popup opens with; a heading, altitude or speed of zero or less opens blank.</summary>
    internal sealed record WarpSeed(string Callsign, string Frd, int Heading, int Altitude, int Speed);

    /// <summary>
    /// Opens a focused free-text box showing <paramref name="placeholder"/> as its prompt, holding
    /// <paramref name="initialText"/> with the caret at <paramref name="caretIndex"/> (clamped to the text). Enter or OK
    /// closes it and hands the trimmed text to <paramref name="onSubmit"/>; a blank submit follows
    /// <paramref name="blank"/> — <see cref="BlankInput.Submits"/> calls it with <c>""</c>, <see cref="BlankInput.Closes"/>
    /// does not call it. Clear and Escape close it without calling it.
    /// </summary>
    public static void ShowInput(
        Control anchor,
        string placeholder,
        string initialText,
        int caretIndex,
        BlankInput blank,
        Func<string, Task> onSubmit
    )
    {
        Popup popup = NewPopup(anchor);
        var textBox = new TextBox
        {
            Text = initialText,
            PlaceholderText = placeholder,
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };

        async Task Submit()
        {
            string text = textBox.Text?.Trim() ?? "";
            popup.Close();
            if (text.Length > 0)
            {
                await onSubmit(text);
            }
            else if (blank == BlankInput.Submits)
            {
                await onSubmit("");
            }
        }

        textBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await Submit();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                popup.Close();
            }
        };

        var okButton = new Button { Content = "OK" };
        okButton.Click += async (_, _) => await Submit();
        var clearButton = new Button { Content = "Clear" };
        clearButton.Click += (_, _) => popup.Close();

        popup.Child = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = InputBorderBrush,
            Background = InputBackground,
            Padding = new Thickness(8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { textBox, okButton, clearButton },
            },
        };

        int caret = Math.Clamp(caretIndex, 0, initialText.Length);
        popup.Opened += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectionStart = caret;
            textBox.SelectionEnd = caret;
            textBox.CaretIndex = caret;
        };

        Open(anchor, popup, "input");
    }

    /// <summary>
    /// Opens a list of <paramref name="items"/> with <paramref name="selected"/> (else the nearest integer item to it)
    /// selected and scrolled into view. Selecting another item closes the list and hands it to <paramref name="onPick"/>.
    /// </summary>
    public static void ShowList(Control anchor, IReadOnlyList<object> items, object? selected, Func<object, Task> onPick)
    {
        Popup popup = NewPopup(anchor);
        var listBox = new ListBox
        {
            MaxHeight = 300,
            Width = 120,
            FontSize = 12,
            Background = PickerBackground,
            Foreground = PickerForeground,
            ItemsSource = items,
        };
        ApplyMonoFont(anchor, listBox);

        bool ignoreSelection = true;
        listBox.SelectionChanged += (_, e) =>
        {
            if (ignoreSelection || (e.AddedItems.Count == 0) || (e.AddedItems[0] is not { } picked))
            {
                return;
            }

            ignoreSelection = true;
            popup.Close();
            _ = onPick(picked);
        };

        popup.Child = PickerBorder(listBox, new Thickness(0));
        popup.Opened += (_, _) =>
        {
            int index = SeedIndex(items, selected);
            if (index >= 0)
            {
                listBox.SelectedIndex = index;
                listBox.ScrollIntoView(items[index]);
            }

            ignoreSelection = false;
        };

        Open(anchor, popup, "list");
    }

    /// <summary>
    /// Opens a focused filter box over <paramref name="sortedNames"/>, listing <paramref name="priorityItems"/> until
    /// the controller types. Typing lists the names starting with the text (case folded, at most
    /// <see cref="MaxFilteredMatches"/>) and selects the first; Up and Down move the selection; Enter picks the selected
    /// name, else the typed text upper-cased; a click on a name picks it once the filter box has lost focus.
    /// </summary>
    public static void ShowFilteredList(Control anchor, string[] sortedNames, IReadOnlyList<object>? priorityItems, Func<string, Task> onPick)
    {
        Popup popup = NewPopup(anchor);
        var textBox = new TextBox { FontSize = 12, PlaceholderText = "Fix name..." };
        var listBox = new ListBox
        {
            MaxHeight = 250,
            FontSize = 12,
            Background = PickerBackground,
            Foreground = PickerForeground,
            ItemsSource = priorityItems ?? [],
        };
        ApplyMonoFont(anchor, textBox);
        ApplyMonoFont(anchor, listBox);

        bool picked = false;
        void Pick(string? value)
        {
            popup.Close();
            if (!string.IsNullOrEmpty(value))
            {
                picked = true;
                _ = onPick(value);
            }
        }

        textBox.TextChanged += (_, _) => FilterNames(textBox, listBox, sortedNames);
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                string? value = listBox.SelectedItem?.ToString();
                Pick(string.IsNullOrEmpty(value) ? textBox.Text?.Trim().ToUpperInvariant() : value);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                popup.Close();
                e.Handled = true;
            }
            else
            {
                e.Handled = MoveSelection(listBox, e.Key);
            }
        };
        listBox.SelectionChanged += (_, e) =>
        {
            if (picked || (e.AddedItems.Count == 0) || textBox.IsFocused)
            {
                return;
            }

            string? value = e.AddedItems[0]?.ToString();
            if (!string.IsNullOrEmpty(value))
            {
                Pick(value);
            }
        };

        popup.Child = PickerBorder(
            new StackPanel
            {
                Width = 200,
                Spacing = 4,
                Children = { textBox, listBox },
            },
            new Thickness(4)
        );
        popup.Opened += (_, _) => textBox.Focus();

        Open(anchor, popup, "filtered-list");
    }

    /// <summary>
    /// Opens the warp form for <see cref="WarpSeed.Callsign"/>: fix or FRD, heading, altitude (feet) and speed (knots
    /// IAS), seeded from <paramref name="seed"/>, the FRD box focused. Enter in any box or OK closes it and, when the FRD
    /// is not blank and the three numbers parse, hands them to <paramref name="onSubmit"/>; Cancel and Escape close it.
    /// </summary>
    public static void ShowWarp(Control anchor, WarpSeed seed, Func<string, int, int, int, Task> onSubmit)
    {
        Popup popup = NewPopup(anchor);
        TextBox frdBox = WarpBox(anchor, "e.g. SFO or SFO180010", seed.Frd);
        TextBox headingBox = WarpBox(anchor, "0-360", PositiveOrBlank(seed.Heading));
        TextBox altitudeBox = WarpBox(anchor, "e.g. 5000", PositiveOrBlank(seed.Altitude));
        TextBox speedBox = WarpBox(anchor, "e.g. 250", PositiveOrBlank(seed.Speed));

        void Submit()
        {
            string? frd = frdBox.Text?.Trim();
            popup.Close();
            if (
                string.IsNullOrEmpty(frd)
                || !int.TryParse(headingBox.Text?.Trim(), out int heading)
                || !int.TryParse(altitudeBox.Text?.Trim(), out int altitude)
                || !int.TryParse(speedBox.Text?.Trim(), out int speed)
            )
            {
                return;
            }

            _ = onSubmit(frd, heading, altitude, speed);
        }

        TextBox[] boxes = [frdBox, headingBox, altitudeBox, speedBox];
        foreach (TextBox box in boxes)
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Submit();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    popup.Close();
                    e.Handled = true;
                }
            };
        }

        var okButton = new Button
        {
            Content = "OK",
            Padding = new Thickness(8, 2),
            FontSize = 11,
        };
        okButton.Click += (_, _) => Submit();
        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(8, 2),
            FontSize = 11,
        };
        cancelButton.Click += (_, _) => popup.Close();

        var form = new StackPanel { Spacing = 6, Width = 280 };
        form.Children.Add(
            new TextBlock
            {
                Text = $"Warp {seed.Callsign}",
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Foreground = WarpHeaderForeground,
            }
        );
        AddWarpField(form, "Fix or FRD", frdBox);
        AddWarpField(form, "Heading", headingBox);
        AddWarpField(form, "Altitude (feet)", altitudeBox);
        AddWarpField(form, "Speed (knots IAS)", speedBox);
        form.Children.Add(
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0),
                Children = { okButton, cancelButton },
            }
        );

        popup.Child = PickerBorder(form, new Thickness(8));
        popup.Opened += (_, _) => frdBox.Focus();

        Open(anchor, popup, "warp");
    }

    /// <summary>
    /// Opens the titled picker of <paramref name="list"/>'s rows: the title and subtitle over a 300 px list of marked
    /// rows, opening with the selected row centred. Clicking a row that has a command closes the picker and hands the row
    /// to <paramref name="onPick"/>; the MVA line takes no click. Typing jumps to the nearest row
    /// (<see cref="MenuTypeAhead"/>) and centres it, Backspace edits what was typed, Up and Down move the selection,
    /// Enter picks the selected row and Escape closes the picker.
    /// </summary>
    public static void ShowRichList(Control anchor, MenuRichList list, Action<MenuRichRow> onPick)
    {
        Popup popup = NewPopup(anchor);
        var listBox = new ListBox
        {
            MaxHeight = RichListMaxHeight,
            FontSize = 13,
            Background = PickerBackground,
            Foreground = PickerForeground,
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel()),
        };
        ApplyMonoFont(anchor, listBox);
        foreach (MenuRichRow row in list.Rows)
        {
            listBox.Items.Add(RichRowItem(row));
        }

        var picker = new RichListPicker(popup, listBox, list, onPick);
        var root = new StackPanel { Width = RichListWidth, Children = { RichListHeader(list), RichHairline(), listBox } };
        popup.Child = PickerBorder(root, new Thickness(0, 0, 0, 6));
        popup.Child.AddHandler(InputElement.KeyDownEvent, picker.OnKeyDown, RoutingStrategies.Tunnel);
        popup.Child.AddHandler(InputElement.TextInputEvent, picker.OnTextInput, RoutingStrategies.Tunnel);
        listBox.SelectionChanged += (_, _) => picker.OnSelectionChanged();
        popup.Opened += (_, _) => picker.OnOpened();

        Open(anchor, popup, "rich-list");
    }

    /// <summary>Opens the Command… flyout for <paramref name="callsign"/> on <paramref name="anchor"/>.</summary>
    public static void ShowCommand(Control anchor, string callsign, Func<string, Task> onSubmit) =>
        Open(anchor, CommandFlyout.Build(anchor, callsign, onSubmit), "command");

    /// <summary>Opens the Note… flyout for <paramref name="callsign"/> on <paramref name="anchor"/>.</summary>
    public static void ShowNote(Control anchor, string callsign, string currentNote, Func<string, Task> sendCommand) =>
        Open(anchor, NoteFlyout.Build(anchor, callsign, currentNote, sendCommand), "note");

    private static Popup NewPopup(Control anchor) =>
        new()
        {
            Placement = PlacementMode.Pointer,
            PlacementTarget = anchor,
            IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = false,
        };

    private static void Open(Control anchor, Popup popup, string kind)
    {
        var overlay = OverlayLayer.GetOverlayLayer(anchor);
        if (overlay is null)
        {
            Log.LogWarning("MenuPopups: no overlay layer for anchor {Anchor}; {Kind} popup not shown", anchor.GetType().Name, kind);
            return;
        }

        overlay.Children.Add(popup);
        popup.Closed += (s, _) =>
        {
            if (s is Popup p)
            {
                overlay.Children.Remove(p);
            }
        };
        Dispatcher.UIThread.Post(() => popup.IsOpen = true);
    }

    private static Border PickerBorder(Control child, Thickness padding) =>
        new()
        {
            Background = PickerBackground,
            BorderBrush = PickerBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = padding,
            Child = child,
        };

    private static void ApplyMonoFont(Control anchor, TemplatedControl target)
    {
        if (anchor.TryFindResource("MonoFont", out object? font) && (font is FontFamily family))
        {
            target.FontFamily = family;
        }
    }

    private static TextBox WarpBox(Control anchor, string placeholder, string text)
    {
        var box = new TextBox
        {
            Text = text,
            PlaceholderText = placeholder,
            FontSize = 12,
        };
        ApplyMonoFont(anchor, box);
        return box;
    }

    private static void AddWarpField(StackPanel form, string label, TextBox box)
    {
        form.Children.Add(
            new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = WarpLabelForeground,
            }
        );
        form.Children.Add(box);
    }

    private static string PositiveOrBlank(int value) => value > 0 ? value.ToString() : "";

    /// <summary>The rich list's title, bold, over its dim subtitle.</summary>
    private static StackPanel RichListHeader(MenuRichList list) =>
        new()
        {
            Margin = new Thickness(12, 8),
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = list.Title,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = WarpHeaderForeground,
                },
                new TextBlock
                {
                    Text = list.Subtitle,
                    FontSize = 12,
                    Foreground = RichHintForeground,
                },
            },
        };

    private static Border RichHairline() => new() { Height = 1, Background = PickerBorderBrush };

    /// <summary>A row of the rich list: the MVA line as a disabled dashed rule, every other row as its mark, value and hint.</summary>
    private static ListBoxItem RichRowItem(MenuRichRow row) =>
        (row.Kind == MenuRichRowKind.MvaLine)
            ? new ListBoxItem
            {
                IsEnabled = false,
                Focusable = false,
                Padding = new Thickness(0),
                MinHeight = 0,
                Content = MvaLine(row),
            }
            : new ListBoxItem
            {
                Padding = new Thickness(0),
                MinHeight = 0,
                Content = RichRowContent(row),
            };

    /// <summary>
    /// A marked row: a 22 px glyph column, the value, and the hint right-aligned in 11 px. The ● row has a blue-tinted
    /// fill, a 3 px left border and a medium-weight value; a row below the MVA is greyed throughout.
    /// </summary>
    private static Border RichRowContent(MenuRichRow row)
    {
        bool now = row.Kind is MenuRichRowKind.Now or MenuRichRowKind.NowAssigned;
        var glyph = new TextBlock { Text = row.Glyph, Foreground = RichGlyphForeground(row.Kind) };
        var label = new TextBlock { Text = row.Label, FontWeight = now ? FontWeight.Medium : FontWeight.Normal };
        if (row.Kind == MenuRichRowKind.BelowMva)
        {
            label.Foreground = RichGreyedForeground;
        }

        var hint = new TextBlock
        {
            Text = row.Hint,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = RichHintBrush(row.Kind),
        };
        Grid.SetColumn(label, 1);
        Grid.SetColumn(hint, 2);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*,Auto"), Children = { glyph, label, hint } };
        return now ? NowRowBorder(grid) : new Border { Padding = new Thickness(12, 4), Child = grid };
    }

    /// <summary>The ● row's frame: a blue-tinted fill with a 3 px blue left border, the padding narrowed by the border's width.</summary>
    private static Border NowRowBorder(Grid grid) =>
        new()
        {
            Padding = new Thickness(9, 4, 12, 4),
            Background = RichNowBackground,
            BorderBrush = RichNowBorderBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Child = grid,
        };

    private static IBrush RichHintBrush(MenuRichRowKind kind) =>
        kind switch
        {
            MenuRichRowKind.BelowMva => RichGreyedForeground,
            MenuRichRowKind.Now or MenuRichRowKind.NowAssigned => PickerForeground,
            _ => RichHintForeground,
        };

    private static IBrush RichGlyphForeground(MenuRichRowKind kind) =>
        kind switch
        {
            MenuRichRowKind.Climb => RichClimbForeground,
            MenuRichRowKind.Descend => RichDescendForeground,
            MenuRichRowKind.Assigned => RichNowBorderBrush,
            MenuRichRowKind.BelowMva => RichGreyedForeground,
            _ => PickerForeground,
        };

    /// <summary>The MVA line: a dashed amber rule over its 11 px amber text.</summary>
    private static StackPanel MvaLine(MenuRichRow row) =>
        new()
        {
            Margin = new Thickness(12, 4),
            Spacing = 3,
            Children =
            {
                new Rectangle
                {
                    Height = 1,
                    Stroke = RichMvaForeground,
                    StrokeThickness = 1,
                    StrokeDashArray = [4, 3],
                },
                new TextBlock
                {
                    Text = row.Label,
                    FontSize = 11,
                    Foreground = RichMvaForeground,
                },
            },
        };

    /// <summary>
    /// The state of one open rich list: what has been typed, and whether a selection change is the popup's own (opening,
    /// a jump, an arrow key) rather than the controller's click, which alone picks.
    /// </summary>
    private sealed class RichListPicker(Popup popup, ListBox listBox, MenuRichList list, Action<MenuRichRow> onPick)
    {
        private readonly MenuTypeAhead _typeAhead = new([.. list.Rows.Select(row => row.Value)]);
        private bool _ignoreSelection = true;

        public void OnOpened()
        {
            listBox.UpdateLayout();
            Select(list.SelectedIndex);
            listBox.Focus();
            _ignoreSelection = false;
        }

        public void OnSelectionChanged()
        {
            if (!_ignoreSelection && (SelectedRow() is { Command: not null } row))
            {
                Pick(row);
            }
        }

        /// <summary>Feeds the typed digits and the letters of <c>FL</c> to the type-ahead; every other character is ignored.</summary>
        public void OnTextInput(object? sender, TextInputEventArgs e)
        {
            foreach (char key in e.Text ?? "")
            {
                if (IsJumpKey(key))
                {
                    SelectIfAny(_typeAhead.Type(key, Environment.TickCount64));
                }
            }

            e.Handled = true;
        }

        /// <summary>
        /// Enter picks the selected row, Escape closes, Backspace edits the jump, and Up, Down, PageUp, PageDown, Home and
        /// End move the selection without sending; each of them is handled.
        /// </summary>
        public void OnKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    PickSelected();
                    break;
                case Key.Escape:
                    popup.Close();
                    break;
                case Key.Back:
                    SelectIfAny(_typeAhead.Backspace(Environment.TickCount64));
                    break;
                default:
                    if (NavigationStart(e.Key) is not { } navigation)
                    {
                        return;
                    }

                    SelectIfAny(ValueRowNear(navigation.Start, navigation.Step));
                    break;
            }

            e.Handled = true;
        }

        private static bool IsJumpKey(char key) => char.IsAsciiDigit(key) || (char.ToUpperInvariant(key) is 'F' or 'L');

        private MenuRichRow? SelectedRow() => (listBox.SelectedIndex >= 0) ? list.Rows[listBox.SelectedIndex] : null;

        private void PickSelected()
        {
            if (SelectedRow() is { Command: not null } row)
            {
                Pick(row);
            }
        }

        private void Pick(MenuRichRow row)
        {
            _ignoreSelection = true;
            popup.Close();
            onPick(row);
        }

        private void SelectIfAny(int? index)
        {
            if (index is { } row)
            {
                Select(row);
            }
        }

        /// <summary>
        /// Where a navigation key's search starts and which way it runs: Up and Down one row, PageUp and PageDown
        /// <see cref="RichListPageRows"/> rows, Home the top and End the bottom; null for any other key.
        /// </summary>
        private (int Start, int Step)? NavigationStart(Key key)
        {
            int selected = listBox.SelectedIndex;
            return key switch
            {
                Key.Up => (selected - 1, -1),
                Key.Down => (selected + 1, 1),
                Key.PageUp => (selected - RichListPageRows, -1),
                Key.PageDown => (selected + RichListPageRows, 1),
                Key.Home => (0, 1),
                Key.End => (list.Rows.Count - 1, -1),
                _ => null,
            };
        }

        /// <summary>
        /// The row typing could land on nearest <paramref name="start"/> (clamped to the list), searched first in
        /// <paramref name="step"/>'s direction, so the MVA line is stepped over; null when the list has no such row.
        /// </summary>
        private int? ValueRowNear(int start, int step)
        {
            if (list.Rows.Count == 0)
            {
                return null;
            }

            int clamped = Math.Clamp(start, 0, list.Rows.Count - 1);
            return ValueRowFrom(clamped, step) ?? ValueRowFrom(clamped, -step);
        }

        private int? ValueRowFrom(int start, int step)
        {
            for (int i = start; (i >= 0) && (i < list.Rows.Count); i += step)
            {
                if (list.Rows[i].Value is not null)
                {
                    return i;
                }
            }

            return null;
        }

        /// <summary>
        /// Selects row <paramref name="index"/> as the popup's own change and scrolls its middle to the viewport's middle,
        /// clamped at the list's ends.
        /// </summary>
        private void Select(int index)
        {
            bool wasIgnoring = _ignoreSelection;
            _ignoreSelection = true;
            listBox.SelectedIndex = index;
            _ignoreSelection = wasIgnoring;

            ScrollViewer? scroller = listBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if ((scroller?.Content is not Visual content) || (listBox.ContainerFromIndex(index) is not { } row))
            {
                Log.LogDebug("MenuPopups: rich list row {Index} not laid out; not centred", index);
                return;
            }

            Point? top = row.TranslatePoint(new Point(0, 0), content);
            double middle = (top?.Y ?? row.Bounds.Y) + (row.Bounds.Height / 2);
            double maxOffset = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(middle - (scroller.Viewport.Height / 2), 0, maxOffset));
        }
    }

    /// <summary>The index of <paramref name="selected"/> in <paramref name="items"/>, else of the nearest integer item, else -1.</summary>
    private static int SeedIndex(IReadOnlyList<object> items, object? selected)
    {
        if (selected is null)
        {
            return -1;
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (Equals(items[i], selected))
            {
                return i;
            }
        }

        return FindClosestIndex(items, selected);
    }

    private static int FindClosestIndex(IReadOnlyList<object> items, object target)
    {
        if (target is not int targetInt)
        {
            return -1;
        }

        int bestIdx = -1;
        int bestDiff = int.MaxValue;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is int val)
            {
                int diff = Math.Abs(val - targetInt);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestIdx = i;
                }
            }
        }

        return bestIdx;
    }

    private static void FilterNames(TextBox textBox, ListBox listBox, string[] sortedNames)
    {
        string prefix = textBox.Text?.Trim().ToUpperInvariant() ?? "";
        if (prefix.Length == 0)
        {
            listBox.ItemsSource = Array.Empty<object>();
            return;
        }

        IReadOnlyList<object> results = PrefixSearch(sortedNames, prefix, MaxFilteredMatches);
        listBox.ItemsSource = results;
        if (results.Count > 0)
        {
            listBox.SelectedIndex = 0;
        }
    }

    /// <summary>Moves the filtered list's selection for Up and Down; returns whether <paramref name="key"/> was one of them.</summary>
    private static bool MoveSelection(ListBox listBox, Key key)
    {
        if ((key != Key.Down) && (key != Key.Up))
        {
            return false;
        }

        if (listBox.ItemCount > 0)
        {
            listBox.SelectedIndex =
                key == Key.Down ? Math.Min(listBox.SelectedIndex + 1, listBox.ItemCount - 1) : Math.Max(listBox.SelectedIndex - 1, 0);
            listBox.ScrollIntoView(listBox.SelectedItem!);
        }

        return true;
    }

    private static IReadOnlyList<object> PrefixSearch(string[] sortedNames, string prefix, int maxResults)
    {
        var results = new List<object>();
        int idx = Array.BinarySearch(sortedNames, prefix, StringComparer.OrdinalIgnoreCase);
        if (idx < 0)
        {
            idx = ~idx;
        }

        for (int i = idx; (i < sortedNames.Length) && (results.Count < maxResults); i++)
        {
            if (sortedNames[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(sortedNames[i]);
            }
            else
            {
                break;
            }
        }

        return results;
    }
}
