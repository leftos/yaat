using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Simulation;
using OakesHarness = Yaat.Sim.Tests.Simulation.DescendViaSupersedesEarlierAltitudeTests.Harness;

namespace Yaat.Sim.Tests;

/// <summary>
/// YAAT-1363: SMAX ("maintain maximum forward speed", 7110.65 5-7-2) holds the fastest the type may fly where it is: its
/// Vmo below the IAS/Mach crossover, past 14 CFR 91.117's 250 kt, and its Mmo above it, switching once at the crossover.
/// It stands until another speed act ends it, like SPD.
/// </summary>
public sealed class SpeedMaxCommandTests
{
    private const double SpeedSnapKts = 2;

    private readonly ITestOutputHelper _output;

    public SpeedMaxCommandTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static double B738Vmo => Assert.IsType<double>(AircraftPerformance.MaxOperatingSpeedKts("B738"));

    private static double B738Mmo => Assert.IsType<double>(AircraftPerformance.MaxOperatingMach("B738"));

    /// <summary>The IAS SMAX holds a B738 to below the changeover: Vmo less a 10 kt margin.</summary>
    private static double B738SmaxKts => Math.Round(B738Vmo - 10);

    /// <summary>The Mach SMAX holds a B738 to above the changeover: Mmo less a 0.02 margin.</summary>
    private static double B738SmaxMach => Math.Round(B738Mmo - 0.02, 2);

    private static AircraftState Airborne(string type, double altitude, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "SWA123",
            AircraftType = type,
            Position = new LatLon(34.771654, -114.542825),
            TrueHeading = new TrueHeading(270),
            TrueTrack = new TrueHeading(270),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                HasFlightPlan = true,
                Departure = "KPHX",
                Destination = "KLAX",
            },
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(270);
        ac.Targets.TargetAltitude = altitude;
        return ac;
    }

    private static CommandResult Send(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"'{text}' should parse: {parsed.Reason}");
        return CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new SerializableRandom(1)));
    }

    private static void FlySeconds(AircraftState aircraft, int seconds, Action<AircraftState> eachSecond)
    {
        for (int second = 0; second < seconds; second++)
        {
            for (int sub = 0; sub < 4; sub++)
            {
                FlightPhysics.Update(aircraft, 0.25);
            }

            eachSecond(aircraft);
        }
    }

    /// <summary>Flies until <paramref name="fixName"/> leaves the route, returning the IAS then, or null if it never does.</summary>
    private static double? FlyUntilSequenced(AircraftState aircraft, string fixName)
    {
        for (int second = 0; second < 3_600; second++)
        {
            FlySeconds(aircraft, 1, _ => { });
            if (!aircraft.Targets.NavigationRoute.Exists(fix => fix.Name == fixName))
            {
                return aircraft.IndicatedAirspeed;
            }
        }

        return null;
    }

    [Theory]
    [InlineData("SMAX")]
    [InlineData("BFS")]
    [InlineData("MAXSPD")]
    public void Smax_ParsesAllThreeAliases(string alias)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(alias);

        Assert.True(parsed.IsSuccess, $"'{alias}' should parse: {parsed.Reason}");
        Assert.IsType<SpeedMaxCommand>(parsed.Value);
    }

    [Theory]
    [InlineData("B738", 8_000, 330.0, null)]
    [InlineData("B738", 35_000, null, 0.80)]
    public void Smax_JetHoldsAMarginBelowTheRedline(string type, double altitude, double? expectedKts, double? expectedMach)
    {
        AircraftState ac = Airborne(type, altitude, ias: 90);

        Assert.True(Send(ac, "SMAX").Success);

        Assert.True(ac.Targets.SpeedMaxHeld);
        Assert.Equal(expectedKts, ac.Targets.AssignedSpeed);
        Assert.Equal(expectedMach, ac.Targets.TargetMach);
    }

    [Fact]
    public void Smax_BelowTheChangeover_FliesVmoLessTheMargin_Past250Below10000()
    {
        AircraftState ac = Airborne("B738", altitude: 8_000, ias: 250);
        Assert.True(WindInterpolator.MachToIas(B738SmaxMach, 8_000) > B738SmaxKts, "premise: Vmo governs at 8,000 ft");

        Assert.True(Send(ac, "SMAX").Success);
        FlySeconds(ac, 90, _ => { });

        Assert.True(ac.Targets.SpeedMaxHeld);
        Assert.Equal(Math.Round(B738SmaxKts), Assert.IsType<double>(ac.Targets.AssignedSpeed));
        Assert.Null(ac.Targets.TargetMach);
        Assert.InRange(ac.IndicatedAirspeed, B738SmaxKts - SpeedSnapKts, B738SmaxKts + SpeedSnapKts);
    }

    [Fact]
    public void Smax_AboveTheChangeover_HoldsMmoLessTheMargin()
    {
        AircraftState ac = Airborne("B738", altitude: 35_000, ias: 250);
        double mmoIas = WindInterpolator.MachToIas(B738SmaxMach, 35_000);
        Assert.True(mmoIas < B738SmaxKts, "premise: Mmo governs at FL350");

        Assert.True(Send(ac, "SMAX").Success);
        FlySeconds(ac, 120, _ => { });

        Assert.True(ac.Targets.SpeedMaxHeld);
        Assert.Equal(B738SmaxMach, Assert.IsType<double>(ac.Targets.TargetMach), 1e-9);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.InRange(ac.IndicatedAirspeed, mmoIas - SpeedSnapKts, mmoIas + SpeedSnapKts);
    }

    [Fact]
    public void Smax_ClimbThroughTheChangeover_SwitchesOnce()
    {
        AircraftState ac = Airborne("B738", altitude: 20_000, ias: 300);
        Assert.True(Send(ac, "SMAX").Success);
        Assert.True(Send(ac, "CM 350").Success);

        (double? Mach, double? Assigned) mode = (ac.Targets.TargetMach, ac.Targets.AssignedSpeed);
        Assert.Equal(((double?)null, (double?)Math.Round(B738SmaxKts)), mode);
        var switchAltitudes = new List<double>();
        FlySeconds(
            ac,
            900,
            a =>
            {
                (double? Mach, double? Assigned) now = (a.Targets.TargetMach, a.Targets.AssignedSpeed);
                if (now != mode)
                {
                    switchAltitudes.Add(a.Altitude);
                    mode = now;
                }
            }
        );

        _output.WriteLine($"Switched at {string.Join(", ", switchAltitudes.Select(alt => alt.ToString("F0")))} ft");
        Assert.InRange(ac.Altitude, 34_990, 35_010);
        double switchedAt = Assert.Single(switchAltitudes);
        Assert.True(
            WindInterpolator.MachToIas(B738SmaxMach, switchedAt) < B738SmaxKts,
            $"switched to Mach at {switchedAt:F0} ft, below the crossover"
        );
        Assert.True(
            WindInterpolator.MachToIas(B738SmaxMach, switchedAt - 100) >= B738SmaxKts,
            $"switched to Mach at {switchedAt:F0} ft, well past the crossover"
        );
        Assert.Equal(B738SmaxMach, Assert.IsType<double>(ac.Targets.TargetMach), 1e-9);
        Assert.Null(ac.Targets.AssignedSpeed);
    }

    private const string RfasRow = "RFAS";
    private const string ConstrainedDirectRow = "DCTF PMD/300 S250";

    [Theory]
    [InlineData("RNS", null, null)]
    [InlineData("SPD 250", 250.0, null)]
    [InlineData("MACH .78", null, 0.78)]
    [InlineData("DSR", null, null)]
    [InlineData("SPDN 250", 250.0, null)]
    [InlineData(RfasRow, null, null)]
    [InlineData("WARP PMD 270 300 280", 280.0, null)]
    [InlineData("CFIX PMD 300 250", 250.0, null)]
    [InlineData(ConstrainedDirectRow, null, null)]
    public void Smax_EndedBy(string command, double? expectedAssigned, double? expectedMach)
    {
        AircraftState ac = Airborne("B738", altitude: 30_000, ias: 280);
        Assert.True(Send(ac, "SMAX").Success);
        FlySeconds(ac, 5, _ => { });
        Assert.NotNull(ac.Targets.TargetMach);

        CommandResult ended = command == ConstrainedDirectRow ? DirectToPmdCrossingAt250(ac) : Send(ac, command);
        Assert.True(ended.Success, $"{command}: {ended.Message}");
        FlySeconds(ac, 5, _ => { });

        double? assigned =
            command == RfasRow ? AircraftPerformance.ApproachSpeed("B738", AircraftCategorization.Categorize("B738")) : expectedAssigned;
        Assert.False(ac.Targets.SpeedMaxHeld);
        Assert.Equal(assigned, ac.Targets.AssignedSpeed);
        Assert.Equal(expectedMach, ac.Targets.TargetMach);
    }

    [Fact]
    public void Smax_EndedBy_FinalGateAutoCancel()
    {
        AircraftState ac = Airborne("B738", altitude: 1_500, ias: 200);
        Assert.True(Send(ac, "SMAX").Success);
        Assert.True(ac.Targets.SpeedMaxHeld);
        ac.TrueTrack = new TrueHeading(280);
        ac.Phases = new PhaseList
        {
            AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon, heading: 280),
            LandingClearance = ClearanceType.ClearedToLand,
        };

        FlightPhysics.Update(ac, 0.25);
        double? assigned = ac.Targets.AssignedSpeed;
        FlightPhysics.Update(ac, 0.25);

        Assert.False(ac.Targets.SpeedMaxHeld);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
        Assert.Equal(assigned, ac.Targets.AssignedSpeed);
    }

    [Fact]
    public void Smax_ThenCrossingSpeed_CrossesAtIt_ThenSmaxIsArmedAgain()
    {
        AircraftState ac = Airborne("B738", altitude: 35_000, ias: 250);
        Assert.True(Send(ac, "SMAX").Success);
        Assert.True(Send(ac, "CFIX PMD 110 250").Success);
        Assert.False(ac.Targets.SpeedMaxHeld);
        Assert.Null(ac.Targets.TargetMach);

        double? crossingIas = null;
        for (int second = 0; (second < 3_600) && (crossingIas is null); second++)
        {
            FlySeconds(ac, 1, _ => { });
            if (!ac.Targets.NavigationRoute.Exists(fix => fix.Name == "PMD"))
            {
                crossingIas = ac.IndicatedAirspeed;
            }
        }

        _output.WriteLine($"Crossed PMD at {crossingIas:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.NotNull(crossingIas);
        Assert.InRange(crossingIas.Value, 240, 260);
        Assert.True(ac.Targets.SpeedMaxHeld);
        Assert.True(WindInterpolator.MachToIas(B738SmaxMach, ac.Altitude) > B738SmaxKts, "premise: below the crossover past the fix");
        Assert.Equal(Math.Round(B738SmaxKts), Assert.IsType<double>(ac.Targets.AssignedSpeed));
        Assert.Null(ac.Targets.TargetMach);
    }

    [Theory]
    [InlineData("SPD 280", 280.0, null)]
    [InlineData("MACH .76", null, 0.76)]
    [InlineData("RNS", null, null)]
    [InlineData("RPS", null, null)]
    [InlineData("DSR", null, null)]
    [InlineData("SPDN 280", 280.0, null)]
    [InlineData(RfasRow, null, null)]
    public void Smax_ThenCrossingSpeed_ThenALaterSpeed_SmaxIsNotArmedAgain(string command, double? expectedAssigned, double? expectedMach)
    {
        AircraftState ac = Airborne("B738", altitude: 35_000, ias: 250);
        Assert.True(Send(ac, "SMAX").Success);
        Assert.True(Send(ac, "CFIX PMD 300 250").Success);

        CommandResult later = Send(ac, command);
        Assert.True(later.Success, $"{command}: {later.Message}");
        Assert.DoesNotContain(ac.Targets.NavigationRoute, fix => fix.RevertSpeedMax);

        Assert.NotNull(FlyUntilSequenced(ac, "PMD"));
        FlySeconds(ac, 5, _ => { });

        double? assigned =
            command == RfasRow ? AircraftPerformance.ApproachSpeed("B738", AircraftCategorization.Categorize("B738")) : expectedAssigned;
        Assert.False(ac.Targets.SpeedMaxHeld);
        Assert.Equal(assigned, ac.Targets.AssignedSpeed);
        Assert.Equal(expectedMach, ac.Targets.TargetMach);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Smax_ThenTwoCrossingSpeeds_CrossesTheLastAtItsSpeed_ThenSmaxIsArmedAgain(bool lastIssuedFirst)
    {
        AircraftState ac = Airborne("B738", altitude: 35_000, ias: 250);
        Assert.True(Send(ac, "SMAX").Success);
        Assert.True(Send(ac, "DCT HEC PMD").Success);
        string[] crossings = lastIssuedFirst ? ["CFIX PMD 110 250", "CFIX HEC 300 300"] : ["CFIX HEC 300 300", "CFIX PMD 110 250"];
        foreach (string crossing in crossings)
        {
            CommandResult result = Send(ac, crossing);
            Assert.True(result.Success, $"{crossing}: {result.Message}");
        }

        AssertCrossesPmdAt250ThenSmaxIsArmedAgain(ac);
    }

    [Fact]
    public void Smax_ThenTwoCrossingSpeeds_OnlyTheLastFixHoldsTheReArm()
    {
        AircraftState ac = Airborne("B738", altitude: 35_000, ias: 250);
        Assert.True(Send(ac, "SMAX").Success);
        Assert.True(Send(ac, "DCT HEC PMD").Success);
        Assert.True(Send(ac, "CFIX HEC 300 300").Success);
        Assert.True(Send(ac, "CFIX PMD 110 250").Success);

        NavigationTarget flagged = Assert.Single(ac.Targets.NavigationRoute, fix => fix.RevertSpeedMax);
        Assert.Equal("PMD", flagged.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Smax_ThenConstrainedDirectWithTwoCrossingSpeeds_ReArmsPastTheLast(bool lastFixHasAltitude)
    {
        AircraftState ac = Airborne("B738", altitude: 35_000, ias: 250);
        Assert.True(Send(ac, "SMAX").Success);
        (double hecLat, double hecLon) = Assert.IsType<(double Lat, double Lon)>(NavigationDatabase.Instance.GetFixPosition("HEC"));
        (double pmdLat, double pmdLon) = Assert.IsType<(double Lat, double Lon)>(NavigationDatabase.Instance.GetFixPosition("PMD"));
        var altitudes = new Dictionary<int, ConstrainedFixAltitude> { [0] = new(30_000, CrossFixAltitudeType.At) };
        if (lastFixHasAltitude)
        {
            altitudes[1] = new ConstrainedFixAltitude(11_000, CrossFixAltitudeType.At);
        }

        var cmd = new ConstrainedForceDirectToCommand(
            [new ResolvedFix("HEC", hecLat, hecLon), new ResolvedFix("PMD", pmdLat, pmdLon)],
            altitudes,
            new Dictionary<int, int> { [0] = 300, [1] = 250 },
            null
        );
        CommandResult result = CommandDispatcher.Dispatch(cmd, ac, TestDispatch.Context(new SerializableRandom(1)));
        Assert.True(result.Success, result.Message);

        NavigationTarget flagged = Assert.Single(ac.Targets.NavigationRoute, fix => fix.RevertSpeedMax);
        Assert.Equal("PMD", flagged.Name);
        AssertCrossesPmdAt250ThenSmaxIsArmedAgain(ac);
    }

    private void AssertCrossesPmdAt250ThenSmaxIsArmedAgain(AircraftState ac)
    {
        Assert.NotNull(FlyUntilSequenced(ac, "HEC"));
        _output.WriteLine($"Crossed HEC at {ac.IndicatedAirspeed:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.False(ac.Targets.SpeedMaxHeld, "SMAX is armed again past HEC, before PMD's crossing speed");

        double? crossingIas = FlyUntilSequenced(ac, "PMD");
        _output.WriteLine($"Crossed PMD at {crossingIas:F0} KIAS, {ac.Altitude:F0} ft");
        Assert.NotNull(crossingIas);
        Assert.True(crossingIas.Value <= 250 + SpeedSnapKts, $"crossed PMD at {crossingIas:F0} KIAS");
        Assert.True(ac.Targets.SpeedMaxHeld);
    }

    [Fact]
    public void Smax_ReArmOnAFix_SurvivesSnapshotRoundTrip()
    {
        var fix = new NavigationTarget
        {
            Name = "PMD",
            Position = new LatLon(34.632, -118.064),
            RevertSpeedMax = true,
        };
        string json = JsonSerializer.Serialize(fix.ToSnapshot(), RecordingJsonOptions.Default);
        var restored = NavigationTarget.FromSnapshot(JsonSerializer.Deserialize<NavigationTargetDto>(json, RecordingJsonOptions.Default)!);

        Assert.True(restored.RevertSpeedMax);
    }

    [Fact]
    public void Smax_DescentThroughTheChangeover_SwitchesOnce_AndNeverPassesTheEnvelope()
    {
        AircraftState ac = Airborne("B738", altitude: 35_000, ias: 250);
        Assert.True(Send(ac, "SMAX").Success);
        Assert.True(Send(ac, "DM 080").Success);

        (double? Mach, double? Assigned) mode = (ac.Targets.TargetMach, ac.Targets.AssignedSpeed);
        Assert.Equal(((double?)B738SmaxMach, (double?)null), mode);
        var switchAltitudes = new List<double>();
        double worstExcess = double.MinValue;
        FlySeconds(
            ac,
            1_200,
            a =>
            {
                double max = Assert.IsType<double>(AircraftPerformance.MaxOperatingIasKts("B738", a.Altitude));
                worstExcess = Math.Max(worstExcess, a.IndicatedAirspeed - max);
                (double? Mach, double? Assigned) now = (a.Targets.TargetMach, a.Targets.AssignedSpeed);
                if (now != mode)
                {
                    switchAltitudes.Add(a.Altitude);
                    mode = now;
                }
            }
        );

        _output.WriteLine($"Switched at {string.Join(", ", switchAltitudes.Select(alt => alt.ToString("F0")))} ft; worst excess {worstExcess:F1} kt");
        Assert.InRange(ac.Altitude, 7_990, 8_010);
        Assert.Single(switchAltitudes);
        Assert.True(worstExcess <= SpeedSnapKts, $"flew {worstExcess:F1} kt past the type's maximum");
        Assert.Equal(Math.Round(B738SmaxKts), Assert.IsType<double>(ac.Targets.AssignedSpeed));
        Assert.InRange(ac.IndicatedAirspeed, B738SmaxKts - SpeedSnapKts, B738SmaxKts + SpeedSnapKts);
        Assert.True(ac.IndicatedAirspeed > 260, "SMAX flies past 91.117's 250 kt below 10,000 ft");
    }

    private static CommandResult DirectToPmdCrossingAt250(AircraftState aircraft)
    {
        (double lat, double lon) = Assert.IsType<(double Lat, double Lon)>(NavigationDatabase.Instance.GetFixPosition("PMD"));
        var cmd = new ConstrainedForceDirectToCommand(
            [new ResolvedFix("PMD", lat, lon)],
            new Dictionary<int, ConstrainedFixAltitude> { [0] = new(30_000, CrossFixAltitudeType.At) },
            new Dictionary<int, int> { [0] = 250 },
            null
        );
        return CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(new SerializableRandom(1)));
    }

    [Theory]
    [InlineData("CAPP I30")]
    [InlineData("DVIA")]
    public void Smax_EndedBy_ApproachClearanceOrDescendVia(string command)
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState ac = harness.Aircraft;
        SendOk(harness, "SMAX");
        Assert.True(ac.Targets.SpeedMaxHeld);
        Tick(harness, 5);

        SendOk(harness, command);
        Assert.False(ac.Targets.SpeedMaxHeld);
        Assert.Null(ac.Targets.AssignedSpeed);
        Assert.Null(ac.Targets.TargetMach);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);

        Tick(harness, 5);
        Assert.False(ac.Targets.SpeedMaxHeld);
        Assert.Null(ac.Targets.TargetMach);
        Assert.False(ac.Targets.HasExplicitSpeedCommand);
    }

    [Fact]
    public void Smax_HelicopterFliesNineTenthsOfVne()
    {
        double vne = Assert.IsType<double>(AircraftPerformance.MaxOperatingSpeedKts("R22"));
        double smaxKts = Math.Round(0.9 * vne);
        Assert.Null(AircraftPerformance.MaxOperatingMach("R22"));
        AircraftState ac = Airborne("R22", altitude: 3_000, ias: 60);

        Assert.True(Send(ac, "SMAX").Success);
        FlySeconds(ac, 60, _ => { });

        Assert.True(ac.Targets.SpeedMaxHeld);
        Assert.Equal(smaxKts, Assert.IsType<double>(ac.Targets.AssignedSpeed));
        Assert.Null(ac.Targets.TargetMach);
        Assert.InRange(ac.IndicatedAirspeed, smaxKts - SpeedSnapKts, smaxKts + SpeedSnapKts);
    }

    [Fact]
    public void Smax_RefusedOnTheGround()
    {
        AircraftState ac = Airborne("B738", altitude: 0, ias: 0);
        ac.IsOnGround = true;

        CommandResult result = CommandDispatcher.Dispatch(new SpeedMaxCommand(), ac, TestDispatch.Context(new SerializableRandom(1)));

        Assert.False(result.Success);
        Assert.False(ac.Targets.SpeedMaxHeld);
        Assert.Null(ac.Targets.AssignedSpeed);
    }

    [Fact]
    public void Smax_InsideFiveMileFinal_RefusedLikeSpd()
    {
        AircraftState ac = Airborne("B738", altitude: 1_500, ias: 160);
        ac.Phases = new PhaseList { AssignedRunway = TestRunwayFactory.Make(thresholdLat: ac.Position.Lat, thresholdLon: ac.Position.Lon) };
        ac.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });

        CommandResult result = CommandDispatcher.Dispatch(new SpeedMaxCommand(), ac, TestDispatch.Context(new SerializableRandom(1)));

        Assert.False(result.Success);
        Assert.Contains("5nm final", result.Message);
        Assert.Contains("5-7-1.b.4", result.Message);
        Assert.False(ac.Targets.SpeedMaxHeld);
    }

    [Fact]
    public void Smax_SurvivesSnapshotRoundTrip()
    {
        string json = JsonSerializer.Serialize(new ControlTargets { SpeedMaxHeld = true }.ToSnapshot(), RecordingJsonOptions.Default);
        var restored = new ControlTargets();
        ControlTargets.RestoreFrom(JsonSerializer.Deserialize<ControlTargetsDto>(json, RecordingJsonOptions.Default)!, restored);

        Assert.True(restored.SpeedMaxHeld);
    }

    [Fact]
    public void Smax_V37Snapshot_MigratesWithTheFlagFalse()
    {
        const string json = """
            {
              "SchemaVersion": 37,
              "ElapsedSeconds": 10,
              "Rng": { "S0": 1, "S1": 2, "S2": 3, "S3": 4 },
              "Aircraft": [ { "Callsign": "AAL1", "AircraftType": "B738", "Targets": { "HasExplicitSpeedCommand": true, "AssignedSpeed": 250 } } ],
              "Scenario": { "ScenarioId": "t", "ScenarioName": "T", "RngSeed": 1, "ElapsedSeconds": 10, "SimRate": 1 }
            }
            """;
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
        ControlTargetsDto targets = Assert.Single(snapshot.Aircraft).Targets;
        Assert.False(targets.SpeedMaxHeld);
        Assert.Equal(250, targets.AssignedSpeed);
    }

    [Fact]
    public void Smax_ApproachClearanceInTheSameBlock_RestoresIt()
    {
        if (BuildHarness() is not { } harness)
        {
            return;
        }

        AircraftState ac = harness.Aircraft;
        SendOk(harness, "SMAX, CAPP I30");

        Assert.True(ac.Targets.SpeedMaxHeld);
        Assert.True(ac.Targets.HasExplicitSpeedCommand);
    }

    private void SendOk(OakesHarness harness, string command)
    {
        CommandResult result = harness.Engine.SendCommand(harness.Aircraft.Callsign, command);
        _output.WriteLine($"{command} -> {result.Message} (IAS {harness.Aircraft.IndicatedAirspeed:F0})");
        Assert.True(result.Success, $"{command}: {result.Message}");
    }

    private static void Tick(OakesHarness harness, int seconds)
    {
        for (int t = 0; t < seconds; t++)
        {
            harness.Engine.TickOneSecond();
        }
    }

    private OakesHarness? BuildHarness()
    {
        OakesHarness? harness = DescendViaSupersedesEarlierAltitudeTests.BuildOakesHarness(_output);
        if (harness is null)
        {
            _output.WriteLine("KOAK ground layout is unavailable, skipping");
        }

        return harness;
    }
}
