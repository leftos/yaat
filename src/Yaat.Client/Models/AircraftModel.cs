using System;
using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Yaat.Client.Models;

public partial class AircraftModel : ObservableObject
{
    [ObservableProperty]
    private string _callsign = "";

    [ObservableProperty]
    private string _aircraftType = "";

    [ObservableProperty]
    private double _latitude;

    [ObservableProperty]
    private double _longitude;

    [ObservableProperty]
    private double _heading;

    [ObservableProperty]
    private double _altitude;

    [ObservableProperty]
    private double _groundSpeed;

    [ObservableProperty]
    private uint _beaconCode;

    [ObservableProperty]
    private string _transponderMode = "C";

    [ObservableProperty]
    private double _verticalSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignedHeadingDisplay))]
    private double? _assignedHeading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignedHeadingDisplay))]
    private string _navigatingTo = "";

    public string AssignedHeadingDisplay =>
        !string.IsNullOrEmpty(NavigatingTo)
            ? NavigatingTo
            : AssignedHeading.HasValue
                ? AssignedHeading.Value.ToString("F0")
                : "";

    [ObservableProperty]
    private double? _assignedAltitude;

    [ObservableProperty]
    private double? _assignedSpeed;

    [ObservableProperty]
    private string _departure = "";

    [ObservableProperty]
    private string _destination = "";

    [ObservableProperty]
    private string _route = "";

    [ObservableProperty]
    private string _flightRules = "IFR";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    private string _status = "";

    public string StatusDisplay => FormatStatus(Status);

    public bool IsDelayedOrDeferred =>
        Status.StartsWith("Delayed", StringComparison.Ordinal)
        || Status.StartsWith("Deferred", StringComparison.Ordinal);

    private static string FormatStatus(string status)
    {
        if (status.StartsWith("Delayed (", StringComparison.Ordinal)
            && status.EndsWith("s)", StringComparison.Ordinal))
        {
            var numStr = status.AsSpan(9, status.Length - 11);
            if (int.TryParse(numStr, out var seconds))
            {
                var minutes = seconds / 60;
                var secs = seconds % 60;
                return $"Delayed {minutes}:{secs:D2}";
            }
        }
        return status;
    }

    [ObservableProperty]
    private string _pendingCommands = "";

    [ObservableProperty]
    private string _currentPhase = "";

    [ObservableProperty]
    private string _assignedRunway = "";

    [ObservableProperty]
    private bool _isOnGround;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseSequenceDisplay))]
    [NotifyPropertyChangedFor(nameof(HasPhases))]
    private string _phaseSequence = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseSequenceDisplay))]
    private int _activePhaseIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClearanceDisplay))]
    [NotifyPropertyChangedFor(nameof(HasClearance))]
    private string _landingClearance = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClearanceDisplay))]
    private string _clearedRunway = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PatternDisplay))]
    [NotifyPropertyChangedFor(nameof(HasPattern))]
    private string _patternDirection = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNavigationRoute))]
    private string _navigationRoute = "";

    [ObservableProperty]
    private string _equipmentSuffix = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CruiseDisplay))]
    [NotifyPropertyChangedFor(nameof(HasCruise))]
    private int _cruiseAltitude;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CruiseDisplay))]
    private int _cruiseSpeed;

    public bool HasPhases => !string.IsNullOrEmpty(PhaseSequence);

    public string PhaseSequenceDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(PhaseSequence))
            {
                return "";
            }

            var parts = PhaseSequence.Split(" > ");
            if (ActivePhaseIndex < 0 || ActivePhaseIndex >= parts.Length)
            {
                return PhaseSequence;
            }

            // The server sends only non-completed phases, so index 0
            // in the split array is the active phase.
            parts[0] = $"[{parts[0]}]";
            return string.Join(" > ", parts);
        }
    }

    public bool HasClearance => !string.IsNullOrEmpty(LandingClearance);

    public string ClearanceDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(LandingClearance))
            {
                return "";
            }

            var humanized = LandingClearance switch
            {
                "ClearedToLand" => "Cleared to land",
                "ClearedForOption" => "Cleared for the option",
                "ClearedTouchAndGo" => "Cleared touch and go",
                "ClearedStopAndGo" => "Cleared stop and go",
                "LineUpAndWait" => "Line up and wait",
                "ClearedForTakeoff" => "Cleared for takeoff",
                _ => LandingClearance,
            };

            if (!string.IsNullOrEmpty(ClearedRunway))
            {
                return $"{humanized} Rwy {ClearedRunway}";
            }
            return humanized;
        }
    }

    public bool HasPattern => !string.IsNullOrEmpty(PatternDirection);

    public string PatternDisplay =>
        string.IsNullOrEmpty(PatternDirection)
            ? ""
            : $"{PatternDirection} traffic";

    public bool HasNavigationRoute => !string.IsNullOrEmpty(NavigationRoute);

    public bool HasCruise => CruiseAltitude > 0;

    public string CruiseDisplay
    {
        get
        {
            if (CruiseAltitude <= 0)
            {
                return "";
            }

            var altStr = CruiseAltitude >= 18000
                ? $"FL{CruiseAltitude / 100}"
                : $"{CruiseAltitude}";

            if (CruiseSpeed > 0)
            {
                return $"{altStr} / {CruiseSpeed} kt";
            }
            return altStr;
        }
    }

    [ObservableProperty]
    private bool _isSelected;

    internal static (int Order, int Seconds) ParseStatusSortKey(string status)
    {
        if (status.StartsWith("Delayed (", StringComparison.Ordinal)
            && status.EndsWith("s)", StringComparison.Ordinal))
        {
            var numStr = status.AsSpan(9, status.Length - 11);
            if (int.TryParse(numStr, out var seconds))
            {
                return (1, seconds);
            }
        }
        if (status.StartsWith("Deferred", StringComparison.Ordinal))
        {
            return (2, 0);
        }
        return (0, 0); // Active
    }
}

public sealed class StatusSortComparer : IComparer
{
    public static readonly StatusSortComparer Instance = new();

    public int Compare(object? x, object? y)
    {
        if (x is not AircraftModel a || y is not AircraftModel b)
        {
            return 0;
        }

        var ka = AircraftModel.ParseStatusSortKey(a.Status);
        var kb = AircraftModel.ParseStatusSortKey(b.Status);

        var orderCmp = ka.Order.CompareTo(kb.Order);
        if (orderCmp != 0)
        {
            return orderCmp;
        }

        return ka.Seconds.CompareTo(kb.Seconds);
    }
}
