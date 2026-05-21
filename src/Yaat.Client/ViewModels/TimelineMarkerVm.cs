using Avalonia.Media;

namespace Yaat.Client.ViewModels;

/// <summary>
/// What a <see cref="TimelineMarkerVm"/> represents — drives the visual differentiation
/// between rule findings (color-coded by severity) and issued commands (neutral grey).
/// </summary>
public enum TimelineMarkerKind
{
    Finding,
    Command,
}

/// <summary>
/// One marker on the M12.5-enhanced timeline bar. Rendered as a vertical tick above
/// the rewind slider; clicking the marker rewinds the simulation to <see cref="TimeSeconds"/>.
/// Findings come from <see cref="Yaat.Client.Services.SoloTrainingEventDto"/> via the
/// session-report poll; commands come from <see cref="MainViewModel.RecordCommandMarker"/>
/// at dispatch time.
/// </summary>
public sealed class TimelineMarkerVm
{
    public required double TimeSeconds { get; init; }
    public required TimelineMarkerKind Kind { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<string> Callsigns { get; init; }
    public required string Id { get; init; }
    public string? Severity { get; init; }
    public string? Category { get; init; }
    public string? CommandText { get; init; }

    public string TimeText => TimeSpan.FromSeconds(TimeSeconds).ToString(@"h\:mm\:ss");
    public string CallsignsText => string.Join("/", Callsigns);

    public IBrush FillBrush =>
        Kind == TimelineMarkerKind.Command
            ? CommandBrush
            : Severity switch
            {
                "Safety" => SafetyBrush,
                "Warning" => WarningBrush,
                "Coach" => CoachBrush,
                _ => CoachBrush,
            };

    public double MarkerHeight => Kind == TimelineMarkerKind.Command ? 4 : 6;
    public double MarkerWidth => Kind == TimelineMarkerKind.Command ? 2 : 3;

    public string ToolTipText =>
        Kind == TimelineMarkerKind.Command ? $"{TimeText} — {CallsignsText}: {CommandText}" : $"{TimeText} — {Severity}: {Title} ({CallsignsText})";

    // Cached brushes — STARS-ish palette.
    private static readonly IBrush SafetyBrush = new SolidColorBrush(Color.FromRgb(220, 60, 60));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.FromRgb(230, 180, 50));
    private static readonly IBrush CoachBrush = new SolidColorBrush(Color.FromRgb(80, 160, 220));
    private static readonly IBrush CommandBrush = new SolidColorBrush(Color.FromRgb(160, 160, 160));
}
