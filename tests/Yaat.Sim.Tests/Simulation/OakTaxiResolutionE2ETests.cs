using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for four taxi-resolution defects observed in one OAK RPO session
/// (S1-OAK-7 "Evaluation Preparation" bundle, client 0.12.6-beta).
///
/// <para><b>Case 1 — N622JQ, t=2257, <c>TAXI C D</c>:</b> the aircraft had landed 28R and was
/// holding after the E exit (node 510, north of the runway). The live server responded
/// "Taxi via G C" — naming taxiway G, which the aircraft never touches — while the installed
/// route recorded in the very next snapshot ran E → C → the <em>full length</em> of D to node 426.
/// Intended behavior: with no onward target after the trailing taxiway D, the route stops at the
/// C/D intersection (node 349), the response names the taxiways actually traversed, and it says
/// explicitly where the aircraft will hold.</para>
/// </summary>
public class OakTaxiResolutionE2ETests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-taxi-resolution-recording.zip";

    /// <summary>C∩D junction on the OAK north field — where "TAXI C D" with no destination stops.</summary>
    private const int JunctionCD = 349;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundCommandHandler", LogLevel.Debug)
            .EnableCategory("TaxiPathfinder", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    /// <summary>
    /// Restores the recording at <paramref name="restoreAtSeconds"/> and issues
    /// <paramref name="command"/> to <paramref name="callsign"/>, returning the command result and
    /// the installed route.
    /// </summary>
    private (CommandResult Result, TaxiRoute Route)? IssueTaxi(int restoreAtSeconds, string callsign, string command)
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            return null;
        }

        using (archive)
        {
            var recording = archive.ToBaseSessionRecording();
            engine.Replay(recording, 0);

            var snapshot = archive.ReadSnapshotAt(restoreAtSeconds);
            Assert.NotNull(snapshot);
            engine.RestoreFromSnapshot(snapshot.State);

            var result = engine.SendCommand(callsign, command);
            output.WriteLine($"{callsign} '{command}' -> success={result.Success} message: {result.Message}");

            var ac = engine.FindAircraft(callsign);
            Assert.NotNull(ac);
            var route = ac.Ground?.AssignedTaxiRoute;
            Assert.NotNull(route);
            output.WriteLine($"installed route: {route.Segments.Count} segments, summary: {route.ToSummary()}");
            output.WriteLine($"segments: {string.Join(" ", route.Segments.Select(s => $"{s.FromNodeId}-{s.ToNodeId}({s.TaxiwayName})"))}");
            return (result, route);
        }
    }

    /// <summary>Distinct letter-only taxiway names traversed by the route, decomposing junction arcs.</summary>
    private static HashSet<string> TraversedTaxiways(TaxiRoute route)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in route.Segments)
        {
            if (seg.Edge.Edge is GroundArc arc)
            {
                foreach (var n in arc.TaxiwayNames)
                {
                    names.Add(n);
                }
            }
            else
            {
                names.Add(seg.TaxiwayName);
            }
        }

        return names;
    }

    [Fact]
    public void TaxiCD_AfterExit_ResponseNamesOnlyTraversedTaxiways()
    {
        var issued = IssueTaxi(2255, "N622JQ", "TAXI C D");
        if (issued is null)
        {
            return;
        }

        var (result, route) = issued.Value;
        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.Message);

        // The response's route summary must only name taxiways the installed route actually
        // touches — the recorded bug echoed "Taxi via G C" for a route that never goes near G.
        var traversed = TraversedTaxiways(route);
        string summaryPart = result.Message["Taxi via ".Length..];
        int cutoff = summaryPart.IndexOfAny(['[', '(', '—']);
        if (cutoff >= 0)
        {
            summaryPart = summaryPart[..cutoff];
        }

        foreach (var token in summaryPart.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('@') || token.StartsWith('$') || token is "on" or "RWY" or "HS")
            {
                continue;
            }

            Assert.True(
                traversed.Contains(token),
                $"response names taxiway '{token}' but the installed route never touches it. "
                    + $"Response: {result.Message} | route: {route.ToSummary()}"
            );
        }
    }

    [Fact]
    public void TaxiCD_AfterExit_StopsAtTheCDJunction()
    {
        var issued = IssueTaxi(2255, "N622JQ", "TAXI C D");
        if (issued is null)
        {
            return;
        }

        var (_, route) = issued.Value;

        // "TAXI C D" gives no onward target after D, so the route must stop where C meets D —
        // not walk the full length of D to its far end at the 15/33 boundary.
        Assert.Equal(JunctionCD, route.Segments[^1].ToNodeId);
    }

    [Fact]
    public void TaxiCD_AfterExit_ResponseSaysWhereTheAircraftWillHold()
    {
        var issued = IssueTaxi(2255, "N622JQ", "TAXI C D");
        if (issued is null)
        {
            return;
        }

        var (result, _) = issued.Value;
        Assert.NotNull(result.Message);
        Assert.Contains("C/D intersection", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
