using CommunityToolkit.Mvvm.ComponentModel;

namespace Yaat.Models;

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
}
