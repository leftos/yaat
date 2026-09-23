using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests;

/// <summary>
/// The departure half of <see cref="SimulationEngine.TickAutoDelete"/>: with
/// <see cref="SimScenarioState.DepartureAutoDeleteDistanceNm"/> set, an airborne departure from the primary airport
/// farther than that from the airport's reference point is removed — tracked or not, whatever the arrival mode, and
/// despite the <see cref="AircraftGroundOps.AutoDeleteExempt"/> flag spawn puts on every ground-started aircraft —
/// unless a controller said <c>NODEL</c>.
/// </summary>
public class DepartureAutoDeleteTests
{
    private const string Callsign = "N152SP";

    /// <summary>One C172 parked at OAK filed OAK → LAX: a ground spawn, so the loader sets AutoDeleteExempt on it.</summary>
    private const string DepartureAtOak = """
        {
          "id": "departure-auto-delete",
          "name": "Departure at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": { "rules": "VFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 5500, "cruiseSpeed": 100, "route": "", "remarks": "", "aircraftType": "C172" }
            }
          ]
        }
        """;

    public DepartureAutoDeleteTests() => TestVnasData.EnsureInitialized();

    [Theory]
    [InlineData(null)]
    [InlineData("Never")]
    public void GroundSpawnedDeparture_PastTheDistance_IsRemoved_WhateverTheArrivalMode(string? arrivalMode)
    {
        SimulationEngine engine = LoadDeparture();
        engine.Scenario!.ClientAutoDeleteOverride = arrivalMode;
        engine.Scenario.DepartureAutoDeleteDistanceNm = 20;
        AircraftState departure = ClimbOut(engine, Callsign, 21);
        Assert.True(departure.Ground.AutoDeleteExempt, "the parking spawn should have set AutoDeleteExempt");

        Assert.Same(departure, Assert.Single(engine.TickAutoDelete()));
        Assert.Null(engine.World.FindAircraft(Callsign));
    }

    [Fact]
    public void RemovedDeparture_IsStampedDeparted_AndYieldsADebriefRow()
    {
        SimulationEngine engine = LoadDeparture();
        engine.Scenario!.DepartureAutoDeleteDistanceNm = 20;
        engine.Scenario.ElapsedSeconds = 600;
        AircraftState departure = ClimbOut(engine, Callsign, 21);

        Assert.Same(departure, Assert.Single(engine.TickAutoDelete()));

        Assert.Equal(CompletionReason.Departed, departure.CompletionReason);
        Assert.Equal(600, departure.CompletedAtSeconds);
        CompletedAircraftRecord record = Assert.Single(engine.World.GetCompletedAircraft());
        Assert.Equal(Callsign, record.Callsign);
        Assert.Equal(CompletionReason.Departed, record.Reason);

        var context = new AircraftDebriefContext([], engine.World.GetCompletedAircraft(), engine.Scenario.PrimaryAirportId);
        SoloTrainingReportData report = new SoloTrainingEvaluator().BuildReport(true, 600, new ApproachReportData([], [], 600, "N/A"), context);
        AircraftDebriefData row = Assert.Single(report.AircraftDebriefs);
        Assert.Equal(OperationKind.Departure, row.Operation);
        Assert.Equal(CompletionReason.Departed, row.CompletionReason);
        Assert.Equal("Departed the area.", row.CoachingNote);
    }

    [Fact]
    public void GroundSpawnedDeparture_InsideTheDistance_Stays()
    {
        SimulationEngine engine = LoadDeparture();
        engine.Scenario!.DepartureAutoDeleteDistanceNm = 20;
        ClimbOut(engine, Callsign, 19);

        Assert.Empty(engine.TickAutoDelete());
        Assert.NotNull(engine.World.FindAircraft(Callsign));
    }

    [Fact]
    public void SettingIsOffByDefault_DepartureIsNeverRemoved()
    {
        SimulationEngine engine = LoadDeparture();
        Assert.Null(engine.Scenario!.DepartureAutoDeleteDistanceNm);
        ClimbOut(engine, Callsign, 200);

        Assert.Empty(engine.TickAutoDelete());
        Assert.NotNull(engine.World.FindAircraft(Callsign));
    }

    [Fact]
    public void TrackedDeparture_PastTheDistance_IsRemoved()
    {
        SimulationEngine engine = LoadDeparture();
        engine.Scenario!.DepartureAutoDeleteDistanceNm = 20;
        AircraftState departure = ClimbOut(engine, Callsign, 21);
        departure.Track.Owner = TrackOwner.CreateStars("NCT_APP", "NCT", 4, "A");

        Assert.Same(departure, Assert.Single(engine.TickAutoDelete()));
    }

    [Fact]
    public void Nodel_KeepsTheDeparture_AndDelStillRemovesIt()
    {
        SimulationEngine engine = LoadDeparture();
        engine.Scenario!.DepartureAutoDeleteDistanceNm = 20;
        AircraftState departure = ClimbOut(engine, Callsign, 21);

        CommandResult nodel = engine.SendCommand(Callsign, "NODEL");
        Assert.True(nodel.Success, nodel.Message);
        Assert.True(departure.Ground.NoDeleteRequested);

        Assert.Empty(engine.TickAutoDelete());
        Assert.NotNull(engine.World.FindAircraft(Callsign));

        CommandResult del = engine.SendCommand(Callsign, "DEL");
        Assert.True(del.Success, del.Message);
        engine.TickAutoDelete();
        Assert.Null(engine.World.FindAircraft(Callsign));
    }

    [Fact]
    public void TaxiNodel_KeepsTheDeparture_OnceItClimbsPastTheDistance()
    {
        SimulationEngine engine = LoadDeparture();
        engine.Scenario!.DepartureAutoDeleteDistanceNm = 20;

        CommandResult taxi = engine.SendCommand(Callsign, "TAXI B 28R NODEL");
        Assert.True(taxi.Success, taxi.Message);
        AircraftState departure = ClimbOut(engine, Callsign, 21);

        Assert.True(departure.Ground.NoDeleteRequested);
        Assert.Empty(engine.TickAutoDelete());
        Assert.NotNull(engine.World.FindAircraft(Callsign));
    }

    [Fact]
    public void ArrivalPastTheDistance_IsNotRemovedByTheDepartureSweep()
    {
        SimulationEngine engine = LoadDeparture();
        engine.Scenario!.DepartureAutoDeleteDistanceNm = 20;
        engine.World.AddAircraft(Airborne(engine, "N2AR", departure: "KLAX", destination: "KOAK", distanceNm: 30));

        Assert.Empty(engine.TickAutoDelete());
        Assert.NotNull(engine.World.FindAircraft("N2AR"));
    }

    [Theory]
    [InlineData("KSFO", "KLAX")]
    [InlineData("KSJC", "KSMF")]
    public void AircraftNotDepartingThePrimaryAirport_IsNotRemoved(string departure, string destination)
    {
        SimulationEngine engine = LoadDeparture();
        engine.Scenario!.DepartureAutoDeleteDistanceNm = 20;
        engine.World.AddAircraft(Airborne(engine, "N3OT", departure, destination, distanceNm: 30));

        Assert.Empty(engine.TickAutoDelete());
        Assert.NotNull(engine.World.FindAircraft("N3OT"));
    }

    [Fact]
    public void LocalFlightBackToThePrimaryAirport_PastTheDistance_IsKept()
    {
        SimulationEngine engine = LoadDeparture();
        engine.Scenario!.DepartureAutoDeleteDistanceNm = 20;
        engine.World.AddAircraft(Airborne(engine, "N4LC", departure: "KOAK", destination: "KOAK", distanceNm: 30));

        Assert.Empty(engine.TickAutoDelete());
        Assert.NotNull(engine.World.FindAircraft("N4LC"));
    }

    [Fact]
    public void LiveTrafficShadowDeparture_PastTheDistance_IsLeftToTheFeed()
    {
        SimulationEngine engine = LoadDeparture();
        engine.Scenario!.DepartureAutoDeleteDistanceNm = 20;
        AircraftState shadow = Airborne(engine, "N5SH", departure: "KOAK", destination: "KLAX", distanceNm: 30);
        shadow.LiveTraffic = new AircraftLiveTraffic();
        engine.World.AddAircraft(shadow);

        Assert.Empty(engine.TickAutoDelete());
        Assert.NotNull(engine.World.FindAircraft("N5SH"));
    }

    [Fact]
    public void DistanceAndNodel_SurviveSnapshotRestore()
    {
        SimulationEngine engine = LoadDeparture();
        engine.Scenario!.DepartureAutoDeleteDistanceNm = 30;
        Assert.True(engine.SendCommand(Callsign, "NODEL").Success);

        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        Assert.Equal(30, snapshot.Scenario.DepartureAutoDeleteDistanceNm);

        SimulationEngine restored = LoadDeparture();
        restored.RestoreFromSnapshot(snapshot);

        Assert.Equal(30, restored.Scenario!.DepartureAutoDeleteDistanceNm);
        Assert.True(restored.World.FindAircraft(Callsign)!.Ground.NoDeleteRequested);
    }

    [Theory]
    [InlineData("30", 30.0)]
    [InlineData("12.5", 12.5)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void RecordedSettingChange_ReplaysOntoTheScenario(string? value, double? expected)
    {
        var recording = new SessionRecording
        {
            Version = 4,
            ScenarioJson = DepartureAtOak,
            RngSeed = 1,
            // Seed a value first so a null/empty change is seen to clear it.
            Actions =
            [
                new RecordedSettingChange(0, "DepartureAutoDeleteDistanceNm", "40"),
                new RecordedSettingChange(0, "DepartureAutoDeleteDistanceNm", value),
            ],
            TotalElapsedSeconds = 1,
        };

        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.Replay(recording, 1);

        Assert.Equal(expected, engine.Scenario!.DepartureAutoDeleteDistanceNm);
    }

    private static SimulationEngine LoadDeparture()
    {
        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.LoadScenario(DepartureAtOak, 1, MagneticDeclination.EvaluationDateUtc);
        Assert.NotNull(engine.World.FindAircraft(Callsign));
        return engine;
    }

    /// <summary>The primary airport's reference point — the datum the sweep measures from.</summary>
    private static LatLon PrimaryAirport(SimulationEngine engine)
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition(engine.Scenario!.PrimaryAirportId!)!.Value;
        return new LatLon(lat, lon);
    }

    private static AircraftState ClimbOut(SimulationEngine engine, string callsign, double distanceNm)
    {
        AircraftState aircraft = engine.World.FindAircraft(callsign)!;
        aircraft.IsOnGround = false;
        aircraft.Altitude = 4500;
        aircraft.Position = GeoMath.ProjectPoint(PrimaryAirport(engine), new TrueHeading(90), distanceNm);
        return aircraft;
    }

    private static AircraftState Airborne(SimulationEngine engine, string callsign, string departure, string destination, double distanceNm) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(PrimaryAirport(engine), new TrueHeading(180), distanceNm),
            Altitude = 9000,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = departure, Destination = destination },
        };
}
