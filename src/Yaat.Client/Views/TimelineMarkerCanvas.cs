using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Yaat.Client.Views;

/// <summary>
/// Panel that arranges its children horizontally by an attached <see cref="TimeProperty"/>
/// against a <see cref="MaxTimeProperty"/>. Used by the M12.5-enhanced MainWindow timeline
/// bar to overlay finding markers above the rewind slider. The thin (~2px) markers sit on
/// top of the slider track; the panel itself is hit-test-transparent for everything except
/// the markers so the slider underneath stays draggable.
/// </summary>
public sealed class TimelineMarkerCanvas : Panel
{
    public static readonly StyledProperty<double> MaxTimeProperty = AvaloniaProperty.Register<TimelineMarkerCanvas, double>(
        nameof(MaxTime),
        defaultValue: 0,
        coerce: (_, v) => double.IsFinite(v) && v > 0 ? v : 0
    );

    public double MaxTime
    {
        get => GetValue(MaxTimeProperty);
        set => SetValue(MaxTimeProperty, value);
    }

    public static readonly AttachedProperty<double> TimeProperty = AvaloniaProperty.RegisterAttached<TimelineMarkerCanvas, Control, double>("Time");

    public static double GetTime(Control control) => control.GetValue(TimeProperty);

    public static void SetTime(Control control, double value) => control.SetValue(TimeProperty, value);

    // Slider thumb is ~10 px wide; the track inset roughly matches. We don't have a direct
    // handle on it from XAML so use a sensible default and let the slider absorb the rest.
    private const double EdgeInsetPx = 8.0;

    static TimelineMarkerCanvas()
    {
        AffectsArrange<TimelineMarkerCanvas>(MaxTimeProperty);
        AffectsArrange<TimelineMarkerCanvas>(TimeProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(availableSize);
        }
        // Adopt the parent's allotted width and a fixed minimal height; markers are thin.
        double height = 0;
        foreach (var child in Children)
        {
            if (child.DesiredSize.Height > height)
            {
                height = child.DesiredSize.Height;
            }
        }
        return new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
            double.IsFinite(availableSize.Height) ? Math.Min(availableSize.Height, height > 0 ? height : 8) : (height > 0 ? height : 8)
        );
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double maxTime = MaxTime;
        double usableWidth = Math.Max(0, finalSize.Width - (2 * EdgeInsetPx));
        foreach (var child in Children)
        {
            double t = GetTime(child);
            double x;
            if (maxTime <= 0 || !double.IsFinite(t))
            {
                x = EdgeInsetPx;
            }
            else
            {
                double clamped = Math.Clamp(t, 0, maxTime);
                x = EdgeInsetPx + ((clamped / maxTime) * usableWidth);
            }
            double width = child.DesiredSize.Width > 0 ? child.DesiredSize.Width : 3;
            double height = child.DesiredSize.Height > 0 ? child.DesiredSize.Height : finalSize.Height;
            child.Arrange(new Rect(x - (width / 2), 0, width, height));
        }
        return finalSize;
    }
}

/// <summary>
/// Small standalone helper for drawing the marker tick. Used by the marker DataTemplate
/// so the same color logic lives in C# rather than scattered through XAML brushes.
/// </summary>
public static class TimelineMarkerVisuals
{
    public static readonly IBrush SafetyFill = new SolidColorBrush(Color.FromRgb(220, 60, 60));
    public static readonly IBrush WarningFill = new SolidColorBrush(Color.FromRgb(230, 180, 50));
    public static readonly IBrush CoachFill = new SolidColorBrush(Color.FromRgb(80, 160, 220));
    public static readonly IBrush BorderStroke = new SolidColorBrush(Color.FromRgb(20, 20, 20));
}
