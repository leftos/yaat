using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>
/// <see cref="PositionJurisdiction"/> over a real OAK departure from parking to the air: Ground owns the movement area,
/// Local owns the runway and its traffic, an AI radar position owns what it tracks, and another airport is nobody's.
/// </summary>
public class PositionJurisdictionTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public PositionJurisdictionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Departure_MovesFromGround_ToLocal_ThroughTheTakeoff()
    {
        if (_zoa is null)
        {
            return;
        }

        var staffed = Staffed();
        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var resolve = (AircraftState ac) => Resolve(engine, ac, staffed)?.Callsign;

        Assert.Equal("OAK_GND", resolve(engine.FindAircraft(AiTestFixture.Callsign)!));

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 28R").Success);
        var taxiing = AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.Phases?.CurrentPhase is TaxiingPhase, 30);
        Assert.Equal("OAK_GND", resolve(taxiing));

        var holdingShort = AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.Phases?.CurrentPhase is HoldingShortPhase, 900);
        Assert.Equal("OAK_TWR", resolve(holdingShort));

        Assert.True(engine.SendCommand(AiTestFixture.Callsign, "CTO").Success);
        var rolling = AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => ac.Phases?.CurrentPhase is TakeoffPhase, 120);
        Assert.Equal("OAK_TWR", resolve(rolling));

        var climbing = AiTestFixture.TickUntil(engine, AiTestFixture.Callsign, ac => !ac.IsOnGround, 120);
        Assert.Equal("OAK_TWR", resolve(climbing));
    }

    [Fact]
    public void Arrival_OnFinal_IsLocals()
    {
        if (_zoa is null)
        {
            return;
        }

        var engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, _zoa, 7, []);
        AiTestFixture.Tick(engine, 2);

        var aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.IsType<FinalApproachPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal("OAK_TWR", Resolve(engine, aircraft, Staffed())?.Callsign);
    }

    [Fact]
    public void AirborneTrack_OwnedByAnAiRadarPosition_IsThatPositions_AndAHumanOwnedTrackIsNobodys()
    {
        if (_zoa is null)
        {
            return;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var staffed = Staffed();
        var approach = staffed.Single(p => p.Callsign == "NCT_APP");
        var cruising = AiTestFixture.Airborne("AAL1", 37.9, -122.0, 8000);

        Assert.Null(Resolve(engine, cruising, staffed));

        cruising.Track.Owner = approach.Identity;
        Assert.Equal("NCT_APP", Resolve(engine, cruising, staffed)?.Callsign);

        // The solo student holds the track: the AI does not act for a human, whatever the phase family says.
        var scenario = engine.Scenario!;
        scenario.SoloTrainingMode = true;
        scenario.StudentPosition = approach.Identity;
        Assert.Null(Resolve(engine, cruising, staffed));
    }

    [Fact]
    public void AircraftAtAnotherAirport_IsNotTheOakCabs()
    {
        if (_zoa is null)
        {
            return;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var atSfo = AiTestFixture.Airborne("UAL2", 37.62, -122.38, 1200);
        atSfo.AirportId = "SFO";
        atSfo.FlightPlan.Destination = "KSFO";
        atSfo.Phases = new PhaseList();
        atSfo.Phases.Add(new FinalApproachPhase());

        Assert.Null(Resolve(engine, atSfo, [TestAiPositions.OakGround(_zoa), TestAiPositions.OakTower(_zoa)]));
    }

    [Fact]
    public void WorldView_SortsByCallsign_AndGroupsByPosition()
    {
        if (_zoa is null)
        {
            return;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var staffed = Staffed();
        var approach = staffed.Single(p => p.Callsign == "NCT_APP");
        var b = AiTestFixture.Airborne("B", 37.9, -122.0, 8000);
        var a = AiTestFixture.Airborne("A", 37.8, -122.1, 9000);
        a.Track.Owner = approach.Identity;
        b.Track.Owner = approach.Identity;
        var parked = engine.FindAircraft(AiTestFixture.Callsign)!;

        var view = AiTestFixture.Context(engine, [parked, b, a], staffed, 0, [], new EngineAiCommandSink(engine)).View;

        Assert.Equal(["A", "B", AiTestFixture.Callsign], view.Snapshot.Select(ac => ac.Callsign));
        Assert.Equal(["A", "B"], view.Jurisdiction(approach).Select(ac => ac.Callsign));
        Assert.Equal([AiTestFixture.Callsign], view.Jurisdiction(staffed.Single(p => p.Callsign == "OAK_GND")).Select(ac => ac.Callsign));
        Assert.Empty(view.Jurisdiction(staffed.Single(p => p.Callsign == "OAK_TWR")));
    }

    private IReadOnlyList<AiPositionConfig> Staffed() =>
        [TestAiPositions.OakGround(_zoa!), TestAiPositions.OakTower(_zoa!), TestAiPositions.NorCalApproach(_zoa!)];

    [Fact]
    public void ApproachOnlyStaffing_KeepsItsRadarOwnedArrival_ThroughFinal()
    {
        if (_zoa is null)
        {
            return;
        }

        // Nobody plays the tower: the AI approach that owns the track stays responsible on final instead of the
        // aircraft dropping out of every jurisdiction.
        var approach = TestAiPositions.NorCalApproach(_zoa);
        var engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, _zoa, 7, []);
        AiTestFixture.Tick(engine, 2);
        var aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.IsType<FinalApproachPhase>(aircraft.Phases?.CurrentPhase);

        Assert.Null(Resolve(engine, aircraft, [approach]));
        aircraft.Track.Owner = approach.Identity;
        Assert.Equal("NCT_APP", Resolve(engine, aircraft, [approach])?.Callsign);
    }

    [Fact]
    public void OnTheRunway_AlongItIsLocals_AcrossItIsGrounds()
    {
        if (_zoa is null)
        {
            return;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var runway = RunwayOccupancy.AirportRunways("OAK").Single(r => r.Id.End1 == "28R" || r.Id.End2 == "28R");
        var onRunway = new AircraftState
        {
            Callsign = "N9",
            AircraftType = "C172",
            Position = new LatLon((runway.Lat1 + runway.Lat2) / 2, (runway.Lon1 + runway.Lon2) / 2),
            TrueHeading = runway.TrueHeading1,
            IsOnGround = true,
            AirportId = "OAK",
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "VFR",
                Departure = "KOAK",
                Destination = "KOAK",
            },
            Phases = new PhaseList(),
        };
        onRunway.Phases.Add(new HoldingInPositionPhase());

        // Holding on the runway, aligned with it: local control's (7110.65 3-1-3.a.4) even though the phase is a ground hold.
        Assert.Equal("OAK_TWR", Resolve(engine, onRunway, Staffed())?.Callsign);

        onRunway.TrueHeading = new TrueHeading(runway.TrueHeading1.Degrees + 90);
        Assert.Equal("OAK_GND", Resolve(engine, onRunway, Staffed())?.Callsign);
    }

    [Fact]
    public void AssignedToAHumanConnection_IsNobodys()
    {
        if (_zoa is null)
        {
            return;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var parked = engine.FindAircraft(AiTestFixture.Callsign)!;
        var staffing = new HeadlessAiStaffing(Staffed(), engine.Scenario!);

        Assert.NotNull(
            PositionJurisdiction.Resolve(
                parked,
                staffing.ActivePositions,
                engine.ResolveGroundLayout,
                RunwayOccupancy.AirportRunways,
                staffing.IsHumanHeld,
                _ => false
            )
        );
        Assert.Null(
            PositionJurisdiction.Resolve(
                parked,
                staffing.ActivePositions,
                engine.ResolveGroundLayout,
                RunwayOccupancy.AirportRunways,
                staffing.IsHumanHeld,
                _ => true
            )
        );
    }

    private static AiPositionConfig? Resolve(SimulationEngine engine, AircraftState aircraft, IReadOnlyList<AiPositionConfig> staffed)
    {
        var staffing = new HeadlessAiStaffing(staffed, engine.Scenario!);
        return PositionJurisdiction.Resolve(
            aircraft,
            staffing.ActivePositions,
            engine.ResolveGroundLayout,
            RunwayOccupancy.AirportRunways,
            staffing.IsHumanHeld,
            staffing.IsAssignedToHuman
        );
    }
}
