using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// One fix row of a fix flyout (Direct to, Cross fix, Depart fix, Hold at fix, and Join radial's navaid rows): the ● mark on the
/// next fix, the fix, its note (distance and magnetic bearing, <c>passed</c>, or a navaid's radial and distance), and its command
/// column, in the outline the fix box draws round the row Enter sends (Join radial outlines its picked navaid's row).
/// </summary>
public sealed class FixRow : Button
{
    private static readonly IImmutableSolidColorBrush OutlineBrush = new ImmutableSolidColorBrush(Color.Parse("#8FC1FF"));

    private readonly Border _outline = new()
    {
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        BorderBrush = Brushes.Transparent,
        Padding = new Thickness(10, 3),
    };
    private readonly TextBlock _mark = new() { Width = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _note = new()
    {
        FontSize = 12,
        Opacity = 0.8,
        Margin = new Thickness(10, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _command = FlyoutPanelParts.CommandText("");

    internal FixRow(string fix, Func<FixRow, Task> chosen)
    {
        Fix = fix;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        grid.Children.Add(_mark);
        var name = new TextBlock { Text = fix, VerticalAlignment = VerticalAlignment.Center };
        FlyoutPanelParts.Mono(name);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Grid.SetColumn(_note, 2);
        grid.Children.Add(_note);
        Grid.SetColumn(_command, 3);
        grid.Children.Add(_command);
        _outline.Child = grid;
        Content = _outline;
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(3, 0, 0, 0);
        BorderBrush = Brushes.Transparent;
        CornerRadius = new CornerRadius(0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Click += async (_, e) =>
        {
            e.Handled = true;
            await chosen(this);
        };
    }

    /// <summary>The fix the row names.</summary>
    public string Fix { get; }

    /// <summary>The command a click sends now; null while the flyout's boxes leave it incomplete.</summary>
    public string? SentCommand { get; private set; }

    /// <summary>The command column: <see cref="SentCommand"/>, or the verb and fix with <c>···</c> while it is null.</summary>
    public string ShownCommand => _command.Text ?? "";

    /// <summary>The distance and magnetic bearing, <c>passed</c>, or empty when the fix has no position.</summary>
    public string Note => _note.Text ?? "";

    /// <summary>Whether the row is the fix the aircraft navigates to, filled and marked ●.</summary>
    public bool IsNext { get; private set; }

    /// <summary>Whether the aircraft has passed the fix since the flyout opened: greyed, and still sending; once passed, it stays so.</summary>
    public bool IsPassed { get; private set; }

    /// <summary>Whether the row is outlined: the row Enter in the fix box sends.</summary>
    public bool IsOutlined { get; private set; }

    /// <summary>The row as one line: the mark, the fix, the note and the command column.</summary>
    public string Line => $"{(IsNext ? "●" : " ")} {Fix}{((Note.Length > 0) ? $" · {Note}" : "")} — {ShownCommand}";

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>Sets the command a click sends (<paramref name="sent"/>, null for none) and the column's text, <paramref name="shown"/>.</summary>
    internal void SetCommand(string? sent, string shown)
    {
        SentCommand = sent;
        _command.Text = shown;
        MenuCommandText.SetCommand(this, sent);
        AutomationProperties.SetName(this, $"{Fix} — {shown}");
    }

    /// <summary>Shows the row's state now: the ● when <paramref name="isNext"/>, and passed from now on once <paramref name="passes"/>.</summary>
    internal void Show(bool isNext, bool passes, string? note)
    {
        IsPassed |= passes;
        IsNext = isNext && !IsPassed;
        _mark.Text = IsNext ? "●" : "";
        _note.Text = IsPassed ? "passed" : (note ?? "");
        Background = IsNext ? FlyoutPanelParts.NowFill : Brushes.Transparent;
        BorderBrush = IsNext ? FlyoutPanelParts.NowEdge : Brushes.Transparent;
        Opacity = IsPassed ? MenuGlyphRowTemplate.DimmedOpacity : 1;
    }

    internal void SetOutlined(bool outlined)
    {
        IsOutlined = outlined;
        _outline.BorderBrush = outlined ? OutlineBrush : Brushes.Transparent;
    }
}

/// <summary>What a fix flyout tells its <see cref="FixListView"/>.</summary>
/// <param name="CommandFor">A fix's command: what a click sends (null for nothing) and what the command column shows.</param>
/// <param name="ExtraMatches">The rows the typed text adds ahead of the other matching fixes (Direct to's FRD row); none for most.</param>
/// <param name="Chosen">What a click or Enter on a row does.</param>
/// <param name="InABox">Whether a key event comes from (or keyboard focus is in) one of the flyout's boxes.</param>
/// <param name="BoxEnter">What Enter in a box does.</param>
internal sealed record FixListBehaviour(
    Func<string, (string? Sent, string Shown)> CommandFor,
    Func<string, IReadOnlyList<string>> ExtraMatches,
    Func<FixRow, Task> Chosen,
    Func<KeyEventArgs, bool> InABox,
    Func<Task> BoxEnter
);

/// <summary>
/// The list a fix flyout shows under its boxes: the route as rows (distance, magnetic bearing, ● on the next fix, passed fixes
/// greyed in place), then as the fix box is typed in every other matching fix, nearest first, under a header naming their count;
/// the outline on the row Enter in the fix box sends; the live refresh; and the keyboard walk (Down from a box onto the ● row,
/// else the first shown row; Down and Up through the rows; Up from the top row back to the fix box; Enter on a row).
/// </summary>
public sealed class FixListView
{
    private const double ListMaxHeight = 300;

    private readonly IMenuAircraft? _aircraft;
    private readonly TextBox _box;
    private readonly FixListBehaviour _behaviour;
    private readonly StackPanel _list = new();
    private readonly TextBlock _routeHeader;
    private readonly TextBlock _matchHeader = GroupHeader("");
    private readonly List<FixRow> _routeRows = [];
    private readonly List<FixRow> _matchRows = [];

    internal FixListView(IMenuAircraft? aircraft, TextBox box, string routeHeader, FixListBehaviour behaviour)
    {
        _aircraft = aircraft;
        _box = box;
        _behaviour = behaviour;
        _routeHeader = GroupHeader(routeHeader);
        View = new ScrollViewer { MaxHeight = ListMaxHeight, Content = _list };
    }

    /// <summary>The list's control, which the flyout lays out under its separator.</summary>
    public Control View { get; }

    /// <summary>The route's rows as the flyout opened, in route order; they never move while it stays open.</summary>
    public IReadOnlyList<FixRow> RouteRows => _routeRows;

    /// <summary>The rows the fix box's text adds: the extra rows first, then every other matching fix, nearest first.</summary>
    public IReadOnlyList<FixRow> MatchRows => _matchRows;

    /// <summary>The row Enter in the fix box sends, outlined: the first shown row while the box holds text; null otherwise.</summary>
    public FixRow? OutlinedRow { get; private set; }

    /// <summary>Each shown group header and row, as text, top to bottom.</summary>
    public IEnumerable<string> Lines =>
        _list.Children.Where(part => part.IsVisible).Select(part => (part is FixRow row) ? row.Line : ((part as TextBlock)?.Text ?? ""));

    /// <summary>Reads the route afresh as <paramref name="fixes"/>, one row each; the caller then filters by the box.</summary>
    public void Reset(IReadOnlyList<string> fixes)
    {
        _routeRows.Clear();
        _routeRows.AddRange(fixes.Select(fix => new FixRow(fix, _behaviour.Chosen)));
    }

    /// <summary>Shows every row's command anew, after a value the commands read changed.</summary>
    public void UpdateCommands()
    {
        foreach (FixRow row in _routeRows.Concat(_matchRows))
        {
            (string? sent, string shown) = _behaviour.CommandFor(row.Fix);
            row.SetCommand(sent, shown);
        }
    }

    /// <summary>
    /// Follows the aircraft, in place: each row's distance and bearing, the ● and the fixes passed since the flyout opened.
    /// Passing advances only while the aircraft navigates to a route row's fix: the rows before it turn passed and stay so; when
    /// it navigates nowhere (or off the route) no row changes and the ● goes.
    /// </summary>
    public void Refresh()
    {
        string next = _aircraft?.NavigatingTo ?? "";
        int nextIndex = (next.Length > 0) ? _routeRows.FindIndex(row => string.Equals(row.Fix, next, StringComparison.OrdinalIgnoreCase)) : -1;
        for (int i = 0; i < _routeRows.Count; i++)
        {
            _routeRows[i].Show(i == nextIndex, i < nextIndex, Note(_routeRows[i].Fix));
        }

        foreach (FixRow row in _matchRows)
        {
            row.Show(false, false, Note(row.Fix));
        }
    }

    /// <summary>
    /// Shows what the fix box now reads: the route rows that match (hidden in place otherwise), the extra rows and every other
    /// match, nearest first, under a header naming their count; then outlines the row Enter sends.
    /// </summary>
    public void Filter()
    {
        string prefix = (_box.Text ?? "").Trim();
        foreach (FixRow row in _routeRows)
        {
            row.IsVisible = (prefix.Length == 0) || row.Fix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        int extras = FillMatches(prefix);
        int fixMatches = _matchRows.Count - extras;
        _routeHeader.IsVisible = _routeRows.Any(row => row.IsVisible);
        _matchHeader.IsVisible = (extras == 0) || (fixMatches > 0);
        _matchHeader.Text =
            (prefix.Length == 0) ? DirectToFlyout.SearchHint
            : (fixMatches > 0) ? $"All fixes · nearest first ({fixMatches})"
            : $"No other fix starts with {prefix.ToUpperInvariant()}";
        _list.Children.Clear();
        _list.Children.Add(_routeHeader);
        _list.Children.AddRange(_routeRows);
        _list.Children.Add(_matchHeader);
        _list.Children.AddRange(_matchRows);
        Outline(prefix);
    }

    /// <summary>The flyout's tunnelling key handler: Enter in a box, Enter on a row, and the Down / Up walk.</summary>
    internal async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        List<FixRow> rows = ShownRows();
        int focused = rows.FindIndex(row => row.IsKeyboardFocusWithin);
        bool inBox = _behaviour.InABox(e);
        switch (e.Key)
        {
            case Key.Enter when inBox:
                e.Handled = true;
                await _behaviour.BoxEnter();
                break;
            case Key.Enter when focused >= 0:
                e.Handled = true;
                await _behaviour.Chosen(rows[focused]);
                break;
            case Key.Down or Key.Up:
                OnWalkKey(e, rows, inBox, focused);
                break;
        }
    }

    private static TextBlock GroupHeader(string text) =>
        new()
        {
            Text = text,
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            Margin = new Thickness(11, 4, 11, 2),
        };

    private string? Note(string fix) => (_aircraft is null) ? null : FixList.DistanceAndBearing(_aircraft.Position, fix);

    /// <summary>Lists the rows <paramref name="prefix"/> adds: the extra rows, then every other matching fix; returns how many are extra.</summary>
    private int FillMatches(string prefix)
    {
        _matchRows.Clear();
        IReadOnlyList<string> extras = _behaviour.ExtraMatches(prefix);
        _matchRows.AddRange(extras.Select(fix => new FixRow(fix, _behaviour.Chosen)));
        if (_aircraft is not null)
        {
            IReadOnlyList<string> matches = FixList.Matches(prefix, _aircraft.Position, [.. _routeRows.Select(row => row.Fix)]);
            _matchRows.AddRange(matches.Select(fix => new FixRow(fix, _behaviour.Chosen)));
        }

        foreach (FixRow row in _matchRows)
        {
            (string? sent, string shown) = _behaviour.CommandFor(row.Fix);
            row.SetCommand(sent, shown);
            row.Show(false, false, Note(row.Fix));
        }

        return extras.Count;
    }

    /// <summary>
    /// Outlines the row Enter sends while the box holds text: the shown row naming <paramref name="prefix"/> exactly, else the
    /// first shown row.
    /// </summary>
    private void Outline(string prefix)
    {
        List<FixRow> shown = ShownRows();
        OutlinedRow =
            (prefix.Length == 0)
                ? null
                : shown.FirstOrDefault(row => string.Equals(row.Fix, prefix, StringComparison.OrdinalIgnoreCase)) ?? shown.FirstOrDefault();
        foreach (FixRow row in _routeRows.Concat(_matchRows))
        {
            row.SetOutlined(ReferenceEquals(row, OutlinedRow));
        }

        OutlinedRow?.BringIntoView();
    }

    /// <summary>The rows the list shows now, top to bottom: the route rows that match the box, then the rows it adds.</summary>
    private List<FixRow> ShownRows() => [.. _routeRows.Where(row => row.IsVisible), .. _matchRows];

    /// <summary>
    /// Down from a box focuses the ● row (else the first shown row); Down and Up walk the rows; Up from the top row returns to
    /// the fix box.
    /// </summary>
    private void OnWalkKey(KeyEventArgs e, List<FixRow> rows, bool inBox, int focused)
    {
        int? next = (inBox, focused, e.Key) switch
        {
            (true, _, Key.Down) when rows.Count > 0 => Math.Max(0, rows.FindIndex(row => row.IsNext)),
            (false, 0, Key.Up) => -1,
            (false, >= 0, Key.Up) => focused - 1,
            (false, >= 0, Key.Down) => Math.Min(focused + 1, rows.Count - 1),
            _ => null,
        };
        if (next is not { } target)
        {
            return;
        }

        e.Handled = true;
        if (target < 0)
        {
            _box.Focus(NavigationMethod.Directional);
            return;
        }

        rows[target].Focus(NavigationMethod.Directional);
        rows[target].BringIntoView();
    }
}
