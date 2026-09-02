using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.ControllerAi.Brains;
using Yaat.Sim.ControllerAi.Knowledge;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi.Knowledge;

/// <summary>
/// OAK's SOP runway knowledge in action: configuration selection (4-2), the SFO coupling (4-2.c), runway assignment (3-4),
/// the conservative tailwind gate, the session knobs, and the Ground brain taxiing a departure to the knowledge runway.
/// </summary>
public class OakRunwayKnowledgeTests
{
    private static readonly DateTime ModelDate = MagneticDeclination.EvaluationDateUtc;
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public OakRunwayKnowledgeTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static FacilityOps Oak => FacilityOpsDatabase.For("OAK") ?? throw new InvalidOperationException("KOAK.json not loaded");

    private static IReadOnlyList<RunwayInfo> OakRunways => RunwayOccupancy.AirportRunways("OAK");

    private static SurfaceWind Wind(double direction, double knots) => new(direction, knots, null, false);

    private static string? NoPartner(string airport) => null;

    [Theory]
    [InlineData(120, 9, "SFOW")]
    [InlineData(300, 3, "SFOW")]
    [InlineData(120, 12, "OAKE")]
    [InlineData(100, 20, "OAKE")]
    [InlineData(300, 12, "SFOW")]
    [InlineData(280, 15, "SFOW")]
    public void Selection_CalmBelowTenKnots_ElseMostAlignedConfiguration(double direction, double knots, string expected)
    {
        var decision = FacilityRunwaySelector.Select(Oak, "OAK", Wind(direction, knots), NoPartner, OakRunways, ModelDate);

        Assert.NotNull(decision);
        Assert.Equal(expected, decision.ConfigurationName);
        Assert.Equal(RunwayUseSource.Knowledge, decision.Source);
        Assert.Equal(Oak.RunwaysAt(expected, "OAK")!.Departure, decision.DepartureRunways);
    }

    [Fact]
    public void Selection_NoWind_IsCalm_AndVariableWindIsCalm()
    {
        Assert.Equal("SFOW", FacilityRunwaySelector.Select(Oak, "OAK", null, NoPartner, OakRunways, ModelDate)!.ConfigurationName);
        Assert.Equal(
            "SFOW",
            FacilityRunwaySelector.Select(Oak, "OAK", new SurfaceWind(120, 14, null, true), NoPartner, OakRunways, ModelDate)!.ConfigurationName
        );
    }

    [Theory]
    [InlineData(300, 3)]
    [InlineData(120, 12)]
    [InlineData(300, 15)]
    public void Selection_SfoInEastFlow_ForcesSfoe_WhateverTheWind(double direction, double knots)
    {
        var decision = FacilityRunwaySelector.Select(
            Oak,
            "OAK",
            Wind(direction, knots),
            airport => airport == "KSFO" ? "SFOE" : null,
            OakRunways,
            ModelDate
        );

        Assert.NotNull(decision);
        Assert.Equal("SFOE", decision.ConfigurationName);
        Assert.Equal(["10L", "10R", "12"], decision.DepartureRunways);
        Assert.Contains("4-2.c", decision.Rationale);
    }

    [Theory]
    [InlineData("B738", "29", "SFOW", "30")]
    [InlineData("A332", "5", "SFOW", "30")]
    [InlineData("DH8D", "8B", "SFOW", "30")]
    [InlineData("C208", "PCM1", "SFOW", "28")]
    [InlineData("C172", "NEW5", "SFOW", "28")]
    [InlineData("C172", "NEW5", "OAKE", "10")]
    [InlineData("B738", "29", "OAKE", "12")]
    public void Assignment_KeepsJetsAndHeavyTurbopropsOffThe28s_AndPicksTheNearestEndForTheRest(
        string type,
        string parking,
        string configuration,
        string expectedPrefix
    )
    {
        if (_zoa is null)
        {
            return;
        }

        var json = AiTestHost
            .ParkedAtOak.Replace("\"parking\": \"SIG1\"", $"\"parking\": \"{parking}\"")
            .Replace("\"aircraftType\": \"C172\"", $"\"aircraftType\": \"{type}\"");
        var engine = AiTestHost.Load(json, _zoa, 7, []);
        var aircraft = engine.FindAircraft(AiTestHost.Callsign)!;
        var sets = Oak.RunwaysAt(configuration, "OAK")!;
        var decision = new RunwayUseDecision("OAK", sets.Departure, sets.Arrival, configuration, RunwayUseSource.Knowledge, "test");

        var runway = FacilityRunwayAssigner.AssignDepartureRunway(Oak, aircraft, decision, OakRunways);

        Assert.StartsWith(expectedPrefix, runway);
        Assert.Contains(runway, sets.Departure);
    }

    [Fact]
    public void Gate_ANineKnotTailwind_IsFineDry_AndOverTheLimitWet_AndTheGustCounts()
    {
        var sets = Oak.RunwaysAt("OAKE", "OAK")!;
        var oake = new RunwayUseDecision("OAK", sets.Departure, sets.Arrival, "OAKE", RunwayUseSource.Knowledge, "test");

        Assert.Same(oake, RunwayUsabilityGate.Apply(oake, Wind(300, 9), wet: false, OakRunways, ModelDate).Usable);
        var (wetUsable, wetRemoved) = RunwayUsabilityGate.Apply(oake, Wind(300, 9), wet: true, OakRunways, ModelDate);
        Assert.Null(wetUsable);
        Assert.Contains("tailwind", wetRemoved);
        // 300 at 5 gusting 14: the gust is the tailwind the runway sees.
        Assert.Null(RunwayUsabilityGate.Apply(oake, new SurfaceWind(300, 5, 14, false), wet: false, OakRunways, ModelDate).Usable);
        // A variable wind can blow from anywhere: 14 kt VRB is 14 kt of tailwind on every end.
        Assert.Null(RunwayUsabilityGate.Apply(oake, new SurfaceWind(120, 14, null, true), wet: false, OakRunways, ModelDate).Usable);
        Assert.Same(oake, RunwayUsabilityGate.Apply(oake, new SurfaceWind(120, 3, null, true), wet: false, OakRunways, ModelDate).Usable);
    }

    [Fact]
    public void Gate_PrunesTheUnusableRunways_AndKeepsTheConfigurationWhileOneSurvives()
    {
        var spread = new RunwayUseDecision("OAK", ["30", "12"], ["30", "12"], "TEST", RunwayUseSource.Knowledge, "test");

        var (usable, removed) = RunwayUsabilityGate.Apply(spread, Wind(300, 12), wet: false, OakRunways, ModelDate);

        Assert.NotNull(usable);
        Assert.Equal(["30"], usable.DepartureRunways);
        Assert.Equal(["30", "12"], usable.ArrivalRunways);
        Assert.Contains("12", removed);
        Assert.Contains("unusable", usable.Rationale);
    }

    [Fact]
    public void State_HoldsTheDecisionThroughSmallWindChanges_AndRedecidesOnABigOne()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, [ground]);
        var aircraft = engine.FindAircraft(AiTestHost.Callsign)!;
        var state = new RunwayInUseState(FacilityOpsDatabase.For);
        engine.World.Weather = new WeatherProfile { WindLayers = [new WindLayer { Direction = 300, Speed = 12 }] };
        var west = state.For("OAK", AiTestHost.Context(engine, [aircraft], [ground], 10, [], new RecordingAiCommandSink()), ground.PositionId);
        Assert.Equal("SFOW", west!.ConfigurationName);

        // A weather timeline hands the world a new profile every second; a wobble is not a new decision.
        engine.World.Weather = new WeatherProfile { WindLayers = [new WindLayer { Direction = 310, Speed = 14 }] };
        Assert.Same(
            west,
            state.For("OAK", AiTestHost.Context(engine, [aircraft], [ground], 11, [], new RecordingAiCommandSink()), ground.PositionId)
        );

        // A veer through the field is.
        engine.World.Weather = new WeatherProfile { WindLayers = [new WindLayer { Direction = 120, Speed = 14 }] };
        var east = state.For("OAK", AiTestHost.Context(engine, [aircraft], [ground], 12, [], new RecordingAiCommandSink()), ground.PositionId);
        Assert.Equal("OAKE", east!.ConfigurationName);
        Assert.False(RunwayInUseState.WindMoved(Wind(300, 12), Wind(310, 14)));
        Assert.True(RunwayInUseState.WindMoved(Wind(300, 12), Wind(300, 17)));
        Assert.True(RunwayInUseState.WindMoved(Wind(300, 12), new SurfaceWind(300, 12, null, true)));
        Assert.True(RunwayInUseState.WindMoved(null, Wind(300, 12)));
    }

    [Fact]
    public void State_KnowledgeDecides_ThenKnobsOverride_AndAWetConflictFallsBackToTheGenericRule()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, [ground]);
        var scenario = engine.Scenario!;
        var aircraft = engine.FindAircraft(AiTestHost.Callsign)!;
        engine.World.Weather = new WeatherProfile { WindLayers = [new WindLayer { Direction = 120, Speed = 12 }] };

        // Knowledge: 12 kt from 120 ⇒ OAKE, and the brain assigns a C172 the nearest of the 10s.
        var knowledge = new RunwayInUseState(FacilityOpsDatabase.For);
        var context = AiTestHost.Context(engine, [aircraft], [ground], 10, [], new RecordingAiCommandSink());
        var decision = knowledge.For("OAK", context, ground.PositionId);
        Assert.NotNull(decision);
        Assert.Equal("OAKE", decision.ConfigurationName);
        Assert.Equal(RunwayUseSource.Knowledge, decision.Source);
        Assert.StartsWith("10", knowledge.DepartureRunwayFor(aircraft, decision, context));

        // A named configuration for the partner drives the coupling rule (light wind, so the tailwind gate stays out of it).
        scenario.ControllerAi = Config(ground, null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["KSFO"] = "SFOE" });
        engine.World.Weather = new WeatherProfile { WindLayers = [new WindLayer { Direction = 300, Speed = 3 }] };
        var coupled = new RunwayInUseState(FacilityOpsDatabase.For).For(
            "OAK",
            AiTestHost.Context(engine, [aircraft], [ground], 11, [], new RecordingAiCommandSink()),
            ground.PositionId
        );
        Assert.Equal("SFOE", coupled!.ConfigurationName);

        // A named configuration for OAK itself is the session's word — kept as set, with the impossible tailwind on the ledger.
        engine.World.Weather = new WeatherProfile { WindLayers = [new WindLayer { Direction = 300, Speed = 12 }] };
        scenario.ControllerAi = Config(ground, null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["KOAK"] = "OAKE" });
        scenario.AiAnomalies.Clear();
        var fixedConfiguration = new RunwayInUseState(FacilityOpsDatabase.For).For(
            "OAK",
            AiTestHost.Context(engine, [aircraft], [ground], 12, [], new RecordingAiCommandSink()),
            ground.PositionId
        );
        Assert.Equal("OAKE", fixedConfiguration!.ConfigurationName);
        Assert.Equal(RunwayUseSource.Override, fixedConfiguration.Source);
        Assert.Equal(["10L", "10R", "12"], fixedConfiguration.DepartureRunways);
        Assert.Contains("kept as set", Assert.Single(scenario.AiAnomalies.Drain()).Detail);

        // The runway designator beats everything.
        scenario.ControllerAi = Config(ground, "30", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["KOAK"] = "OAKE" });
        var fixedRunway = new RunwayInUseState(FacilityOpsDatabase.For).For(
            "OAK",
            AiTestHost.Context(engine, [aircraft], [ground], 13, [], new RecordingAiCommandSink()),
            ground.PositionId
        );
        Assert.Equal(["30"], fixedRunway!.DepartureRunways);

        // Wet with a 9 kt tailwind on the 10s: the coupling's SFOE fails the gate, the generic rule takes 30, and the
        // conflict is on the ledger.
        scenario.ControllerAi = Config(ground, null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["KSFO"] = "SFOE" });
        engine.World.Weather = new WeatherProfile { Precipitation = "RA", WindLayers = [new WindLayer { Direction = 300, Speed = 9 }] };
        scenario.AiAnomalies.Clear();
        var conflicted = new RunwayInUseState(FacilityOpsDatabase.For).For(
            "OAK",
            AiTestHost.Context(engine, [aircraft], [ground], 14, [], new RecordingAiCommandSink()),
            ground.PositionId
        );
        Assert.Equal(RunwayUseSource.Generic, conflicted!.Source);
        Assert.Equal("30", conflicted.PrimaryDepartureRunway);
        var anomaly = Assert.Single(scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.KnowledgeConflict, anomaly.Kind);
        Assert.Contains("SFOE", anomaly.Detail);
    }

    [Fact]
    public void State_TwoFacilitiesCouplingToEachOther_Terminate_AndBothDecisionsAreCached()
    {
        if (_zoa is null)
        {
            return;
        }

        var sfo = new FacilityOps
        {
            SchemaVersion = FacilityOps.CurrentSchemaVersion,
            FacilityId = "SFO",
            AirportId = "KSFO",
            SourceDocument = "test",
            RunwayConfigurations =
            [
                new RunwayConfiguration
                {
                    Name = "SFOW",
                    Runways = new Dictionary<string, ConfigurationRunways>
                    {
                        ["KSFO"] = new() { Departure = ["01L", "01R"], Arrival = ["28L", "28R"] },
                    },
                    Source = "test",
                },
                new RunwayConfiguration
                {
                    Name = "SFOE",
                    Runways = new Dictionary<string, ConfigurationRunways>
                    {
                        ["KSFO"] = new() { Departure = ["10L", "10R"], Arrival = ["19L", "19R"] },
                        ["KOAK"] = new() { Departure = ["10L", "10R", "12"], Arrival = ["10L", "10R", "12"] },
                    },
                    Source = "test",
                },
            ],
            RunwaySelection = new RunwaySelectionPolicy
            {
                CalmWindBelowKt = 10,
                CalmConfiguration = "SFOW",
                WindAlignedCandidates = ["SFOW", "SFOE"],
                PartnerCouplings =
                [
                    new PartnerCoupling
                    {
                        PartnerAirportId = "KOAK",
                        PartnerConfiguration = "OAKE",
                        UseConfiguration = "SFOE",
                        Source = "test",
                    },
                ],
                Source = "test",
            },
            RunwayAssignmentPolicy = [],
        };
        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, [ground]);
        var aircraft = engine.FindAircraft(AiTestHost.Callsign)!;
        engine.World.Weather = new WeatherProfile { WindLayers = [new WindLayer { Direction = 120, Speed = 12 }] };
        var state = new RunwayInUseState(airport =>
            NavigationDatabase.AirportIdsMatch(airport ?? "", "KSFO") ? sfo : FacilityOpsDatabase.For(airport)
        );
        var context = AiTestHost.Context(engine, [aircraft], [ground], 10, [], new RecordingAiCommandSink());

        // OAK asks SFO, SFO asks OAK back and gets nothing, decides on its wind, and OAK follows it.
        var oak = state.For("OAK", context, ground.PositionId);
        var sfoDecision = state.For("SFO", context, ground.PositionId);

        Assert.Equal("SFOE", oak!.ConfigurationName);
        Assert.Equal("SFOE", sfoDecision!.ConfigurationName);
        Assert.Contains("4-2.c", oak.Rationale);
        Assert.Same(sfoDecision, state.For("KSFO", context, ground.PositionId));
    }

    [Fact]
    public void ControllerAiConfig_RunwayConfigurations_SurviveTheSnapshot()
    {
        if (_zoa is null)
        {
            return;
        }

        var config = Config(
            TestAiPositions.OakGround(_zoa),
            "30",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["KSFO"] = "SFOE" }
        );

        var back = ControllerAiConfig.FromSnapshot(config.ToSnapshot());

        Assert.Equal("30", back.RunwayInUse);
        Assert.Equal("SFOE", back.RunwayConfigurations["ksfo"]);
    }

    [Fact]
    public void GroundBrain_TaxiesADepartureToTheKnowledgeRunway()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.LoadWith(AiTestHost.ParkedAtOak, _zoa, 7, [ground], null, p => new GroundBrain(p));
        engine.World.Weather = new WeatherProfile { WindLayers = [new WindLayer { Direction = 120, Speed = 12 }] };
        var aiId = AiConnectionId.Format(ground.PositionId);
        RecordedCommand? taxi = null;
        for (int t = 0; t < 120 && taxi is null; t++)
        {
            AiTestHost.Tick(engine, 1);
            taxi = engine.Scenario!.ActionLog.OfType<RecordedCommand>().FirstOrDefault(a => a.ConnectionId == aiId);
        }

        Assert.NotNull(taxi);
        Assert.StartsWith("TAXIAUTO 10", taxi.Command);
    }

    private static ControllerAiConfig Config(AiPositionConfig ground, string? runwayInUse, IReadOnlyDictionary<string, string> configurations) =>
        new()
        {
            Seed = 7,
            EnabledPositionIds = [ground.PositionId],
            RoleOverrides = AiTestHost.NoOverrides,
            RunwayInUse = runwayInUse,
            RunwayConfigurations = configurations,
        };
}
