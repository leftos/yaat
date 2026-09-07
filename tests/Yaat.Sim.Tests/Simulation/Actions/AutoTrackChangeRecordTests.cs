using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// <see cref="RecordedAutoTrackChange"/> as a recorded input: a CRC <c>.AUTOTRACK</c> delta applied through the one
/// Sim body every run kind uses. Issuing one creates the caller's roster entry if the scenario had none, steals each
/// named airport from whoever held it, claims the airborne untracked departures that now match, and lands in the
/// action log so a rewind or a from-scratch reconstruction reproduces it. Real ZOA positions from the committed
/// config snapshot.
/// </summary>
public class AutoTrackChangeRecordTests
{
    private const string OakTwrPositionId = "01GEAMB98RKCPP9HCNPW5AVDA5"; // OAK_TWR (3O)
    private const string SfoDepPositionId = "01GEAS6JN7VA6A676WY55NY2DT"; // SFO_DEP (4U)
    private const string OakAppPositionId = "01HAZMB6QJANHSNTSG7HYAJPE8"; // OAK_APP (3Y)

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AutoTrackChangeRecordTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// The bare engine over the parked-at-OAK fixture with the ZOA config and a roster in which SFO_DEP alone
    /// auto-tracks KOAK — the shape the reported bug had (a scenario default the CRC caller must take over).
    /// </summary>
    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        engine.Scenario!.AtcPositions.Add(Resolve(_zoa, SfoDepPositionId, ["KOAK"]));
        return engine;
    }

    private static ResolvedAtcPosition Resolve(ArtccConfigRoot config, string positionId, List<string> autoTrackAirportIds) =>
        new()
        {
            Source = new ScenarioAtc
            {
                Id = positionId,
                ArtccId = "ZOA",
                PositionId = positionId,
                AutoTrackAirportIds = autoTrackAirportIds,
            },
            Owner = config.ResolvePosition(positionId)!,
            Tcp = config.GetTcpForPosition(positionId),
        };

    /// <summary>The fixture's C172 lifted off OAK: airborne, above the floor, untracked, filed out of KOAK.</summary>
    private static AircraftState AirborneDeparture(SimulationEngine engine)
    {
        var aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        aircraft.IsOnGround = false;
        aircraft.Altitude = 3000;
        return aircraft;
    }

    private static List<string> CaptureTerminal(SimulationEngine engine)
    {
        var lines = new List<string>();
        engine.TerminalEntryEmitted += entry => lines.Add(entry.Message);
        return lines;
    }

    private static List<string> AirportsFor(SimulationEngine engine, string positionId) =>
        engine.Scenario!.AtcPositions.Single(p => p.Source.PositionId == positionId).Source.AutoTrackAirportIds;

    [Fact]
    public void RoundTripsThePolymorphicSerializer()
    {
        RecordedAction record = new RecordedAutoTrackChange(12, OakTwrPositionId, ["KOAK", "-KSFO", "none"]);

        var json = JsonSerializer.Serialize(record, RecordingJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<RecordedAction>(json, RecordingJsonOptions.Default);

        var change = Assert.IsType<RecordedAutoTrackChange>(restored);
        Assert.Equal(12, change.ElapsedSeconds);
        Assert.Equal(OakTwrPositionId, change.PositionId);
        Assert.Equal(["KOAK", "-KSFO", "none"], change.Entries);
        Assert.Contains("\"AutoTrackChange\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void IssueDerivedCreatesTheRosterEntryStealsTheAirportAndClaimsAnAirborneDeparture()
    {
        if (_zoa is null || Engine() is not { } engine)
        {
            return;
        }

        var aircraft = AirborneDeparture(engine);
        var lines = CaptureTerminal(engine);

        var result = engine.Actions.IssueDerived(new RecordedAutoTrackChange(engine.Scenario!.ElapsedSeconds, OakTwrPositionId, ["KOAK"]));

        Assert.True(result.Success, result.Message);
        Assert.Equal(["KOAK"], AirportsFor(engine, OakTwrPositionId));
        Assert.Empty(AirportsFor(engine, SfoDepPositionId));

        var oakTwr = _zoa.ResolvePosition(OakTwrPositionId)!;
        Assert.NotNull(aircraft.Track.Owner);
        Assert.True(aircraft.Track.Owner!.MatchesPosition(oakTwr));

        var recorded = Assert.IsType<RecordedAutoTrackChange>(engine.Scenario.ActionLog[^1]);
        Assert.Equal(OakTwrPositionId, recorded.PositionId);
        Assert.Contains(
            lines,
            line =>
                line.StartsWith("Auto-track airports for", StringComparison.Ordinal)
                && line.Contains(oakTwr.Callsign, StringComparison.Ordinal)
                && line.EndsWith(": KOAK", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void NegativeEntryRemovesGloballyAndNoneClearsTheCallerOnly()
    {
        if (_zoa is null || Engine() is not { } engine)
        {
            return;
        }

        var scenario = engine.Scenario!;
        scenario.AtcPositions.Add(Resolve(_zoa, OakAppPositionId, ["KSFO"]));

        engine.Actions.IssueDerived(new RecordedAutoTrackChange(scenario.ElapsedSeconds, OakTwrPositionId, ["-KOAK"]));
        Assert.Empty(AirportsFor(engine, SfoDepPositionId));
        Assert.Empty(AirportsFor(engine, OakTwrPositionId));
        Assert.Equal(["KSFO"], AirportsFor(engine, OakAppPositionId));

        engine.Actions.IssueDerived(new RecordedAutoTrackChange(scenario.ElapsedSeconds, OakTwrPositionId, ["KOAK"]));
        Assert.Equal(["KOAK"], AirportsFor(engine, OakTwrPositionId));

        engine.Actions.IssueDerived(new RecordedAutoTrackChange(scenario.ElapsedSeconds, OakTwrPositionId, ["none"]));
        Assert.Empty(AirportsFor(engine, OakTwrPositionId));
        Assert.Equal(["KSFO"], AirportsFor(engine, OakAppPositionId));
    }

    /// <summary>
    /// A position the room's ARTCC config cannot place and the roster does not hold is refused, not invented: the
    /// delta has no target, so nothing is applied and nothing reaches the log.
    /// </summary>
    [Fact]
    public void AnUnresolvablePositionIsRefusedAndNotLogged()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var scenario = engine.Scenario!;
        scenario.ArtccConfig = null;
        int before = scenario.ActionLog.Count;

        var result = engine.Actions.IssueDerived(new RecordedAutoTrackChange(scenario.ElapsedSeconds, OakTwrPositionId, ["KOAK"]));

        Assert.False(result.Success);
        Assert.Equal(before, scenario.ActionLog.Count);
        Assert.DoesNotContain(scenario.AtcPositions, p => p.Source.PositionId == OakTwrPositionId);
        Assert.Equal(["KOAK"], AirportsFor(engine, SfoDepPositionId));
    }

    [Fact]
    public void ApplyRecordedReproducesTheRoster()
    {
        if (_zoa is null || Engine() is not { } engine)
        {
            return;
        }

        var aircraft = AirborneDeparture(engine);
        var record = new RecordedAutoTrackChange(engine.Scenario!.ElapsedSeconds, OakTwrPositionId, ["KOAK"]);

        var result = engine.Actions.ApplyRecorded(record);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["KOAK"], AirportsFor(engine, OakTwrPositionId));
        Assert.Empty(AirportsFor(engine, SfoDepPositionId));
        Assert.True(aircraft.Track.Owner!.MatchesPosition(_zoa.ResolvePosition(OakTwrPositionId)!));
        Assert.Empty(engine.Scenario.ActionLog.OfType<RecordedAutoTrackChange>());
    }
}
