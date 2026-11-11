using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-1468: a hold short that becomes unmakeable while a runway crossing flies the aircraft is judged again on the taxi phase's
/// first tick after the crossing hands the route back. N462NX (C56X) on OAK, <c>TAXI B C D @SIG7</c> cleared across 28L and
/// 28R, is told <c>HS C</c> late in the 28R crossing, at its 15 kt crossing speed: the dispatcher judges the bar against the
/// crossing's stale route cursor and finds it makeable, but the crossing hands over with C's stop a few feet ahead, well inside
/// the firm stopping distance. The pilot says it is unable at once, and the aircraft stops at the firm-rate stop.
/// </summary>
public sealed class CrossingHandoffUnableHoldShortTests
{
    private const string Callsign = "N462NX";

    /// <summary>
    /// Sub-ticks into the 28R crossing at which <c>HS C</c> is given (measured): at 15 kt, makeable at dispatch, and the crossing
    /// hands over a few sub-ticks later at 11 kt with C's stop 3 ft ahead.
    /// </summary>
    private const int HoldShortSubTick = 45;

    /// <summary>How far (ft) past its moved stop the aircraft may come to rest (the firm-rate tests' bound).</summary>
    private const double RestsAtTheStopFt = 3.0;

    /// <summary>A hand-off at a crawl, under <see cref="TaxiingPhase.StoppedGroundSpeedKts"/>.</summary>
    private const double CrawlKts = 1.5;

    private const double SubTick = 1.0 / SimulationEngine.PhysicsSubTickRate;

    public CrossingHandoffUnableHoldShortTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// Moving above 2 kt at the hand-off with C's stop inside the firm stopping distance: on the hand-off tick the bar is flagged
    /// unable and the pilot says "unable to hold short of C, stopping" once; the aircraft comes to rest at the moved firm-rate stop,
    /// not past it, and reports where it holds without saying unable again.
    /// </summary>
    [Fact]
    public void HoldShortInsideTheFirmStopAtTheHandoff_IsUnableAndSaidOnce()
    {
        if (HandOffWithHoldShortOfC(_ => { }) is not { } run)
        {
            return;
        }

        Assert.True(
            run.Aircraft.GroundSpeed > TaxiingPhase.StoppedGroundSpeedKts,
            $"{Callsign} handed over at {run.Aircraft.GroundSpeed:F1} kt, not moving"
        );
        double handoffGapFt = TaxiingPhase.AlongRouteDistanceToHoldShortFt(run.Layout, run.Route, run.Aircraft.Position, run.Bar);
        Assert.True(
            TaxiingPhase.IsHoldShortUnmakeable(run.Layout, run.Route, run.Aircraft, run.Bar, SubTick),
            $"C's stop was {handoffGapFt:F1} ft ahead at {run.Aircraft.GroundSpeed:F1} kt at the hand-off: makeable, staging lost"
        );

        run.Engine.TickPhysics(SubTick);
        run.Engine.TickPostPhysics();

        Assert.True(run.Bar.Unable, "the hold short of C was not flagged unable on the hand-off tick");
        PilotSpeechText unable = Assert.Single(run.Said, s => s.Terminal.Contains("unable to hold short of", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("unable to hold short of C, stopping", unable.Terminal);
        Assert.EndsWith(", unable to hold short of charlie, stopping", unable.Tts);

        HoldingShortPhase? held = null;
        for (int second = 0; (second < 60) && (held is null); second++)
        {
            run.Engine.TickOneSecond();
            held = run.Aircraft.Phases?.CurrentPhase as HoldingShortPhase;
        }

        Assert.NotNull(held);
        Assert.Same(run.Bar, held.HoldShort);
        Assert.Equal(HoldShortArrival.BeyondAfterUnableCall, held.Arrival);
        Assert.True(run.Bar.StopMoved, "the unable bar's stop was not moved to the firm-rate stop");
        var movedStop = new LatLon(run.Bar.Latitude ?? double.NaN, run.Bar.Longitude ?? double.NaN);
        double restPastStopFt = GeoMath.AlongTrackDistanceNm(run.Aircraft.Position, movedStop, run.Aircraft.TrueHeading) * GeoMath.FeetPerNm;
        Assert.True(restPastStopFt <= RestsAtTheStopFt, $"{Callsign} came to rest {restPastStopFt:F1} ft past its moved stop");
        Assert.Single(run.Said, s => s.Terminal.Contains("unable to hold short of", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The same hand-off at a crawl, at or under 2 kt: the bar is not judged, so it is not flagged and nothing is said.
    /// </summary>
    [Fact]
    public void HoldShortAtTheHandoffAtACrawl_IsNotJudged()
    {
        if (HandOffWithHoldShortOfC(_ => { }) is not { } run)
        {
            return;
        }

        run.Aircraft.IndicatedAirspeed = CrawlKts;
        run.Engine.TickPhysics(SubTick);
        run.Engine.TickPostPhysics();

        Assert.False(run.Bar.Unable, "the hold short of C was flagged unable at a crawl");
        Assert.DoesNotContain(run.Said, s => s.Terminal.Contains("unable", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A bar with no nameable target — its name blanked, so <see cref="TaxiingPhase.UnableCallTarget"/> resolves nothing to call:
    /// the hand-off tick says nothing, leaves the bar not unable and its stop where it is, so no held-beyond report refers to a
    /// call the pilot never made.
    /// </summary>
    [Fact]
    public void HoldShortWithNoNameableTargetAtTheHandoff_IsNotFlaggedUnableAndNothingIsSaid()
    {
        if (HandOffWithHoldShortOfC(_ => { }) is not { } run)
        {
            return;
        }

        HoldShortPoint bar = ReplaceTargetName(run, "");
        Assert.Null(TaxiingPhase.UnableCallTarget(run.Aircraft, bar, run.Layout, ActiveRunways.Empty));

        run.Engine.TickPhysics(SubTick);
        run.Engine.TickPostPhysics();

        Assert.False(bar.Unable, "the hold short was flagged unable with no nameable target for the call");
        Assert.False(bar.StopMoved, "the unnameable bar's stop was moved for a call the pilot never made");
        Assert.DoesNotContain(run.Said, s => s.Terminal.Contains("unable", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A bar whose name no hold-short target parses — <c>@A12</c> is a parking token — still gets its unable call: the raw-name
    /// fallback names the bar as it is written, so the bar is flagged unable and the pilot says "unable to hold short of @A12".
    /// </summary>
    [Fact]
    public void HoldShortWithAnUnparseableTargetNameAtTheHandoff_MakesTheCallNamingTheRawName()
    {
        if (HandOffWithHoldShortOfC(_ => { }) is not { } run)
        {
            return;
        }

        HoldShortPoint bar = ReplaceTargetName(run, "@A12");
        Assert.Null(TaxiingPhase.UnableCallTarget(run.Aircraft, bar, run.Layout, ActiveRunways.Empty));

        run.Engine.TickPhysics(SubTick);
        run.Engine.TickPostPhysics();

        Assert.True(bar.Unable, "the hold short was not flagged unable though the raw-name fallback names it");
        PilotSpeechText unable = Assert.Single(run.Said, s => s.Terminal.Contains("unable to hold short of", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("unable to hold short of @A12, stopping", unable.Terminal);
    }

    /// <summary>
    /// The phase list and route snapshotted at the hand-off, before the taxi phase's first tick, and restored: the restored phase
    /// still follows the crossing and is on its first tick, so it judges the bar as the live one does — unable, said once. Restored
    /// again after that tick, it does not judge or say it a second time.
    /// </summary>
    [Fact]
    public void SnapshotAtTheHandoff_Restored_JudgesTheSameOnce()
    {
        if (HandOffWithHoldShortOfC(_ => { }) is not { } run)
        {
            return;
        }

        Restore(run);
        run.Engine.TickPhysics(SubTick);
        run.Engine.TickPostPhysics();

        HoldShortPoint restoredBar = BarOfC(run.Aircraft);
        Assert.True(restoredBar.Unable, "the restored hold short of C was not flagged unable on the hand-off tick");
        Assert.Single(run.Said, s => s.Terminal.Contains("unable to hold short of", StringComparison.OrdinalIgnoreCase));

        Restore(run);
        run.Engine.TickPhysics(SubTick);
        run.Engine.TickPostPhysics();
        run.Engine.TickOneSecond();

        Assert.True(BarOfC(run.Aircraft).Unable, "the twice-restored hold short of C lost its unable flag");
        Assert.Single(run.Said, s => s.Terminal.Contains("unable to hold short of", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Solo training with the student working ground: the unable call is queued as a pilot transmission the student hears.
    /// </summary>
    [Fact]
    public void HoldShortInsideTheFirmStopAtTheHandoff_SoloGroundStudent_HearsTheUnableCall()
    {
        if (
            HandOffWithHoldShortOfC(scenario =>
            {
                scenario.SoloTrainingMode = true;
                scenario.StudentPositionType = "GND";
            })
            is not { } run
        )
        {
            return;
        }

        run.Engine.TickPhysics(SubTick);

        Assert.True(run.Bar.Unable, "the hold short of C was not flagged unable on the hand-off tick");
        PilotTransmission call = Assert.Single(
            run.Aircraft.PendingPilotTransmissions,
            t => t.Text.Contains("unable to hold short of", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal("unable to hold short of C, stopping", call.Text);
        Assert.EndsWith(", unable to hold short of charlie, stopping", call.SpeechText);
        Assert.DoesNotContain(run.Aircraft.PendingWarnings, w => w.Contains("unable", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Solo training with the student working ground, but the pilot tuned to a position nobody in the room staffs: the unable
    /// call is not a radio call the student hears, and the terminal warning carries it instead.
    /// </summary>
    [Fact]
    public void HoldShortInsideTheFirmStopAtTheHandoff_SoloTunedAwayFromTheStudent_WarnsOnly()
    {
        if (
            HandOffWithHoldShortOfC(scenario =>
            {
                scenario.SoloTrainingMode = true;
                scenario.StudentPositionType = "GND";
            })
            is not { } run
        )
        {
            return;
        }

        run.Aircraft.TunedPosition = TrackOwner.Elsewhere;
        run.Engine.TickPhysics(SubTick);

        Assert.True(run.Bar.Unable, "the hold short of C was not flagged unable on the hand-off tick");
        Assert.DoesNotContain(
            run.Aircraft.PendingPilotTransmissions,
            t => t.Text.Contains("unable to hold short of", StringComparison.OrdinalIgnoreCase)
        );
        string warning = Assert.Single(run.Aircraft.PendingWarnings, w => w.Contains("unable to hold short of", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("unable to hold short of C, stopping", warning);
    }

    /// <summary>
    /// A runway bar carried as the combined centreline id (<c>28R/10L</c>) is named by one end, never both: with 28R the active end
    /// at OAK, a crossing bar and a runway <c>HS</c> bar on B south of 28R are both named "runway 28R" by the unable call.
    /// </summary>
    [Theory]
    [InlineData(HoldShortReason.RunwayCrossing)]
    [InlineData(HoldShortReason.ExplicitHoldShort)]
    public void UnableCallTarget_CombinedRunwayBar_NamesTheActiveEnd(HoldShortReason reason)
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        GroundNode southBar28R =
            layout
                .Nodes.Values.Where(n =>
                    (n.Type == GroundNodeType.RunwayHoldShort)
                    && (n.RunwayId is { } id)
                    && id.Contains("28R")
                    && n.Edges.Any(e => e.MatchesTaxiway("B"))
                )
                .MinBy(n => GeoMath.DistanceNm(n.Position, (layout.FindIntersectionNode("V", "B") ?? n).Position))
            ?? throw new InvalidOperationException("OAK has no 28R hold-short bar on B");
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C56X",
            Position = southBar28R.Position,
            TrueHeading = new TrueHeading(0.0),
            IsOnGround = true,
        };
        var bar = new HoldShortPoint
        {
            NodeId = southBar28R.Id,
            Reason = reason,
            TargetName = "28R/10L",
        };
        var warnings = new List<string>();
        ActiveRunways active = ActiveRunwayListParser.FromTokenLists(new Dictionary<string, List<string>?> { ["OAK"] = ["28R"] }, "test", warnings);
        Assert.Empty(warnings);

        HoldShortTarget target = Assert.IsType<HoldShortTarget>(TaxiingPhase.UnableCallTarget(aircraft, bar, layout, active));
        Assert.Equal("28R", target.Target);
        PilotSpeechText call = PilotResponder.BuildUnableHoldShortOnTheMove(aircraft, target);
        Assert.DoesNotContain("/", call.Terminal);
        Assert.Contains("runway 28R", call.Terminal);
        Assert.DoesNotContain("one zero left", call.Tts);
    }

    /// <summary>The route's hold short of C on the aircraft's assigned route as it stands now.</summary>
    private static HoldShortPoint BarOfC(AircraftState aircraft) =>
        Assert.Single(Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute).HoldShortPoints, h => h.TargetName == "C");

    /// <summary>
    /// Replaces the route's C bar with a copy carrying <paramref name="targetName"/> (<see cref="HoldShortPoint.TargetName"/> is
    /// init-only) and returns the copy.
    /// </summary>
    private static HoldShortPoint ReplaceTargetName(HandOff run, string targetName)
    {
        int index = run.Route.HoldShortPoints.IndexOf(run.Bar);
        var renamed = new HoldShortPoint
        {
            NodeId = run.Bar.NodeId,
            Reason = run.Bar.Reason,
            TargetName = targetName,
            IsCleared = run.Bar.IsCleared,
            ClearedByAutoCross = run.Bar.ClearedByAutoCross,
            RecrossesLandingRunway = run.Bar.RecrossesLandingRunway,
            IsExpedited = run.Bar.IsExpedited,
            Latitude = run.Bar.Latitude,
            Longitude = run.Bar.Longitude,
            WindowJudged = run.Bar.WindowJudged,
            TailOverRunwayNodeId = run.Bar.TailOverRunwayNodeId,
            EndsNoDestinationRoute = run.Bar.EndsNoDestinationRoute,
        };
        run.Route.HoldShortPoints[index] = renamed;
        return renamed;
    }

    /// <summary>Replaces the aircraft's phase list and assigned route with ones restored from their snapshots.</summary>
    private static void Restore(HandOff run)
    {
        PhaseList phases = run.Aircraft.Phases ?? throw new InvalidOperationException($"{Callsign} has no phase list");
        TaxiRoute route = run.Aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException($"{Callsign} has no taxi route");
        run.Aircraft.Phases = PhaseList.FromSnapshot(phases.ToSnapshot(), run.Layout);
        run.Aircraft.Ground.AssignedTaxiRoute =
            TaxiRoute.FromSnapshot(route.ToSnapshot(), run.Layout) ?? throw new InvalidOperationException($"{Callsign}'s route did not restore");
        Assert.IsType<CrossingRunwayPhase>(run.Aircraft.Phases.Phases[run.Aircraft.Phases.CurrentIndex - 1]);
    }

    /// <summary>The engine, layout, aircraft, its route, the C bar and every pilot speech it said, at the hand-off.</summary>
    private sealed record HandOff(
        SimulationEngine Engine,
        AirportGroundLayout Layout,
        AircraftState Aircraft,
        TaxiRoute Route,
        HoldShortPoint Bar,
        List<PilotSpeechText> Said
    );

    /// <summary>
    /// Taxis N462NX up B cleared across 28L and 28R, gives <c>HS C</c> <see cref="HoldShortSubTick"/> sub-ticks into the 28R
    /// crossing (asserting it is not answered unable), and ticks sub-tick by sub-tick until the crossing has handed the route
    /// back: the taxi phase is current and has not yet ticked. Null when the KOAK layout is unavailable.
    /// </summary>
    private static HandOff? HandOffWithHoldShortOfC(Action<SimScenarioState> configure)
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return null;
        }

        SimScenarioState scenario = NewScenario();
        scenario.RpoShowPilotSpeech = true;
        configure?.Invoke(scenario);
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped()) { Scenario = scenario };
        GroundNode start = layout.FindIntersectionNode("V", "B") ?? throw new InvalidOperationException("OAK has no V/B junction");
        GroundNode southBar28L =
            layout
                .Nodes.Values.Where(n =>
                    (n.Type == GroundNodeType.RunwayHoldShort)
                    && (n.RunwayId is { } id)
                    && id.Contains("28L")
                    && n.Edges.Any(e => e.MatchesTaxiway("B"))
                )
                .MinBy(n => GeoMath.DistanceNm(n.Position, start.Position))
            ?? throw new InvalidOperationException("OAK has no 28L hold-short bar on B");
        AircraftState aircraft = SpawnHolding(
            engine,
            layout,
            start.Position,
            new TrueHeading(GeoMath.BearingTo(start.Position, southBar28L.Position))
        );
        var said = new List<PilotSpeechText>();
        engine.PilotSpeechEmitted += (who, speech) =>
        {
            if (who == Callsign)
            {
                said.Add(speech);
            }
        };

        Assert.True(engine.SendCommand(Callsign, "TAXI B C D @SIG7 HS 28L").Success);
        Assert.True(
            TickUntil(engine, 600, () => (aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.TargetName: { } t }) && t.Contains("28L"))
        );
        Assert.True(engine.SendCommand(Callsign, "CROSS").Success);
        engine.TickOneSecond();
        Assert.True(engine.SendCommand(Callsign, "CROSS").Success);
        Assert.True(TickUntil(engine, 300, () => (aircraft.Phases?.CurrentPhase is CrossingRunwayPhase { RunwayId: { } r }) && r.Contains("28R")));
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);

        for (int sub = 0; sub < HoldShortSubTick; sub++)
        {
            engine.TickPhysics(SubTick);
        }

        Assert.IsType<CrossingRunwayPhase>(aircraft.Phases?.CurrentPhase);
        CommandResult hs = engine.SendCommand(Callsign, "HS C");
        Assert.True(hs.Success, hs.Message);
        HoldShortPoint bar = Assert.Single(route.HoldShortPoints, h => h.TargetName == "C");
        Assert.False(bar.Unable, $"HS C was answered unable at dispatch: {hs.Message}");

        for (int sub = 0; (sub < 40) && (aircraft.Phases?.CurrentPhase is CrossingRunwayPhase); sub++)
        {
            engine.TickPhysics(SubTick);
        }

        TaxiingPhase taxi = Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        Assert.Equal(0.0, taxi.ElapsedSeconds);
        Assert.Same(route, aircraft.Ground.AssignedTaxiRoute);
        Assert.False(bar.Unable, "the hold short of C was flagged unable before the hand-off");
        return new HandOff(engine, layout, aircraft, route, bar, said);
    }

    private static SimScenarioState NewScenario() =>
        new()
        {
            ScenarioId = "yaat-1468",
            ScenarioName = "YAAT-1468 crossing hand-off unable hold short",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
        };

    private static AircraftState SpawnHolding(SimulationEngine engine, AirportGroundLayout layout, LatLon position, TrueHeading heading)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C56X",
            Position = position,
            TrueHeading = heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KSNA",
                Destination = "KOAK",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(41000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    private static bool TickUntil(SimulationEngine engine, int maxSeconds, Func<bool> done)
    {
        for (int second = 0; second < maxSeconds; second++)
        {
            engine.TickOneSecond();
            if (done())
            {
                return true;
            }
        }

        return false;
    }
}
