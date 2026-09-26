using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A TAXI issued from the ramp a few feet inside the navigator's turning diameter of a fillet's near end, facing away
/// from it — where a bare <c>PUSH T7B</c> off SFO gate E2 stands when the scenario's wall-clock <c>WAIT 30 TAXI T7B $7B</c>
/// preset fires mid-push (SKW5707 in the S1-SFO-2 recording). The route is a free-space leg to the fillet's ramp end,
/// the T7B–RAMP fillet, then T7B. The entry-alignment arc is aimed at the first node outside the turning diameter — the
/// fillet's FAR end — and rolls out on the straight line through it; the leg it aimed past is retired and the fillet
/// becomes the current segment with the aircraft ~50 ft from its start. Played as its Bézier from the nearest curve
/// point, the ~27 ft cross-track offset is bled off over the ~20 ft of arc that remain, ~2 ft per sub-tick on top of the
/// 1.5 ft driven, and invariant I8 throws. A node aim that ends on a fillet must hand over on the tangent line to the
/// aimed node, not on a curve the aircraft is not standing on.
/// </summary>
public sealed class NodeAimedEntryOntoFilletTests(ITestOutputHelper output)
{
    private const string Callsign = "SKW5707";
    private const string AircraftType = "E75L";
    private const string Gate = "E2";
    private const string Taxiway = "T7B";
    private const string Ramp = "RAMP";
    private const string TaxiCommand = "TAXI T7B $7B";

    /// <summary>Inside the 2 × 25 ft jet turning diameter FindAimNode walks past, outside the 25 ft radius.</summary>
    private const double StandOffFt = 38.0;

    /// <summary>Behind the fillet's ramp end, off its chord (measured 205° in the recording).</summary>
    private const double StandOffBearingRelDeg = 205.0;

    /// <summary>Facing away from the fillet, so the entry is a reversal (measured 194° off the chord).</summary>
    private const double HeadingRelDeg = 194.0;

    private const int TickBudgetSeconds = 120;

    /// <summary>How closely a restored run's per-second ground speed must follow the uninterrupted run's, knots.</summary>
    private const double SpeedTraceToleranceKts = 0.05;

    [Fact]
    public void TaxiFromInsideTheTurningDiameterOfAFilletEnd_HandsOverOnTheAimedLine_NeverTeleports()
    {
        if (BuildCase(StandOffFt, StandOffBearingRelDeg, HeadingRelDeg) is not { } fc)
        {
            return;
        }

        double diameterFt = 2.0 * CategoryPerformance.MainGearTurnRadiusFt(AircraftCategorization.Categorize(AircraftType));
        double toRampEndFt = GeoMath.DistanceNm(fc.Pose, fc.RampEnd.Position) * GeoMath.FeetPerNm;
        double toFarEndFt = GeoMath.DistanceNm(fc.Pose, fc.FarEnd.Position) * GeoMath.FeetPerNm;
        Assert.True(
            toRampEndFt < diameterFt,
            $"ramp end {fc.RampEnd.Id} is {toRampEndFt:F1} ft away; the aim only walks past a node inside {diameterFt:F0} ft"
        );
        Assert.True(
            toFarEndFt >= diameterFt,
            $"far end {fc.FarEnd.Id} is {toFarEndFt:F1} ft away; it must be the first node outside {diameterFt:F0} ft"
        );

        TaxiRoute route = SendTaxiOverTheFilletSecond(fc);
        AircraftState aircraft = fc.Aircraft;

        int reachedAt = -1;
        Exception? fault = Record.Exception(() =>
        {
            reachedAt = SfoGroundHarness.TickUntil(
                fc.Ground.Engine,
                () => route.CurrentSegmentIndex >= 2,
                TickBudgetSeconds,
                second =>
                    output.WriteLine(
                        $"t={second, 3} hdg={aircraft.TrueHeading.Degrees, 6:F1} gs={aircraft.GroundSpeed, 5:F2} seg={route.CurrentSegmentIndex}/{route.Segments.Count} "
                            + $"toFarEnd={FeetTo(aircraft, fc.FarEnd), 6:F1}ft phase={aircraft.Phases?.CurrentPhase?.Name}"
                    )
            );
        });

        Assert.True(
            fault is null,
            $"the navigator wrote {Callsign} somewhere it did not drive to on the way to the fillet's far end: {fault?.Message}"
        );
        Assert.True(reachedAt > 0, $"{Callsign} never arrived at the fillet's far end {fc.FarEnd.Id} within {TickBudgetSeconds} s");
    }

    /// <summary>
    /// The same TAXI, with the whole sim snapshotted and restored while the aircraft is half-way along the straight
    /// it was handed over on. The navigator's snapshot does not carry its primitive, so the restore rebuilds it from
    /// the route's current segment — the fillet — and must rebuild the straight rather than the curve the aircraft is
    /// ~20 ft off.
    /// </summary>
    [Fact]
    public void NodeAimedEntryOntoFillet_SnapshotMidStraight_ResumesWithoutTeleport()
    {
        if (BuildCase(StandOffFt, StandOffBearingRelDeg, HeadingRelDeg) is not { } fc)
        {
            return;
        }

        TaxiRoute route = SendTaxiOverTheFilletSecond(fc);
        TickToMidStraight(fc, route);
        output.WriteLine(
            $"snapshot at {FeetTo(fc.Aircraft, fc.FarEnd):F1} ft from {fc.FarEnd.Id}, {FeetTo(fc.Aircraft, fc.Fillet):F1} ft off the curve"
        );

        fc.Ground.Engine.RestoreFromSnapshot(fc.Ground.Engine.CaptureSnapshot());
        AircraftState restored = Assert.IsType<AircraftState>(fc.Ground.Engine.FindAircraft(Callsign));
        TaxiRoute restoredRoute = Assert.IsType<TaxiRoute>(restored.Ground.AssignedTaxiRoute);

        int reachedAt = -1;
        Exception? fault = Record.Exception(() =>
            reachedAt = SfoGroundHarness.TickUntil(fc.Ground.Engine, () => restoredRoute.CurrentSegmentIndex >= 2, TickBudgetSeconds, null)
        );

        Assert.True(fault is null, $"the navigator wrote the restored {Callsign} somewhere it did not drive to: {fault?.Message}");
        Assert.True(reachedAt > 0, $"the restored {Callsign} never arrived at the fillet's far end {fc.FarEnd.Id} within {TickBudgetSeconds} s");
    }

    /// <summary>
    /// The straight the alignment arc hands over on is a straight: nothing on it corners, so the fillet's cornering
    /// profile — the speed its curve allows — does not cap the aircraft along it. Held to the profile, the aircraft
    /// creeps the whole line at the speed of the fillet's tightest stretch.
    /// </summary>
    [Fact]
    public void AimedStraightOverFillet_IsNotHeldToTheFilletsCorneringSpeed()
    {
        if (BuildCase(StandOffFt, StandOffBearingRelDeg, HeadingRelDeg) is not { } fc)
        {
            return;
        }

        TaxiRoute route = SendTaxiOverTheFilletSecond(fc);
        int onFilletAt = SfoGroundHarness.TickUntil(fc.Ground.Engine, () => route.CurrentSegmentIndex >= 1, TickBudgetSeconds, null);
        Assert.True(onFilletAt > 0, $"{Callsign} never reached the fillet segment within {TickBudgetSeconds} s");

        double corneringCapKts = CorneringCapAtFilletEntryKts(fc);
        double peakKts = 0.0;
        SfoGroundHarness.TickUntil(
            fc.Ground.Engine,
            () => route.CurrentSegmentIndex != 1,
            TickBudgetSeconds,
            second =>
            {
                peakKts = Math.Max(peakKts, fc.Aircraft.GroundSpeed);
                output.WriteLine(
                    $"t={second, 3} gs={fc.Aircraft.GroundSpeed, 5:F2} toFarEnd={FeetTo(fc.Aircraft, fc.FarEnd), 6:F1}ft onArc={fc.Aircraft.Ground.LastNavDiag?.OnArc}"
                );
            }
        );

        output.WriteLine($"fillet's slowest cornering speed {corneringCapKts:F2} kt; peak ground speed on the aimed straight {peakKts:F2} kt");
        Assert.True(
            peakKts > corneringCapKts + CorneringCapMarginKts,
            $"{Callsign} was held to {peakKts:F2} kt on the aimed straight, the fillet's cornering speed ({corneringCapKts:F2} kt), "
                + "though the straight does not corner"
        );
    }

    /// <summary>
    /// A snapshot and restore half-way along the aimed straight resumes on the same speed trace as the run that was
    /// never interrupted: the restore rebuilds the same straight with the same speed caps, so every second's ground
    /// speed matches.
    /// </summary>
    [Fact]
    public void AimedStraightOverFillet_SnapshotMidStraight_ResumesOnTheSameSpeedTrace()
    {
        if (BuildCase(StandOffFt, StandOffBearingRelDeg, HeadingRelDeg) is not { } fc)
        {
            return;
        }

        TaxiRoute route = SendTaxiOverTheFilletSecond(fc);
        TickToMidStraight(fc, route);
        StateSnapshotDto snapshot = fc.Ground.Engine.CaptureSnapshot();

        List<double> uninterrupted = SpeedTraceToFarEnd(fc, route);
        fc.Ground.Engine.RestoreFromSnapshot(snapshot);
        AircraftState restored = Assert.IsType<AircraftState>(fc.Ground.Engine.FindAircraft(Callsign));
        TaxiRoute restoredRoute = Assert.IsType<TaxiRoute>(restored.Ground.AssignedTaxiRoute);
        List<double> resumed = SpeedTrace(fc, restored, uninterrupted.Count);

        for (int i = 0; i < uninterrupted.Count; i++)
        {
            output.WriteLine($"+{i + 1, 3}s uninterrupted {uninterrupted[i], 5:F2} restored {resumed[i], 5:F2}");
        }

        for (int i = 0; i < uninterrupted.Count; i++)
        {
            Assert.True(
                Math.Abs(uninterrupted[i] - resumed[i]) <= SpeedTraceToleranceKts,
                $"{i + 1} s after the snapshot the restored {Callsign} taxis at {resumed[i]:F2} kt, the uninterrupted run at {uninterrupted[i]:F2} kt"
            );
        }

        Assert.True(restoredRoute.CurrentSegmentIndex >= 2, $"the restored {Callsign} did not reach the fillet's far end on the same trace");
    }

    /// <summary>
    /// The branch where the alignment arc is aimed at the current fillet's OWN end node: the aircraft stands on the
    /// fillet's ramp end, turned nearly round from its entry, so the route starts on the fillet with a reversal whose aim is the fillet's
    /// far end, outside the turning diameter. The arc rolls out on the line through that node, so the fillet is flown
    /// as that line and the arrival is not a teleport onto the curve.
    /// </summary>
    [Fact]
    public void TaxiFromTheFilletsRampEndTurnedOffIt_HandsOverOnTheLineToItsOwnEnd_NeverTeleports()
    {
        if (BuildCase(standOffFt: 0.0, standOffBearingRelDeg: 0.0, headingRelDeg: 0.0) is not { } fc)
        {
            return;
        }

        // Nose turned nearly round from the fillet's entry tangent: a reversal onto a painted leg, which is aimed at a
        // route node rather than at the leg's bearing.
        CubicBezier curve = fc.Fillet.ToBezier();
        double entryDeg =
            fc.Fillet.Nodes[0].Id == fc.RampEnd.Id ? curve.TangentBearing(0.0) : new TrueHeading(curve.TangentBearing(1.0)).ToReciprocal().Degrees;
        fc.Aircraft.TrueHeading = new TrueHeading(entryDeg + OwnEndOffEntryDeg);
        output.WriteLine($"entry tangent {entryDeg:F1}°, nose {fc.Aircraft.TrueHeading.Degrees:F1}°");

        CommandResult result = fc.Ground.Engine.SendCommand(Callsign, TaxiCommand);
        Assert.True(result.Success, $"{TaxiCommand} failed: {result.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(fc.Aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.True(
            (route.Segments.Count >= 2)
                && (route.Segments[0].Edge.Edge is GroundArc)
                && (route.Segments[0].FromNodeId == fc.RampEnd.Id)
                && (route.Segments[0].ToNodeId == fc.FarEnd.Id),
            $"expected the fillet {fc.RampEnd.Id}→{fc.FarEnd.Id} as the route's first segment; the route does not exercise the aim-at-own-end case"
        );

        bool flownAsLine = false;
        int reachedAt = -1;
        Exception? fault = Record.Exception(() =>
            reachedAt = SfoGroundHarness.TickUntil(
                fc.Ground.Engine,
                () => route.CurrentSegmentIndex >= 1,
                TickBudgetSeconds,
                second =>
                {
                    bool onLine = (route.CurrentSegmentIndex == 0) && (fc.Aircraft.Ground.LastNavDiag is { OnArc: false });
                    flownAsLine |= onLine;
                    output.WriteLine(
                        $"t={second, 3} hdg={fc.Aircraft.TrueHeading.Degrees, 6:F1} gs={fc.Aircraft.GroundSpeed, 5:F2} seg={route.CurrentSegmentIndex} "
                            + $"toFarEnd={FeetTo(fc.Aircraft, fc.FarEnd), 6:F1}ft offCurve={FeetTo(fc.Aircraft, fc.Fillet), 5:F1}ft onLine={onLine}"
                    );
                }
            )
        );

        Assert.True(fault is null, $"the navigator wrote {Callsign} somewhere it did not drive to on the fillet: {fault?.Message}");
        Assert.True(reachedAt > 0, $"{Callsign} never arrived at the fillet's far end {fc.FarEnd.Id} within {TickBudgetSeconds} s");
        Assert.True(flownAsLine, $"{Callsign} never flew the fillet as the aimed line; the case does not exercise the own-end hand-over");
    }

    /// <summary>How far off the fillet's entry tangent the own-end case turns the nose: past the 135° reversal threshold.</summary>
    private const double OwnEndOffEntryDeg = 160.0;

    /// <summary>How far above the fillet's slowest cornering speed the aimed straight must get to show it is not held to it, knots.</summary>
    private const double CorneringCapMarginKts = 0.5;

    private sealed record FilletCase(SfoGround Ground, AircraftState Aircraft, GroundArc Fillet, GroundNode RampEnd, GroundNode FarEnd, LatLon Pose);

    /// <summary>
    /// SFO with SKW5707 holding <paramref name="standOffFt"/> from the ramp end of the T7B–RAMP fillet nearest gate E2,
    /// on a bearing <paramref name="standOffBearingRelDeg"/> off the fillet's chord, nose <paramref name="headingRelDeg"/>
    /// off it; null when SFO's layout is missing.
    /// </summary>
    private FilletCase? BuildCase(double standOffFt, double standOffBearingRelDeg, double headingRelDeg)
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return null;
        }

        AirportGroundLayout layout = ground.Layout;
        GroundNode gate = layout.FindParkingByName(Gate) ?? throw new InvalidOperationException($"SFO layout has no parking named '{Gate}'");
        GroundArc fillet =
            layout
                .Arcs.Where(a => a.MatchesTaxiway(Taxiway) && a.MatchesTaxiway(Ramp))
                .OrderBy(a => GeoMath.DistanceNm(gate.Position, a.Nodes[0].Position) + GeoMath.DistanceNm(gate.Position, a.Nodes[1].Position))
                .FirstOrDefault()
            ?? throw new InvalidOperationException($"SFO layout has no {Taxiway}–{Ramp} fillet");
        GroundNode rampEnd =
            GeoMath.DistanceNm(gate.Position, fillet.Nodes[0].Position) < GeoMath.DistanceNm(gate.Position, fillet.Nodes[1].Position)
                ? fillet.Nodes[0]
                : fillet.Nodes[1];
        GroundNode farEnd = fillet.OtherNode(rampEnd);

        double chordDeg = GeoMath.BearingTo(rampEnd.Position, farEnd.Position);
        LatLon pose = GeoMath.ProjectPoint(rampEnd.Position, new TrueHeading(chordDeg + standOffBearingRelDeg), standOffFt / GeoMath.FeetPerNm);
        AircraftState aircraft = SpawnHolding(ground, pose, new TrueHeading(chordDeg + headingRelDeg));
        return new FilletCase(ground, aircraft, fillet, rampEnd, farEnd, pose);
    }

    /// <summary>Sends the TAXI and asserts the route is the approach leg to the ramp end, then the fillet, then T7B.</summary>
    private TaxiRoute SendTaxiOverTheFilletSecond(FilletCase fc)
    {
        CommandResult result = fc.Ground.Engine.SendCommand(Callsign, TaxiCommand);
        Assert.True(result.Success, $"{TaxiCommand} failed: {result.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(fc.Aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);
        Assert.True(
            (route.Segments.Count >= 3)
                && (route.Segments[0].ToNodeId == fc.RampEnd.Id)
                && (route.Segments[1].Edge.Edge is GroundArc)
                && (route.Segments[1].FromNodeId == fc.RampEnd.Id)
                && (route.Segments[1].ToNodeId == fc.FarEnd.Id),
            $"expected approach leg → {fc.RampEnd.Id} then the fillet {fc.RampEnd.Id}→{fc.FarEnd.Id}; the route does not exercise the aim-past-a-fillet case"
        );
        return route;
    }

    /// <summary>Ticks to the hand-over onto the fillet segment, then on to half-way from there to the fillet's far end, on the aimed line.</summary>
    private static void TickToMidStraight(FilletCase fc, TaxiRoute route)
    {
        int onFilletAt = SfoGroundHarness.TickUntil(fc.Ground.Engine, () => route.CurrentSegmentIndex >= 1, TickBudgetSeconds, null);
        Assert.True(onFilletAt > 0, $"{Callsign} never reached the fillet segment within {TickBudgetSeconds} s");
        Assert.Equal(1, route.CurrentSegmentIndex);
        double handOverFt = FeetTo(fc.Aircraft, fc.FarEnd);

        int midwayAt = SfoGroundHarness.TickUntil(
            fc.Ground.Engine,
            () => (route.CurrentSegmentIndex != 1) || (FeetTo(fc.Aircraft, fc.FarEnd) <= handOverFt / 2.0),
            TickBudgetSeconds,
            null
        );
        Assert.True(midwayAt > 0, $"{Callsign} never got half-way from the hand-over ({handOverFt:F1} ft) to {fc.FarEnd.Id}");
        Assert.True(route.CurrentSegmentIndex == 1, $"{Callsign} left the fillet segment before half-way; the snapshot premise does not hold");
        Assert.True(
            fc.Aircraft.Ground.LastNavDiag is { OnArc: false },
            $"{Callsign} is on a curve half-way to {fc.FarEnd.Id}, not on the aimed line"
        );
    }

    /// <summary>Each second's ground speed from now until the route reaches its third segment, plus a few seconds beyond.</summary>
    private static List<double> SpeedTraceToFarEnd(FilletCase fc, TaxiRoute route)
    {
        var trace = new List<double>();
        int reachedAt = SfoGroundHarness.TickUntil(
            fc.Ground.Engine,
            () => route.CurrentSegmentIndex >= 2,
            TickBudgetSeconds,
            _ => trace.Add(fc.Aircraft.GroundSpeed)
        );
        Assert.True(reachedAt > 0, $"{Callsign} never arrived at the fillet's far end {fc.FarEnd.Id} within {TickBudgetSeconds} s");
        trace.AddRange(SpeedTrace(fc, fc.Aircraft, TraceTailSeconds));
        return trace;
    }

    /// <summary>Each second's ground speed of <paramref name="aircraft"/> over the next <paramref name="seconds"/> seconds.</summary>
    private static List<double> SpeedTrace(FilletCase fc, AircraftState aircraft, int seconds)
    {
        var trace = new List<double>(seconds);
        for (int i = 0; i < seconds; i++)
        {
            fc.Ground.Engine.TickOneSecond();
            trace.Add(aircraft.GroundSpeed);
        }

        return trace;
    }

    /// <summary>Seconds of trace past the fillet's far end, onto T7B.</summary>
    private const int TraceTailSeconds = 3;

    /// <summary>The fillet's cornering profile at its entry: its slowest sample, the speed its tightest stretch allows.</summary>
    private static double CorneringCapAtFilletEntryKts(FilletCase fc) =>
        fc.Fillet.SpeedProfile(AircraftCategorization.Categorize(AircraftType)).Min(s => s.SpeedKts);

    private static double FeetTo(AircraftState aircraft, GroundNode node) => GeoMath.DistanceNm(aircraft.Position, node.Position) * GeoMath.FeetPerNm;

    private static double FeetTo(AircraftState aircraft, GroundArc fillet)
    {
        CubicBezier curve = fillet.ToBezier();
        (double lat, double lon) = curve.Evaluate(curve.ClosestT(aircraft.Position, ClosestTIterations));
        return GeoMath.DistanceNm(aircraft.Position, new LatLon(lat, lon)) * GeoMath.FeetPerNm;
    }

    private const int ClosestTIterations = 20;

    /// <summary>
    /// A stationary aircraft holding at an arbitrary ramp pose — <see cref="SfoGroundHarness.SpawnAt"/> without the node.
    /// </summary>
    private static AircraftState SpawnHolding(SfoGround ground, LatLon position, TrueHeading heading)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = position,
            TrueHeading = heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ground.Layout));
        aircraft.Ground.Layout = ground.Layout;
        ground.Engine.World.AddAircraft(aircraft);
        return aircraft;
    }
}
