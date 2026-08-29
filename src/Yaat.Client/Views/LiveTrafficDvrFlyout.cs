using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

/// <summary>
/// The DVR control behind the live-session badge: where the room stands in the feed, the span the server's raw log can
/// replay, a slider + HH:mm box to pick an instant, "Go to" (the server drops every non-shadow aircraft and re-acquires
/// the shadows there) and "Go Live". Built in code so it reads the freshly fetched window each time it opens.
/// </summary>
public sealed class LiveTrafficDvrFlyout
{
    private readonly MainViewModel _vm;
    private readonly Flyout _flyout;
    private readonly Slider _slider;
    private readonly TextBox _timeBox;
    private readonly TextBlock _pickText;
    private readonly Button _goTo;
    private bool _syncing;

    public LiveTrafficDvrFlyout(MainViewModel vm)
    {
        _vm = vm;
        _flyout = new Flyout { Placement = PlacementMode.Top };
        var window = vm.LiveTrafficWindow;
        bool available = window is { Available: true, StartUtc: not null, EndUtc: not null };
        const double startSeconds = 0;
        double endSeconds = available ? (window!.EndUtc!.Value - window.StartUtc!.Value).TotalSeconds : 1;

        var header = new TextBlock { Text = $"Room is at {vm.LiveFeedTimeText}", FontWeight = Avalonia.Media.FontWeight.Bold };
        var windowText = new TextBlock
        {
            Text = vm.LiveWindowText,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 360,
        };
        _slider = new Slider
        {
            Minimum = startSeconds,
            Maximum = endSeconds,
            Width = 360,
            IsEnabled = available,
            TickFrequency = 60,
        };
        _timeBox = new TextBox
        {
            Width = 80,
            PlaceholderText = "HH:mm",
            IsEnabled = available,
        };
        _pickText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        _goTo = new Button { Content = "Go to", IsEnabled = false };
        var goLive = new Button { Content = "Go Live", IsEnabled = vm.ShowGoLive };

        if (available && vm.LiveFeedTimeUtc is { } feedTime)
        {
            _slider.Value = Math.Clamp((feedTime - window!.StartUtc!.Value).TotalSeconds, startSeconds, endSeconds);
            _timeBox.Text = feedTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        _slider.ValueChanged += (_, _) => OnSliderChanged();
        _timeBox.TextChanged += (_, _) => OnTimeBoxChanged();
        _goTo.Click += async (_, _) =>
        {
            if (Picked() is { } utc)
            {
                _flyout.Hide();
                await vm.SeekLiveTrafficAsync(utc);
            }
        };
        goLive.Click += async (_, _) =>
        {
            _flyout.Hide();
            await vm.GoLiveCommand.ExecuteAsync(null);
        };

        var pickRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Go to (UTC):", VerticalAlignment = VerticalAlignment.Center },
                _timeBox,
                _pickText,
            },
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _goTo, goLive },
        };
        var panel = new StackPanel
        {
            Spacing = 6,
            Margin = new Avalonia.Thickness(4),
            Children = { header, windowText, _slider, pickRow, buttons },
        };
        _flyout.Content = panel;
        UpdatePick();
    }

    public void ShowAt(Control target) => _flyout.ShowAt(target);

    private DateTimeOffset? Picked()
    {
        var window = _vm.LiveTrafficWindow;
        if (window is not { Available: true, StartUtc: { } start })
        {
            return null;
        }

        return start.AddSeconds(Math.Round(_slider.Value));
    }

    private void OnSliderChanged()
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        if (Picked() is { } utc)
        {
            _timeBox.Text = utc.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        _syncing = false;
        UpdatePick();
    }

    private void OnTimeBoxChanged()
    {
        if (_syncing)
        {
            return;
        }

        var window = _vm.LiveTrafficWindow;
        if (window is not { Available: true, StartUtc: { } start, EndUtc: { } end })
        {
            return;
        }

        var parsed = LiveSessionWindow.ParseStartAt(_timeBox.Text, end, out var error);
        if (error is null && parsed is { } utc && utc >= start)
        {
            _syncing = true;
            _slider.Value = (utc - start).TotalSeconds;
            _syncing = false;
        }

        UpdatePick();
    }

    private void UpdatePick()
    {
        var picked = Picked();
        _pickText.Text = picked is { } utc ? $"= {utc:HH:mm:ss}Z" : "";
        _goTo.IsEnabled = picked is not null;
    }
}
