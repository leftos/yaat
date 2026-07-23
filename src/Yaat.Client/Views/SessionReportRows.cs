using Yaat.Client.Services;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.Views;

// Presentation rows for SessionReportWindow's grids. Top-level (not nested in the window) so the
// XAML compiler can name them in x:DataType and type-check the column bindings.

internal sealed record ScoreBucketRow(string Name, string Summary, double PercentKept)
{
    public static ScoreBucketRow FromDto(SoloTrainingScoreBucketDto dto)
    {
        int kept = Math.Max(0, dto.PointsAvailable - dto.PointsLost);
        double percent = dto.PointsAvailable > 0 ? kept * 100.0 / dto.PointsAvailable : 0.0;
        return new ScoreBucketRow(dto.Name, $"{kept}/{dto.PointsAvailable}", percent);
    }
}

internal sealed record EventRow(
    string StartedText,
    string Severity,
    string Category,
    string Title,
    string Description,
    string RuleReference,
    string CallsignsText,
    string ExposureText,
    string RequiredText,
    string ActualText
)
{
    public static EventRow FromDto(SoloTrainingEventDto dto)
    {
        var started = TimeSpan.FromSeconds(dto.StartedAtSeconds);
        var exposure = TimeSpan.FromSeconds(dto.ExposureSeconds);
        string required = !string.IsNullOrWhiteSpace(dto.RequiredText)
            ? dto.RequiredText
            : FormatRequirement(dto.RequiredHorizontalNm, dto.RequiredVerticalFt);
        string actual = !string.IsNullOrWhiteSpace(dto.ActualText) ? dto.ActualText : FormatRequirement(dto.ActualHorizontalNm, dto.ActualVerticalFt);
        return new EventRow(
            started.ToString(@"h\:mm\:ss"),
            dto.Severity,
            dto.Category,
            dto.Title,
            dto.Description,
            dto.RuleReference,
            string.Join("/", dto.Callsigns),
            exposure.ToString(@"m\:ss"),
            required,
            actual
        );
    }

    private static string FormatRequirement(double? horizontalNm, double? verticalFt)
    {
        if (horizontalNm.HasValue && verticalFt.HasValue)
        {
            return $"{horizontalNm.Value:F1} NM / {verticalFt.Value:F0} ft";
        }

        if (horizontalNm.HasValue)
        {
            return $"{horizontalNm.Value:F1} NM";
        }

        if (verticalFt.HasValue)
        {
            return $"{verticalFt.Value:F0} ft";
        }

        return "";
    }
}

internal sealed record AircraftDebriefRow(
    string Callsign,
    string AircraftType,
    string OperationText,
    string RouteText,
    string SpawnedText,
    string CompletedText,
    string StatusText,
    string FindingsText,
    string CoachingNote,
    double SpawnedAtSeconds
)
{
    public static AircraftDebriefRow FromDto(AircraftDebriefDto dto)
    {
        string route = (dto.FiledDeparture, dto.FiledDestination) switch
        {
            (null, null) => "—",
            ({ Length: > 0 } d, null) => $"{d} →",
            (null, { Length: > 0 } a) => $"→ {a}",
            ({ Length: > 0 } d, { Length: > 0 } a) => $"{d} → {a}",
            _ => "—",
        };

        string spawned = TimeSpan.FromSeconds(dto.SpawnedAtSeconds).ToString(@"h\:mm\:ss");
        string completed = dto.CompletedAtSeconds.HasValue ? TimeSpan.FromSeconds(dto.CompletedAtSeconds.Value).ToString(@"h\:mm\:ss") : "—";

        string status = dto.CompletionReason switch
        {
            "Landed" when !string.IsNullOrEmpty(dto.CompletionDetail) => $"Landed RW {RunwayIdentifier.ToDisplayDesignator(dto.CompletionDetail)}",
            "Landed" => "Landed",
            "HandedOff" when !string.IsNullOrEmpty(dto.CompletionDetail) => $"Handed off {dto.CompletionDetail}",
            "HandedOff" => "Handed off",
            "Dropped" => "Dropped",
            _ => "Active",
        };

        int total = dto.SeparationFindingCount + dto.RunwayWakeFindingCount + dto.AdvisoryFindingCount + dto.ApproachFindingCount;
        string findings = total == 0 ? "0" : $"{total} ({dto.SafetyFindingCount}S / {dto.WarningFindingCount}W / {dto.CoachFindingCount}C)";

        return new AircraftDebriefRow(
            dto.Callsign,
            dto.AircraftType,
            dto.Operation,
            route,
            spawned,
            completed,
            status,
            findings,
            dto.CoachingNote,
            dto.SpawnedAtSeconds
        );
    }
}
