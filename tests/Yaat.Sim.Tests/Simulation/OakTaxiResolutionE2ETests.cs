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

    /// <summary>
    /// <b>Case 4 — N8312H, t=1849, <c>TAXI F 33 D C B RWY 28R HS 33</c>:</b> the resolved route
    /// back-taxis runway 15/33 as commanded, but the explicit <c>HS 33</c> was silently dropped —
    /// the only hold-short was the 28R destination bar, so the pilot would roll onto 15/33 without
    /// ever stopping. Naming a runway in the path authorizes taxiing onto it, but an explicit
    /// <c>HS</c> for that runway must override the straight-on entry with a hold-short bar at the
    /// entry side, and the response must echo it.
    /// </summary>
    [Fact]
    public void TaxiF33_WithHs33_ArmsHoldShortBeforeEnteringTheRunway()
    {
        var issued = IssueTaxi(1845, "N8312H", "TAXI F 33 D C B RWY 28R HS 33");
        if (issued is null)
        {
            return;
        }

        var (result, route) = issued.Value;
        Assert.True(result.Success, result.Message);

        // The commanded back-taxi on 15/33 is preserved.
        int firstRunwaySegment = route.Segments.FindIndex(s => s.Edge.Edge.IsRunwayCenterline && s.Edge.Edge.MatchesRunway("33"));
        Assert.True(firstRunwaySegment >= 0, $"route must still back-taxi 15/33 as commanded: {route.ToSummary()}");

        // HS 33 arms an uncleared hold-short at the runway entry — at or before the first
        // along-runway segment.
        var hs33 = route.HoldShortPoints.Find(h =>
            (h.Reason == HoldShortReason.ExplicitHoldShort) && (h.TargetName is not null) && h.TargetName.Contains("33", StringComparison.Ordinal)
        );
        if (hs33 is null)
        {
            Assert.Fail(
                $"HS 33 must arm a hold-short; got [{string.Join(", ", route.HoldShortPoints.Select(h => $"{h.TargetName}@{h.NodeId}({h.Reason})"))}]"
            );
            return;
        }

        Assert.False(hs33.IsCleared);
        int hsSegment = route.Segments.FindIndex(s => (s.FromNodeId == hs33.NodeId) || (s.ToNodeId == hs33.NodeId));
        Assert.True(
            hsSegment >= 0 && hsSegment <= firstRunwaySegment,
            $"HS 33 bar (segment {hsSegment}) must sit at or before the runway entry (segment {firstRunwaySegment})"
        );

        // The response echoes the honored hold-short.
        Assert.NotNull(result.Message);
        Assert.Contains("HS", result.Message, StringComparison.Ordinal);
        Assert.Contains("33", result.Message[result.Message.IndexOf("HS", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Case 2 — N124QR, t=1281, <c>TAXI C D @GA1</c>:</b> GA1 parking is a short taxi EAST along
    /// C from the E exit; D is the far-northwest parallel and leads away from it — the commanded
    /// route contradicts the destination. The resolver taxied the full length of C west, took C1
    /// onto runway 10L, back-taxied its entire length, and came back up E and C to the ramp — an
    /// uncommanded runway excursion. It must instead resolve the sane route to GA1 without touching
    /// any runway surface, and warn that the contradictory D was dropped.
    /// </summary>
    [Fact]
    public void TaxiCDToGa1_NeverRoutesAlongARunway_AndWarnsAboutDroppedD()
    {
        var issued = IssueTaxi(1280, "N124QR", "TAXI C D @GA1");
        if (issued is null)
        {
            return;
        }

        var (result, route) = issued.Value;
        Assert.True(result.Success, result.Message);

        // The route must never travel along a runway the controller didn't put in the path.
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.IsRunwayCenterline);

        // It still reaches GA1.
        Assert.Equal("GA1", route.DestinationParking);

        // The unreachable-toward-GA1 taxiway D is dropped with a warning naming it.
        Assert.NotNull(result.Message);
        Assert.Contains(route.Warnings, w => w.Contains("D", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Case 3 — N8312H, t=1869/1874, <c>TAXI D C B RWY 28R</c>:</b> the aircraft was mid-taxi
    /// WESTBOUND on a previous clearance when re-cleared southeast toward 28R. The resolver honored
    /// D in the direction of travel and produced a huge K/F detour flagged "not in authorized
    /// path". The RPO held the aircraft and re-issued — a held aircraft can turn around on the
    /// spot, so the identical command must then resolve to the direct D C B route (which the RPO
    /// only obtained by WARPG-ing the aircraft to a node).
    /// </summary>
    [Fact]
    public void TaxiDCB_WhileHeld_TurnsAroundInsteadOfDetouring()
    {
        var issued = IssueTaxi(1875, "N8312H", "TAXI D C B RWY 28R");
        if (issued is null)
        {
            return;
        }

        var (result, route) = issued.Value;
        Assert.True(result.Success, result.Message);

        // The held aircraft turns around and taxis D C B directly — no unauthorized detour via K/F.
        var traversed = TraversedTaxiways(route);
        Assert.False(traversed.Contains("K"), $"route detours via K: {route.ToSummary()}");
        Assert.False(traversed.Contains("F"), $"route detours via F: {route.ToSummary()}");
        Assert.DoesNotContain(route.Warnings, w => w.Contains("not in the route issued", StringComparison.OrdinalIgnoreCase));

        // And still ends at the 28R departure hold-short.
        Assert.Contains(route.HoldShortPoints, h => (h.Reason == HoldShortReason.DestinationRunway) && (h.TargetName == "28R"));
    }

    /// <summary>
    /// The held aircraft must physically execute the about-face — a clean resolution that then
    /// deadlocks against the aircraft's westbound pose would be no better than the detour.
    /// </summary>
    [Fact]
    public void TaxiDCB_WhileHeld_AircraftActuallyTaxisTheReversedRoute()
    {
        var archive = RecordingLoader.OpenArchive(RecordingPath);
        var engine = BuildEngine();
        if (archive is null || engine is null)
        {
            return;
        }

        using (archive)
        {
            engine.Replay(archive.ToBaseSessionRecording(), 0);
            var snapshot = archive.ReadSnapshotAt(1875);
            Assert.NotNull(snapshot);
            engine.RestoreFromSnapshot(snapshot.State);

            var result = engine.SendCommand("N8312H", "TAXI D C B RWY 28R");
            Assert.True(result.Success, result.Message);

            var ac = engine.FindAircraft("N8312H");
            Assert.NotNull(ac);
            var startPos = ac.Position;

            for (int t = 0; t < 120; t++)
            {
                engine.TickOneSecond();
            }

            var route = ac.Ground.AssignedTaxiRoute;
            Assert.NotNull(route);
            output.WriteLine(
                $"after 120s: segment {route.CurrentSegmentIndex}/{route.Segments.Count}, moved {GeoMath.DistanceNm(startPos, ac.Position) * GeoMath.FeetPerNm:F0} ft"
            );
            Assert.True(route.CurrentSegmentIndex > 5, $"aircraft made no progress on the reversed route (segment {route.CurrentSegmentIndex})");
        }
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
