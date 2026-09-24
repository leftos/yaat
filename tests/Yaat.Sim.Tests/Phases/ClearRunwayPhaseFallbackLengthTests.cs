using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Phases;

/// <summary>
/// <see cref="ClearRunwayPhase"/> (<c>CLRWY</c>) pulls the aircraft ½ its length past the runway holding position so
/// every part of it has crossed the marking (AIM 2-3-5.a.1). A type the FAA database does not carry takes its length
/// from the CWT fallback, the same source the runway exit and the landing roll use.
/// </summary>
public class ClearRunwayPhaseFallbackLengthTests(ITestOutputHelper output)
{
    /// <summary>SFO taxiway M, west-side hold-short of 01L/19R.</summary>
    private const int MWestHoldShortNodeId = 882;

    /// <summary>SFO taxiway M, east-side hold-short of 01L/19R — the runway holding position the aircraft pulls clear of.</summary>
    private const int MEastHoldShortNodeId = 883;

    private const string UnknownType = "ZZZZ";

    [Fact]
    public void UnknownType_TargetOffsetUsesCwtFallbackLength()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        Assert.Null(FaaAircraftDatabase.Get(UnknownType));
        Assert.Null(WakeTurbulenceData.GetCwt(UnknownType));
        double expectedHalfFt = HoldShortAnnotator.CwtFallbackLengthFt(UnknownType) / 2.0;
        Assert.NotEqual(30.0, expectedHalfFt);

        Assert.True(layout.Nodes.TryGetValue(MWestHoldShortNodeId, out GroundNode? westNode), $"SFO layout has no node {MWestHoldShortNodeId}");
        Assert.True(layout.Nodes.TryGetValue(MEastHoldShortNodeId, out GroundNode? runwayNode), $"SFO layout has no node {MEastHoldShortNodeId}");
        Assert.Equal(GroundNodeType.RunwayHoldShort, runwayNode.Type);

        var aircraft = new AircraftState
        {
            Callsign = "N123ZZ",
            AircraftType = UnknownType,
            Position = westNode.Position,
            TrueHeading = new TrueHeading(GeoMath.BearingTo(westNode.Position, runwayNode.Position)),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KSFO", Destination = "KLAX" },
        };

        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, new TaxiCommand(Path: ["M"], HoldShorts: [], DestinationRunway: null), layout);
        Assert.True(result.Success, $"TAXI M failed: {result.Message}");
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        // The approach node is the route node on the runway side of the crossed hold-short, as CLRWY picks it.
        TaxiRouteSegment? intoBar = route.Segments.FirstOrDefault(s => s.ToNodeId == MEastHoldShortNodeId);
        Assert.NotNull(intoBar);
        int approachNodeId = intoBar.FromNodeId;

        aircraft.Position = runwayNode.Position;
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.25,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };

        var phase = new ClearRunwayPhase(MEastHoldShortNodeId, approachNodeId);
        phase.OnStart(ctx);

        TaxiRoute? clearance = phase.ClearanceRoute;
        Assert.NotNull(clearance);
        Assert.Equal(MEastHoldShortNodeId, clearance.Segments[0].FromNodeId);
        double pastBarFt = clearance.Segments.Sum(s => s.Edge.DistanceNm) * GeoMath.FeetPerNm;
        output.WriteLine($"approach node {approachNodeId}; target is {pastBarFt:F1}ft past the bar; expected {expectedHalfFt:F1}ft");

        Assert.Equal(expectedHalfFt, pastBarFt, 0.5);
    }
}
