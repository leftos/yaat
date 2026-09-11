using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// The ½-fuselage tail-clearance leg a <see cref="CrossingRunwayPhase"/> appends past the exit-side
/// hold-short must follow the aircraft's own taxi route, and the phase must hand the route back at the
/// segment the aircraft actually ends up on.
///
/// <para>
/// SFO background aircraft KLM605 (<c>TAXI G B M1 M5 @A6S</c>) crosses 01L/19R on G and then turns onto
/// B through the fillet immediately past the far-side bar. The tail-clear used to be built by walking the
/// <em>graph's</em> straightest continuation past the exit node, which carried the aircraft ~60 ft straight
/// on along G — off the painted line and past the fillet's entry — while the onward
/// <see cref="TaxiingPhase"/> resumed on that fillet from behind. The navigator's closed-form Bézier
/// playback then wrote the aircraft onto the middle of the curve in one sub-tick (the no-teleport guard,
/// Invariant I8, now rejects that).
/// </para>
/// </summary>
public class CrossingRunwayTailClearTests(ITestOutputHelper output)
{
    /// <summary>SFO taxiway G, entry-side (east) hold-short of 01L/19R — where KLM605 starts its crossing.</summary>
    private const int EntryHoldShortNodeId = 879;

    private const double TickSeconds = 0.25;

    /// <summary>Slack for "the aircraft stands on this segment": one fuselage-free tick of travel.</summary>
    private const double OnSegmentToleranceFt = 5.0;

    private sealed record CrossingFixture(
        AirportGroundLayout Layout,
        AircraftState Aircraft,
        PhaseContext Ctx,
        TaxiRoute Route,
        int EntryIndex,
        int ExitIndex,
        int ExitNodeId,
        CrossingRunwayPhase Phase
    );

    private AirportGroundLayout? LoadSfoLayout()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new TestAirportGroundData().GetLayout("SFO");
    }

    /// <summary>
    /// The far-side hold-short of the same runway: the node the crossing exits at. Mirrors
    /// <c>TaxiingPhase.FindSameRunwayExitNode</c> — the first runway hold-short of the entry bar's runway
    /// that the route reaches after the entry.
    /// </summary>
    private static int? FindExitNodeId(TaxiRoute route, AirportGroundLayout layout, GroundNode entryNode, int entryIndex)
    {
        for (int i = entryIndex; i < route.Segments.Count; i++)
        {
            int nodeId = route.Segments[i].ToNodeId;
            if (
                (nodeId != entryNode.Id)
                && layout.Nodes.TryGetValue(nodeId, out var node)
                && (node.Type == GroundNodeType.RunwayHoldShort)
                && (node.RunwayId is { } nodeRunway)
                && (entryNode.RunwayId is { } entryRunway)
                && nodeRunway.Equals(entryRunway)
            )
            {
                return nodeId;
            }
        }

        return null;
    }

    /// <summary>
    /// KLM605 at the 01L/19R hold-short on G, cleared <c>TAXI G B M1 M5 @A6S</c>, with the crossing phase
    /// built and started exactly as <c>TaxiingPhase.BuildResumePhases</c> builds it (both bars cleared, the
    /// route left where the aircraft stands). Null when the SFO layout is unavailable.
    /// </summary>
    private CrossingFixture? BuildFixture()
    {
        var layout = LoadSfoLayout();
        if (layout is null)
        {
            return null;
        }

        Assert.True(layout.Nodes.TryGetValue(EntryHoldShortNodeId, out var entryNode), $"SFO layout has no node {EntryHoldShortNodeId}");
        Assert.Equal(GroundNodeType.RunwayHoldShort, entryNode.Type);

        // Face across the runway: the nearest hold-short of the same runway on the far side is the node the
        // crossing exits at, so its bearing is the crossing direction.
        var farSide = layout
            .Nodes.Values.Where(n =>
                (n.Id != entryNode.Id)
                && (n.Type == GroundNodeType.RunwayHoldShort)
                && (n.RunwayId is { } rwy)
                && (entryNode.RunwayId is { } entryRwy)
                && rwy.Equals(entryRwy)
                && n.Edges.Any(e => e.MatchesTaxiway("G"))
            )
            .OrderBy(n => GeoMath.DistanceNm(entryNode.Position, n.Position))
            .FirstOrDefault();
        Assert.NotNull(farSide);

        double crossingBearing = GeoMath.BearingTo(entryNode.Position, farSide.Position);
        output.WriteLine(
            $"entry={entryNode.Id}@({entryNode.Position.Lat:F6},{entryNode.Position.Lon:F6}) rwy={entryNode.RunwayId} "
                + $"farSide={farSide.Id}@({farSide.Position.Lat:F6},{farSide.Position.Lon:F6}) brg={crossingBearing:F1}"
        );

        var aircraft = new AircraftState
        {
            Callsign = "KLM605",
            AircraftType = "B77W",
            Position = entryNode.Position,
            TrueHeading = new TrueHeading(crossingBearing),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "EHAM", Destination = "KSFO" },
        };

        var taxi = new TaxiCommand(Path: ["G", "B", "M1", "M5"], HoldShorts: [], DestinationParking: "A6S");
        var result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        Assert.True(result.Success, $"TAXI G B M1 M5 @A6S failed: {result.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        int entryIndex = route.Segments.FindIndex(s => s.FromNodeId == EntryHoldShortNodeId);
        Assert.True(entryIndex >= 0, $"route does not leave node {EntryHoldShortNodeId}");

        int? exitNodeId = FindExitNodeId(route, layout, entryNode, entryIndex);
        Assert.NotNull(exitNodeId);
        int exitIndex = route.Segments.FindIndex(entryIndex, s => s.ToNodeId == exitNodeId.Value);
        Assert.True(exitIndex >= 0, $"route never reaches exit node {exitNodeId}");

        for (int i = Math.Max(0, entryIndex - 1); i < Math.Min(route.Segments.Count, exitIndex + 4); i++)
        {
            var seg = route.Segments[i];
            output.WriteLine(
                $"  route[{i}] {seg.FromNodeId} -> {seg.ToNodeId} on {seg.TaxiwayName} ({seg.Edge.DistanceNm * GeoMath.FeetPerNm:F0}ft)"
            );
        }

        // BuildResumePhases clears both bars before handing off: the entry bar by the hold it resumes from,
        // the exit bar by FindRunwayCrossingExitNode.
        if (route.GetHoldShortAt(EntryHoldShortNodeId) is { } entryHs)
        {
            entryHs.IsCleared = true;
        }
        if (route.GetHoldShortAt(exitNodeId.Value) is { } exitHs)
        {
            exitHs.IsCleared = true;
        }

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = TickSeconds,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };

        var phase = new CrossingRunwayPhase(EntryHoldShortNodeId, exitNodeId.Value, entryNode.RunwayId?.ToString());
        phase.OnStart(ctx);

        return new CrossingFixture(layout, aircraft, ctx, route, entryIndex, exitIndex, exitNodeId.Value, phase);
    }

    /// <summary>Distance from <paramref name="point"/> to the nearest chord of the route's segments after the crossing exit.</summary>
    private static double DistanceToRouteAheadFt(TaxiRoute route, int exitIndex, LatLon point)
    {
        double best = double.MaxValue;
        for (int i = exitIndex + 1; i < route.Segments.Count; i++)
        {
            var seg = route.Segments[i];
            best = Math.Min(best, GeoMath.DistanceToSegmentFt(point, seg.Edge.FromNode.Position, seg.Edge.ToNode.Position));
        }

        return best;
    }

    /// <summary>
    /// The tail-clearance leg must end on the route the aircraft is going to drive — the fillet off G onto
    /// B — not on the graph's straight continuation of G past the exit bar.
    /// </summary>
    [Fact]
    public void TailClearance_EndsOnTheRoutesOwnContinuation_NotTheGraphsStraightOne()
    {
        var fixture = BuildFixture();
        if (fixture is null)
        {
            return;
        }

        var slice = fixture.Phase.CrossingRoute;
        Assert.NotNull(slice);
        foreach (var seg in slice.Segments)
        {
            output.WriteLine($"  slice {seg.FromNodeId} -> {seg.ToNodeId} on {seg.TaxiwayName} ({seg.Edge.DistanceNm * GeoMath.FeetPerNm:F0}ft)");
        }

        var exitNode = fixture.Layout.Nodes[fixture.ExitNodeId];
        var tailClearEnd = slice.Segments[^1].Edge.ToNode.Position;
        double pastExitFt = GeoMath.DistanceNm(exitNode.Position, tailClearEnd) * GeoMath.FeetPerNm;
        double offRouteFt = DistanceToRouteAheadFt(fixture.Route, fixture.ExitIndex, tailClearEnd);
        output.WriteLine($"tail-clear end is {pastExitFt:F1}ft past the exit bar, {offRouteFt:F1}ft off the route ahead");

        Assert.True(pastExitFt > 10.0, $"expected a tail-clearance leg past the exit bar, but the slice ends {pastExitFt:F1}ft from it");
        Assert.True(
            offRouteFt <= 1.0,
            $"the tail-clearance leg ends {offRouteFt:F1}ft off the route the aircraft is about to drive "
                + $"({pastExitFt:F1}ft past the exit bar) — it followed the graph's straight continuation, not the route"
        );
    }

    /// <summary>
    /// On completion the crossing hands the route back at the segment the aircraft is standing on, so the
    /// onward <see cref="TaxiingPhase"/> picks up where the crossing left it rather than on a segment
    /// already behind it.
    /// </summary>
    [Fact]
    public void CrossingCompletion_LeavesTheRouteOnTheSegmentTheAircraftStandsOn()
    {
        var fixture = BuildFixture();
        if (fixture is null)
        {
            return;
        }

        bool complete = false;
        int ticks = 0;
        for (; ticks < 400; ticks++)
        {
            FlightPhysics.Update(fixture.Aircraft, TickSeconds);
            if (fixture.Phase.OnTick(fixture.Ctx))
            {
                complete = true;
                break;
            }
        }

        Assert.True(complete, $"the crossing never completed within {ticks * TickSeconds:F0}s");

        var route = fixture.Route;
        int idx = route.CurrentSegmentIndex;
        output.WriteLine($"crossing completed after {ticks * TickSeconds:F1}s, route index {idx}/{route.Segments.Count}");
        Assert.InRange(idx, 0, route.Segments.Count - 1);

        var seg = route.Segments[idx];
        var (foot, alongNm, _) = GeoMath.FootOfPerpendicular(fixture.Aircraft.Position, seg.Edge.FromNode.Position, seg.Edge.ToNode.Position);
        double alongFt = alongNm * GeoMath.FeetPerNm;
        double segFt = GeoMath.DistanceNm(seg.Edge.FromNode.Position, seg.Edge.ToNode.Position) * GeoMath.FeetPerNm;
        double offFt = GeoMath.DistanceNm(fixture.Aircraft.Position, foot) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"resume segment {seg.FromNodeId} -> {seg.ToNodeId} on {seg.TaxiwayName}: along={alongFt:F1}ft of {segFt:F1}ft, off={offFt:F1}ft"
        );

        Assert.True(
            (alongFt >= -OnSegmentToleranceFt) && (alongFt <= segFt + OnSegmentToleranceFt),
            $"the route resumes on segment {idx} ({seg.FromNodeId} -> {seg.ToNodeId}), but the aircraft is {alongFt:F1}ft "
                + $"along a {segFt:F1}ft segment — it is not standing on the segment the route hands back"
        );
        Assert.True(offFt <= 15.0, $"the aircraft is {offFt:F1}ft abeam the segment the route hands back ({seg.FromNodeId} -> {seg.ToNodeId})");
    }

    /// <summary>SFO taxiway M, west-side hold-short of 01L/19R — where the heavy starts its crossing.</summary>
    private const int MEntryHoldShortNodeId = 882;

    /// <summary>The far-side (east) hold-short of 01L/19R on M — the crossing's exit bar.</summary>
    private const int MExitHoldShortNodeId = 883;

    /// <summary>The next runway holding position along M: 01R/19L, one 188 ft segment past the exit bar.</summary>
    private const int MNextRunwayBarNodeId = 902;

    /// <summary>A 777-300ER: at 242 ft it does not fit between SFO's 01L and 01R holding positions on M.</summary>
    private const string HeavyType = "B77W";

    /// <summary>
    /// An uncleared RUNWAY holding position a fuselage length past the crossing's exit suppresses the
    /// tail-clearance extension exactly as a taxiway one does. SFO taxiway M crosses 01L/19R and meets the
    /// 01R/19L bar 188 ft later; a 777-300ER (242 ft) cannot fit between the two, so driving ½ a fuselage past
    /// the exit to clear the runway it just crossed carries it into the holding position markings of a runway it
    /// has no clearance to enter (AIM 2-3-5.a.1, AIM 4-3-18.a.5). Hold-short compliance outranks tail clearance:
    /// the crossing ends at the exit bar.
    /// </summary>
    [Fact]
    public void UnclearedRunwayHoldShortJustPastTheExit_SuppressesTheTailClearance()
    {
        var layout = LoadSfoLayout();
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.TryGetValue(MEntryHoldShortNodeId, out var entryNode), $"SFO layout has no node {MEntryHoldShortNodeId}");
        Assert.True(layout.Nodes.TryGetValue(MExitHoldShortNodeId, out var exitNode), $"SFO layout has no node {MExitHoldShortNodeId}");
        Assert.True(layout.Nodes.TryGetValue(MNextRunwayBarNodeId, out var nextBar), $"SFO layout has no node {MNextRunwayBarNodeId}");
        Assert.Equal(GroundNodeType.RunwayHoldShort, entryNode.Type);
        Assert.Equal(GroundNodeType.RunwayHoldShort, exitNode.Type);
        Assert.Equal(GroundNodeType.RunwayHoldShort, nextBar.Type);
        Assert.Equal(entryNode.RunwayId, exitNode.RunwayId);
        Assert.NotEqual(entryNode.RunwayId, nextBar.RunwayId);

        var aircraft = new AircraftState
        {
            Callsign = "UAL869",
            AircraftType = HeavyType,
            Position = entryNode.Position,
            TrueHeading = new TrueHeading(GeoMath.BearingTo(entryNode.Position, exitNode.Position)),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KSFO", Destination = "RJAA" },
        };

        var result = GroundCommandHandler.TryTaxi(aircraft, new TaxiCommand(Path: ["M"], HoldShorts: [], DestinationRunway: null), layout);
        Assert.True(result.Success, $"TAXI M failed: {result.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);

        output.WriteLine(
            $"crossing {entryNode.RunwayId} on M: {MEntryHoldShortNodeId} -> {MExitHoldShortNodeId}, then {nextBar.RunwayId} "
                + $"at node {MNextRunwayBarNodeId}; route {route.FormatTaxiwaySequence()}"
        );

        int exitIdx = route.Segments.FindIndex(s => s.ToNodeId == MExitHoldShortNodeId);
        Assert.True(exitIdx >= 0, $"the route never reaches the exit bar {MExitHoldShortNodeId}: {route.FormatTaxiwaySequence()}");
        Assert.True(exitIdx + 1 < route.Segments.Count, "the route must continue past the exit bar for there to be a tail-clearance leg");
        Assert.Equal(MNextRunwayBarNodeId, route.Segments[exitIdx + 1].ToNodeId);

        // BuildResumePhases clears both bars of the crossing before handing off; the runway beyond is not cleared.
        if (route.GetHoldShortAt(MEntryHoldShortNodeId) is { } entryHs)
        {
            entryHs.IsCleared = true;
        }
        if (route.GetHoldShortAt(MExitHoldShortNodeId) is { } exitHs)
        {
            exitHs.IsCleared = true;
        }

        var nextHs = route.GetHoldShortAt(MNextRunwayBarNodeId);
        Assert.NotNull(nextHs);
        Assert.False(nextHs.IsCleared, $"the fixture needs an UNCLEARED hold-short at node {MNextRunwayBarNodeId}");

        double gapFt = GeoMath.DistanceNm(exitNode.Position, nextBar.Position) * GeoMath.FeetPerNm;
        double lengthFt = FaaAircraftDatabase.Get(HeavyType)?.LengthFt ?? 0.0;
        output.WriteLine($"exit bar {MExitHoldShortNodeId} -> {MNextRunwayBarNodeId} is {gapFt:F1}ft; the {HeavyType} is {lengthFt:F1}ft long");
        Assert.True(gapFt <= lengthFt, $"the fixture needs the next bar within a fuselage length: {gapFt:F1}ft gap, {lengthFt:F1}ft aircraft");

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = TickSeconds,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };

        var phase = new CrossingRunwayPhase(MEntryHoldShortNodeId, MExitHoldShortNodeId, entryNode.RunwayId?.ToString());
        phase.OnStart(ctx);

        var slice = phase.CrossingRoute;
        Assert.NotNull(slice);
        foreach (var seg in slice.Segments)
        {
            output.WriteLine($"  slice {seg.FromNodeId} -> {seg.ToNodeId} on {seg.TaxiwayName} ({seg.Edge.DistanceNm * GeoMath.FeetPerNm:F0}ft)");
        }

        var last = slice.Segments[^1];
        double pastExitFt = GeoMath.DistanceNm(exitNode.Position, last.Edge.ToNode.Position) * GeoMath.FeetPerNm;
        Assert.True(
            last.ToNodeId == MExitHoldShortNodeId,
            $"the crossing ran {pastExitFt:F1}ft past the exit bar toward the uncleared {MNextRunwayBarNodeId} holding position"
        );
    }
}
