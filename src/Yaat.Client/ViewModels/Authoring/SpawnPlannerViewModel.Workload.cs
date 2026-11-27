using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring.Spawns;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The spawn planner's Workload mode: a curve from a start to an end percentage through control-point rows, its interval
/// limits and jitter, an auto-fill of evenly spaced peaks, and either a fill that places every matched aircraft or a walk
/// along the curve.
/// </summary>
public sealed partial class SpawnPlannerViewModel
{
    /// <summary>The error a preview or Apply shows when a workload figure is not a number.</summary>
    public const string WorkloadErrorText = "Every workload percentage and point time must be a number.";

    [ObservableProperty]
    private int _bucketSizeSec = SpawnPlanWorkload.Default.BucketSizeSec;

    [ObservableProperty]
    private int _minIntervalSec = SpawnPlanWorkload.Default.MinIntervalSec;

    [ObservableProperty]
    private int _maxIntervalSec = SpawnPlanWorkload.Default.MaxIntervalSec;

    [ObservableProperty]
    private int _jitterSec = SpawnPlanWorkload.Default.JitterSec;

    [ObservableProperty]
    private double _startPercent = SpawnPlanWorkload.Default.StartPercent;

    [ObservableProperty]
    private double _endPercent = SpawnPlanWorkload.Default.EndPercent;

    [ObservableProperty]
    private bool _distributeAllMatched = SpawnPlanWorkload.Default.DistributeAllMatched;

    [ObservableProperty]
    private int _autoPeaks = SpawnPlanWorkload.Default.AutoPeaks;

    [ObservableProperty]
    private double _autoPeakPercent = SpawnPlanWorkload.Default.AutoPeakPercent;

    [ObservableProperty]
    private double _autoTroughPercent = SpawnPlanWorkload.Default.AutoTroughPercent;

    /// <summary>Gets the curve's control points, in the order the rows show them; the graph's drag edits them in place.</summary>
    public ObservableCollection<WorkloadPointRowViewModel> ControlPoints { get; } = [];

    /// <summary>The curve the workload inputs draw over the planning length, or null when one of them is not a number.</summary>
    /// <returns>The curve, or null.</returns>
    public WorkloadCurve? BuildCurve() => (WorkloadError() is null) ? new WorkloadCurve(PlanningLengthSec, StartPercent, EndPercent, Points()) : null;

    /// <summary>
    /// Replaces the control points with <see cref="AutoPeaks"/> evenly spaced peaks and the troughs between them; with no
    /// planning length the points stay and <see cref="PreviewError"/> says why.
    /// </summary>
    [RelayCommand]
    public void AutoFill()
    {
        if (PlanningLengthSec <= 0)
        {
            PreviewError = LengthErrorText;
            return;
        }

        IReadOnlyList<WorkloadPoint> points = WorkloadCurve.AutoPoints(PlanningLengthSec, AutoPeaks, AutoPeakPercent, AutoTroughPercent);
        ControlPoints.Clear();
        foreach (WorkloadPoint point in points)
        {
            ControlPoints.Add(new WorkloadPointRowViewModel(point.TimeSec / 60.0, point.Percent));
        }
    }

    /// <summary>Adds a control point halfway through the planning length at half workload.</summary>
    [RelayCommand]
    public void AddPoint() => ControlPoints.Add(new WorkloadPointRowViewModel(PlanningLengthMinutes / 2.0, 50));

    /// <summary>Removes a control point row.</summary>
    /// <param name="row">The row to remove; a row not in the list is ignored.</param>
    [RelayCommand]
    public void RemovePoint(WorkloadPointRowViewModel? row)
    {
        if (row is not null)
        {
            _ = ControlPoints.Remove(row);
        }
    }

    private SpawnPlanWorkload WorkloadPlan() =>
        new(
            BucketSizeSec,
            MinIntervalSec,
            MaxIntervalSec,
            JitterSec,
            StartPercent,
            EndPercent,
            DistributeAllMatched,
            AutoPeaks,
            AutoPeakPercent,
            AutoTroughPercent
        );

    private void LoadWorkload(SpawnPlanWorkload workload, IReadOnlyList<SpawnPlanPoint> points)
    {
        BucketSizeSec = workload.BucketSizeSec;
        MinIntervalSec = workload.MinIntervalSec;
        MaxIntervalSec = workload.MaxIntervalSec;
        JitterSec = workload.JitterSec;
        StartPercent = workload.StartPercent;
        EndPercent = workload.EndPercent;
        DistributeAllMatched = workload.DistributeAllMatched;
        AutoPeaks = workload.AutoPeaks;
        AutoPeakPercent = workload.AutoPeakPercent;
        AutoTroughPercent = workload.AutoTroughPercent;
        ControlPoints.Clear();
        foreach (SpawnPlanPoint point in points)
        {
            ControlPoints.Add(new WorkloadPointRowViewModel(point.TimeMinutes, point.Percent));
        }
    }

    private string? WorkloadError()
    {
        bool finite =
            (double.IsFinite(StartPercent))
            && (double.IsFinite(EndPercent))
            && (ControlPoints.All(point => (double.IsFinite(point.TimeMinutes)) && (double.IsFinite(point.Percent))));
        return finite ? null : WorkloadErrorText;
    }

    private List<WorkloadPoint> Points() => [.. ControlPoints.Select(point => new WorkloadPoint(point.TimeMinutes * 60, point.Percent))];

    private IReadOnlyList<int> WorkloadSlots(int count, Random random)
    {
        var curve = new WorkloadCurve(PlanningLengthSec, StartPercent, EndPercent, Points());
        var settings = new WorkloadSettings(MinIntervalSec, MaxIntervalSec, JitterSec);
        return DistributeAllMatched ? SpawnSchedule.FillSlots(curve, count, settings, random) : SpawnSchedule.WalkSlots(curve, settings, random);
    }
}

/// <summary>One workload control point row: a percentage at a time in minutes from the start.</summary>
/// <param name="timeMinutes">The point's time in minutes.</param>
/// <param name="percent">The workload at that time.</param>
public sealed partial class WorkloadPointRowViewModel(double timeMinutes, double percent) : ObservableObject
{
    [ObservableProperty]
    private double _timeMinutes = timeMinutes;

    [ObservableProperty]
    private double _percent = percent;

    /// <summary>Gets the point as a plan file holds it.</summary>
    public SpawnPlanPoint ToPlan() => new(TimeMinutes, Percent);
}
