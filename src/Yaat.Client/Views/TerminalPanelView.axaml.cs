using System.Collections.Specialized;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class TerminalPanelView : UserControl
{
    private readonly List<TerminalEntryKind> _lineKinds = [];

    // Parallel to _lineKinds: the TerminalEntry that produced each rendered line, for
    // right-click → "Rewind to this moment" hit-testing. One entry per line (messages never
    // contain newlines), so rendered-line index maps 1:1 to this list.
    private readonly List<TerminalEntry> _lineEntries = [];
    private MenuItem? _rewindMenuItem;
    private TerminalEntry? _contextEntry;
    private TerminalColorizer? _colorizer;
    private ScrollViewer? _scrollViewer;
    private bool _autoScroll = true;
    private bool _isScrollingProgrammatically;
    private string _lastSearchText = string.Empty;

    public TerminalPanelView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        var prefs = (DataContext as MainViewModel)?.Preferences;
        _colorizer = new TerminalColorizer(_lineKinds, () => prefs?.TerminalColors ?? Yaat.Client.Models.TerminalColorScheme.Default);
        TerminalEditor.TextArea.TextView.LineTransformers.Add(_colorizer);
        TerminalEditor.TextArea.Caret.CaretBrush = Brushes.Transparent;

        if (prefs is not null)
        {
            prefs.TerminalColorsChanged += OnTerminalColorsChanged;
        }

        _scrollViewer = TerminalEditor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnScrollChanged;
        }

        foreach (var (toggle, _) in EnumerateCategoryToggles())
        {
            // Tunnel routing fires before ToggleButton's built-in click handling so we
            // can suppress the IsChecked toggle when the user is Shift+Clicking to solo.
            toggle.AddHandler(PointerPressedEvent, OnTogglePointerPressed, RoutingStrategies.Tunnel);
        }

        if (DataContext is MainViewModel vm)
        {
            vm.TerminalEntries.CollectionChanged += OnEntriesChanged;
            vm.TerminalFilterChanged += OnFilterChanged;
            RebuildDocument(vm);

            _rewindMenuItem = new MenuItem { Header = "Rewind to this moment" };
            _rewindMenuItem.Click += OnRewindToMomentClick;
            var clearItem = new MenuItem { Header = "Clear" };
            clearItem.Click += (_, _) => vm.TerminalEntries.Clear();
            TerminalEditor.ContextMenu = new ContextMenu { Items = { _rewindMenuItem, new Separator(), clearItem } };

            // Tunnel runs before the ContextMenu's own bubbling open handler, so the entry under
            // the pointer is resolved (and the Rewind item's state refreshed) before the menu shows.
            TerminalEditor.AddHandler(ContextRequestedEvent, OnTerminalContextRequested, RoutingStrategies.Tunnel);
        }
    }

    private void OnTerminalColorsChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _colorizer?.ReloadColors();
            TerminalEditor.TextArea.TextView.Redraw();
        });
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        TerminalEditor.RemoveHandler(ContextRequestedEvent, OnTerminalContextRequested);

        foreach (var (toggle, _) in EnumerateCategoryToggles())
        {
            toggle.RemoveHandler(PointerPressedEvent, OnTogglePointerPressed);
        }

        if (DataContext is MainViewModel vm)
        {
            vm.TerminalEntries.CollectionChanged -= OnEntriesChanged;
            vm.TerminalFilterChanged -= OnFilterChanged;
            if (vm.Preferences is { } prefs)
            {
                prefs.TerminalColorsChanged -= OnTerminalColorsChanged;
            }
        }

        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer = null;
        }

        if (_colorizer is not null)
        {
            TerminalEditor.TextArea.TextView.LineTransformers.Remove(_colorizer);
            _colorizer = null;
        }

        base.OnUnloaded(e);
    }

    private IEnumerable<(ToggleButton Button, TerminalEntryKind Kind)> EnumerateCategoryToggles()
    {
        yield return (CmdToggle, TerminalEntryKind.Command);
        yield return (RspToggle, TerminalEntryKind.Response);
        yield return (SysToggle, TerminalEntryKind.System);
        yield return (SayToggle, TerminalEntryKind.Say);
        yield return (WrnToggle, TerminalEntryKind.Warning);
        yield return (ErrToggle, TerminalEntryKind.Error);
        yield return (ChatToggle, TerminalEntryKind.Chat);
        yield return (TdlsToggle, TerminalEntryKind.Tdls);
        yield return (StripToggle, TerminalEntryKind.Strip);
    }

    private void OnTogglePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (sender is not ToggleButton btn)
        {
            return;
        }

        var match = EnumerateCategoryToggles().FirstOrDefault(t => ReferenceEquals(t.Button, btn));
        if (match.Button is null)
        {
            return;
        }

        vm.OnTerminalCategoryShiftClicked(match.Kind);
        e.Handled = true;
    }

    private void OnFilterChanged()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        // Capture the auto-scroll intent before RebuildDocument replaces the document text,
        // which resets the ScrollViewer to the top and (via OnScrollChanged) would clobber
        // _autoScroll to false. Restoring it keeps a bottom-pinned view pinned across a filter
        // rebuild — e.g. undoing a Shift+Click solo — instead of jumping to the top.
        var stickToBottom = _autoScroll;
        var newSearch = vm.TerminalSearchText ?? string.Empty;
        var searchJustCleared = _lastSearchText.Length > 0 && newSearch.Length == 0;
        _lastSearchText = newSearch;

        RebuildDocument(vm);

        if (stickToBottom || searchJustCleared)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ScrollToBottom, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var vm = DataContext as MainViewModel;
        var doc = TerminalEditor.Document;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is { Count: > 0 }:
            {
                var entry = (TerminalEntry)e.NewItems[0]!;
                if (vm is not null && !vm.IsEntryVisible(entry))
                {
                    break;
                }

                var text = FormatEntry(entry, vm?.TerminalTimestampMode ?? TerminalTimestampMode.WallClock);
                if (doc.TextLength > 0)
                {
                    doc.Insert(doc.TextLength, "\n" + text);
                }
                else
                {
                    doc.Insert(0, text);
                }

                _lineKinds.Add(entry.Kind);
                _lineEntries.Add(entry);
                break;
            }

            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex == 0:
            {
                if (vm is not null)
                {
                    RebuildDocument(vm);
                }

                break;
            }

            default:
            {
                if (vm is not null)
                {
                    RebuildDocument(vm);
                }

                break;
            }
        }

        if (_autoScroll)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ScrollToBottom, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void RebuildDocument(MainViewModel vm)
    {
        _lineKinds.Clear();
        _lineEntries.Clear();
        var sb = new StringBuilder();
        var first = true;
        foreach (var entry in vm.GetFilteredTerminalEntries())
        {
            if (!first)
            {
                sb.Append('\n');
            }

            sb.Append(FormatEntry(entry, vm.TerminalTimestampMode));
            _lineKinds.Add(entry.Kind);
            _lineEntries.Add(entry);
            first = false;
        }

        TerminalEditor.Document.Text = sb.ToString();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer is null || _isScrollingProgrammatically)
        {
            return;
        }

        var atBottom = _scrollViewer.Offset.Y >= _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height - 20;
        _autoScroll = atBottom;
    }

    private void ScrollToBottom()
    {
        if (_scrollViewer is null)
        {
            return;
        }

        _isScrollingProgrammatically = true;
        _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, _scrollViewer.Extent.Height);
        _isScrollingProgrammatically = false;
        _autoScroll = true;
    }

    private void OnTerminalContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        _contextEntry = null;
        if (e.TryGetPosition(TerminalEditor, out var point))
        {
            var pos = TerminalEditor.GetPositionFromPoint(point);
            if (pos is { } p)
            {
                var lineIndex = p.Line - 1; // TextViewPosition.Line is 1-based.
                if (lineIndex >= 0 && lineIndex < _lineEntries.Count)
                {
                    _contextEntry = _lineEntries[lineIndex];
                }
            }
        }

        UpdateRewindMenuItem();
    }

    private void UpdateRewindMenuItem()
    {
        if (_rewindMenuItem is null)
        {
            return;
        }

        var vm = DataContext as MainViewModel;
        var elapsed = _contextEntry?.ElapsedSeconds;
        var canRewind = elapsed is not null && vm is { IsTimelineAvailable: true };
        _rewindMenuItem.IsEnabled = canRewind;
        _rewindMenuItem.Header = canRewind ? $"Rewind to {FormatElapsed(elapsed!.Value)}" : "Rewind to this moment";
    }

    private async void OnRewindToMomentClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && _contextEntry?.ElapsedSeconds is { } seconds)
        {
            await vm.RewindToSeconds(seconds);
        }
    }

    /// <summary>Formats scenario-elapsed seconds as m:ss (or h:mm:ss past an hour) for terminal display.</summary>
    internal static string FormatElapsed(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private static string KindTag(TerminalEntryKind kind) =>
        kind switch
        {
            TerminalEntryKind.Command => "CMD",
            TerminalEntryKind.Response => "RSP",
            TerminalEntryKind.System => "SYS",
            TerminalEntryKind.Say => "SAY",
            TerminalEntryKind.Warning => "WRN",
            TerminalEntryKind.Error => "ERR",
            TerminalEntryKind.Chat => "CHAT",
            TerminalEntryKind.Tdls => "TDLS",
            TerminalEntryKind.Strip => "STRP",
            _ => "???",
        };

    private static void AppendTimestamp(StringBuilder sb, TerminalEntry entry, TerminalTimestampMode mode)
    {
        var wall = entry.Timestamp.ToString("HH:mm:ss");
        var sim = entry.ElapsedSeconds is { } seconds ? FormatElapsed(seconds) : "--:--";
        switch (mode)
        {
            case TerminalTimestampMode.SimElapsed:
                sb.Append(sim);
                break;
            case TerminalTimestampMode.Both:
                sb.Append(wall);
                sb.Append("  [");
                sb.Append(sim);
                sb.Append(']');
                break;
            default:
                sb.Append(wall);
                break;
        }
    }

    /// <summary>Formats one terminal line. Public for unit testing the timestamp-mode rendering.</summary>
    public static string FormatEntry(TerminalEntry entry, TerminalTimestampMode timestampMode)
    {
        var sb = new StringBuilder();
        AppendTimestamp(sb, entry, timestampMode);
        sb.Append("  ");
        sb.Append(KindTag(entry.Kind).PadRight(4));
        if (!string.IsNullOrEmpty(entry.Initials))
        {
            sb.Append("  ");
            sb.Append(entry.Initials);
        }

        if (!string.IsNullOrEmpty(entry.Callsign))
        {
            sb.Append("  ");
            sb.Append(entry.Callsign);
        }

        sb.Append("  ");
        sb.Append(entry.Message);
        return sb.ToString();
    }
}
