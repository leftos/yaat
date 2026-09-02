using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// S2-OAK-2 bundle, trimmed to its first 330 s (<c>oak-u-w-fillet-corner-recording.zip</c>): SWA2600 (B738) pushes
/// back from gate 20 onto TE at t=200 and, when its preset WAIT expires at t≈235, taxis <c>TE U W W1</c> to runway 30.
/// In the recording the U→W corner was resolved through the junction centre (node 17) and flown as a square pivot:
/// 3 kt through the turn, then a swing back onto W. The route must turn over the fillet arc 694→691 and the
/// navigator must play that arc at its cornering speed in one monotonic sweep.
/// </summary>
public class OakUwFilletCornerTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/oak-u-w-fillet-corner-recording.zip";
    private const string Callsign = "SWA2600";
    private const int ArcEntryNode = 694;
    private const int JunctionCentreNode = 17;
    private const int ArcExitNode = 691;

    /// <summary>The taxi route is resolved when the preset WAIT expires (t≈235 in the recording).</summary>
    private const int RouteResolvedBySeconds = 240;

    /// <summary>SWA2600 has cleared the U/W corner and is on W well before this.</summary>
    private const int CornerWindowEndSeconds = 330;

    /// <summary>Well above the 3 kt nose-wheel pivot, below the fillet's ~9 kt arc speed for a jet.</summary>
    private const double MinCornerSpeedKts = 6.0;

    /// <summary>The corner is one left turn; any step to the right beyond this is a swing back onto W.</summary>
    private const double MaxHeadingReversalDeg = 3.0;

    [Fact]
    public void Swa2600_TeUWW1_FliesTheUwFilletAtArcSpeed()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        TestVnasData.EnsureInitialized();
        if (recording is null || TestVnasData.NavigationDb is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("GroundNavigator", LogLevel.Debug).InitializeSimLog();
        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.Replay(recording, 0);
        for (int t = 1; t <= RouteResolvedBySeconds; t++)
        {
            engine.ReplayOneSecond();
        }

        var aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine(route.ToSummary());

        var corner = Assert.Single(route.Segments, s => s.FromNodeId == ArcEntryNode && s.ToNodeId == ArcExitNode);
        Assert.IsType<GroundArc>(corner.Edge.Edge);
        Assert.DoesNotContain(route.Segments, s => s.ToNodeId == JunctionCentreNode);
        RouteGeometryAsserts.AssertNoSquarePivotWhereFilletExists(route, Callsign);

        var recorder = new TickRecorder(aircraft);
        var cornerHeadings = new List<double>();
        double minCornerIas = double.MaxValue;
        for (int t = RouteResolvedBySeconds + 1; t <= CornerWindowEndSeconds; t++)
        {
            engine.ReplayOneSecond();
            recorder.Record(t);
            var current = aircraft.Ground.AssignedTaxiRoute;
            int arcIndex = current?.Segments.FindIndex(s => s.FromNodeId == ArcEntryNode && s.ToNodeId == ArcExitNode) ?? -1;
            if (current is null || arcIndex < 0)
            {
                break;
            }

            int idx = current.CurrentSegmentIndex;
            int target = idx < current.Segments.Count ? current.Segments[idx].ToNodeId : -1;
            output.WriteLine(
                $"t={t, 3} ias={aircraft.IndicatedAirspeed, 5:F1} hdg={aircraft.TrueHeading.Degrees, 5:F1} seg={idx}/{current.Segments.Count} -> {target}"
            );
            if (idx >= arcIndex - 1 && idx <= arcIndex + 1)
            {
                cornerHeadings.Add(aircraft.TrueHeading.Degrees);
                minCornerIas = Math.Min(minCornerIas, aircraft.IndicatedAirspeed);
            }
        }

        string tickPath = Path.Combine(TickRecorder.FindRepoRoot(), ".tmp", "oak-uw-fillet-corner-ticks.json");
        recorder.WriteJson(tickPath);
        output.WriteLine($"ticks: {tickPath}");

        Assert.True(cornerHeadings.Count > 0, $"{Callsign} never reached the U/W corner before t={CornerWindowEndSeconds}");
        Assert.True(
            minCornerIas >= MinCornerSpeedKts,
            $"slowest through the U/W fillet was {minCornerIas:F1} kt (expected >= {MinCornerSpeedKts} kt — a nose-wheel pivot, not the fillet)"
        );

        double worstReversalDeg = 0;
        for (int i = 1; i < cornerHeadings.Count; i++)
        {
            worstReversalDeg = Math.Max(worstReversalDeg, GeoMath.SignedBearingDifference(cornerHeadings[i - 1], cornerHeadings[i]));
        }

        Assert.True(
            worstReversalDeg <= MaxHeadingReversalDeg,
            $"heading swung back {worstReversalDeg:F1}° to the right inside the U/W corner (the turn is one left sweep)"
        );
    }
}
