using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using EditorAmendment = Yaat.Client.Models.FlightPlanAmendment;
using SimAmendment = Yaat.Sim.Simulation.FlightPlanAmendment;

namespace Yaat.Client.Tests;

/// <summary>
/// The Flight Plan Editor's amend of a plan the user left as it was changes nothing the server holds: a fix-qualified
/// altitude (<c>170/SJC/110</c>) and its fix-passage latch, an above altitude (<c>ABV/170</c>), a filed Mach number (whose
/// SPD box shows empty and sends 0), and remarks kept as intrafacility and interfacility parts. The editor text comes from
/// the hub <see cref="AircraftDto"/> through <see cref="AircraftModel"/>, and the amendment is applied by the simulation's
/// own <see cref="SimulationEngine.AmendFlightPlan"/>.
/// </summary>
public class FlightPlanEditorRoundTripTests
{
    private const string Callsign = "AAL123";

    public static TheoryData<PlannedAltitude> UnchangedAltitudes =>
        [
            PlannedAltitude.UntilFix(17000, "SJC", 11000),
            PlannedAltitude.UntilFix(17000, "SJC090020", 11000),
            PlannedAltitude.UntilFix(17000, "3730N/12200W", 11000),
            PlannedAltitude.Above(17000),
        ];

    [Theory]
    [MemberData(nameof(UnchangedAltitudes))]
    public void AnUnchangedAltitude_RoundTripsEqual_AndKeepsTheFixLatch(PlannedAltitude altitude)
    {
        AircraftState ac = Aircraft(altitude);
        ac.FlightPlan.AltitudeFixApproached = true;
        ac.FlightPlan.AltitudeFixPassed = true;
        var model = AircraftModel.FromDto(Dto(ac.FlightPlan));

        EditorAmendment amendment = Amend(ac, altText: model.CruiseAltitudeDisplay, spdText: "450", rmkText: "");

        Assert.Equal(altitude, amendment.Altitude);
        Assert.Equal(altitude, ac.FlightPlan.Altitude);
        Assert.True(ac.FlightPlan.AltitudeFixPassed);
        Assert.True(ac.FlightPlan.AltitudeFixApproached);
    }

    [Fact]
    public void AnEmptySpeedBox_KeepsAFiledMach()
    {
        AircraftState ac = Aircraft(PlannedAltitude.Ifr(35000));
        ac.FlightPlan.SetMach(78);

        EditorAmendment amendment = Amend(ac, altText: "350", spdText: "", rmkText: "");

        Assert.Equal(0, amendment.CruiseSpeed);
        Assert.Equal(78, ac.FlightPlan.CruiseMach);
        Assert.Equal(0, ac.FlightPlan.CruiseSpeed);
    }

    [Fact]
    public void UnchangedRemarks_GoOutUnedited_AndKeepBothParts()
    {
        AircraftState ac = Aircraft(PlannedAltitude.Ifr(35000));
        ac.FlightPlan.IntrafacilityRemarks = "LOCAL";
        ac.FlightPlan.InterfacilityRemarks = "CHARTS";

        EditorAmendment amendment = Amend(ac, altText: "350", spdText: "450", rmkText: " LOCAL CHARTS ");

        Assert.Null(amendment.Remarks);
        Assert.Equal("LOCAL", ac.FlightPlan.IntrafacilityRemarks);
        Assert.Equal("CHARTS", ac.FlightPlan.InterfacilityRemarks);
    }

    [Fact]
    public void ChangedRemarks_GoOut_AndReplaceTheParts()
    {
        AircraftState ac = Aircraft(PlannedAltitude.Ifr(35000));
        ac.FlightPlan.IntrafacilityRemarks = "LOCAL";
        ac.FlightPlan.InterfacilityRemarks = "CHARTS";

        EditorAmendment amendment = Amend(ac, altText: "350", spdText: "450", rmkText: "NO CHARTS");

        Assert.Equal("NO CHARTS", amendment.Remarks);
        Assert.Equal("", ac.FlightPlan.IntrafacilityRemarks);
        Assert.Equal("NO CHARTS", ac.FlightPlan.InterfacilityRemarks);
    }

    private static EditorAmendment Amend(AircraftState ac, string altText, string spdText, string rmkText)
    {
        EditorAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: ac.FlightPlan.AircraftType,
            eqText: ac.FlightPlan.EquipmentSuffix,
            icaoEqText: ac.FlightPlan.IcaoEquipmentCodes,
            depText: ac.FlightPlan.Departure,
            destText: ac.FlightPlan.Destination,
            spdText: spdText,
            altText: altText,
            rteText: ac.FlightPlan.Route,
            rmkText: rmkText,
            strippedRemarksPrefix: "",
            originalRemarks: ac.FlightPlan.Remarks
        );

        var engine = new SimulationEngine(new NoGroundData());
        engine.World.AddAircraft(ac);
        engine.AmendFlightPlan(Callsign, ToSimAmendment(amendment));
        return amendment;
    }

    /// <summary>The editor's amendment as the room applies it: the hub carries each field across unchanged.</summary>
    private static SimAmendment ToSimAmendment(EditorAmendment amendment) =>
        new(
            ClearBeaconCode: false,
            AircraftType: amendment.AircraftType,
            EquipmentSuffix: amendment.EquipmentSuffix,
            IcaoEquipmentCodes: amendment.IcaoEquipmentCodes,
            Departure: amendment.Departure,
            Destination: amendment.Destination,
            CruiseSpeed: amendment.CruiseSpeed,
            Altitude: amendment.Altitude,
            FlightRules: amendment.FlightRules,
            Route: amendment.Route,
            Remarks: amendment.Remarks,
            Scratchpad1: amendment.Scratchpad1,
            Scratchpad2: amendment.Scratchpad2,
            BeaconCode: amendment.BeaconCode
        );

    private static AircraftState Aircraft(PlannedAltitude altitude) =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Altitude = 17000,
            Transponder = new AircraftTransponder
            {
                Code = 4571,
                AssignedCode = 4571,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                FlightRules = "IFR",
                AircraftType = "B738",
                EquipmentSuffix = "L",
                Departure = "KOAK",
                Destination = "KLAX",
                CruiseSpeed = 450,
                Altitude = altitude,
            },
        };

    private static AircraftDto Dto(AircraftFlightPlan plan) =>
        new(
            Callsign: Callsign,
            AircraftType: "B738",
            Latitude: 37.5,
            Longitude: -122.0,
            Heading: 90,
            Altitude: 17000,
            GroundSpeed: 450,
            BeaconCode: 4571,
            TransponderMode: "C",
            IsIdenting: false,
            VerticalSpeed: 0,
            AssignedHeading: null,
            AssignedAltitude: null,
            AssignedSpeed: null,
            Departure: plan.Departure,
            Destination: plan.Destination,
            Route: plan.Route,
            FlightRules: plan.FlightRules,
            Status: "",
            Remarks: plan.Remarks,
            CruiseAltitude: plan.Altitude.CruiseFeet ?? 0,
            CruiseSpeed: plan.CruiseSpeed,
            BlockFloorAltitude: plan.Altitude.BlockFloorFeet,
            IsVfrOnTop: plan.Altitude.IsVfrOnTop,
            IsAbove: plan.Altitude.IsAbove,
            AltitudeFix: plan.Altitude.AltitudeFix,
            AltitudeAfterFixFeet: plan.Altitude.AfterFixFeet
        );

    /// <summary>No airport layouts: an airborne flight-plan amendment reads none.</summary>
    private sealed class NoGroundData : IAirportGroundData
    {
        public AirportGroundLayout? GetLayout(string airportId) => null;

        public string? GetSourceGeoJson(string airportId) => null;
    }
}
