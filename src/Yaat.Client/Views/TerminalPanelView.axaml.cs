using System.Collections.Specialized;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class TerminalPanelView : UserControl
{
    private readonly List<TerminalEntryKind> _lineKinds = [];
    private TerminalColorizer? _colorizer;
    private bool _isNearBottom = true;

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

        TerminalEditor.TextArea.TextView.ScrollOffsetChanged += OnScrollOffsetChanged;

        if (DataContext is MainViewModel vm)
        {
            vm.TerminalEntries.CollectionChanged += OnEntriesChanged;
            RebuildDocument(vm);
        }
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.TerminalEntries.CollectionChanged -= OnEntriesChanged;
        }

        TerminalEditor.TextArea.TextView.ScrollOffsetChanged -= OnScrollOffsetChanged;

        if (_colorizer is not null)
        {
            TerminalEditor.TextArea.TextView.LineTransformers.Remove(_colorizer);
            _colorizer = null;
        }

        base.OnUnloaded(e);
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var doc = TerminalEditor.Document;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is { Count: > 0 }:
            {
                var entry = (TerminalEntry)e.NewItems[0]!;
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
                var firstLine = doc.GetLineByNumber(1);
                doc.Remove(0, firstLine.TotalLength);
                if (_lineKinds.Count > 0)
                {
                    _lineKinds.RemoveAt(0);
                }

                break;
            }

            default:
            {
                if (DataContext is MainViewModel vm)
                {
                    RebuildDocument(vm);
                }

                break;
            }
        }

        if (_isNearBottom)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => TerminalEditor.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void RebuildDocument(MainViewModel vm)
    {
        _lineKinds.Clear();
        var sb = new StringBuilder();
        var first = true;
        foreach (var entry in vm.TerminalEntries)
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

    private void OnScrollOffsetChanged(object? sender, EventArgs e)
    {
        var textView = TerminalEditor.TextArea.TextView;
        var viewportBottom = textView.ScrollOffset.Y + textView.Bounds.Height;
        var documentHeight = textView.DocumentHeight;
        _isNearBottom = documentHeight - viewportBottom < 20;
    }

    private static string FormatEntry(TerminalEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append(entry.Timestamp.ToString("HH:mm:ss"));
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
