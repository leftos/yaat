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
