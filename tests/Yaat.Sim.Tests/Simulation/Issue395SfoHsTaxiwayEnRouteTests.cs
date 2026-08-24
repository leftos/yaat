using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #395: a taxiway hold-short target that the cleared taxiways already
/// cross en route must not be appended to the path as a routing waypoint.
///
/// Recording: S2-SFO-3 | High Intensity — SKW6887 (CRJ7) spawns at parking F5 at t=37 with the
/// preset <c>TAXI T7A A A1 1R HS H</c>. Taxiway A crosses H on the way to A1, so the controller
/// meant "T7A, A, A1 to runway 1R, hold short of H on the way". The HS-taxiway fold
/// (<c>GroundCommandHandler.AugmentPathWithHoldShortTaxiways</c>, added for OAK
/// <c>TAXI D C HS E RWY 28R</c> where E lies BEYOND the last cleared taxiway) appended H after A1,
/// and the pathfinder obliged: A1 → A2 → M1 → A → H → the 1R bar on H — 170 segments crossing
/// 01L/19R twice, echoed as <c>T7A A A1 A2 M1 A H HS H RWY 1R</c>. The aircraft held short of H
/// correctly, then after RES flew the loop.
///
/// The fix resolves the clearance as cleared first and only folds the HS taxiway when the
/// as-cleared route cannot bind the target en route, reach the destination, and continue on a
/// cleared taxiway past the hold-short. The T-junction shape behind that last criterion (the
/// cleared path merely touches the HS taxiway and then reaches the runway over free numbered
/// pavement) has no known reproduction on the OAK/SFO layouts; it is guarded by construction only.
/// </summary>
public class Issue395SfoHsTaxiwayEnRouteTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue395-sfo-hs-taxiway-en-route-recording.zip";
    private const string Callsign = "SKW6887";
    private const int JustAfterSpawn = 40;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private static AirportGroundLayout? LoadSfo()
    {
        TestVnasData.EnsureInitialized();
        string path = Path.Combine("TestData", "sfo.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("SFO", File.ReadAllText(path), null) : null;
    }

    private static List<string> SegmentTaxiways(TaxiRoute route) =>
        route.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void LogRoute(string label, TaxiRoute route)
    {
        output.WriteLine($"[{label}] segments={route.Segments.Count} sequence=\"{route.FormatTaxiwaySequence()}\" summary=\"{route.ToSummary()}\"");
        output.WriteLine($"[{label}] taxiways=[{string.Join(", ", SegmentTaxiways(route))}]");
        output.WriteLine($"[{label}] warnings=[{string.Join(" | ", route.Warnings)}]");
        foreach (var hs in route.HoldShortPoints)
        {
            output.WriteLine($"[{label}]   HS node={hs.NodeId} target={hs.TargetName} reason={hs.Reason}");
        }
    }

    /// <summary>
    /// The route the controller meant: T7A, A, A1 to the 1R bar, holding short of H on A. No
    /// detour via A2/M1/H, no runway crossing.
    /// </summary>
    private static void AssertCleanRouteToRunway1R(TaxiRoute route)
    {
        Assert.Equal("T7A A A1", route.FormatTaxiwaySequence());

        var taxiways = SegmentTaxiways(route);
        Assert.DoesNotContain("A2", taxiways, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("M1", taxiways, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("H", taxiways, StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(route.HoldShortPoints, hs => hs.Reason == HoldShortReason.RunwayCrossing);
        Assert.Single(
            route.HoldShortPoints,
            hs => (hs.Reason == HoldShortReason.ExplicitHoldShort) && string.Equals(hs.TargetName, "H", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            route.HoldShortPoints,
            hs => (hs.Reason == HoldShortReason.DestinationRunway) && string.Equals(hs.TargetName, "1R", StringComparison.OrdinalIgnoreCase)
        );

        string summary = route.ToSummary();
        Assert.Contains("HS H", summary, StringComparison.Ordinal);
        Assert.Contains("RWY 1R", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(route.Warnings, w => w.Contains("not in the route issued", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Skw6887_HsH_RouteStaysOnAToA1()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, JustAfterSpawn);

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        LogRoute("replay", route);

        AssertCleanRouteToRunway1R(route);
    }

    [Fact]
    public void Skw6887_HoldsShortOfH_ThenResumesToRunway1R()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, JustAfterSpawn);
        Assert.NotNull(engine.FindAircraft(Callsign));

        HoldingShortPhase? holdShortH = null;
        for (int t = 0; t < 300; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            Assert.False(
                ac.Phases?.CurrentPhase is CrossingRunwayPhase,
                $"Entered a runway crossing before holding short of H at t={JustAfterSpawn + t}"
            );

            if (ac.Phases?.CurrentPhase is HoldingShortPhase hs && string.Equals(hs.HoldShort.TargetName, "H", StringComparison.OrdinalIgnoreCase))
            {
                holdShortH = hs;
                output.WriteLine($"{Callsign} holding short of H after {t}s");
                break;
            }
        }

        Assert.NotNull(holdShortH);

        var result = engine.SendCommand(Callsign, "RES");
        Assert.True(result.Success, $"RES failed: {result.Message}");

        // Keep replaying the recording's other actions (the LUAW/CTO cycle that drains the 1R
        // departure queue ahead of SKW6887) — TickOneSecond would leave that queue frozen.
        HoldingShortPhase? holdShort1R = null;
        for (int t = 0; t < 600; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            Assert.False(ac.Phases?.CurrentPhase is CrossingRunwayPhase, $"Entered a runway crossing on the way to 1R at +{t}s after RES");

            if (t % 60 == 0)
            {
                var route = ac.Ground.AssignedTaxiRoute;
                output.WriteLine(
                    $"+{t}s phase={ac.Phases?.CurrentPhase?.GetType().Name} ias={ac.IndicatedAirspeed:F0} twy={ac.Ground.CurrentTaxiway} "
                        + $"seg={route?.CurrentSegmentIndex}/{route?.Segments.Count} queue={ac.Ground.RunwayQueuePosition} yield={ac.Ground.AutoYieldTarget}"
                );
            }

            if (ac.Phases?.CurrentPhase is HoldingShortPhase hs && string.Equals(hs.HoldShort.TargetName, "1R", StringComparison.OrdinalIgnoreCase))
            {
                holdShort1R = hs;
                output.WriteLine($"{Callsign} holding short of 1R {t}s after RES");
                break;
            }
        }

        Assert.NotNull(holdShort1R);
    }

    /// <summary>
    /// Layout-level twin of the replay test — the same clearance issued from parking F5 on the real
    /// SFO layout, with no recording and no node ids.
    /// </summary>
    [Fact]
    public void TaxiT7aAA1_1R_HsH_FromF5_DoesNotDetourViaH()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var f5 = layout.FindParkingByName("F5");
        Assert.NotNull(f5);

        var ac = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "CRJ7",
            Position = f5.Position,
            TrueHeading = new TrueHeading(349),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "SFO" },
        };
        ac.Phases = new PhaseList();

        var parsed = CommandParser.Parse("TAXI T7A A A1 1R HS H");
        Assert.True(parsed.IsSuccess, $"parse failed: {parsed.Reason}");
        var taxi = Assert.IsType<TaxiCommand>(parsed.Value);

        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout);
        Assert.True(result.Success, $"TAXI failed: {result.Message}");
        var route = ac.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        LogRoute("layout", route);

        AssertCleanRouteToRunway1R(route);
    }
}
