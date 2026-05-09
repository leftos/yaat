using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

public sealed class PilotInitialContactEligibilityTests
{
    private static readonly TrackOwner StudentTower = TrackOwner.CreateStars("SFO_TWR", "SFO", 3, "T");
    private static readonly TrackOwner StudentApproach = TrackOwner.CreateStars("NCT_APP", "NCT", 4, "A");
    private static readonly TrackOwner OtherApproach = TrackOwner.CreateStars("NCT_APP", "NCT", 5, "B");

    [Fact]
    public void CanInitiate_TowerStudent_AllowsPendingHandoff()
    {
        var aircraft = MakeAircraft();
        aircraft.Track.Owner = OtherApproach;
        aircraft.Track.HandoffPeer = StudentTower;

        var allowed = PilotInitialContactEligibility.CanInitiateWithStudent(aircraft, Context(StudentTower, "TWR"));

        Assert.True(allowed);
    }

    [Fact]
    public void CanInitiate_ApproachStudent_RequiresAcceptedHandoff()
    {
        var aircraft = MakeAircraft();
        aircraft.Track.Owner = TrackOwner.CreateStars("ZOA_CTR", "ZOA", 1, "C");
        aircraft.Track.HandoffPeer = StudentApproach;

        var pendingAllowed = PilotInitialContactEligibility.CanInitiateWithStudent(aircraft, Context(StudentApproach, "APP"));

        aircraft.Track.Owner = StudentApproach;
        aircraft.Track.HandoffPeer = null;
        var acceptedAllowed = PilotInitialContactEligibility.CanInitiateWithStudent(aircraft, Context(StudentApproach, "APP"));

        Assert.False(pendingAllowed);
        Assert.True(acceptedAllowed);
    }

    [Fact]
    public void CanInitiate_TowerStudent_BlocksOtherOwnerWithoutHandoffOrSopException()
    {
        var aircraft = MakeAircraft();
        aircraft.Track.Owner = OtherApproach;

        var allowed = PilotInitialContactEligibility.CanInitiateWithStudent(aircraft, Context(StudentTower, "TWR"));

        Assert.False(allowed);
    }

    [Fact]
    public void CanInitiate_TowerStudent_AllowsConfiguredApproachToTowerTransferWithoutTrackHandoff()
    {
        var aircraft = MakeAircraft(destination: "KSFO");
        aircraft.Track.Owner = OtherApproach;
        var transfers = new InitialContactTransferCatalog([
            new InitialContactTransferRule
            {
                ArtccId = "ZOA",
                AirportId = "SFO",
                FromPositionType = "APP",
                ToPositionType = "TWR",
                AllowsWithoutTrackHandoff = true,
            },
        ]);

        var allowed = PilotInitialContactEligibility.CanInitiateWithStudent(
            aircraft,
            new InitialContactEligibilityContext(StudentTower, "TWR", "ZOA", "KSFO", transfers)
        );

        Assert.True(allowed);
    }

    private static AircraftState MakeAircraft(string destination = "") =>
        new()
        {
            Callsign = "AAL123",
            AircraftType = "B738",
            Position = new LatLon(37.0, -122.0),
            TrueHeading = new TrueHeading(280),
            FlightPlan = new AircraftFlightPlan { FlightRules = "IFR", Destination = destination },
        };

    private static InitialContactEligibilityContext Context(TrackOwner student, string studentPositionType) =>
        new(student, studentPositionType, "ZOA", "KSFO", InitialContactTransferCatalog.Empty);
}
