using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A TAXI to an aircraft at rest on a runway between two of its centreline nodes starts where the aircraft is, on the runway:
/// the route's first leg runs from the aircraft along the centreline toward the taxiway it turns off on, never from the
/// nearest graph node off the runway. Over the real SFO layout, with no LAHSO hold involved.
/// </summary>
public class TaxiStartsOnRunwayTests(ITestOutputHelper output)
{
    /// <summary>Where the aircraft rests on 28R, ft from its landing threshold: past E, short of D, no runway between it and D.</summary>
    private const double RestAlongFt = 7000;

    /// <summary>The taxiway that leaves 28R ahead of the aircraft.</summary>
    private const string BranchAhead = "D";

    [Fact]
    public void TaxiD_StoppedMidRunway_FirstLegRollsAlongTheRunwayFromTheAircraft()
    {
        if (SfoGroundHarness.Build(output, AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway);
        LatLon threshold = LandingThreshold.Resolve(runway, ground.Layout);
        LatLon rest = GeoMath.ProjectPoint(threshold, runway.TrueHeading, RestAlongFt / GeoMath.FeetPerNm);
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            "TST001",
            "E75L",
            (VirtualNode.Create(rest.Lat, rest.Lon), runway.TrueHeading),
            new HoldingInPositionPhase()
        );

        CommandResult taxi = ground.Engine.SendCommand(aircraft.Callsign, $"TAXI {BranchAhead}");
        output.WriteLine($"TAXI {BranchAhead}: success={taxi.Success} message={taxi.Message}");
        Assert.True(taxi.Success, $"TAXI {BranchAhead} refused: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.ToSummary()}");

        TaxiRouteSegment first = route.Segments[0];
        double startOffsetFt = GeoMath.DistanceNm(aircraft.Position, first.Edge.FromNode.Position) * GeoMath.FeetPerNm;
        LatLon legEnd = first.Edge.ToNode.Position;
        double legEndOffCentrelineFt = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(legEnd, threshold, runway.TrueHeading)) * GeoMath.FeetPerNm;
        double legEndAheadFt = GeoMath.AlongTrackDistanceNm(legEnd, aircraft.Position, runway.TrueHeading) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"first leg #{first.FromNodeId} -> #{first.ToNodeId} ({first.TaxiwayName}): starts {startOffsetFt:F0} ft from the aircraft, "
                + $"ends {legEndAheadFt:F0} ft ahead and {legEndOffCentrelineFt:F0} ft off the 28R centreline"
        );

        Assert.True(startOffsetFt <= AirportGroundLayout.AtNodeToleranceFt, $"the route starts {startOffsetFt:F0} ft from the aircraft");
        Assert.True(legEndOffCentrelineFt <= runway.WidthFt / 2.0, $"the first leg ends {legEndOffCentrelineFt:F0} ft off the runway centreline");
        Assert.True(legEndAheadFt > 0, $"the first leg ends {-legEndAheadFt:F0} ft behind the aircraft");
    }

    /// <summary>The LAHSO hold point short of 1L, ft from the 28R landing threshold: between the L and E/T branches.</summary>
    private const double LahsoHoldAlongFt = 4947;

    /// <summary>The aircraft's callsign in every staging.</summary>
    private const string Callsign = "TST001";

    /// <summary>An aircraft at rest on the 28R centreline, holding in position, with 28R as its assigned runway.</summary>
    private sealed record OnRunway(SfoGround Ground, AircraftState Aircraft, RunwayInfo Runway, LatLon Threshold);

    /// <summary>
    /// Spawns <see cref="Callsign"/> at rest on the 28R centreline <paramref name="alongFt"/> from its landing threshold, nose
    /// down the runway, in <see cref="HoldingInPositionPhase"/>, 28R its assigned runway. Null when the layout or nav data is absent.
    /// </summary>
    private OnRunway? Stage(double alongFt) => Stage(alongFt, "E75L", new HoldingInPositionPhase());

    /// <summary>
    /// <see cref="Stage(double)"/> for an aircraft of <paramref name="type"/> in <paramref name="phase"/>. Null when the layout or nav
    /// data is absent.
    /// </summary>
    private OnRunway? Stage(double alongFt, string type, Phase phase)
    {
        if (SfoGroundHarness.Build(output, AutoCrossRunwayMode.Off) is not { } ground)
        {
            return null;
        }

        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", "28R");
        Assert.NotNull(runway);
        LatLon threshold = LandingThreshold.Resolve(runway, ground.Layout);
        LatLon rest = GeoMath.ProjectPoint(threshold, runway.TrueHeading, alongFt / GeoMath.FeetPerNm);
        AircraftState aircraft = SfoGroundHarness.SpawnAt(
            ground,
            Callsign,
            type,
            (VirtualNode.Create(rest.Lat, rest.Lon), runway.TrueHeading),
            phase
        );
        aircraft.Phases!.AssignedRunway = runway;
        return new OnRunway(ground, aircraft, runway, threshold);
    }

    private CommandResult Send(OnRunway staged, string command)
    {
        CommandResult result = staged.Ground.Engine.SendCommand(Callsign, command);
        string readback = result.PilotReadback is HandlerReadback handler ? handler.Speech.Terminal : "(none)";
        output.WriteLine($"{command}: success={result.Success} message={result.Message} readback={readback}");
        return result;
    }

    /// <summary>Distance (ft) of <paramref name="position"/> from the 28R centreline.</summary>
    private static double OffCentrelineFt(OnRunway staged, LatLon position) =>
        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, staged.Threshold, staged.Runway.TrueHeading)) * GeoMath.FeetPerNm;

    /// <summary>Distance (ft) of <paramref name="position"/> along 28R ahead of the aircraft's rest point; negative behind it.</summary>
    private static double AheadOfAircraftFt(OnRunway staged, LatLon rest, LatLon position) =>
        GeoMath.AlongTrackDistanceNm(position, rest, staged.Runway.TrueHeading) * GeoMath.FeetPerNm;

    [Fact]
    public void TaxiE_FromTheLahsoHoldPosition_RollsAlong28RFromTheAircraft()
    {
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        CommandResult taxi = Send(staged, "TAXI E");
        Assert.True(taxi.Success, $"TAXI E refused: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(staged.Aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.ToSummary()}");

        TaxiRouteSegment first = route.Segments[0];
        LatLon rest = staged.Aircraft.Position;
        double startOffsetFt = GeoMath.DistanceNm(rest, first.Edge.FromNode.Position) * GeoMath.FeetPerNm;
        double legEndOffFt = OffCentrelineFt(staged, first.Edge.ToNode.Position);
        double legEndAheadFt = AheadOfAircraftFt(staged, rest, first.Edge.ToNode.Position);
        output.WriteLine($"first leg #{first.FromNodeId} -> #{first.ToNodeId}: starts {startOffsetFt:F0} ft off, ends {legEndAheadFt:F0} ft ahead");

        Assert.True(startOffsetFt <= AirportGroundLayout.AtNodeToleranceFt, $"the route starts {startOffsetFt:F0} ft from the aircraft");
        Assert.True(legEndOffFt <= (staged.Runway.WidthFt / 2.0), $"the first leg ends {legEndOffFt:F0} ft off the runway centreline");
        Assert.True(legEndAheadFt > 0, $"the first leg ends {-legEndAheadFt:F0} ft behind the aircraft");
    }

    [Fact]
    public void TaxiD_StoppedMidRunway_DoesNotHoldAtItsOwnRunwayBar()
    {
        if (Stage(RestAlongFt) is not { } staged)
        {
            return;
        }

        AssertPassesOwnRunwayBar(staged, BranchAhead, $"TAXI {BranchAhead}");
    }

    /// <summary>
    /// From the LAHSO hold point, holding in position: <c>TAXI E CROSS 1L</c> crosses 1L along 28R, then leaves 28R by E. The
    /// carried 28R bar is the one on E, where the route leaves 28R, not the route's first crossing (1L), so the aircraft passes
    /// the 28R/E bar without stopping there.
    /// </summary>
    [Fact]
    public void TaxiECross1L_FromTheHold_PassesThe28RBarOnEWithoutStopping()
    {
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        AssertPassesOwnRunwayBar(staged, "E", "TAXI E CROSS 1L");
    }

    /// <summary>
    /// Sends <paramref name="command"/> and ticks until the aircraft is past the 28R bar on <paramref name="taxiway"/> nearest
    /// it, at most 300 s: it gets past, and never stops within 100 ft of that bar.
    /// </summary>
    private void AssertPassesOwnRunwayBar(OnRunway staged, string taxiway, string command)
    {
        AircraftState aircraft = staged.Aircraft;
        GroundNode bar = TestLayoutNodes
            .RunwayHoldShortsOnTaxiway(staged.Ground.Layout, "28R", taxiway)
            .MinBy(n => GeoMath.DistanceNm(n.Position, aircraft.Position))!;
        double barOffFt = OffCentrelineFt(staged, bar.Position);
        output.WriteLine($"28R bar on {taxiway}: #{bar.Id}, {barOffFt:F0} ft off the centreline");

        CommandResult taxi = Send(staged, command);
        Assert.True(taxi.Success, $"{command} refused: {taxi.Message}");

        double slowestNearBarKts = double.MaxValue;
        bool past = false;
        for (int t = 1; (t <= 300) && !past; t++)
        {
            staged.Ground.Engine.TickOneSecond();
            double toBarFt = GeoMath.DistanceNm(aircraft.Position, bar.Position) * GeoMath.FeetPerNm;
            if (toBarFt <= 100)
            {
                slowestNearBarKts = Math.Min(slowestNearBarKts, aircraft.GroundSpeed);
            }

            past = OffCentrelineFt(staged, aircraft.Position) > (barOffFt + 20);
            if (past || (t % 20 == 0))
            {
                output.WriteLine(
                    $"t+{t}s: phase={aircraft.Phases!.CurrentPhase?.Name ?? "none"} gs={aircraft.GroundSpeed:F1} to bar {toBarFt:F0} ft"
                );
            }
        }

        Assert.True(past, $"never passed the 28R bar #{bar.Id}; phase {aircraft.Phases!.CurrentPhase?.Name ?? "none"}");
        Assert.True(slowestNearBarKts > SfoGroundHarness.StationarySpeedKts, $"stopped at the 28R bar ({slowestNearBarKts:F1} kt)");
    }

    /// <summary>The taxiway that leaves 28R behind the aircraft at the LAHSO hold point.</summary>
    private const string BranchBehind = "L";

    [Fact]
    public void BareTaxiL_TurnOffBehind_QueriesBackTaxiAndKeepsHolding()
    {
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        Phase? before = staged.Aircraft.Phases!.CurrentPhase;
        CommandResult taxi = Send(staged, $"TAXI {BranchBehind}");

        Assert.False(taxi.Success, "a bare TAXI to a turn-off behind the aircraft was accepted");
        Assert.Equal("confirm back-taxi on runway 28R to L?", taxi.Message);
        PilotSpeechText query = Assert.IsType<PilotSpeechText>(taxi.PilotUnable);
        Assert.EndsWith(", confirm back-taxi on runway two eight right to lima?", query.Tts);
        Assert.Same(before, staged.Aircraft.Phases.CurrentPhase);
        Assert.Null(staged.Aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// Sends <c>TAXI 28R L</c> to the aircraft at the LAHSO hold point and ticks until it taxis on L, at most 300 s. Returns the
    /// farthest (ft) it strayed from the 28R centreline until its turn about put it on 28R's reciprocal, and the TAXI's result.
    /// </summary>
    private (double FarthestOffFt, CommandResult Taxi) BackTaxiToL(OnRunway staged)
    {
        AircraftState aircraft = staged.Aircraft;
        CommandResult taxi = Send(staged, $"TAXI 28R {BranchBehind}");
        Assert.True(taxi.Success, $"TAXI 28R {BranchBehind} refused: {taxi.Message}");

        double reciprocalDeg = (staged.Runway.TrueHeading.Degrees + 180.0) % 360.0;
        double farthestOffFt = 0;
        bool turnedAbout = false;
        bool onL = false;
        for (int t = 1; (t <= 300) && !onL; t++)
        {
            staged.Ground.Engine.TickOneSecond();
            onL = string.Equals(aircraft.Ground.CurrentTaxiway, BranchBehind, StringComparison.OrdinalIgnoreCase);
            if (!turnedAbout)
            {
                farthestOffFt = Math.Max(farthestOffFt, OffCentrelineFt(staged, aircraft.Position));
                turnedAbout = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, reciprocalDeg) < 10.0;
            }

            if (onL || (t % 10 == 0))
            {
                output.WriteLine(
                    $"t+{t}s: phase={aircraft.Phases!.CurrentPhase?.Name ?? "none"} taxiway={aircraft.Ground.CurrentTaxiway ?? "(none)"} "
                        + $"gs={aircraft.GroundSpeed:F2} hdg={aircraft.TrueHeading.Degrees:F0} off={OffCentrelineFt(staged, aircraft.Position):F1} ft"
                );
            }
        }

        Assert.True(onL, $"never reached {BranchBehind}; taxiway {aircraft.Ground.CurrentTaxiway ?? "(none)"}");
        Assert.True(turnedAbout, "never turned about onto the reciprocal of 28R before reaching L");
        return (farthestOffFt, taxi);
    }

    [Fact]
    public void Taxi28RL_TurnOffBehind_TurnsAboutAndBackTaxis()
    {
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        (_, CommandResult taxi) = BackTaxiToL(staged);
        HandlerReadback readback = Assert.IsType<HandlerReadback>(taxi.PilotReadback);
        output.WriteLine($"readback tts: {readback.Speech.Tts}");
        Assert.Contains("runway two eight right", readback.Speech.Tts);
        Assert.Contains("lima", readback.Speech.Tts);
        Assert.DoesNotContain("one zero left", readback.Speech.Tts);
    }

    /// <summary>
    /// Holding in position on a 28R centreline node (within <see cref="AirportGroundLayout.AtNodeToleranceFt"/>) just past
    /// where L leaves 28R: L is behind, so a bare <c>TAXI L</c> is a back-taxi the pilot queries, and the aircraft keeps holding.
    /// </summary>
    [Fact]
    public void BareTaxiL_AtACentrelineNode_QueriesBackTaxi()
    {
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        GroundNode node = CentrelineNodeAheadOfL(staged);
        staged.Aircraft.Position = node.Position;
        AssertBackTaxiQueried(staged, $"TAXI {BranchBehind}", "confirm back-taxi on runway 28R to L?");
    }

    /// <summary>
    /// A type whose turn about does not fit 28R's half-width, at the LAHSO hold point: a bare <c>TAXI L</c> is still queried as a
    /// back-taxi (the query comes before any fit verdict); <c>TAXI 28R L</c> is refused for want of room to turn around.
    /// </summary>
    [Fact]
    public void BareTaxiL_TypeTooBigToTurnOn28R_QueriesNotNoRoom()
    {
        string[] candidates = ["A388", "A225", "A124", "B748", "B744", "A35K", "B77W", "C5M"];
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        double halfWidthFt = staged.Runway.WidthFt / 2.0;
        string? tooBig = candidates.FirstOrDefault(t => !TurnAboutFit.EvaluateOnRunway(t, AircraftCategorization.Categorize(t), halfWidthFt).Fits);
        output.WriteLine($"28R half-width {halfWidthFt:F0} ft; first candidate that does not fit: {tooBig ?? "(none)"}");
        Assert.NotNull(tooBig);

        if (Stage(LahsoHoldAlongFt, tooBig, new HoldingInPositionPhase()) is not { } big)
        {
            return;
        }

        AssertBackTaxiQueried(big, $"TAXI {BranchBehind}", "confirm back-taxi on runway 28R to L?");
        CommandResult named = Send(big, $"TAXI 28R {BranchBehind}");
        Assert.False(named.Success, $"TAXI 28R {BranchBehind} accepted for a {tooBig}");
        Assert.StartsWith("Unable, no room to turn around on ", named.Message);
        Assert.DoesNotContain("back-taxi", named.Message);
    }

    /// <summary>
    /// A scenario-scripted <c>TAXI L</c> (a clearance no controller issued, like <c>TAXIAUTO</c>, which cannot name the runway
    /// first) to L, behind the aircraft at the LAHSO hold point: never refused with the back-taxi query; it keeps the
    /// turn-about rules from before the query existed, and an E75L fits a turn about on 28R.
    /// </summary>
    [Fact]
    public void ScriptedTaxiL_ToATurnOffBehind_FromTheRunway_IsNotQueried()
    {
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        AircraftState aircraft = staged.Aircraft;
        ParseResult<ParsedCommand> parsed = CommandParser.Parse($"TAXI {BranchBehind}");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        DispatchContext scripted = staged.Ground.Engine.BuildDispatchContext(aircraft, isScenarioScripted: true, facilityHint: null);
        CommandResult taxi = CommandDispatcher.Dispatch(Assert.IsType<TaxiCommand>(parsed.Value), aircraft, scripted);
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        output.WriteLine(
            $"scripted TAXI {BranchBehind}: {taxi.Success} {taxi.Message}; route {route?.ToSummary() ?? "(none)"}, {route?.TurnAboutShape}"
        );

        Assert.DoesNotContain("back-taxi", taxi.Message);
        Assert.True(taxi.Success, $"scripted TAXI {BranchBehind} refused: {taxi.Message}");
        Assert.NotNull(route);
        Assert.NotEqual(TaxiTurnAboutShape.None, route.TurnAboutShape);
    }

    /// <summary>
    /// Holding in position on the 28R centreline node just past L, cleared <c>TAXI 28R L</c>: the route's first segment runs back
    /// along 28R from the node, an approved back-taxi, and the readback says so.
    /// </summary>
    [Fact]
    public void Taxi28RL_AtACentrelineNode_ReadsBackBackTaxi()
    {
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        staged.Aircraft.Position = CentrelineNodeAheadOfL(staged).Position;
        CommandResult taxi = Send(staged, $"TAXI 28R {BranchBehind}");
        Assert.True(taxi.Success, $"TAXI 28R {BranchBehind} refused: {taxi.Message}");
        HandlerReadback readback = Assert.IsType<HandlerReadback>(taxi.PilotReadback);
        output.WriteLine($"readback tts: {readback.Speech.Tts}");
        Assert.Equal("back-taxi on runway 28R, taxi via L, hold short of runway 10R", readback.Speech.Terminal);
        Assert.Equal("back-taxi on runway two eight right, taxi via lima, hold short of runway one zero right", readback.Speech.Tts);
    }

    /// <summary>
    /// A type whose turn about does not fit 28R's half-width, at the LAHSO hold point, cleared <c>TAXI 28R L</c>: refused for want of
    /// room to turn around on the runway it stands on, named as the runway end it faces ("runway 28R"), not by the centreline edge's
    /// name.
    /// </summary>
    [Fact]
    public void NoRoomToTurnAbout_OnARunway_NamesTheRunway()
    {
        string[] candidates = ["A388", "A225", "A124", "B748", "B744", "A35K", "B77W", "C5M"];
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        double halfWidthFt = staged.Runway.WidthFt / 2.0;
        string? tooBig = candidates.FirstOrDefault(t => !TurnAboutFit.EvaluateOnRunway(t, AircraftCategorization.Categorize(t), halfWidthFt).Fits);
        Assert.NotNull(tooBig);
        if (Stage(LahsoHoldAlongFt, tooBig, new HoldingInPositionPhase()) is not { } big)
        {
            return;
        }

        CommandResult named = Send(big, $"TAXI 28R {BranchBehind}");
        Assert.False(named.Success, $"TAXI 28R {BranchBehind} accepted for a {tooBig}");
        Assert.Equal("Unable, no room to turn around on runway 28R, request a route ahead", named.Message);
        PilotSpeechText unable = Assert.IsType<PilotSpeechText>(named.PilotUnable);
        output.WriteLine($"unable tts: {unable.Tts}");
        Assert.Equal("unable, no room to turn around on runway 28R, request a route ahead.", unable.Terminal);
        Assert.EndsWith(", unable, no room to turn around on runway two eight right, request a route ahead.", unable.Tts);
    }

    /// <summary>
    /// The route <c>TAXI E</c> builds from the LAHSO hold point (28R bar on E, then the 1L and 1R crossings), with another
    /// runway's crossing annotated ahead of the 28R bar, so the route's first runway crossing is another runway's (no real SFO
    /// route from 28R crosses another runway before it leaves 28R: <c>TAXI D</c> is refused as needing E). For an aircraft
    /// standing on 28R the crossing carried as cleared is the 28R bar on E, where the route leaves 28R, not that first crossing.
    /// </summary>
    [Fact]
    public void CarriedCrossing_PastAnotherRunwaysCrossing_IsTheOwnRunwayLeavingBar()
    {
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        string exit = "E";
        LatLon rest = staged.Aircraft.Position;
        CommandResult taxi = Send(staged, $"TAXI {exit}");
        Assert.True(taxi.Success, $"TAXI {exit} refused: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(staged.Aircraft.Ground.AssignedTaxiRoute);
        AirportGroundLayout layout = staged.Ground.Layout;
        HoldShortPoint other = route.HoldShortPoints.First(h =>
            (h.Reason == HoldShortReason.RunwayCrossing) && !RunwayIdentifier.Parse(h.TargetName!).Overlaps(RunwayIdentifier.Parse("28R"))
        );
        route.HoldShortPoints.Remove(other);
        route.HoldShortPoints.Insert(0, other);
        AircraftState standing = SfoGroundHarness.SpawnAt(
            staged.Ground,
            "TST002",
            "E75L",
            (VirtualNode.Create(rest.Lat, rest.Lon), staged.Runway.TrueHeading),
            new HoldingInPositionPhase()
        );
        static bool Is28R(HoldShortPoint h) => RunwayIdentifier.Parse(h.TargetName!).Overlaps(RunwayIdentifier.Parse("28R"));
        output.WriteLine($"hold shorts: {string.Join(", ", route.HoldShortPoints.Select(h => $"{h.TargetName}@{h.NodeId}({h.Reason})"))}");

        HoldShortPoint first = route.HoldShortPoints.First(h => h.Reason == HoldShortReason.RunwayCrossing);
        Assert.False(Is28R(first), $"the route's first crossing is of {first.TargetName}, not another runway's");
        HoldShortPoint? carried = GroundCommandHandler.CarriedCrossing(route, layout, standing, "28R");
        Assert.NotNull(carried);
        Assert.True(Is28R(carried), $"carried the crossing of {carried.TargetName}");
        Assert.Contains(carried.NodeId, TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", exit).Select(n => n.Id));
    }

    /// <summary>
    /// With no assigned runway, the back-taxi query names the 28R/10L end the aircraft faces: nose down 28R, runway 28R.
    /// </summary>
    [Fact]
    public void BareTaxiL_NoAssignedRunway_NamesTheEndTheAircraftFaces()
    {
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        staged.Aircraft.Phases!.AssignedRunway = null;
        AssertBackTaxiQueried(staged, $"TAXI {BranchBehind}", "confirm back-taxi on runway 28R to L?");
    }

    /// <summary>
    /// A taxi on a runway centreline in <see cref="TaxiingPhase"/> (not a rollout) starts like any non-rollout state on the
    /// runway: from the aircraft, along the centreline toward the turn-off ahead.
    /// </summary>
    [Fact]
    public void Taxi_WhileTaxiingAlongARunway_StartsFromTheAircraft()
    {
        if (Stage(RestAlongFt, "E75L", new TaxiingPhase()) is not { } staged)
        {
            return;
        }

        CommandResult taxi = Send(staged, $"TAXI {BranchAhead}");
        Assert.True(taxi.Success, $"TAXI {BranchAhead} refused: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(staged.Aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.ToSummary()}");

        TaxiRouteSegment first = route.Segments[0];
        LatLon rest = staged.Aircraft.Position;
        double startOffsetFt = GeoMath.DistanceNm(rest, first.Edge.FromNode.Position) * GeoMath.FeetPerNm;
        Assert.True(startOffsetFt <= AirportGroundLayout.AtNodeToleranceFt, $"the route starts {startOffsetFt:F0} ft from the aircraft");
        Assert.True(OffCentrelineFt(staged, first.Edge.ToNode.Position) <= (staged.Runway.WidthFt / 2.0), "the first leg leaves the runway");
        Assert.True(AheadOfAircraftFt(staged, rest, first.Edge.ToNode.Position) > 0, "the first leg ends behind the aircraft");
    }

    /// <summary>
    /// Sends <paramref name="command"/>: refused with the back-taxi query <paramref name="expected"/>, the phase unchanged and no
    /// route assigned.
    /// </summary>
    private void AssertBackTaxiQueried(OnRunway staged, string command, string expected)
    {
        Phase? before = staged.Aircraft.Phases!.CurrentPhase;
        CommandResult taxi = Send(staged, command);
        Assert.False(taxi.Success, $"{command} was accepted");
        Assert.Equal(expected, taxi.Message);
        Assert.IsType<PilotSpeechText>(taxi.PilotUnable);
        Assert.Same(before, staged.Aircraft.Phases.CurrentPhase);
        Assert.Null(staged.Aircraft.Ground.AssignedTaxiRoute);
    }

    /// <summary>
    /// The 28R centreline node next ahead (down 28R) of where L leaves it, at least 50 ft past that junction and itself on no L
    /// edge: a <c>TAXI L</c> from it runs back along the runway.
    /// </summary>
    private GroundNode CentrelineNodeAheadOfL(OnRunway staged)
    {
        double AlongFt(GroundNode n) => GeoMath.AlongTrackDistanceNm(n.Position, staged.Threshold, staged.Runway.TrueHeading) * GeoMath.FeetPerNm;
        List<GroundNode> centreline = [.. staged.Ground.Layout.Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesRunway("28R")))];
        GroundNode lJunction = centreline
            .Where(n => n.Edges.Any(e => e.MatchesTaxiway(BranchBehind)) && (AlongFt(n) < LahsoHoldAlongFt))
            .MaxBy(AlongFt)!;
        GroundNode ahead = centreline
            .Where(n => (AlongFt(n) > (AlongFt(lJunction) + 50)) && !n.Edges.Any(e => e.MatchesTaxiway(BranchBehind)))
            .MinBy(AlongFt)!;
        output.WriteLine($"L leaves 28R at #{lJunction.Id} ({AlongFt(lJunction):F0} ft); next centreline node #{ahead.Id} ({AlongFt(ahead):F0} ft)");
        return ahead;
    }

    [Fact]
    public void TurnAboutVerdict_AndNavigatorDraw_AgreeOnRunway()
    {
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        double? halfWidthFt = staged.Ground.Layout.RunwayHalfWidthFtAt(staged.Aircraft.Position, "28R");
        Assert.NotNull(halfWidthFt);
        (double farthestOffFt, _) = BackTaxiToL(staged);
        output.WriteLine($"farthest off the 28R centreline before {BranchBehind}: {farthestOffFt:F1} ft of {halfWidthFt:F0} ft");
        Assert.True(
            farthestOffFt <= halfWidthFt,
            $"left 28R's pavement in the turn about: {farthestOffFt:F1} ft off, half-width {halfWidthFt:F0} ft"
        );
    }

    [Fact]
    public void TaxiE_FromTheRunway_ReadbackHoldsShortOf1LAndClaimsNoCrossingOf28R()
    {
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        CommandResult taxi = Send(staged, "TAXI E");
        Assert.True(taxi.Success, $"TAXI E refused: {taxi.Message}");
        HandlerReadback readback = Assert.IsType<HandlerReadback>(taxi.PilotReadback);
        output.WriteLine($"echo: {taxi.Message}");
        output.WriteLine($"readback terminal: {readback.Speech.Terminal}");
        output.WriteLine($"readback tts: {readback.Speech.Tts}");

        Assert.DoesNotContain("cross", taxi.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("via on", taxi.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hold short of runway one left", readback.Speech.Tts);
        Assert.DoesNotContain("cross runway", readback.Speech.Tts);
        Assert.DoesNotContain("cross runway", readback.Speech.Terminal);
    }

    [Fact]
    public void TurnAboutFit_OnRunway_E75LFitsOn28R()
    {
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        double? halfWidthFt = staged.Ground.Layout.RunwayHalfWidthFtAt(staged.Aircraft.Position, "28R");
        Assert.NotNull(halfWidthFt);
        AircraftCategory category = AircraftCategorization.Categorize("E75L");
        TurnAboutFitResult onRunway = TurnAboutFit.EvaluateOnRunway("E75L", category, halfWidthFt.Value);
        TurnAboutFitResult onTaxiway = TurnAboutFit.Evaluate("E75L", category);
        output.WriteLine(
            $"28R half-width {halfWidthFt:F0} ft: fits={onRunway.Fits}; TDG half-width {onTaxiway.HalfWidthFt:F1} ft: fits={onTaxiway.Fits}"
        );

        Assert.True(onRunway.Fits, $"E75L does not fit a turn about on 28R ({halfWidthFt:F0} ft half-width)");
        Assert.Equal(halfWidthFt.Value, onRunway.HalfWidthFt);
        Assert.False(onTaxiway.Fits, $"E75L fits a turn about at its TDG half-width {onTaxiway.HalfWidthFt:F1} ft");
    }

    /// <summary>
    /// In <see cref="RunwayExitPhase"/> mid-way along the 28R centreline, FOLLOWG's start (<see cref="FollowRoutePlanner.StartOf"/>)
    /// and TAXI's (<see cref="AirportGroundLayout.PickTaxiStart"/> with <see cref="RunwayTaxiStart.Of(AircraftState)"/>) are the same
    /// node: the rollout's own start, not the centreline node ahead a non-rollout state starts at.
    /// </summary>
    [Fact]
    public void FollowgAndTaxi_InRunwayExitPhaseMidCentreline_StartAtTheRolloutsNode()
    {
        Assert.False(RunwayTaxiStart.Of(new LandingPhase().Name, "28R").FromCenterline);
        Assert.False(RunwayTaxiStart.Of(new RunwayExitPhase().Name, "28R").FromCenterline);
        if (Stage(RestAlongFt, "E75L", new RunwayExitPhase()) is not { } staged)
        {
            return;
        }

        AircraftState aircraft = staged.Aircraft;
        AirportGroundLayout layout = staged.Ground.Layout;
        var traffic = TaxiStartTraffic.Of(aircraft, []);
        GroundNode? taxiStart = layout.PickTaxiStart(aircraft.Position, aircraft.TrueHeading, traffic, RunwayTaxiStart.Of(aircraft))?.Node;
        GroundNode? followStart = FollowRoutePlanner.StartOf(layout, aircraft, requireAhead: false, () => [])?.Node;
        GroundNode? centrelineStart = layout.PickTaxiStart(aircraft.Position, aircraft.TrueHeading, traffic, new RunwayTaxiStart(true, "28R"))?.Node;
        output.WriteLine(
            $"phase {aircraft.Phases!.CurrentPhase?.Name}: TAXI #{taxiStart?.Id}, FOLLOWG #{followStart?.Id}, centreline ahead #{centrelineStart?.Id}"
        );

        Assert.NotNull(taxiStart);
        Assert.Equal(taxiStart.Id, followStart?.Id);
        Assert.NotEqual(centrelineStart?.Id, taxiStart.Id);
    }

    /// <summary>
    /// On the D stub 60 ft off the 28R centreline, inside the runway's width: the junction pavement bound and the turn-about fit
    /// are the taxiway's (the type's TDG half-width), not the runway's half-width.
    /// </summary>
    [Fact]
    public void JunctionPavementBound_OnATaxiwayStubInside28R_IsTheTaxiwayBound()
    {
        if (Stage(RestAlongFt) is not { } staged)
        {
            return;
        }

        AircraftState aircraft = staged.Aircraft;
        AirportGroundLayout layout = staged.Ground.Layout;
        GroundNode junction = layout
            .Nodes.Values.Where(n => n.Edges.Any(e => e.MatchesRunway("28R")) && n.Edges.Any(e => (e is GroundEdge) && e.MatchesTaxiway(BranchAhead)))
            .MinBy(n => GeoMath.DistanceNm(n.Position, aircraft.Position))!;
        GroundEdge stub = junction.Edges.OfType<GroundEdge>().First(e => e.MatchesTaxiway(BranchAhead));
        GroundNode far = stub.OtherNode(junction);
        LatLon onStub = GeoMath.ProjectPoint(
            junction.Position,
            new TrueHeading(GeoMath.BearingTo(junction.Position, far.Position)),
            60.0 / GeoMath.FeetPerNm
        );
        aircraft.Position = onStub;
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = category,
            DeltaSeconds = 0,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };
        double taxiwayBoundFt = GroundNavigator.JunctionPavementBoundFt(aircraft.AircraftType, category);
        double boundFt = GroundNavigator.JunctionPavementBoundFt(ctx);
        TurnAboutFitResult fit = TurnAboutFit.EvaluateAt(aircraft.AircraftType, category, layout, onStub, "28R");
        output.WriteLine(
            $"D stub #{junction.Id}->#{far.Id}, {OffCentrelineFt(staged, onStub):F0} ft off 28R: "
                + $"bound {boundFt:F1} ft (taxiway {taxiwayBoundFt:F1}), "
                + $"fit half-width {fit.HalfWidthFt:F1} ft"
        );

        Assert.True(OffCentrelineFt(staged, onStub) < (staged.Runway.WidthFt / 2.0), "the stub point is not inside 28R's width");
        Assert.Equal(taxiwayBoundFt, boundFt);
        Assert.Equal(TurnAboutFit.Evaluate(aircraft.AircraftType, category).HalfWidthFt, fit.HalfWidthFt);
    }

    [Fact]
    public void TaxiE_TurnOffAhead_ReachesTheBranchAtCornerSpeed()
    {
        if (Stage(LahsoHoldAlongFt) is not { } staged)
        {
            return;
        }

        AircraftState aircraft = staged.Aircraft;
        CommandResult taxi = Send(staged, "TAXI E");
        Assert.True(taxi.Success, $"TAXI E refused: {taxi.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.ToSummary()}");

        int branchIndex = route.Segments.FindIndex(s => !s.Edge.Edge.IsRunwayCenterline && (s.Edge.Edge.TaxiwayName != "RAMP"));
        Assert.True(branchIndex > 0, "the route never leaves the runway centreline");
        TaxiRouteSegment into = route.Segments[branchIndex - 1];
        TaxiRouteSegment off = route.Segments[branchIndex];
        GroundNode branch = off.Edge.FromNode;
        double turnDeg = GeoMath.AbsBearingDifference(into.Edge.ArrivalBearing, off.Edge.DepartureBearing);
        double cornerKts = GroundNavigator.CornerSpeed(
            AircraftCategorization.Categorize(aircraft.AircraftType),
            turnDeg,
            into.Edge.Edge.DistanceNm,
            off.Edge.Edge.DistanceNm
        );
        output.WriteLine($"branch #{branch.Id} onto {off.TaxiwayName}: turn {turnDeg:F0} deg, corner speed {cornerKts:F2} kt");

        double closestFt = double.MaxValue;
        double speedAtBranchKts = 0;
        for (int t = 1; t <= 300; t++)
        {
            staged.Ground.Engine.TickOneSecond();
            double toBranchFt = GeoMath.DistanceNm(aircraft.Position, branch.Position) * GeoMath.FeetPerNm;
            if (toBranchFt < closestFt)
            {
                closestFt = toBranchFt;
                speedAtBranchKts = aircraft.GroundSpeed;
            }
            else if (closestFt < 100)
            {
                break;
            }
        }

        output.WriteLine($"closest to the branch {closestFt:F0} ft at {speedAtBranchKts:F2} kt");
        Assert.True(closestFt < 100, $"never reached the branch (closest {closestFt:F0} ft)");
        Assert.True(speedAtBranchKts <= (cornerKts + 1.0), $"reached the branch at {speedAtBranchKts:F2} kt, corner speed {cornerKts:F2} kt");
    }
}
