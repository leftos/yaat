using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Unit tests for GitHub issue #161: aircraft pushed onto SFO taxiway A at the
/// D-gates with <c>PUSH A FACE E</c> (heading snaps to 125°) and then given
/// <c>TAXI A F1 B Z S S3 10R</c> traces a small clockwise loop before settling
/// southbound. Root cause is that <see cref="GroundCommandHandler.TryTaxi"/>
/// resolves the start node via <c>FindNearestNode</c> — which picks an
/// absolute-nearest node on the wrong A-RAMP branch (#1251 going NE) instead
/// of a node on the A-RAMP arc the aircraft is actually sitting on (#1253 →
/// #1252 going SE, aligned with the aircraft's heading).
/// </summary>
public class Issue161PushFaceThenTaxiStartNodeTests(ITestOutputHelper output)
{
    // Post-pushback pose from the recorded bundle. After PUSH A FACE E
    // (snapped to 125°), the aircraft rests on the A-RAMP fillet arc near
    // SFO D-gates with its nose pointing toward the SE-bound A continuation.
    private const double PushedLat = 37.61762017;
    private const double PushedLon = -122.37960174;
    private const double PushedHeadingDeg = 125.0;

    private AirportGroundLayout? LoadSfoLayout()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();

        var groundData = new TestAirportGroundData();
        return groundData.GetLayout("SFO");
    }

    private static AircraftState MakeAircraft(double lat, double lon, double headingDeg)
    {
        var ac = new AircraftState
        {
            Callsign = "SKW3404",
            AircraftType = "E75L",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(headingDeg),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "SFO", Destination = "SNA" },
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingAfterPushbackPhase());
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
        };
        ac.Phases.Start(ctx);
        return ac;
    }

    /// <summary>
    /// The resolved route's first segment must have a bearing within 90° of
    /// the aircraft's current heading. If it doesn't, the navigator's
    /// entry-alignment slow-turn pivots &gt;90° before driving — exactly the
    /// "spin" the user reported.
    /// </summary>
    [Fact]
    public void TaxiAfterPushFaceE_FirstSegmentBearingMatchesHeading()
    {
        var layout = LoadSfoLayout();
        if (layout is null)
        {
            return;
        }

        var aircraft = MakeAircraft(PushedLat, PushedLon, PushedHeadingDeg);

        output.WriteLine($"Aircraft pose: ({aircraft.Position.Lat:F6},{aircraft.Position.Lon:F6}) hdg={aircraft.TrueHeading.Degrees:F0}");
        NearestNodeHelper.Log(output, "Pre-taxi", aircraft, layout, count: 5);

        var taxi = new TaxiCommand(Path: ["A", "F1", "B", "Z", "S", "S3"], HoldShorts: [], DestinationRunway: "10R");

        var result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);

        for (int i = 0; i < Math.Min(8, route.Segments.Count); i++)
        {
            var seg = route.Segments[i];
            output.WriteLine($"  [{i}] {seg.FromNodeId} -> {seg.ToNodeId} on {seg.TaxiwayName}");
        }

        var firstSeg = route.Segments[0];
        Assert.True(layout.Nodes.TryGetValue(firstSeg.FromNodeId, out var fromNode), $"FromNode {firstSeg.FromNodeId} missing");
        Assert.True(layout.Nodes.TryGetValue(firstSeg.ToNodeId, out var toNode), $"ToNode {firstSeg.ToNodeId} missing");

        double segBearing = GeoMath.BearingTo(fromNode.Position, toNode.Position);
        double headingDelta = GeoMath.AbsBearingDifference(segBearing, aircraft.TrueHeading.Degrees);

        output.WriteLine(
            $"First segment {firstSeg.FromNodeId} -> {firstSeg.ToNodeId}: bearing={segBearing:F0}, "
                + $"aircraft hdg={aircraft.TrueHeading.Degrees:F0}, delta={headingDelta:F0}"
        );

        Assert.True(
            headingDelta < 90.0,
            $"First segment bearing {segBearing:F0} is {headingDelta:F0}° off the aircraft's heading "
                + $"{aircraft.TrueHeading.Degrees:F0} — the navigator will pivot >90° before driving "
                + "and the aircraft will visibly spin"
        );
    }
}
