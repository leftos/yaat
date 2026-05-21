using Avalonia.Media;

namespace Yaat.Client.ViewModels;

/// <summary>
/// One marker on the M12.5-enhanced timeline bar. Rendered as a vertical tick above
/// the rewind slider; clicking the marker rewinds the simulation to <see cref="TimeSeconds"/>.
/// Markers come from <see cref="Yaat.Client.Services.SoloTrainingEventDto"/> via the
/// session-report poll in <see cref="MainViewModel"/>.
/// </summary>
public sealed class TimelineMarkerVm
{
    public required double TimeSeconds { get; init; }
    public required string Severity { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required IReadOnlyList<string> Callsigns { get; init; }
    public required string Id { get; init; }

    public string TimeText => TimeSpan.FromSeconds(TimeSeconds).ToString(@"h\:mm\:ss");
    public string CallsignsText => string.Join("/", Callsigns);

    public IBrush FillBrush =>
        Severity switch
        {
            "Safety" => SafetyBrush,
            "Warning" => WarningBrush,
            "Coach" => CoachBrush,
            _ => CoachBrush,
        };

    public string ToolTipText => $"{TimeText} — {Severity}: {Title} ({CallsignsText})";

    // Cached brushes — STARS-ish palette.
    private static readonly IBrush SafetyBrush = new SolidColorBrush(Color.FromRgb(220, 60, 60));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.FromRgb(230, 180, 50));
    private static readonly IBrush CoachBrush = new SolidColorBrush(Color.FromRgb(80, 160, 220));
}
