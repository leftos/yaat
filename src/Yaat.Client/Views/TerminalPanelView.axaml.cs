using System.Collections.Specialized;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class TerminalPanelView : UserControl
{
    private bool _isNearBottom = true;

    public TerminalPanelView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainViewModel vm)
        {
            vm.TerminalEntries.CollectionChanged += OnEntriesChanged;
            RebuildInlines(vm);
        }

        TerminalScrollViewer.ScrollChanged += OnScrollChanged;
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.TerminalEntries.CollectionChanged -= OnEntriesChanged;
        }

        TerminalScrollViewer.ScrollChanged -= OnScrollChanged;

        base.OnUnloaded(e);
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            RebuildInlines(vm);
        }

        if (_isNearBottom)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => TerminalScrollViewer.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private void RebuildInlines(MainViewModel vm)
    {
        var tb = TerminalTextBlock;
        tb.Inlines?.Clear();
        var inlines = tb.Inlines ??= [];
        var first = true;
        foreach (var entry in vm.TerminalEntries)
        {
            if (!first)
            {
                inlines.Add(new LineBreak());
            }

            inlines.Add(new Run(FormatEntry(entry)) { Foreground = GetBrush(entry.Kind) });
            first = false;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        _isNearBottom = TerminalScrollViewer.Offset.Y >= TerminalScrollViewer.ScrollBarMaximum.Y - 20;
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

    private static IBrush GetBrush(TerminalEntryKind kind) =>
        kind switch
        {
            TerminalEntryKind.Command => Brushes.White,
            TerminalEntryKind.Response => Brushes.LightGray,
            TerminalEntryKind.System => Brushes.Gray,
            TerminalEntryKind.Say => Brushes.Green,
            TerminalEntryKind.Warning => Brushes.Orange,
            TerminalEntryKind.Error => Brushes.Red,
            _ => Brushes.White,
        };
}
