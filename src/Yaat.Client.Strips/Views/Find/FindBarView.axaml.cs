using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Yaat.Client.Find;

namespace Yaat.Client.Views.Find;

/// <summary>
/// Code-behind for the shared in-view Find bar. Focuses (and selects) its query box when the
/// bound <see cref="FindController"/> becomes visible, and maps Enter / Shift+Enter / F3 /
/// Shift+F3 / Esc inside the box to next / previous / close so those keys never leak to the host
/// view's strip / TDLS shortcuts.
/// </summary>
public partial class FindBarView : UserControl
{
    private FindController? _controller;

    public FindBarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_controller is not null)
        {
            _controller.PropertyChanged -= OnControllerPropertyChanged;
        }
        _controller = DataContext as FindController;
        if (_controller is not null)
        {
            _controller.PropertyChanged += OnControllerPropertyChanged;
        }
    }

    private void OnControllerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if ((e.PropertyName == nameof(FindController.IsVisible)) && (_controller?.IsVisible == true))
        {
            FocusInput();
        }
    }

    /// <summary>Focuses the query box and selects its text so re-opening replaces the prior query.</summary>
    public void FocusInput()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                QueryBox.Focus();
                QueryBox.SelectAll();
            },
            DispatcherPriority.Input
        );
    }

    private void OnQueryBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Enter:
            case Key.F3:
                if (shift)
                {
                    _controller.Previous();
                }
                else
                {
                    _controller.Next();
                }
                e.Handled = true;
                break;
            case Key.Escape:
                _controller.Close();
                e.Handled = true;
                break;
            default:
                break;
        }
    }
}
