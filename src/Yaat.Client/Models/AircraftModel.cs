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
    private double? _assignedHeading;

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
    private bool _isSelected;
}
