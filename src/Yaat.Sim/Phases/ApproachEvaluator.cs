using Yaat.Sim;

namespace Yaat.Sim.Phases;

/// <summary>
/// Per-room approach quality tracker. Collects <see cref="ApproachScore"/> objects drained from
/// <see cref="SimulationWorld"/>, computes separation from preceding same-runway traffic, grades
/// each approach, and builds aggregate reports on demand.
/// </summary>
public sealed class ApproachEvaluator
{
    private readonly List<StoredApproach> _stored = [];

    // --- Public API ---

    /// <summary>
    /// Records an establishment score. Computes separation to the closest preceding aircraft
    /// on the same runway that is already established (has an establishment position stored).
    /// </summary>
    public void RecordEstablishment(ApproachScore score, List<AircraftState> snapshot)
    {
        double? separation = ComputeSeparation(score, snapshot);
        _stored.Add(new StoredApproach(score, separation));
    }

    /// <summary>
    /// Updates the stored score for the given callsign with the landing timestamp, then
    /// grades the approach and stamps the <see cref="StoredApproach.Grade"/> field.
    /// </summary>
    public void RecordLanding(ApproachScore score)
    {
        var stored = FindStored(score.Callsign, score.RunwayId);
        if (stored is null)
        {
            // Landing received without prior establishment — store it now.
            _stored.Add(new StoredApproach(score, null));
            stored = _stored[^1];
        }

        stored.Score.LandedAtSeconds = score.LandedAtSeconds;
        stored.Grade = ComputeGrade(stored.Score);
    }

    /// <summary>Builds a complete approach report for the current session.</summary>
    public ApproachReportData BuildReport(double scenarioElapsedSeconds)
    {
        var approaches = _stored.Select(s => new ScoredApproach(s.Score, s.Grade ?? ComputeGrade(s.Score), s.SeparationNm)).ToList();

        var runwayStats = BuildRunwayStats(approaches, scenarioElapsedSeconds);
        string overallGrade = ComputeOverallGrade(approaches);

        return new ApproachReportData(approaches, runwayStats, scenarioElapsedSeconds, overallGrade);
    }

    /// <summary>
    /// Formats a one-line establishment broadcast suitable for the terminal panel.
    /// Example: "Established: angle=15°, dist=8.2nm, GS +120ft, 160kts"
    /// </summary>
    public static string FormatEstablishmentBroadcast(ApproachScore score)
    {
        string angleStr = $"{score.InterceptAngleDeg:F0}°";
        string distStr = $"{score.InterceptDistanceNm:F1}nm";
        string gsSign = score.GlideSlopeDeviationFt >= 0 ? "+" : "";
        string gsStr = $"GS {gsSign}{score.GlideSlopeDeviationFt:F0}ft";
        string spdStr = $"{score.SpeedAtInterceptKts:F0}kts";
        string legal = score.IsInterceptAngleLegal && score.IsInterceptDistanceLegal ? "" : " [ILLEGAL]";
        string forced = score.WasForced ? " [FORCED]" : "";
        return $"Established: angle={angleStr}, dist={distStr}, {gsStr}, {spdStr}{legal}{forced}";
    }

    /// <summary>
    /// Formats a one-line landing broadcast suitable for the terminal panel.
    /// Example: "Landed Rwy 28R — Grade: B (GS +180ft above)"
    /// </summary>
    public static string FormatLandingBroadcast(ApproachScore score)
    {
        string grade = ComputeGrade(score);
        string gsSign = score.GlideSlopeDeviationFt >= 0 ? "+" : "";
        string gsDetail = $"GS {gsSign}{score.GlideSlopeDeviationFt:F0}ft";
        string side = score.GlideSlopeDeviationFt >= 0 ? "above" : "below";
        return $"Landed Rwy {score.RunwayId} — Grade: {grade} ({gsDetail} {side})";
    }

    /// <summary>Clears all stored scores. Called on scenario unload.</summary>
    public void Reset()
    {
        _stored.Clear();
    }

    // --- Grading ---

    /// <summary>
    /// Demerit-based grading rubric:
    /// A — all legal, GS within ±100 ft, not forced
    /// B — all legal, GS within ±300 ft
    /// C — one minor violation (angle near limit, or GS 300–500 ft off)
    /// D — illegal angle or illegal distance
    /// F — multiple violations or forced
    /// </summary>
    public static string ComputeGrade(ApproachScore score)
    {
        int demerits = 0;

        if (score.WasForced)
        {
            demerits += 3;
        }

        if (!score.IsInterceptAngleLegal)
        {
            demerits += 2;
        }

        if (!score.IsInterceptDistanceLegal)
        {
            demerits += 2;
        }

        double absGs = Math.Abs(score.GlideSlopeDeviationFt);
        if (absGs > 500)
        {
            demerits += 2;
        }
        else if (absGs > 300)
        {
            demerits += 1;
        }

        return demerits switch
        {
            0 when absGs <= 100 => "A",
            0 => "B",
            1 => "C",
            2 => "D",
            _ => "F",
        };
    }

    // --- Private helpers ---

    private StoredApproach? FindStored(string callsign, string runwayId)
    {
        for (int i = _stored.Count - 1; i >= 0; i--)
        {
            var s = _stored[i];
            if (
                s.Score.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase)
                && s.Score.RunwayId.Equals(runwayId, StringComparison.OrdinalIgnoreCase)
            )
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds all stored approaches on the same runway that were established before this score,
    /// and returns the closest separation distance. Uses aircraft snapshot for current positions
    /// as a proxy when the preceding aircraft is still airborne.
    /// </summary>
    private double? ComputeSeparation(ApproachScore score, List<AircraftState> snapshot)
    {
        double? minSep = null;

        foreach (var stored in _stored)
        {
            if (!stored.Score.RunwayId.Equals(score.RunwayId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (stored.Score.EstablishedAtSeconds >= score.EstablishedAtSeconds)
            {
                continue;
            }

            // Prefer current aircraft position from snapshot for still-airborne traffic.
            double precedingLat = stored.Score.EstablishedLat;
            double precedingLon = stored.Score.EstablishedLon;

            var live = snapshot.FirstOrDefault(a => a.Callsign.Equals(stored.Score.Callsign, StringComparison.OrdinalIgnoreCase));
            if (live is not null)
            {
                precedingLat = live.Latitude;
                precedingLon = live.Longitude;
            }

            double sep = GeoMath.DistanceNm(score.EstablishedLat, score.EstablishedLon, precedingLat, precedingLon);
            if (minSep is null || sep < minSep.Value)
            {
                minSep = sep;
            }
        }

        return minSep;
    }

    private static List<RunwayStats> BuildRunwayStats(List<ScoredApproach> approaches, double scenarioElapsedSeconds)
    {
        var byRunway = new Dictionary<string, List<ScoredApproach>>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in approaches)
        {
            if (!byRunway.TryGetValue(a.Score.RunwayId, out var list))
            {
                list = [];
                byRunway[a.Score.RunwayId] = list;
            }

            list.Add(a);
        }

        var stats = new List<RunwayStats>();

        foreach (var (runwayId, group) in byRunway)
        {
            var landed = group.Where(a => a.Score.LandedAtSeconds.HasValue).OrderBy(a => a.Score.LandedAtSeconds!.Value).ToList();
            int landingCount = landed.Count;

            double arrivalRatePerHour = 0;
            double averageTimeBetweenSec = 0;

            if (scenarioElapsedSeconds > 0 && landingCount > 0)
            {
                arrivalRatePerHour = landingCount / (scenarioElapsedSeconds / 3600.0);
            }

            if (landed.Count >= 2)
            {
                var intervals = new List<double>();
                for (int i = 1; i < landed.Count; i++)
                {
                    intervals.Add(landed[i].Score.LandedAtSeconds!.Value - landed[i - 1].Score.LandedAtSeconds!.Value);
                }

                averageTimeBetweenSec = intervals.Average();
            }

            double? minSep = null;
            foreach (var a in group)
            {
                if (a.SeparationNm.HasValue && (minSep is null || a.SeparationNm.Value < minSep.Value))
                {
                    minSep = a.SeparationNm;
                }
            }

            stats.Add(new RunwayStats(runwayId, landingCount, arrivalRatePerHour, averageTimeBetweenSec, minSep));
        }

        return stats;
    }

    private static string ComputeOverallGrade(List<ScoredApproach> approaches)
    {
        if (approaches.Count == 0)
        {
            return "N/A";
        }

        // Numeric score: A=4, B=3, C=2, D=1, F=0
        static int GradeValue(string g) =>
            g switch
            {
                "A" => 4,
                "B" => 3,
                "C" => 2,
                "D" => 1,
                _ => 0,
            };

        double avg = approaches.Average(a => GradeValue(a.Grade));

        return avg switch
        {
            >= 3.5 => "A",
            >= 2.5 => "B",
            >= 1.5 => "C",
            >= 0.5 => "D",
            _ => "F",
        };
    }

    // --- Inner types ---

    private sealed class StoredApproach(ApproachScore score, double? separationNm)
    {
        public ApproachScore Score { get; } = score;
        public double? SeparationNm { get; } = separationNm;
        public string? Grade { get; set; }
    }
}

// --- Report data models ---

public record ApproachReportData(List<ScoredApproach> Approaches, List<RunwayStats> RunwayStats, double ScenarioElapsedSeconds, string OverallGrade);

public record ScoredApproach(ApproachScore Score, string Grade, double? SeparationNm);

public record RunwayStats(
    string RunwayId,
    int LandingCount,
    double ArrivalRatePerHour,
    double AverageTimeBetweenLandingsSec,
    double? MinSeparationNm
);
