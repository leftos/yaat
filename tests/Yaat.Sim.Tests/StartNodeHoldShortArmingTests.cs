using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Runtime arming of a hold-short on the taxi route's own start node (the second half of
/// issue #316). The materializer binds the bar an aircraft is re-routed from
/// (<c>segments[0].FromNodeId</c> — SFO's F bar for a <c>TAXI F C HS 10R</c>), but
/// <c>ArriveAtNode</c> can never fire for that node — it is no segment's ToNode — so
/// <c>TaxiingPhase.TryHoldAtRouteStartNode</c> must take the stop.
///
/// The gap: the check ran once, on the phase's first tick, gated on being within 150 ft of
/// the bar. An aircraft re-routed while still rolling toward the bar from farther out (a
/// runway exit hand-off whose nearest node is the bar on a long sparse stretch) sailed
/// through the gate's one shot and taxied across the runway without a crossing clearance.
/// </summary>
public sealed class StartNodeHoldShortArmingTests(ITestOutputHelper output)
{
    private static AirportGroundLayout? LoadSfo()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout("SFO");
    }

    private static AircraftState Aircraft(LatLon position, double heading, double ias)
    {
        return new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            IsOnGround = true,
            IndicatedAirspeed = ias,
            FlightPlan = new AircraftFlightPlan { Departure = "KSFO" },
        };
    }

    private static PhaseContext Context(AircraftState aircraft, AirportGroundLayout layout)
    {
        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 0.25,
            GroundLayout = layout,
            AircraftLookup = null,
            Logger = NullLogger.Instance,
        };
    }

    private void AssertHoldsShortOf10R(double startOffsetFt, double startIasKts)
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var nearBar = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "10R", "F");
        var farBar = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "10R", "C");
        if (nearBar is null || farBar is null)
        {
            return;
        }

        var route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            fromNodeId: nearBar.Id,
            taxiwayNames: ["F", "C"],
            out string? failReason,
            new ExplicitPathOptions
            {
                AirportId = "SFO",
                ExplicitHoldShorts = [HoldShortTarget.Parse("10R")],
                DestinationRunway = "28R",
            },
            AircraftCategory.Jet
        );
        Assert.Null(failReason);
        Assert.NotNull(route);
        Assert.Contains(route.HoldShortPoints, h => (h.NodeId == nearBar.Id) && !h.IsCleared);

        // Place the aircraft short of the bar along the route's own departure axis, pointed at it.
        double departureBearing = route.Segments[0].Edge.DepartureBearing;
        var position = GeoMath.ProjectPoint(nearBar.Position, new TrueHeading((departureBearing + 180.0) % 360.0), startOffsetFt / GeoMath.FeetPerNm);

        var aircraft = Aircraft(position, departureBearing, startIasKts);
        aircraft.Ground.AssignedTaxiRoute = route;
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new TaxiingPhase());
        var ctx = Context(aircraft, layout);
        aircraft.Phases.Start(ctx);

        double barSpanFt = GeoMath.DistanceNm(nearBar.Position, farBar.Position) * GeoMath.FeetPerNm;
        double minFarBarFt = double.MaxValue;
        for (int i = 0; i < 1200; i++)
        {
            // Mirror the engine's split: phases steer (heading + IAS targets), physics integrates.
            PhaseRunner.Tick(aircraft, ctx);
            FlightPhysics.Update(aircraft, ctx.DeltaSeconds);
            minFarBarFt = Math.Min(minFarBarFt, GeoMath.DistanceNm(aircraft.Position, farBar.Position) * GeoMath.FeetPerNm);
            if (i % 100 == 0)
            {
                output.WriteLine(
                    $"t={i * 0.25:F0}s phase={aircraft.Phases.CurrentPhase?.GetType().Name} ias={aircraft.IndicatedAirspeed:F1} "
                        + $"hdg={aircraft.TrueHeading.Degrees:F0} segIdx={route.CurrentSegmentIndex} "
                        + $"distBar={GeoMath.DistanceNm(aircraft.Position, nearBar.Position) * GeoMath.FeetPerNm:F0}"
                );
            }

            if (aircraft.Phases.CurrentPhase is HoldingShortPhase)
            {
                break;
            }
        }

        double distFromBarFt = GeoMath.DistanceNm(aircraft.Position, nearBar.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"final phase={aircraft.Phases.CurrentPhase?.GetType().Name ?? "null"} ias={aircraft.IndicatedAirspeed:F1} "
                + $"distFromNearBar={distFromBarFt:F0} ft, closest approach to far bar {minFarBarFt:F0} ft (bar span {barSpanFt:F0} ft)"
        );

        var hold = Assert.IsType<HoldingShortPhase>(aircraft.Phases.CurrentPhase);
        Assert.Contains("10R", hold.HoldShort.TargetName ?? "");
        Assert.True(aircraft.IndicatedAirspeed < 1.0, $"should be stopped at the bar; ias={aircraft.IndicatedAirspeed:F1}");

        // Held on the approach side of the crossing: never past the near bar toward the far one.
        Assert.True(
            minFarBarFt > barSpanFt * 0.75,
            $"aircraft entered the 10R crossing uncleared (came within {minFarBarFt:F0} ft of the far bar; span {barSpanFt:F0})"
        );
        Assert.True(distFromBarFt < 200, $"stopped {distFromBarFt:F0} ft from the bar — never reached its hold point");
    }

    [Fact]
    public void ParkedOnTheBar_Arms()
    {
        // The original #316 case: re-routed while already holding at the bar.
        AssertHoldsShortOf10R(startOffsetFt: 40, startIasKts: 0);
    }

    [Fact]
    public void ApproachingFromBeyondTheParkedRadius_ArmsInsteadOfCrossing()
    {
        // Re-routed while still rolling toward the bar from beyond the 150 ft parked radius:
        // must brake to the bar and hold, not sail through the one-shot check and cross 10R.
        AssertHoldsShortOf10R(startOffsetFt: 250, startIasKts: 15);
    }
}
