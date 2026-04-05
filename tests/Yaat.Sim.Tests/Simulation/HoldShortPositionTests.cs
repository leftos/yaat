using MartinCostello.Logging.XUnit;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for aircraft hold-short positioning with aircraft length offsets.
///
/// Bug: aircraft snap/stop with their center at the hold-short node, so the
/// nose encroaches past the hold-short line (entry) and the tail stays on the
/// runway (exit). Fix: offset by half the aircraft length so the nose is AT
/// the hold-short node on entry and the tail is AT the node on exit.
///
/// Recording: S1-SFO-2 Ground Control 28/01 — UAL300 (B772) pushes from gate,
/// taxis via A F, holds short of runway 1L, then crosses 1L and 1R.
/// </summary>
public class HoldShortPositionTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/hold-short-position-recording.zip";
    private const double DefaultAircraftLengthFt = 60.0;

    private static SessionRecording? LoadRecording() => RecordingLoader.Load(RecordingPath);

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(output).SetMinimumLevel(LogLevel.Debug));
        SimLog.Initialize(loggerFactory);

        return new SimulationEngine(groundData);
    }

    /// <summary>
    /// UAL300 taxis via A F with HS 01L. When it reaches the hold-short node
    /// for runway 1L, the aircraft center must be offset backward so the nose
    /// (center + halfLength along heading) does not cross the hold-short node.
    ///
    /// Before the fix: center snaps to the node → nose extends past by ~50 ft.
    /// After the fix: center is halfLength before the node → nose is AT the node.
    /// </summary>
    [Fact]
    public void UAL300_HoldShortOf1L_NoseDoesNotCrossHoldShortNode()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        // TAXI command issued at t=1329. Replay to that point + some buffer
        // for the aircraft to reach the hold-short.
        engine.Replay(recording, 1329);

        // Tick forward until UAL300 is holding short (speed ≈ 0 at a hold-short node)
        AircraftState? aircraft = null;
        bool foundHoldShort = false;

        for (int t = 1; t <= 300; t++)
        {
            engine.ReplayOneSecond();
            aircraft = engine.FindAircraft("UAL300");
            if (aircraft is null)
            {
                continue;
            }

            // Check if the aircraft is holding short: speed ≈ 0 and has an uncleared hold-short
            var route = aircraft.AssignedTaxiRoute;
            if (route is null)
            {
                continue;
            }

            bool isHoldingShort =
                (aircraft.GroundSpeed < 1.0)
                && route.HoldShortPoints.Any(hs =>
                    !hs.IsCleared
                    && hs.TargetName is not null
                    && (
                        hs.TargetName.Contains("1L", StringComparison.OrdinalIgnoreCase)
                        || hs.TargetName.Contains("01L", StringComparison.OrdinalIgnoreCase)
                    )
                );

            if (isHoldingShort)
            {
                foundHoldShort = true;
                break;
            }
        }

        Assert.True(foundHoldShort, "UAL300 never reached hold-short of 1L within 300 seconds of taxi command");
        Assert.NotNull(aircraft);

        // Find the hold-short node for 1L
        var holdShortPoint = aircraft.AssignedTaxiRoute!.HoldShortPoints.First(hs =>
            !hs.IsCleared && hs.TargetName is not null && hs.TargetName.Contains("1L", StringComparison.OrdinalIgnoreCase)
        );

        var groundData = new TestAirportGroundData();
        var sfoLayout = groundData.GetLayout("SFO");
        Assert.NotNull(sfoLayout);

        var hsNode = sfoLayout.Nodes[holdShortPoint.NodeId];

        // Get aircraft length
        double lengthFt = FaaAircraftDatabase.Get(aircraft.AircraftType)?.LengthFt ?? DefaultAircraftLengthFt;
        double halfLengthNm = (lengthFt / 2.0) / GeoMath.FeetPerNm;

        // The aircraft center must be at least halfLength from the hold-short node
        // (so the nose doesn't cross). Allow 10% tolerance for floating-point drift.
        double distToNodeNm = GeoMath.DistanceNm(aircraft.Latitude, aircraft.Longitude, hsNode.Latitude, hsNode.Longitude);
        double distToNodeFt = distToNodeNm * GeoMath.FeetPerNm;

        output.WriteLine(
            $"UAL300 at ({aircraft.Latitude:F6}, {aircraft.Longitude:F6}), "
                + $"hold-short node {holdShortPoint.NodeId} at ({hsNode.Latitude:F6}, {hsNode.Longitude:F6}), "
                + $"dist={distToNodeFt:F1}ft, halfLength={lengthFt / 2.0:F1}ft, "
                + $"aircraft type={aircraft.AircraftType}, heading={aircraft.TrueHeading}"
        );

        Assert.True(
            distToNodeFt >= (lengthFt / 2.0) * 0.9,
            $"Aircraft center is only {distToNodeFt:F1}ft from hold-short node — "
                + $"nose crosses the hold-short line. Expected >= {(lengthFt / 2.0) * 0.9:F1}ft "
                + $"(half aircraft length {lengthFt / 2.0:F1}ft with 10% tolerance)"
        );
    }

    /// <summary>
    /// Diagnostic test: logs UAL300 position over time from the taxi command
    /// to understand the hold-short behavior.
    /// </summary>
    [Fact]
    public void Diagnostic_UAL300_TaxiAndHoldShort()
    {
        var recording = LoadRecording();
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        engine.Replay(recording, 1329);

        for (int t = 1; t <= 300; t++)
        {
            engine.ReplayOneSecond();
            var ac = engine.FindAircraft("UAL300");
            if (ac is null)
            {
                continue;
            }

            var route = ac.AssignedTaxiRoute;
            string phase = ac.Phases?.CurrentPhase?.Name ?? "none";
            string holdShorts = route is not null
                ? string.Join(", ", route.HoldShortPoints.Select(hs => $"{hs.TargetName}(cleared={hs.IsCleared})"))
                : "no route";

            if (t % 5 == 0 || ac.GroundSpeed < 1.0)
            {
                output.WriteLine(
                    $"t={t} gs={ac.GroundSpeed:F1}kts hdg={ac.TrueHeading} "
                        + $"pos=({ac.Latitude:F6},{ac.Longitude:F6}) "
                        + $"phase={phase} twy={ac.CurrentTaxiway ?? "?"} "
                        + $"holdShorts=[{holdShorts}]"
                );

                if (ac.GroundSpeed < 1.0 && route is not null)
                {
                    // Log distance to all uncleared hold-short nodes
                    var groundData = new TestAirportGroundData();
                    var layout = groundData.GetLayout("SFO");
                    if (layout is not null)
                    {
                        foreach (var hs in route.HoldShortPoints.Where(h => !h.IsCleared))
                        {
                            if (layout.Nodes.TryGetValue(hs.NodeId, out var node))
                            {
                                double dist = GeoMath.DistanceNm(ac.Latitude, ac.Longitude, node.Latitude, node.Longitude);
                                output.WriteLine($"  -> HS node {hs.NodeId} ({hs.TargetName}): dist={dist * GeoMath.FeetPerNm:F1}ft");
                            }
                        }
                    }

                    break; // Stop after first hold-short stop
                }
            }
        }
    }
}
