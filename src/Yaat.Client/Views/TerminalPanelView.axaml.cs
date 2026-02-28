using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.VisualTree;

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

        if (DataContext is ViewModels.MainViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged -= OnScrollChanged;
        }

        base.OnUnloaded(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModels.MainViewModel.TerminalText))
        {
            return;
        }

        var sv = GetScrollViewer();
        if (_isNearBottom && sv is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => sv.ScrollToEnd(),
                Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private ScrollViewer? GetScrollViewer()
    {
        if (_scrollViewer is not null)
        {
            return _scrollViewer;
        }

        var textBox = this.FindControl<TextBox>("TerminalTextBox");
        _scrollViewer = textBox?.FindDescendantOfType<ScrollViewer>();
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnScrollChanged;
        }

        return _scrollViewer;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer is null)
        {
            return;
        }

        _isNearBottom = _scrollViewer.Offset.Y >= _scrollViewer.ScrollBarMaximum.Y - 20;
    }
}
