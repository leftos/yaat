using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public static class FlightPlanEditorManager
{
    private static readonly ILogger Log = AppLog.CreateLogger("FlightPlanEditorManager");
    private static FlightPlanEditorWindow? _openEditor;

    public static FlightPlanEditorWindow Open(AircraftModel ac, MainViewModel vm)
    {
        if (_openEditor is not null)
        {
            _openEditor.LoadAircraft(ac);
            _openEditor.RestoreAndActivate();
            return _openEditor;
        }

        var window = new FlightPlanEditorWindow(
            ac,
            async (callsign, amendment) =>
            {
                try
                {
                    var dto = new FlightPlanAmendmentDto(
                        AircraftType: amendment.AircraftType,
                        EquipmentSuffix: amendment.EquipmentSuffix,
                        Departure: amendment.Departure,
                        Destination: amendment.Destination,
                        CruiseSpeed: amendment.CruiseSpeed,
                        Altitude: amendment.Altitude,
                        FlightRules: amendment.FlightRules,
                        Route: amendment.Route,
                        Remarks: amendment.Remarks,
                        Scratchpad1: amendment.Scratchpad1,
                        Scratchpad2: amendment.Scratchpad2,
                        BeaconCode: amendment.BeaconCode,
                        IcaoEquipmentCodes: amendment.IcaoEquipmentCodes
                    );

                    await vm.Connection.AmendFlightPlanAsync(callsign, dto);
                }
                catch (Exception ex)
                {
                    Log.LogError(ex, "Failed to amend flight plan for {Callsign}", callsign);
                }
            },
            async callsign =>
            {
                try
                {
                    await vm.Connection.RequestNewBeaconCodeAsync(callsign);
                }
                catch (Exception ex)
                {
                    Log.LogError(ex, "Failed to request new beacon code for {Callsign}", callsign);
                }
            }
        );

        window.Closed += (_, _) => _openEditor = null;
        _openEditor = window;

        new WindowGeometryHelper(window, vm.Preferences, "FlightPlanEditor", 720, 250).Restore();

        // Shown un-owned so the editor never locks above the main window or other tool windows (#287).
        window.Show();
        return window;
    }

    /// <summary>The flight-plan editor window open now, or null when none is.</summary>
    internal static FlightPlanEditorWindow? OpenEditor => _openEditor;

    /// <summary>
    /// Reacts to an aircraft removal on the server (an auto-delete on parking or departure-distance,
    /// or a manual DEL). When the removed aircraft is the one the editor shows, the editor stays
    /// open — the instructor keeps their place — cleared and bound to no aircraft; any other removal
    /// leaves the editor, in-progress edits included, untouched.
    /// </summary>
    public static void HandleAircraftDeleted(string callsign)
    {
        if (_openEditor is { } editor && editor.Callsign == callsign)
        {
            editor.ClearAircraft();
        }
    }

    public static void Close()
    {
        _openEditor?.Close();
        _openEditor = null;
    }
}
