using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Views;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The room snapshot <see cref="MainViewModel.BuildSpeechContext"/> captures for the accurate speech mode: the room airport,
/// whether the position works approach, the facility radio names and one plain entry per aircraft, with an assigned runway only
/// for an aircraft at the room airport.
/// </summary>
public class MainViewModelSpeechRoomTests
{
    [AvaloniaFact]
    public void PushToTalkContext_CarriesTheRoom()
    {
        MainViewModel vm = NewVm();
        try
        {
            vm.ActiveScenarioPrimaryAirportId = "OAK";
            vm.SetActivePositionFromServer(Position("NCT_A_APP", "NorCal Approach", "Tracon"));
            vm.OnlineControllers.Add(Controller("OAK_TWR", "Oakland Tower"));
            vm.Aircraft.Add(
                new AircraftModel
                {
                    Callsign = "SWA2495",
                    AircraftType = "B738/L",
                    IsOnGround = true,
                    Departure = "KOAK",
                    Destination = "KLAX",
                    AssignedRunway = "30",
                }
            );
            vm.Aircraft.Add(
                new AircraftModel
                {
                    Callsign = "N4926F",
                    AircraftType = "C172",
                    IsOnGround = false,
                    Departure = "KSAC",
                    Destination = "KOAK",
                    AssignedRunway = "28R",
                    Route = "SAC OAK",
                    ExpectedApproach = "I28R",
                    ActiveStarId = "EMZOH4",
                    DestinationRunway = "28L",
                }
            );

            RoomSnapshot room = Assert.IsType<RoomSnapshot>(vm.BuildSpeechContext(includeRoom: true).Room);

            Assert.Equal("KOAK", room.AirportIcao);
            Assert.True(room.ApproachPosition);
            Assert.Contains("NorCal Approach", room.FacilityRadioNames);
            Assert.Contains("Oakland Tower", room.FacilityRadioNames);
            RoomAircraftSnapshot departure = Assert.Single(room.Aircraft, a => a.Callsign == "SWA2495");
            Assert.Equal("30", departure.AssignedRunway);
            Assert.Equal("B738", departure.AircraftType);
            RoomAircraftSnapshot arrival = Assert.Single(room.Aircraft, a => a.Callsign == "N4926F");
            Assert.Equal("28R", arrival.AssignedRunway);
            Assert.Equal("SAC OAK", arrival.Route);
            Assert.Equal("I28R", arrival.ExpectedApproach);
            Assert.Equal("KOAK", arrival.Destination);
            Assert.Equal("KSAC", arrival.Departure);
            Assert.Equal("EMZOH4", arrival.ActiveStarId);
            Assert.Equal("28L", arrival.DestinationRunway);
        }
        finally
        {
            vm.ActiveScenarioPrimaryAirportId = null;
        }
    }

    [AvaloniaFact]
    public void PushToTalkContext_AircraftAwayFromTheRoomAirport_HasNoAssignedRunway()
    {
        MainViewModel vm = NewVm();
        try
        {
            vm.ActiveScenarioPrimaryAirportId = "OAK";
            vm.Aircraft.Add(
                new AircraftModel
                {
                    Callsign = "UAL123",
                    AircraftType = "A320",
                    IsOnGround = false,
                    Departure = "KOAK",
                    Destination = "KSFO",
                    AssignedRunway = "28L",
                }
            );

            RoomSnapshot room = Assert.IsType<RoomSnapshot>(vm.BuildSpeechContext(includeRoom: true).Room);

            Assert.Null(Assert.Single(room.Aircraft).AssignedRunway);
        }
        finally
        {
            vm.ActiveScenarioPrimaryAirportId = null;
        }
    }

    [AvaloniaFact]
    public void PushToTalkContext_CarriesTheRoomAirportsLayout()
    {
        MainViewModel vm = NewVm();
        try
        {
            AirportGroundLayout oak = MenuGoldenFixtures.OakDomainLayout;
            vm.Ground.SetDomainLayoutForTesting(oak);
            vm.ActiveScenarioPrimaryAirportId = "OAK";

            Assert.Same(oak, Assert.IsType<RoomSnapshot>(vm.BuildSpeechContext(includeRoom: true).Room).Layout);
        }
        finally
        {
            vm.ActiveScenarioPrimaryAirportId = null;
        }
    }

    [AvaloniaFact]
    public void PushToTalkContext_LayoutOfAnotherAirport_IsLeftOut()
    {
        MainViewModel vm = NewVm();
        try
        {
            vm.Ground.SetDomainLayoutForTesting(MenuGoldenFixtures.OakDomainLayout);
            vm.ActiveScenarioPrimaryAirportId = "SFO";

            RoomSnapshot room = Assert.IsType<RoomSnapshot>(vm.BuildSpeechContext(includeRoom: true).Room);

            Assert.Equal("KSFO", room.AirportIcao);
            Assert.Null(room.Layout);
        }
        finally
        {
            vm.ActiveScenarioPrimaryAirportId = null;
        }
    }

    [AvaloniaFact]
    public void TypedContext_HasNoRoom()
    {
        MainViewModel vm = NewVm();
        try
        {
            vm.ActiveScenarioPrimaryAirportId = "OAK";
            vm.Aircraft.Add(new AircraftModel { Callsign = "SWA2495", Destination = "KOAK" });

            Assert.Null(vm.BuildSpeechContext(includeRoom: false).Room);
        }
        finally
        {
            vm.ActiveScenarioPrimaryAirportId = null;
        }
    }

    [AvaloniaFact]
    public void NoScenarioAndNoLayout_HasNoRoom()
    {
        MainViewModel vm = NewVm();
        vm.Aircraft.Add(new AircraftModel { Callsign = "SWA2495", Destination = "KOAK" });

        Assert.Null(vm.BuildSpeechContext(includeRoom: true).Room);
    }

    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static PositionDisplayConfigDto Position(string callsign, string radioName, string positionType) =>
        new([], [], [], "1A", new ActivePositionDto(callsign, callsign, radioName, "135.100", "NCT", "NorCal", positionType, "1A"), 0);

    private static OnlineControllerDto Controller(string callsign, string radioName) =>
        new(callsign, callsign, "118.300", "OAK", "Oakland", "Atct", "1C", radioName, "Test Controller", true, false, "1C");
}
