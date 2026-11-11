using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

// A runway transition must never re-add a STAR fix the aircraft has already flown: joining ALTTA9's
// RW29R transition (IF TTE, TF ALTTA) onto a route that is already past TTE used to put TTE back
// behind the aircraft, which then turned back at ALTTA.
public class S3Fat3StarEndTurnBackTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/s3-fat-3-star-end-recording.yaat-bug-report-bundle.zip";

    private SimulationEngine BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var groundData = RecordingGroundData.Evergreen("the ALTTA9 route past TTE and CLAND after a deferred CAPP");
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData, PushPlanStore.CreateShipped());
    }

    private static List<string> RouteNames(AircraftState ac) => [.. ac.Targets.NavigationRoute.Select(n => n.Name)];

    [Fact]
    public void SpawnPastTte_OnAlttaAltta9_RouteIsAlttaOnly()
    {
        TestVnasData.EnsureInitialized();
        const string scenarioJson = """
            { "id": "test", "name": "Test", "aircraft": [{
                "aircraftId": "N160TM", "aircraftType": "TBM9",
                "startingConditions": { "type": "FixOrFrd", "fix": "EXTRA", "altitude": 11000, "speed": 210,
                                        "navigationPath": "ALTTA ALTTA9.29R" },
                "flightplan": { "rules": "IFR", "departure": "KCMA", "destination": "KFAT",
                                "route": "FIM TTE ALTTA9", "cruiseAltitude": 26000, "cruiseSpeed": 323 },
                "presetCommands": [] }] }
            """;
        ScenarioLoadResult result = ScenarioLoader.Load(scenarioJson, null, new SerializableRandom(42), MagneticDeclination.EvaluationDateUtc);
        Assert.Equal(["ALTTA"], RouteNames(result.ImmediateAircraft[0].State));
    }

    [Fact]
    public void EappAfterPassingTte_DoesNotAppendTte()
    {
        RecordingArchive? archive = RecordingLoader.OpenArchive(RecordingPath);
        Assert.NotNull(archive);
        using (archive)
        {
            SessionRecording recording = archive.ToBaseSessionRecording();
            SimulationEngine engine = BuildEngine();
            engine.Replay(recording, 0);
            TimedSnapshot? snapshot = archive.ReadSnapshotAt(535);
            Assert.NotNull(snapshot);
            engine.RestoreFromSnapshot(snapshot.State);
            engine.FastForwardTo(540, recording.Actions); // recorded EAPP I29RY at t=537
            AircraftState ac = engine.FindAircraft("N32BR")!;
            Assert.Equal("29R", ac.Procedure.DestinationRunway);
            Assert.Equal(["ALTTA"], RouteNames(ac));
        }
    }

    [Fact]
    public void EmptyRoute_GetsTheWholeRunwayTransition()
    {
        TestVnasData.EnsureInitialized();
        CifpStarProcedure? star = NavigationDatabase.Instance.GetStar("KFAT", "ALTTA9");
        Assert.NotNull(star);
        CifpTransition? transition = NavigationCommandHandler.LookupRunwayTransition(star.RunwayTransitions, "29R");
        Assert.NotNull(transition);

        List<CifpLeg> legs = NavigationCommandHandler.SelectRunwayTransitionLegsToAppend(transition.Legs, star, []);

        Assert.Equal(["TTE", "ALTTA"], legs.Select(l => l.FixIdentifier));
    }

    [Fact]
    public void SpawnUpstreamOfTte_KeepsTheWholeTransitionWithoutDuplicate()
    {
        TestVnasData.EnsureInitialized();
        const string scenarioJson = """
            { "id": "test", "name": "Test", "aircraft": [{
                "aircraftId": "N200WM", "aircraftType": "TBM9",
                "startingConditions": { "type": "FixOrFrd", "fix": "FIM", "altitude": 11000, "speed": 210,
                                        "navigationPath": "TTE ALTTA9.29R" },
                "flightplan": { "rules": "IFR", "departure": "KCMA", "destination": "KFAT",
                                "route": "FIM TTE ALTTA9", "cruiseAltitude": 26000, "cruiseSpeed": 323 },
                "presetCommands": [] }] }
            """;
        ScenarioLoadResult result = ScenarioLoader.Load(scenarioJson, null, new SerializableRandom(42), MagneticDeclination.EvaluationDateUtc);
        Assert.Equal(["TTE", "ALTTA"], RouteNames(result.ImmediateAircraft[0].State));
    }

    [Fact]
    public void SpawnUpstreamOfTte_OnAltta9_11L_EndsAtAlttaWithTheVmCourse()
    {
        TestVnasData.EnsureInitialized();
        const string scenarioJson = """
            { "id": "test", "name": "Test", "aircraft": [{
                "aircraftId": "N210WM", "aircraftType": "TBM9",
                "startingConditions": { "type": "FixOrFrd", "fix": "FIM", "altitude": 11000, "speed": 210,
                                        "navigationPath": "TTE ALTTA9.11L" },
                "flightplan": { "rules": "IFR", "departure": "KCMA", "destination": "KFAT",
                                "route": "FIM TTE ALTTA9", "cruiseAltitude": 26000, "cruiseSpeed": 323 },
                "presetCommands": [] }] }
            """;
        ScenarioLoadResult result = ScenarioLoader.Load(scenarioJson, null, new SerializableRandom(42), MagneticDeclination.EvaluationDateUtc);
        AircraftState ac = result.ImmediateAircraft[0].State;

        // RW11L ends in a VM leg publishing 310°; the spawned route must carry that course on ALTTA
        // so the route-exhausted branch flies it instead of holding the arrival heading.
        Assert.Equal(["TTE", "ALTTA"], RouteNames(ac));
        NavigationTarget altta = ac.Targets.NavigationRoute[^1];
        Assert.Equal("ALTTA", altta.Name);
        Assert.NotNull(altta.TerminalCourseMagnetic);
        Assert.Equal(310.0, altta.TerminalCourseMagnetic!.Value, 1);
    }
}
