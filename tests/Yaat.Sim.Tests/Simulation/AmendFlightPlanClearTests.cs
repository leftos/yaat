using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression tests for <see cref="SimulationEngine.AmendFlightPlan"/>'s clear semantics.
///
/// The handler uses <c>null</c> to mean "leave this field unchanged" (load-bearing for
/// targeted single-field amendments from <c>RoomEngine</c>) and the *non-null* value to
/// mean "set the field to exactly this." That contract is what lets the Flight Plan
/// Editor send an empty string to wipe Departure/Destination/Route and a zero to wipe
/// CruiseSpeed/CruiseAltitude — matching CRC's <c>FlightPlanEditorViewModel.BuildFlightPlan</c>.
/// </summary>
public class AmendFlightPlanClearTests(ITestOutputHelper output)
{
    private SimulationEngine BuildEngine()
    {
        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private static AircraftState AddAircraftWithFlightPlan(SimulationEngine engine, string callsign)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "DA42",
            FlightPlan = new AircraftFlightPlan
            {
                AircraftType = "DA42",
                EquipmentSuffix = "A",
                Departure = "KOAK",
                Destination = "KOAK",
                CruiseSpeed = 250,
                CruiseAltitude = 1000,
                FlightRules = "VFR",
                Route = "PATTERN",
                HasFlightPlan = true,
            },
        };
        engine.World.AddAircraft(ac);
        return ac;
    }

    [Fact]
    public void EmptyDeparture_ClearsField()
    {
        var engine = BuildEngine();
        AddAircraftWithFlightPlan(engine, "N342T");

        engine.AmendFlightPlan(
            "N342T",
            new FlightPlanAmendment(
                AircraftType: null,
                EquipmentSuffix: null,
                Departure: "",
                Destination: null,
                CruiseSpeed: null,
                CruiseAltitude: null,
                FlightRules: null,
                Route: null,
                Remarks: null,
                Scratchpad1: null,
                Scratchpad2: null,
                BeaconCode: null
            )
        );

        var ac = engine.FindAircraft("N342T");
        Assert.NotNull(ac);
        Assert.Equal("", ac.FlightPlan.Departure);
        Assert.Equal("KOAK", ac.FlightPlan.Destination);
    }

    [Fact]
    public void NullDeparture_LeavesFieldUnchanged()
    {
        // Documents the load-bearing partial-update semantic that targeted single-field
        // amendments from RoomEngine (RD/APT/DEST handlers) depend on.
        var engine = BuildEngine();
        AddAircraftWithFlightPlan(engine, "N342T");

        engine.AmendFlightPlan(
            "N342T",
            new FlightPlanAmendment(
                AircraftType: null,
                EquipmentSuffix: null,
                Departure: null,
                Destination: "KSFO",
                CruiseSpeed: null,
                CruiseAltitude: null,
                FlightRules: null,
                Route: null,
                Remarks: null,
                Scratchpad1: null,
                Scratchpad2: null,
                BeaconCode: null
            )
        );

        var ac = engine.FindAircraft("N342T");
        Assert.NotNull(ac);
        Assert.Equal("KOAK", ac.FlightPlan.Departure);
        Assert.Equal("KSFO", ac.FlightPlan.Destination);
    }

    [Fact]
    public void EmptyDestinationAndRoute_ClearsBothFields()
    {
        var engine = BuildEngine();
        AddAircraftWithFlightPlan(engine, "N342T");

        engine.AmendFlightPlan(
            "N342T",
            new FlightPlanAmendment(
                AircraftType: null,
                EquipmentSuffix: null,
                Departure: null,
                Destination: "",
                CruiseSpeed: null,
                CruiseAltitude: null,
                FlightRules: null,
                Route: "",
                Remarks: null,
                Scratchpad1: null,
                Scratchpad2: null,
                BeaconCode: null
            )
        );

        var ac = engine.FindAircraft("N342T");
        Assert.NotNull(ac);
        Assert.Equal("", ac.FlightPlan.Destination);
        Assert.Equal("", ac.FlightPlan.Route);
    }

    [Fact]
    public void ZeroCruiseSpeedAndAltitude_ClearsValues()
    {
        var engine = BuildEngine();
        AddAircraftWithFlightPlan(engine, "N342T");

        engine.AmendFlightPlan(
            "N342T",
            new FlightPlanAmendment(
                AircraftType: null,
                EquipmentSuffix: null,
                Departure: null,
                Destination: null,
                CruiseSpeed: 0,
                CruiseAltitude: 0,
                FlightRules: null,
                Route: null,
                Remarks: null,
                Scratchpad1: null,
                Scratchpad2: null,
                BeaconCode: null
            )
        );

        var ac = engine.FindAircraft("N342T");
        Assert.NotNull(ac);
        Assert.Equal(0, ac.FlightPlan.CruiseSpeed);
        Assert.Equal(0, ac.FlightPlan.CruiseAltitude);
    }
}
