using System.Collections.Specialized;
using Avalonia.Controls;

namespace Yaat.Client.Views;

public partial class TerminalPanelView : UserControl
{
    private ScrollViewer? _scrollViewer;
    private bool _isNearBottom = true;

    public TerminalPanelView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _scrollViewer = this.FindControl<ScrollViewer>("TerminalScroll");

        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnScrollChanged;
        }

        if (DataContext is ViewModels.MainViewModel vm)
        {
            vm.TerminalEntries.CollectionChanged += OnEntriesChanged;
        }
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm)
        {
            vm.TerminalEntries.CollectionChanged -= OnEntriesChanged;
        }

        base.OnUnloaded(e);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer is null)
        {
            return;
        }

        _isNearBottom = _scrollViewer.Offset.Y >= _scrollViewer.ScrollBarMaximum.Y - 20;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isNearBottom && _scrollViewer is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _scrollViewer.ScrollToEnd();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }
}
