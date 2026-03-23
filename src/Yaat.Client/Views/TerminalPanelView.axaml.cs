using System.Collections.Specialized;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class TerminalPanelView : UserControl
{
    private readonly List<TerminalEntryKind> _lineKinds = [];
    private TerminalColorizer? _colorizer;
    private ScrollViewer? _scrollViewer;
    private bool _autoScroll = true;
    private bool _isScrollingProgrammatically;

    public TerminalPanelView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _colorizer = new TerminalColorizer(_lineKinds);
        TerminalEditor.TextArea.TextView.LineTransformers.Add(_colorizer);
        TerminalEditor.TextArea.Caret.CaretBrush = Brushes.Transparent;

        _scrollViewer = TerminalEditor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnScrollChanged;
        }

        if (DataContext is MainViewModel vm)
        {
            vm.TerminalEntries.CollectionChanged += OnEntriesChanged;
            vm.TerminalFilterChanged += OnFilterChanged;
            RebuildDocument(vm);
        }
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.TerminalEntries.CollectionChanged -= OnEntriesChanged;
            vm.TerminalFilterChanged -= OnFilterChanged;
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

    private void OnFilterChanged()
    {
        if (DataContext is MainViewModel vm)
        {
            RebuildDocument(vm);
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
                if (vm is not null && !vm.IsEntryVisible(entry.Kind))
                {
                    break;
                }

                var text = FormatEntry(entry);
                if (doc.TextLength > 0)
                {
                    doc.Insert(doc.TextLength, "\n" + text);
                }
                else
                {
                    doc.Insert(0, text);
                }

                _lineKinds.Add(entry.Kind);
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
        var sb = new StringBuilder();
        var first = true;
        foreach (var entry in vm.GetFilteredTerminalEntries())
        {
            if (!first)
            {
                sb.Append('\n');
            }

            sb.Append(FormatEntry(entry));
            _lineKinds.Add(entry.Kind);
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
            _ => "???",
        };

    private static string FormatEntry(TerminalEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append(entry.Timestamp.ToString("HH:mm:ss"));
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
