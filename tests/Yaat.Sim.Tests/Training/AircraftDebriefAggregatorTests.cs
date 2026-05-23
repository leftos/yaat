using Xunit;
using Yaat.Sim;
using Yaat.Sim.Phases;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests.Training;

/// <summary>
/// Step 2 of M12.4: covers the per-aircraft debrief aggregator inside
/// <see cref="SoloTrainingEvaluator.BuildReport"/>. The aggregator groups findings
/// (already produced by the M10.7 evaluator surface) by callsign, classifies
/// each aircraft's operation kind against the scenario primary airport, and emits
/// a one-line coaching note per row.
/// </summary>
public class AircraftDebriefAggregatorTests
{
    public AircraftDebriefAggregatorTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft(string callsign, string departure = "", string destination = "") =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = !string.IsNullOrEmpty(departure) || !string.IsNullOrEmpty(destination),
                Departure = departure,
                Destination = destination,
            },
        };

    private static ApproachReportData EmptyApproachReport(double elapsed) => new([], [], elapsed, "N/A");

    [Fact]
    public void BuildReport_EmptyDebriefContext_ProducesNoDebriefs()
    {
        var evaluator = new SoloTrainingEvaluator();

        var report = evaluator.BuildReport(true, 100, EmptyApproachReport(100), AircraftDebriefContext.Empty);

        Assert.Empty(report.AircraftDebriefs);
    }

    [Fact]
    public void BuildReport_OneActiveAircraftNoFindings_EmitsCleanCoachingNote()
    {
        var evaluator = new SoloTrainingEvaluator();
        var ac = MakeAircraft("N123AB", departure: "OAK", destination: "RNO");
        ac.SpawnedAtSeconds = 30;
        var context = new AircraftDebriefContext([ac], [], "OAK");

        var report = evaluator.BuildReport(true, 100, EmptyApproachReport(100), context);

        var row = Assert.Single(report.AircraftDebriefs);
        Assert.Equal("N123AB", row.Callsign);
        Assert.Equal(OperationKind.Departure, row.Operation);
        Assert.Equal(CompletionReason.Active, row.CompletionReason);
        Assert.Equal(0, row.SeparationFindingCount);
        Assert.Equal("In service.", row.CoachingNote);
    }

    [Fact]
    public void BuildReport_CompletedHandoffNoFindings_EmitsCleanHandoffNote()
    {
        var evaluator = new SoloTrainingEvaluator();
        var record = new CompletedAircraftRecord(
            "AAL222",
            "B738",
            "100",
            FiledDeparture: "OAK",
            FiledDestination: "PHX",
            SpawnedAtSeconds: 10,
            CompletedAtSeconds: 240,
            Reason: CompletionReason.HandedOff,
            Detail: "NCT_F_APP"
        );
        var context = new AircraftDebriefContext([], [record], "OAK");

        var report = evaluator.BuildReport(true, 300, EmptyApproachReport(300), context);

        var row = Assert.Single(report.AircraftDebriefs);
        Assert.Equal(OperationKind.Departure, row.Operation);
        Assert.Equal(CompletionReason.HandedOff, row.CompletionReason);
        Assert.Equal(240.0, row.CompletedAtSeconds);
        Assert.Equal("Clean handoff to NCT_F_APP.", row.CoachingNote);
    }

    [Fact]
    public void BuildReport_LandedClean_EmitsLandedNoteWithRunway()
    {
        var evaluator = new SoloTrainingEvaluator();
        var record = new CompletedAircraftRecord(
            "N9225L",
            "C172",
            "200",
            FiledDeparture: "OAK",
            FiledDestination: "OAK",
            SpawnedAtSeconds: 5,
            CompletedAtSeconds: 420,
            Reason: CompletionReason.Landed,
            Detail: "28R"
        );
        var context = new AircraftDebriefContext([], [record], "OAK");

        var report = evaluator.BuildReport(true, 500, EmptyApproachReport(500), context);

        var row = Assert.Single(report.AircraftDebriefs);
        Assert.Equal(OperationKind.Departure, row.Operation); // pattern (dep==dest==primary) reads as Departure
        Assert.Equal("Clean landing 28R.", row.CoachingNote);
    }

    [Fact]
    public void BuildReport_FindingsGroupedByCallsign_PopulateCountsAndNote()
    {
        var evaluator = new SoloTrainingEvaluator();
        var a = MakeAircraft("AAL1", departure: "OAK", destination: "PHX");
        var b = MakeAircraft("UAL2", departure: "SFO", destination: "OAK");

        // Drive a separation finding involving both aircraft so each row aggregates one.
        a.Position = new LatLon(37.6213, -122.3790);
        a.Altitude = 5000;
        a.FlightPlan.FlightRules = "IFR";
        a.IsOnGround = false;
        a.TrueHeading = new TrueHeading(90);
        a.HasMadeInitialContact = true;
        b.Position = GeoMath.ProjectPoint(a.Position, new TrueHeading(90), 2.5);
        b.Altitude = 5000;
        b.FlightPlan.FlightRules = "IFR";
        b.IsOnGround = false;
        b.TrueHeading = new TrueHeading(90);
        b.HasMadeInitialContact = true;

        evaluator.Evaluate([a, b], scenarioElapsedSeconds: 60, Data.Airspace.AirspaceDatabase.Default);

        var context = new AircraftDebriefContext([a, b], [], "OAK");

        var report = evaluator.BuildReport(true, 60, EmptyApproachReport(60), context);

        Assert.Equal(2, report.AircraftDebriefs.Count);
        var rowA = Assert.Single(report.AircraftDebriefs, r => r.Callsign == "AAL1");
        var rowB = Assert.Single(report.AircraftDebriefs, r => r.Callsign == "UAL2");

        // Both rows should reference the same finding IDs (proves callsign grouping). The
        // separation pair fires one Separation + zero-or-more co-firing advisories, all of
        // which name both callsigns and so land on both rows.
        Assert.True(rowA.SeparationFindingCount >= 1);
        Assert.True(rowB.SeparationFindingCount >= 1);
        Assert.True(rowA.SafetyFindingCount >= 1);
        Assert.StartsWith("Safety:", rowA.CoachingNote);
        Assert.Equal(rowA.FindingIds.OrderBy(id => id), rowB.FindingIds.OrderBy(id => id));
    }

    [Fact]
    public void BuildReport_ClassifiesOperation_AgainstPrimaryAirportWithIcaoTolerance()
    {
        var evaluator = new SoloTrainingEvaluator();
        var departing = MakeAircraft("DEP1", departure: "KOAK", destination: "KLAS");
        var arriving = MakeAircraft("ARR1", departure: "KLAS", destination: "OAK");
        var transit = MakeAircraft("TRA1", departure: "SFO", destination: "RNO");
        var unknown = MakeAircraft("UNK1");

        var context = new AircraftDebriefContext([departing, arriving, transit, unknown], [], "OAK");

        var report = evaluator.BuildReport(true, 10, EmptyApproachReport(10), context);

        Assert.Equal(OperationKind.Departure, report.AircraftDebriefs.Single(r => r.Callsign == "DEP1").Operation);
        Assert.Equal(OperationKind.Arrival, report.AircraftDebriefs.Single(r => r.Callsign == "ARR1").Operation);
        Assert.Equal(OperationKind.Transit, report.AircraftDebriefs.Single(r => r.Callsign == "TRA1").Operation);
        Assert.Equal(OperationKind.Unknown, report.AircraftDebriefs.Single(r => r.Callsign == "UNK1").Operation);
    }

    [Fact]
    public void BuildReport_SortsByLaunchTime()
    {
        var evaluator = new SoloTrainingEvaluator();
        var ac1 = MakeAircraft("LATE1");
        ac1.SpawnedAtSeconds = 200;
        var ac2 = MakeAircraft("EARLY1");
        ac2.SpawnedAtSeconds = 50;
        var ac3 = MakeAircraft("MID1");
        ac3.SpawnedAtSeconds = 100;

        var context = new AircraftDebriefContext([ac1, ac2, ac3], [], "OAK");

        var report = evaluator.BuildReport(true, 300, EmptyApproachReport(300), context);

        Assert.Collection(
            report.AircraftDebriefs,
            r => Assert.Equal("EARLY1", r.Callsign),
            r => Assert.Equal("MID1", r.Callsign),
            r => Assert.Equal("LATE1", r.Callsign)
        );
    }

    [Fact]
    public void BuildReport_TwoCompletedSameCallsign_KeepsMostRecent()
    {
        // Delete + respawn + land of the same callsign leaves two records in the registry;
        // the Aircraft tab should only show the most recent run.
        var evaluator = new SoloTrainingEvaluator();
        var older = new CompletedAircraftRecord(
            "N123AB",
            "C172",
            "100",
            FiledDeparture: "OAK",
            FiledDestination: "OAK",
            SpawnedAtSeconds: 10,
            CompletedAtSeconds: 120,
            Reason: CompletionReason.Landed,
            Detail: "28R"
        );
        var newer = new CompletedAircraftRecord(
            "N123AB",
            "C172",
            "100",
            FiledDeparture: "OAK",
            FiledDestination: "OAK",
            SpawnedAtSeconds: 300,
            CompletedAtSeconds: 450,
            Reason: CompletionReason.Landed,
            Detail: "10L"
        );
        var context = new AircraftDebriefContext([], [older, newer], "OAK");

        var report = evaluator.BuildReport(true, 500, EmptyApproachReport(500), context);

        var row = Assert.Single(report.AircraftDebriefs);
        Assert.Equal(450.0, row.CompletedAtSeconds);
        Assert.Equal("10L", row.CompletionDetail);
        Assert.Equal(300.0, row.SpawnedAtSeconds);
    }

    [Fact]
    public void BuildReport_SecondCallWithSameInputs_ReusesCachedDebriefList()
    {
        // Production polls every 2-5 s; without caching the aggregator rebuilds every row
        // and re-allocates the AircraftDebriefData records each call. Identity check is the
        // only way to confirm the cache short-circuited (record equality would pass even
        // on a rebuild).
        var evaluator = new SoloTrainingEvaluator();
        var ac = MakeAircraft("N123AB", departure: "OAK", destination: "RNO");
        var context = new AircraftDebriefContext([ac], [], "OAK");

        var first = evaluator.BuildReport(true, 100, EmptyApproachReport(100), context);
        var second = evaluator.BuildReport(true, 100, EmptyApproachReport(100), context);

        Assert.Same(first.AircraftDebriefs, second.AircraftDebriefs);
    }

    [Fact]
    public void BuildReport_AfterFlightPlanAmend_RebuildsDebriefs()
    {
        var evaluator = new SoloTrainingEvaluator();
        var ac = MakeAircraft("N123AB", departure: "SFO", destination: "OAK");
        var context = new AircraftDebriefContext([ac], [], "OAK");

        var first = evaluator.BuildReport(true, 100, EmptyApproachReport(100), context);
        ac.FlightPlan.Departure = "OAK";
        var second = evaluator.BuildReport(true, 100, EmptyApproachReport(100), context);

        Assert.NotSame(first.AircraftDebriefs, second.AircraftDebriefs);
        Assert.Equal(OperationKind.Arrival, first.AircraftDebriefs.Single().Operation);
        Assert.Equal(OperationKind.Departure, second.AircraftDebriefs.Single().Operation);
    }

    [Fact]
    public void BuildReport_AfterCompletionChange_RebuildsDebriefs()
    {
        // Mutate the aircraft's completion state between calls — cache must invalidate.
        var evaluator = new SoloTrainingEvaluator();
        var ac = MakeAircraft("N123AB", departure: "OAK", destination: "OAK");
        var context = new AircraftDebriefContext([ac], [], "OAK");

        var first = evaluator.BuildReport(true, 100, EmptyApproachReport(100), context);
        ac.CompletedAtSeconds = 200;
        ac.CompletionReason = CompletionReason.Landed;
        ac.CompletionDetail = "28R";
        var second = evaluator.BuildReport(true, 250, EmptyApproachReport(250), context);

        Assert.NotSame(first.AircraftDebriefs, second.AircraftDebriefs);
        Assert.Equal(CompletionReason.Landed, second.AircraftDebriefs.Single().CompletionReason);
    }

    [Fact]
    public void BuildReport_ActiveAndCompletedSameCallsign_PrefersActive()
    {
        var evaluator = new SoloTrainingEvaluator();
        var ac = MakeAircraft("N123AB", departure: "OAK", destination: "RNO");
        ac.SpawnedAtSeconds = 500; // respawned with same callsign

        var staleRecord = new CompletedAircraftRecord(
            "N123AB",
            "C172",
            "100",
            FiledDeparture: "OAK",
            FiledDestination: "RNO",
            SpawnedAtSeconds: 10,
            CompletedAtSeconds: 200,
            Reason: CompletionReason.HandedOff,
            Detail: "NCT_F_APP"
        );
        var context = new AircraftDebriefContext([ac], [staleRecord], "OAK");

        var report = evaluator.BuildReport(true, 600, EmptyApproachReport(600), context);

        var row = Assert.Single(report.AircraftDebriefs);
        Assert.Equal(CompletionReason.Active, row.CompletionReason);
        Assert.Equal(500.0, row.SpawnedAtSeconds);
    }
}
