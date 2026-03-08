using Avalonia.Controls;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public static class FlightPlanEditorManager
{
    private static FlightPlanEditorWindow? _openEditor;

    public static void Open(AircraftModel ac, MainViewModel vm, Window? owner)
    {
        Close();

        var window = new FlightPlanEditorWindow(
            ac,
            async (callsign, amendment) =>
            {
                var dto = new FlightPlanAmendmentDto(
                    AircraftType: amendment.AircraftType,
                    EquipmentSuffix: amendment.EquipmentSuffix,
                    Departure: amendment.Departure,
                    Destination: amendment.Destination,
                    CruiseSpeed: amendment.CruiseSpeed,
                    CruiseAltitude: amendment.CruiseAltitude,
                    FlightRules: amendment.FlightRules,
                    Route: amendment.Route,
                    Remarks: amendment.Remarks,
                    Scratchpad1: amendment.Scratchpad1,
                    Scratchpad2: amendment.Scratchpad2,
                    BeaconCode: amendment.BeaconCode
                );

                await vm.Connection.AmendFlightPlanAsync(callsign, dto);
            }
        );

        window.Closed += (_, _) => _openEditor = null;
        _openEditor = window;

        new WindowGeometryHelper(window, vm.Preferences, "FlightPlanEditor", 640, 250).Restore();

        if (owner is not null)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    public static void Close()
    {
        if (_openEditor is not null)
        {
            _openEditor.Close();
            _openEditor = null;
        }
    }
}
