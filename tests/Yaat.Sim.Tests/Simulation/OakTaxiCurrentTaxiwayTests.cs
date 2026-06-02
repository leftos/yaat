using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E: a TAXI command must infer the taxiway the aircraft is already on rather than
/// requiring the controller to name it or warning about it.
///
/// Recording: S2-OAK-5 (ZOA, OAK). Two aircraft reproduce the same root cause:
///   * JSX170 (arrival) exited 28R onto taxiway W5 and is in HoldingAfterExitPhase.
///     `TAXI W` must succeed — today it fails "Cannot taxi via W … unreachable" because
///     the pathfinder bridges from W5 (capped at 3 hops) instead of starting on W5.
///     The user's workaround was the redundant `TAXI W5 W`.
///   * N157LE (departure) is holding short on taxiway C. `TAXI E RWY 28R` must read
///     back cleanly — today it returns
///     "Taxi via C C - E E RWY 28R [Taxiing via C (not in authorized path)]":
///     a spurious unauthorized-taxiway warning for the taxiway it is already on, and a
///     junction-arc token ("C - E") leaked into the readback by TaxiRoute.ToSummary().
/// </summary>
public class OakTaxiCurrentTaxiwayTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-taxi-current-taxiway-recording.zip";

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Debug).InitializeSimLog();

        return new SimulationEngine(groundData);
    }

    [Fact]
    public void Diagnostic_PreconditionsAtCommandTime()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");

        engine.Replay(recording, 1043);
        var jsx = engine.FindAircraft("JSX170");
        if (jsx is not null)
        {
            output.WriteLine(
                $"JSX170 @1043: phase={jsx.Phases?.CurrentPhase?.Name} currentTwy={jsx.Ground.CurrentTaxiway} "
                    + $"onGround={jsx.IsOnGround} pos=({jsx.Position.Lat:F5},{jsx.Position.Lon:F5}) hdg={jsx.TrueHeading:F0}"
            );
            if (layout is not null)
            {
                NearestNodeHelper.Log(output, "  JSX170:", jsx, layout);
            }
        }
        else
        {
            output.WriteLine("JSX170 @1043: NOT FOUND");
        }

        engine.Replay(recording, 1894);
        var n157 = engine.FindAircraft("N157LE");
        if (n157 is not null)
        {
            output.WriteLine(
                $"N157LE @1894: phase={n157.Phases?.CurrentPhase?.Name} currentTwy={n157.Ground.CurrentTaxiway} "
                    + $"onGround={n157.IsOnGround} pos=({n157.Position.Lat:F5},{n157.Position.Lon:F5}) hdg={n157.TrueHeading:F0}"
            );
            if (layout is not null)
            {
                NearestNodeHelper.Log(output, "  N157LE:", n157, layout);
            }
        }
        else
        {
            output.WriteLine("N157LE @1894: NOT FOUND");
        }
    }

    [Fact]
    public void Jsx170_TaxiW_FromW5_Succeeds()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1043);
        var aircraft = engine.FindAircraft("JSX170");
        Assert.NotNull(aircraft);

        // Precondition: aircraft exited onto W5 and is holding after exit.
        Assert.IsType<HoldingAfterExitPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal("W5", aircraft.Ground.CurrentTaxiway);

        var result = engine.SendCommand("JSX170", "TAXI W");
        output.WriteLine($"TAXI W response: success={result.Success} msg={result.Message}");

        Assert.True(result.Success, $"TAXI W should succeed from W5 but failed: {result.Message}");
    }

    [Fact]
    public void N157LE_TaxiE_FromC_NoWarningNoJunctionArc()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1894);
        var aircraft = engine.FindAircraft("N157LE");
        Assert.NotNull(aircraft);

        // Precondition: aircraft is on taxiway C.
        Assert.Equal("C", aircraft.Ground.CurrentTaxiway);

        var result = engine.SendCommand("N157LE", "TAXI E RWY 28R");
        output.WriteLine($"TAXI E RWY 28R response: success={result.Success} msg={result.Message}");

        Assert.True(result.Success, $"TAXI E RWY 28R should succeed but failed: {result.Message}");

        // No spurious "not in authorized path" warning for taxiway C (the aircraft is on it).
        Assert.DoesNotContain("not in authorized path", result.Message, StringComparison.OrdinalIgnoreCase);

        // No junction/membership arc token ("C - E") in the readback.
        Assert.DoesNotContain(" - ", result.Message);

        // The route reaches taxiway E and has a destination hold-short for 28R.
        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.Contains(route.Segments, s => s.TaxiwayName.Equals("E", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(route.HoldShortPoints, h => h.Reason == HoldShortReason.DestinationRunway && (h.TargetName?.Contains("28R") ?? false));
    }
}
