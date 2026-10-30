using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Situation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// The named exits ahead an arrival can make (YAAT-463): on the rollout, <see cref="LandingPhase.ListExitsAhead"/> lists exactly the
/// taxiways an <c>EL</c>/<c>ER &lt;twy&gt;</c> would be accepted for, read-only; on the last miles of final,
/// <see cref="FinalApproachExitForecast"/> forecasts the same judgement from a projected touchdown; the <c>Situation</c> step stores
/// the list on <see cref="AircraftSituationState.ExitsAhead"/>. Real OAK and SFO layouts and navdata.
/// </summary>
public class LandingExitListTests
{
    private const string GroundDataWhy = "named-exit list and exit choice on today's OAK and SFO layouts";

    private const string ReplayRecordingPath = "TestData/issue276-er-exit-left-recording.zip";
    private const string ReplayCallsign = "LXJ574";

    // LXJ574's hand-off to the runway exit, measured on the replay with the Situation step's list left out.
    private const double ExitHandoffSecond = 540;
    private const string ExitTaxiway = "D";
    private const int ExitHoldShortId = 832;

    public LandingExitListTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // --- Staging ---

    /// <summary>A <see cref="LandingPhase"/> started on the ground, so it is in its rollout from the first tick.</summary>
    private sealed record Staged(LandingPhase Phase, PhaseContext Ctx, AircraftState Aircraft, RunwayInfo Runway, AirportGroundLayout Layout)
    {
        /// <summary>Stages the same aircraft afresh, for a judgement that must not see an earlier one's refusal.</summary>
        public required Func<Staged> Restage { get; init; }

        public IReadOnlyList<ExitAheadDto> List() => Assert.IsAssignableFrom<IReadOnlyList<ExitAheadDto>>(Phase.ListExitsAhead(Aircraft));

        /// <summary>Whether <c>EL</c>/<c>ER &lt;twy&gt;</c> is accepted, judged on a fresh staging.</summary>
        public bool Accepts(string taxiway, ExitSide side)
        {
            Staged fresh = Restage();
            var preference = new ExitPreference { Side = side, Taxiway = taxiway };
            return fresh.Phase.EvaluateAndApplyNamedExitInstruction(fresh.Aircraft, preference, expedite: false).Allowed;
        }
    }

    /// <summary>An arrival on final in an engine, cleared to land, with the production landing chain behind its final.</summary>
    private sealed record OnFinal(SimulationEngine Engine, AircraftState Aircraft, RunwayInfo Runway, AirportGroundLayout Layout, LatLon Threshold)
    {
        public IReadOnlyList<ExitAheadDto>? Forecast() => FinalApproachExitForecast.ListExitsAhead(Aircraft, Layout, null, 0);

        /// <summary>Puts the aircraft on the extended centreline <paramref name="distNm"/> from the landing threshold, on the glidepath.</summary>
        public void MoveTo(double distNm)
        {
            Aircraft.Position = GeoMath.ProjectPoint(Threshold, Runway.TrueHeading.ToReciprocal(), distNm);
            Aircraft.Altitude = GlideSlopeGeometry.AltitudeAtDistance(
                distNm,
                Runway.ElevationFt,
                AircraftCategorization.Categorize(Aircraft.AircraftType)
            );
        }
    }

    private static Staged? StageRollout(string airport, string designator, string aircraftType, double alongFt, double ias, LahsoTarget? lahso)
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout(airport);
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway(airport, designator);
        if ((layout is null) || (runway is null))
        {
            return null;
        }

        LatLon position = GeoMath.ProjectPoint(LandingThreshold.Resolve(runway, layout), runway.TrueHeading, alongFt / GeoMath.FeetPerNm);
        var aircraft = new AircraftState
        {
            Callsign = "UAL463",
            AircraftType = aircraftType,
            Position = position,
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = ias,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
            Phases = new PhaseList { AssignedRunway = runway, LahsoHoldShort = lahso },
        };
        aircraft.Ground.Layout = layout;
        PhaseContext ctx = new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategorization.Categorize(aircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };

        var phase = new LandingPhase();
        phase.OnStart(ctx);
        Assert.Equal(LandingPhase.State.Rollout, phase.CurrentState);
        return new Staged(phase, ctx, aircraft, runway, layout)
        {
            Restage = () => StageRollout(airport, designator, aircraftType, alongFt, ias, lahso)!,
        };
    }

    /// <summary>
    /// <see cref="ShortFinalArrival.SpawnClearedToLand"/> <paramref name="distNm"/> out, then flying and targeting
    /// <paramref name="ias"/> before the first tick; null when the data is missing.
    /// </summary>
    private static OnFinal? SpawnOnFinal(string airport, string designator, string aircraftType, double distNm, double ias)
    {
        if (ShortFinalArrival.SpawnClearedToLand(airport, designator, aircraftType, "UAL463", distNm) is not { } spawned)
        {
            return null;
        }

        (SimulationEngine engine, AircraftState aircraft, RunwayInfo runway) = spawned;
        aircraft.IndicatedAirspeed = ias;
        aircraft.Targets.TargetSpeed = ias;
        AirportGroundLayout layout = aircraft.Ground.Layout!;
        return new OnFinal(engine, aircraft, runway, layout, LandingThreshold.Resolve(runway, layout));
    }

    /// <summary>
    /// Ticks <paramref name="engine"/> a second at a time until <paramref name="done"/> holds, failing after <paramref name="maxSeconds"/>.
    /// </summary>
    private static void TickUntil(SimulationEngine engine, Func<bool> done, int maxSeconds, string what)
    {
        for (int t = 0; t < maxSeconds; t++)
        {
            if (done())
            {
                return;
            }

            engine.TickOneSecond();
        }

        Assert.True(done(), $"never reached {what} within {maxSeconds} s");
    }

    private static bool IsRollingOut(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase is LandingPhase { CurrentState: LandingPhase.State.Rollout };

    /// <summary>
    /// Every taxiway name on an edge — straight, branch or arc — at a centerline node of <paramref name="designator"/>, the authored
    /// no-turnoff taxiways included.
    /// </summary>
    private static List<string> TaxiwaysOnCenterline(AirportGroundLayout layout, string designator)
    {
        SortedSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (
            GroundNode node in layout.Nodes.Values.Where(node => node.Edges.Any(edge => edge.IsRunwayCenterline && edge.MatchesRunway(designator)))
        )
        {
            foreach (IGroundEdge edge in node.Edges)
            {
                string[] edgeNames = edge is GroundArc arc ? arc.TaxiwayNames : [edge.TaxiwayName];
                names.UnionWith(edgeNames.Where(name => !name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)));
            }
        }

        return [.. names];
    }

    private static HashSet<string> NoTurnoff(AirportGroundLayout layout, string designator) =>
        new(layout.FindRunway(designator)?.NoTurnoffForEnd(designator) ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>The taxiways with a bar of <paramref name="designator"/> inside its holding distance, and those whose every bar is.</summary>
    private static (HashSet<string> AnyShort, HashSet<string> AllShort) ShortBarTaxiways(AirportGroundLayout layout, string designator)
    {
        Dictionary<string, List<bool>> barsAtHolding = new(StringComparer.OrdinalIgnoreCase);
        foreach (GroundNode bar in layout.GetRunwayHoldShortNodes(designator))
        {
            bool atHolding = layout.IsAtRunwayHoldingDistance(bar, designator);
            foreach (string name in bar.Edges.Select(edge => edge.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                barsAtHolding.TryAdd(name, []);
                barsAtHolding[name].Add(atHolding);
            }
        }

        return (
            new(barsAtHolding.Where(pair => pair.Value.Contains(false)).Select(pair => pair.Key), StringComparer.OrdinalIgnoreCase),
            new(barsAtHolding.Where(pair => !pair.Value.Contains(true)).Select(pair => pair.Key), StringComparer.OrdinalIgnoreCase)
        );
    }

    /// <summary>Whether <c>EL</c>/<c>ER &lt;twy&gt;</c> is accepted on a fresh copy of <paramref name="aircraft"/>, so no refusal lingers.</summary>
    private static bool AcceptedOnACopy(AircraftState aircraft, AirportGroundLayout layout, string taxiway, ExitSide side)
    {
        var copy = AircraftState.FromSnapshot(aircraft.ToSnapshot(), layout);
        copy.Ground.Layout = layout;
        var landing = (LandingPhase)copy.Phases!.CurrentPhase!;
        return landing.EvaluateAndApplyNamedExitInstruction(copy, new ExitPreference { Side = side, Taxiway = taxiway }, expedite: false).Allowed;
    }

    private static string Json(object dto) => JsonSerializer.Serialize(dto, dto.GetType(), RecordingJsonOptions.Default);

    private static bool Lists(IReadOnlyList<ExitAheadDto> list, string taxiway, ExitSide side) =>
        list.Any(row => string.Equals(row.Taxiway, taxiway, StringComparison.OrdinalIgnoreCase) && (row.Side == side));

    private static HashSet<(string Taxiway, ExitSide Side)> Keys(IEnumerable<ExitAheadDto> list) =>
        [.. list.Select(row => (row.Taxiway.ToUpperInvariant(), row.Side))];

    /// <summary>The connections ahead of <paramref name="from"/> that the unnamed centerline walk offers, one per taxiway, in walk order.</summary>
    private static List<AirportGroundLayout.CenterlineExitResult> ConnectionsAhead(AirportGroundLayout layout, RunwayInfo runway, LatLon from)
    {
        List<AirportGroundLayout.CenterlineExitResult> found = [];
        _ = layout.FindOnSidePreferredExit(
            from.Lat,
            from.Lon,
            runway.TrueHeading,
            runway.Designator,
            preference: null,
            sidePref: null,
            filter: candidate =>
            {
                if (GeoMath.AlongTrackDistanceNm(candidate.Path[0].Position, from, runway.TrueHeading) > 0)
                {
                    found.Add(candidate);
                }

                return AirportGroundLayout.CandidateVerdict.Skip;
            }
        );
        return found;
    }

    /// <summary>The first connection of <paramref name="exit"/>'s taxiway on its side whose branch is ahead of <paramref name="from"/>.</summary>
    private static AirportGroundLayout.CenterlineExitResult? FirstConnectionAhead(
        AirportGroundLayout layout,
        RunwayInfo runway,
        LatLon from,
        (string Taxiway, ExitSide Side) exit
    ) =>
        layout.FindOnSidePreferredExit(
            from.Lat,
            from.Lon,
            runway.TrueHeading,
            runway.Designator,
            new ExitPreference { Side = exit.Side, Taxiway = exit.Taxiway },
            exit.Side,
            filter: candidate =>
                (candidate.Side == exit.Side) && (GeoMath.AlongTrackDistanceNm(candidate.Path[0].Position, from, runway.TrueHeading) > 0)
                    ? AirportGroundLayout.CandidateVerdict.Accept
                    : AirportGroundLayout.CandidateVerdict.Skip
        );

    // --- Rollout ---

    /// <summary>
    /// (a) Down a flown rollout, over every taxiway name on the runway's centerline: a taxiway is listed on a side only when
    /// <c>EL</c>/<c>ER &lt;twy&gt;</c> would be accepted; one accepted on a side but not listed there is listed on the other (the
    /// search's off-side fallback); authored no-turnoff taxiways and taxiways whose every bar is short of the holding distance are
    /// never listed. The listing leaves the landing phase and the aircraft exactly as they were.
    /// </summary>
    [Theory]
    [InlineData("OAK", "30")]
    [InlineData("SFO", "28R")]
    [InlineData("SFO", "28L")]
    public void RolloutList_AgreesWithTheNamedExitInstruction(string airport, string designator)
    {
        if (ShortFinalArrival.SpawnClearedToLand(airport, designator, "B738", "UAL463", 1.0) is not { } spawned)
        {
            return;
        }

        (SimulationEngine engine, AircraftState aircraft, RunwayInfo runway) = spawned;
        AirportGroundLayout layout = aircraft.Ground.Layout!;
        List<string> universe = TaxiwaysOnCenterline(layout, runway.Designator);
        HashSet<string> noTurnoff = NoTurnoff(layout, runway.Designator);
        (HashSet<string> anyShort, HashSet<string> allShort) = ShortBarTaxiways(layout, runway.Designator);
        TickUntil(engine, () => IsRollingOut(aircraft), 120, "the rollout");

        int samples = 0;
        int listedRows = 0;
        for (int t = 0; (t < 90) && IsRollingOut(aircraft); t += 3)
        {
            var landing = (LandingPhase)aircraft.Phases!.CurrentPhase!;
            string phaseBefore = Json(landing.ToSnapshot());
            string aircraftBefore = Json(aircraft.ToSnapshot());
            ResolvedExitInfo? candidateBefore = landing.CandidateExit;

            IReadOnlyList<ExitAheadDto> list = Assert.IsAssignableFrom<IReadOnlyList<ExitAheadDto>>(landing.ListExitsAhead(aircraft));

            Assert.Equal(phaseBefore, Json(landing.ToSnapshot()));
            Assert.Equal(aircraftBefore, Json(aircraft.ToSnapshot()));
            Assert.Same(candidateBefore, landing.CandidateExit);

            foreach (string taxiway in universe)
            {
                bool acceptedLeft = AcceptedOnACopy(aircraft, layout, taxiway, ExitSide.Left);
                bool acceptedRight = AcceptedOnACopy(aircraft, layout, taxiway, ExitSide.Right);
                bool listedLeft = Lists(list, taxiway, ExitSide.Left);
                bool listedRight = Lists(list, taxiway, ExitSide.Right);

                string at = $"{taxiway} at t+{t} s, gs {aircraft.GroundSpeed:F0} kt";
                Assert.True(!listedLeft || acceptedLeft, $"EL {at} is listed but refused");
                Assert.True(!listedRight || acceptedRight, $"ER {at} is listed but refused");
                if (noTurnoff.Contains(taxiway) || allShort.Contains(taxiway))
                {
                    Assert.False(listedLeft || listedRight, $"{at} is a no-turnoff or short-bar taxiway but is listed");
                    continue;
                }

                if (anyShort.Contains(taxiway))
                {
                    continue;
                }

                string state = $"{at}: accepted L/R {acceptedLeft}/{acceptedRight}, listed L/R {listedLeft}/{listedRight}";
                Assert.True(!acceptedLeft || listedLeft || listedRight, state);
                Assert.True(!acceptedRight || listedRight || listedLeft, state);
            }

            samples++;
            listedRows += list.Count;
            for (int s = 0; s < 3; s++)
            {
                engine.TickOneSecond();
            }
        }

        Assert.True(samples >= 3, $"only {samples} samples on the rollout");
        Assert.True(listedRows > 0, "no exit was ever listed");
    }

    /// <summary>(b) At the same point, a faster aircraft lists a strict subset, and the exits it drops are the nearer ones.</summary>
    [Fact]
    public void FasterAircraft_DropsTheNearerExits()
    {
        if (
            (StageRollout("OAK", "30", "B738", 1500, 70, null) is not { } slow)
            || (StageRollout("OAK", "30", "B738", 1500, 125, null) is not { } fast)
        )
        {
            return;
        }

        IReadOnlyList<ExitAheadDto> slowList = slow.List();
        IReadOnlyList<ExitAheadDto> fastList = fast.List();

        Assert.True(Keys(fastList).IsProperSubsetOf(Keys(slowList)), $"slow: {string.Join(", ", slowList)}; fast: {string.Join(", ", fastList)}");
        HashSet<(string, ExitSide)> kept = Keys(fastList);
        List<ExitAheadDto> dropped = [.. slowList.Where(row => !kept.Contains((row.Taxiway.ToUpperInvariant(), row.Side)))];
        Assert.True((fastList.Count == 0) || (dropped.Min(row => row.DistanceFt) < fastList.Min(row => row.DistanceFt)));
    }

    /// <summary>
    /// (c) Rows carry the side of their connection: each row's taxiway has a bar of this runway on that side, and OAK 30's exits
    /// toward the terminal are right-side rows.
    /// </summary>
    [Fact]
    public void Rows_CarryTheSideOfTheirConnection()
    {
        if (StageRollout("OAK", "30", "B738", 1500, 60, null) is not { } staged)
        {
            return;
        }

        IReadOnlyList<ExitAheadDto> list = staged.List();
        LatLon threshold = LandingThreshold.Resolve(staged.Runway, staged.Layout);
        foreach (ExitAheadDto row in list)
        {
            bool barOnThatSide = staged
                .Layout.GetRunwayHoldShortNodes(staged.Runway.Designator)
                .Where(bar => bar.Edges.Any(edge => string.Equals(edge.TaxiwayName, row.Taxiway, StringComparison.OrdinalIgnoreCase)))
                .Any(bar =>
                    (GeoMath.SignedCrossTrackDistanceNm(bar.Position, threshold, staged.Runway.TrueHeading) > 0) == (row.Side == ExitSide.Right)
                );
            Assert.True(barOnThatSide, $"{row.Taxiway} is listed on the {row.Side} side with no bar there");
        }

        Assert.Contains(list, row => row.Side == ExitSide.Right);
    }

    /// <summary>(d) Under LAHSO, rows whose branch point is past the hold-short point's stop limit are left out.</summary>
    [Fact]
    public void Lahso_RemovesTheExitsBeyondTheHoldShortPoint()
    {
        const double AircraftAlongFt = 1500;
        if (StageRollout("OAK", "30", "B738", AircraftAlongFt, 60, null) is not { } open)
        {
            return;
        }

        List<ExitAheadDto> openList = [.. open.List().OrderBy(row => row.DistanceFt)];
        ExitAheadDto nearest = openList[0];
        ExitAheadDto farther = openList.First(row => row.DistanceFt > nearest.DistanceFt + 400);
        double limitFromAircraftFt = (nearest.DistanceFt + farther.DistanceFt) / 2.0;
        double setbackFt = 50.0 + (AircraftLength.ResolveFt("B738") / 2.0);
        double holdShortFt = AircraftAlongFt + limitFromAircraftFt + setbackFt;
        LatLon holdShortPoint = GeoMath.ProjectPoint(
            LandingThreshold.Resolve(open.Runway, open.Layout),
            open.Runway.TrueHeading,
            holdShortFt / GeoMath.FeetPerNm
        );
        var lahso = new LahsoTarget
        {
            Lat = holdShortPoint.Lat,
            Lon = holdShortPoint.Lon,
            DistFromThresholdNm = holdShortFt / GeoMath.FeetPerNm,
            CrossingRunwayId = "33",
        };

        Staged held = StageRollout("OAK", "30", "B738", AircraftAlongFt, 60, lahso)!;
        IReadOnlyList<ExitAheadDto> heldList = held.List();

        Assert.True(Lists(heldList, nearest.Taxiway, nearest.Side), $"{nearest.Taxiway} fits before the hold-short point but is not listed");
        Assert.False(Lists(heldList, farther.Taxiway, farther.Side), $"{farther.Taxiway} lies past the hold-short point but is listed");
        Assert.All(heldList, row => Assert.True(row.DistanceFt <= limitFromAircraftFt + 50, $"{row.Taxiway} at {row.DistanceFt} ft"));
    }

    /// <summary>(e) A taxiway the crew has given up on this landing is still listed when it passes the test.</summary>
    [Fact]
    public void GivenUpTaxiway_StaysListedWhenItPasses()
    {
        if (StageRollout("OAK", "30", "B738", 1500, 60, null) is not { } staged)
        {
            return;
        }

        ExitAheadDto row = staged.List()[0];
        staged.Aircraft.Phases!.GivenUpExitTaxiways.Add(row.Taxiway);

        Assert.True(Lists(staged.List(), row.Taxiway, row.Side));
    }

    /// <summary>
    /// (f) and (i) One row per taxiway and side, at its first connection ahead (every connection is makeable at this speed), the
    /// distance to that branch point along the runway rounded to the nearest 100 ft.
    /// </summary>
    [Fact]
    public void OneRowPerTaxiwayAndSide_AtItsFirstConnection_RoundedTo100Ft()
    {
        if (StageRollout("OAK", "30", "B738", 1500, 40, null) is not { } staged)
        {
            return;
        }

        IReadOnlyList<ExitAheadDto> list = staged.List();
        Assert.NotEmpty(list);
        Assert.Equal(list.Count, Keys(list).Count);
        foreach (ExitAheadDto row in list)
        {
            AirportGroundLayout.CenterlineExitResult first = Assert.IsType<AirportGroundLayout.CenterlineExitResult>(
                FirstConnectionAhead(staged.Layout, staged.Runway, staged.Aircraft.Position, (row.Taxiway, row.Side))
            );
            double exactFt =
                GeoMath.AlongTrackDistanceNm(first.Path[0].Position, staged.Aircraft.Position, staged.Runway.TrueHeading) * GeoMath.FeetPerNm;
            Assert.Equal(0, row.DistanceFt % 100);
            Assert.Equal((int)(Math.Round(exactFt / 100.0, MidpointRounding.AwayFromZero) * 100), row.DistanceFt);
        }
    }

    /// <summary>
    /// Each row carries its exit's turn-off angle in whole degrees, the angle the layout's exit search measures for that taxiway's
    /// first connection on that side (<see cref="AirportGroundLayout.CenterlineExitResult.ExitAngle"/>): on OAK 28R's rollout the
    /// left exits include both a forward high-speed turn-off and one bending back, so no single fixed angle fits every row.
    /// </summary>
    [Fact]
    public void Rows_CarryTheLayoutsTurnOffAngle_InWholeDegrees()
    {
        if (StageRollout("OAK", "28R", "B738", 1500, 40, null) is not { } staged)
        {
            return;
        }

        IReadOnlyList<ExitAheadDto> list = staged.List();
        Assert.NotEmpty(list);
        foreach (ExitAheadDto row in list)
        {
            AirportGroundLayout.CenterlineExitResult first = Assert.IsType<AirportGroundLayout.CenterlineExitResult>(
                FirstConnectionAhead(staged.Layout, staged.Runway, staged.Aircraft.Position, (row.Taxiway, row.Side))
            );
            Assert.Equal((int)Math.Round(first.ExitAngle, MidpointRounding.AwayFromZero), row.TurnOffAngleDeg);
        }

        Assert.Contains(list, row => row.TurnOffAngleDeg < 60);
        Assert.Contains(list, row => row.TurnOffAngleDeg > 100);
    }

    [Fact]
    public void Oak28R_ExitE_AngleIsItsTurnOffOnThePavement_NotABendBeyond()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "28R"));
        GroundNode bar = Assert.Single(TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "E"));
        double angle = Assert.NotNull(layout.ComputeExitAngle(bar, "E", runway.TrueHeading));
        Assert.InRange(angle, 70.0, 78.0); // E turns off 28R at 74.2° until past the runway edge; the bar's outward edge read 68.1°
    }

    /// <summary>
    /// The path angle over E from the 28R centreline node where it leaves the runway to its bar reads the turn-off across the
    /// runway edge (74.2°), not the 45.4° leg beyond it or the bend toward C the bar sits on.
    /// </summary>
    [Fact]
    public void Oak28R_ExitE_PathAngle_IsItsTurnOffOnThePavement()
    {
        if (Oak28RExitEChain() is not { } exitE)
        {
            return;
        }

        double angle = Assert.NotNull(exitE.Layout.ComputePathExitAngle(exitE.Chain, "E", exitE.Runway.TrueHeading));
        Assert.True((angle >= 70.0) && (angle <= 78.0), $"E reads {angle:F1}° over {DescribeHops(exitE.Chain, exitE.Runway.TrueHeading)}");
    }

    private static string DescribeHops(List<GroundNode> chain, TrueHeading runwayHeading) =>
        string.Join(
            "; ",
            Enumerable
                .Range(0, chain.Count - 1)
                .Select(i =>
                {
                    IGroundEdge edge = chain[i].Edges.First(e => e.OtherNode(chain[i]).Id == chain[i + 1].Id);
                    double bearing = GeoMath.BearingTo(chain[i].Position, chain[i + 1].Position);
                    double angle = runwayHeading.AbsAngleTo(new TrueHeading(bearing));
                    double lengthFt = edge.DistanceNm * GeoMath.FeetPerNm;
                    return $"#{chain[i].Id}->#{chain[i + 1].Id} {edge.GetType().Name} '{edge.TaxiwayName}' {lengthFt:F1} ft {angle:F1}°";
                })
        );

    /// <summary>
    /// The exit angle does not depend on how the turn-off is drawn: OAK 28R's E reads the same with its first leg's middle node
    /// (the first node joining two straight edges in line within 10°) removed, so the leg is one hop instead of two.
    /// </summary>
    [Fact]
    public void ExitAngle_DoesNotDependOnHowTheLegIsDrawn()
    {
        if (Oak28RExitEChain() is not { } exitE)
        {
            return;
        }

        List<GroundNode> chain = exitE.Chain;
        int middle = Enumerable
            .Range(1, chain.Count - 2)
            .FirstOrDefault(i => IsStraightHop(chain[i - 1], chain[i]) && IsStraightHop(chain[i], chain[i + 1]) && IsInLine(chain, i), -1);
        Assert.True(middle > 0, $"E's chain has no node joining two straight edges in line: {Describe(chain)}");

        List<GroundNode> merged = [.. chain.Take(middle), .. chain.Skip(middle + 1)];
        double drawn = Assert.NotNull(exitE.Layout.TurnOffAngle(chain, exitE.Runway.TrueHeading));
        double asOneEdge = Assert.NotNull(exitE.Layout.TurnOffAngle(merged, exitE.Runway.TrueHeading));
        Assert.InRange(drawn, 70.0, 78.0);
        Assert.InRange(asOneEdge, drawn - 0.5, drawn + 0.5);
    }

    /// <summary>
    /// An exit that doubles back against the landing direction reads over 90°: the first real OAK or SFO exit whose first hop
    /// off the centreline is straight at more than 100° to the landing heading.
    /// </summary>
    [Fact]
    public void ExitAngle_DoublingBack_ReadsOver90()
    {
        ExitChainFixture exit = Assert.IsType<ExitChainFixture>(
            RealExitChains().FirstOrDefault(e => e.Hops[0].Straight && (e.Hops[0].AngleDeg > 100.0))
        );
        double angle = Assert.NotNull(exit.Layout.TurnOffAngle(exit.Chain, exit.Heading));
        string hops = DescribeHops(exit.Chain, exit.Heading);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{exit.Layout.AirportId} {exit.Taxiway} off {exit.Heading.Degrees:F0}°: {angle:F1}° over {hops}"
        );
        Assert.True(angle > 90.0, $"{exit.Taxiway} reads {angle:F1}° over {hops}");
    }

    /// <summary>
    /// The exit angle does not depend on the order the layout lists each node's edges in, so a freshly parsed layout and one read
    /// back another way score every exit alike: every OAK runway bar's exit angle, from both landing directions, on a fresh parse
    /// and on a second fresh parse with every node's edges reversed.
    /// </summary>
    [Fact]
    public void ExitAngle_SameWhateverTheEdgeOrder()
    {
        TestVnasData.EnsureInitialized();
        if ((TestAirportGroundData.LoadUncached("OAK") is not { } parsed) || (TestAirportGroundData.LoadUncached("OAK") is not { } reversed))
        {
            return;
        }

        foreach (GroundNode node in reversed.Nodes.Values)
        {
            node.Edges.Reverse();
        }

        int compared = 0;
        foreach (GroundRunway groundRunway in parsed.Runways)
        {
            foreach (GroundNode bar in parsed.GetRunwayHoldShortNodes(groundRunway.Id.End1))
            {
                if (RunwayHoldBars.BarTaxiwayName(bar) is not { } taxiway)
                {
                    continue;
                }

                foreach (string end in (string[])[groundRunway.Id.End1, groundRunway.Id.End2])
                {
                    if (NavigationDatabase.Instance.GetRunway("OAK", end) is not { } runway)
                    {
                        continue;
                    }

                    double? asParsed = parsed.ComputeExitAngle(bar, taxiway, runway.TrueHeading);
                    double? asReversed = reversed.ComputeExitAngle(reversed.Nodes[bar.Id], taxiway, runway.TrueHeading);
                    Assert.True(asParsed == asReversed, $"{taxiway} bar #{bar.Id} off {end}: {asParsed:F2}° parsed, {asReversed:F2}° reversed");
                    compared++;
                }
            }
        }

        Assert.True(compared > 20, $"only {compared} OAK exits compared");
    }

    /// <summary>
    /// A fillet built for the other landing direction is not the turn-off: OAK's high-speed exits W3 off 12, W4 and W5 off 30 and
    /// J off 10L and 28R each start on a short arc drawn for the opposite runway end, whose tangent at the centreline points back
    /// up the runway; the exit is the chain leaving the runway forward, so each reads a high-speed angle.
    /// </summary>
    [Fact]
    public void ExitAngle_FilletForTheOppositeDirection_IsNotTheTurnOff()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            return;
        }

        AssertExitAnglesAtMost45(layout, "OAK", [("12", "W3"), ("30", "W4"), ("30", "W5"), ("10L", "J"), ("28R", "J")]);
    }

    /// <summary>
    /// An exit's chain ends on the centreline of the runway its bar protects, not on another runway's it crosses first: SFO's T
    /// bar of 28R/10L off 28R and H bar of 01R/19L off 19L each read their own high-speed turn-off.
    /// </summary>
    [Fact]
    public void ExitAngle_ChainEndsOnTheBarsOwnRunway()
    {
        TestVnasData.EnsureInitialized();
        if (new TestAirportGroundData().GetLayout("SFO") is not { } layout)
        {
            return;
        }

        AssertExitAnglesAtMost45(layout, "SFO", [("28R", "T"), ("19L", "H")]);
    }

    /// <summary>
    /// Each runway end and taxiway has a bar (<see cref="RunwayHoldBars.BarTaxiwayName"/>) reading an exit angle of at most 45°
    /// landing on that end — the turn-off for that direction; a taxiway crossing the runway also has a bar on the far side, which
    /// that direction reaches only by doubling back. The failures, with every bar's angle, are reported together.
    /// </summary>
    private static void AssertExitAnglesAtMost45(AirportGroundLayout layout, string airport, (string Runway, string Taxiway)[] exits)
    {
        List<string> failures = [];
        foreach ((string designator, string taxiway) in exits)
        {
            RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway(airport, designator));
            List<GroundNode> bars =
            [
                .. layout
                    .GetRunwayHoldShortNodes(designator)
                    .Where(bar => string.Equals(RunwayHoldBars.BarTaxiwayName(bar), taxiway, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(bar => bar.Id),
            ];
            List<(int Id, double? Angle)> angles = [.. bars.Select(bar => (bar.Id, layout.ComputeExitAngle(bar, taxiway, runway.TrueHeading)))];
            string read = string.Join(", ", angles.Select(a => $"#{a.Id} {a.Angle:F2}°"));
            if (!angles.Any(a => (a.Angle is { } angle) && (angle <= 45.0)))
            {
                failures.Add($"{taxiway} off {designator}: [{read}]");
            }
        }

        Assert.True(failures.Count == 0, $"{airport} exits not read as high-speed: {string.Join("; ", failures)}");
    }

    private sealed record ExitHop(bool Straight, double AngleDeg);

    private sealed record ExitChainFixture(
        AirportGroundLayout Layout,
        string Taxiway,
        TrueHeading Heading,
        List<GroundNode> Chain,
        List<ExitHop> Hops
    );

    /// <summary>
    /// Every runway bar's exit chain at OAK then SFO, once per landing direction: walked back from the bar along its taxiway
    /// (<see cref="RunwayHoldBars.BarTaxiwayName"/>), each step nearer the runway centreline, and reversed; each hop measured here
    /// by its chord, apart from the code under test.
    /// </summary>
    private static IEnumerable<ExitChainFixture> RealExitChains()
    {
        TestVnasData.EnsureInitialized();
        foreach (string airport in (string[])["OAK", "SFO"])
        {
            if (new TestAirportGroundData().GetLayout(airport) is not { } layout)
            {
                continue;
            }

            foreach (GroundRunway groundRunway in layout.Runways)
            {
                foreach (ExitChainFixture exit in ExitChainsOf(airport, layout, groundRunway))
                {
                    yield return exit;
                }
            }
        }
    }

    private static IEnumerable<ExitChainFixture> ExitChainsOf(string airport, AirportGroundLayout layout, GroundRunway groundRunway)
    {
        foreach (GroundNode bar in layout.GetRunwayHoldShortNodes(groundRunway.Id.End1).OrderBy(n => n.Id))
        {
            if ((RunwayHoldBars.BarTaxiwayName(bar) is not { } taxiway) || (WalkToCentreline(groundRunway, bar, taxiway) is not { } chain))
            {
                continue;
            }

            foreach (string end in (string[])[groundRunway.Id.End1, groundRunway.Id.End2])
            {
                if (NavigationDatabase.Instance.GetRunway(airport, end) is { } runway)
                {
                    yield return new ExitChainFixture(layout, taxiway, runway.TrueHeading, chain, ChordHops(chain, runway.TrueHeading));
                }
            }
        }
    }

    private static List<GroundNode>? WalkToCentreline(GroundRunway groundRunway, GroundNode bar, string taxiway)
    {
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(groundRunway);
        List<GroundNode> chain = [bar];
        GroundNode node = bar;
        while (!AirportGroundLayout.HasRunwayCenterlineEdge(node))
        {
            GroundNode from = node;
            double fromFt = AirportGroundLayout.CrossTrackFromCenterlineFt(rect, from);
            GroundNode? nearer = from
                .Edges.Where(e => !e.IsRunwayCenterline && e.MatchesTaxiway(taxiway))
                .Select(e => e.OtherNode(from))
                .Where(n => AirportGroundLayout.CrossTrackFromCenterlineFt(rect, n) < fromFt)
                .MinBy(n => AirportGroundLayout.CrossTrackFromCenterlineFt(rect, n));
            if ((nearer is null) || (chain.Count >= 50))
            {
                return null;
            }

            chain.Add(nearer);
            node = nearer;
        }

        chain.Reverse();
        return chain.Count >= 2 ? chain : null;
    }

    private static List<ExitHop> ChordHops(List<GroundNode> chain, TrueHeading heading) =>
        [
            .. Enumerable
                .Range(0, chain.Count - 1)
                .Select(i => new ExitHop(
                    IsStraightHop(chain[i], chain[i + 1]),
                    heading.AbsAngleTo(new TrueHeading(GeoMath.BearingTo(chain[i].Position, chain[i + 1].Position)))
                )),
        ];

    private static bool IsStraightHop(GroundNode from, GroundNode to) =>
        from.Edges.Any(e => (e is GroundEdge) && (e.OtherNode(from).Id == to.Id))
        && !from.Edges.Any(e => (e is GroundArc) && (e.OtherNode(from).Id == to.Id));

    private static bool IsInLine(List<GroundNode> chain, int i)
    {
        var inbound = new TrueHeading(GeoMath.BearingTo(chain[i - 1].Position, chain[i].Position));
        var outbound = new TrueHeading(GeoMath.BearingTo(chain[i].Position, chain[i + 1].Position));
        return inbound.AbsAngleTo(outbound) <= 10.0;
    }

    /// <summary>
    /// OAK 28R's E from the centreline node where it leaves the runway to its bar: walked back from the bar along E, each step
    /// nearer the 28R centreline, then reversed so it runs runway to bar.
    /// </summary>
    private static (AirportGroundLayout Layout, RunwayInfo Runway, List<GroundNode> Chain)? Oak28RExitEChain()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return null;
        }

        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("OAK", "28R"));
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(Assert.IsType<GroundRunway>(layout.FindRunway("28R")));
        GroundNode node = Assert.Single(TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "E"));
        List<GroundNode> chain = [node];
        while (!AirportGroundLayout.HasRunwayCenterlineEdge(node))
        {
            GroundNode from = node;
            double fromFt = AirportGroundLayout.CrossTrackFromCenterlineFt(rect, from);
            GroundNode nearer =
                from.Edges.Where(e => !e.IsRunwayCenterline && e.MatchesTaxiway("E"))
                    .Select(e => e.OtherNode(from))
                    .Where(n => AirportGroundLayout.CrossTrackFromCenterlineFt(rect, n) < fromFt)
                    .MinBy(n => AirportGroundLayout.CrossTrackFromCenterlineFt(rect, n))
                ?? throw new InvalidOperationException($"E has no step nearer 28R from #{from.Id} ({fromFt:F1} ft); walked {Describe(chain)}");
            Assert.True(chain.Count < 50, $"E walk from the bar does not reach 28R: {Describe(chain)}");
            chain.Add(nearer);
            node = nearer;
        }

        chain.Reverse();
        return (layout, runway, chain);
    }

    private static string Describe(IEnumerable<GroundNode> chain) => string.Join(" ", chain.Select(n => $"#{n.Id}"));

    /// <summary>
    /// The runway's exits the "Exit at" box offers (<see cref="LandingPhase.ListRunwayExits"/>), on OAK 28R's right side: only
    /// connections clearly past the landing threshold, so B, which crosses the runway at its approach end, is left out and the
    /// list starts at E; every row is on the right side, not planned, and at least 100 ft from the threshold.
    /// </summary>
    [Fact]
    public void RunwayExits_Oak28RRight_LeaveOutTheThresholdCrossing_StartAtE()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        if ((layout is null) || (runway is null))
        {
            return;
        }

        IReadOnlyList<ExitAheadDto> right = LandingPhase.ListRunwayExits(layout, runway, ExitSide.Right);

        Assert.NotEmpty(right);
        Assert.DoesNotContain(right, exit => exit.Taxiway == "B");
        Assert.Equal("E", right[0].Taxiway);
        Assert.All(
            right,
            exit =>
            {
                Assert.Equal(ExitSide.Right, exit.Side);
                Assert.False(exit.Planned);
                Assert.True(exit.DistanceFt > 0, $"{exit.Taxiway} is {exit.DistanceFt} ft from the threshold.");
            }
        );
    }

    /// <summary>Rows come left side first, then right, each side nearest first.</summary>
    [Fact]
    public void Rows_AreOrderedLeftThenRight_NearestFirst()
    {
        if (StageRollout("SFO", "28R", "B738", 1500, 40, null) is not { } staged)
        {
            return;
        }

        IReadOnlyList<ExitAheadDto> list = staged.List();
        int lastLeft = list.ToList().FindLastIndex(row => row.Side == ExitSide.Left);
        int firstRight = list.ToList().FindIndex(row => row.Side == ExitSide.Right);
        Assert.True((lastLeft >= 0) && (firstRight >= 0), $"want rows on both sides: {string.Join(", ", list)}");
        Assert.True(lastLeft < firstRight, $"a left row comes after a right one: {string.Join(", ", list)}");
        foreach (ExitSide side in new[] { ExitSide.Left, ExitSide.Right })
        {
            List<int> distances = [.. list.Where(row => row.Side == side).Select(row => row.DistanceFt)];
            Assert.Equal([.. distances.Order()], distances);
        }
    }

    /// <summary>
    /// ATL 26R keeps bars short of its holding distance with nothing beyond (A4 among them): one is accepted when named, but a short
    /// stub is not an exit a crew would offer to take, so it is never listed.
    /// </summary>
    [Fact]
    public void ShortBarExit_IsAcceptedByName_ButNotListed()
    {
        if (StageRollout("ATL", "26R", "B738", 0, 30, null) is not { } staged)
        {
            return;
        }

        IReadOnlyList<ExitAheadDto> list = staged.List();
        (HashSet<string> anyShort, _) = ShortBarTaxiways(staged.Layout, "26R");
        HashSet<string> noTurnoff = NoTurnoff(staged.Layout, "26R");
        string[] shortBarNames = ["SG", "SG2", "R3", "R7", "R11", "N10", "C", "B13", "A4"];
        List<string> acceptedUnlisted = [];
        foreach (string taxiway in shortBarNames.Where(name => anyShort.Contains(name) && !noTurnoff.Contains(name)))
        {
            foreach (ExitSide side in new[] { ExitSide.Left, ExitSide.Right })
            {
                if (staged.Accepts(taxiway, side) && !Lists(list, taxiway, side))
                {
                    acceptedUnlisted.Add($"{taxiway}/{side}");
                }
            }
        }

        Assert.True(acceptedUnlisted.Count > 0, $"no short-bar taxiway is accepted by name and left unlisted: {string.Join(", ", list)}");
        Assert.Contains("A4", acceptedUnlisted.Select(entry => entry.Split('/')[0]));
    }

    /// <summary>
    /// SFO 28R's Q ends at a junction whose bar belongs to the joining taxiway (the exit hops to it): it is listed when makeable,
    /// judged from the runway's taxiway names rather than the unnamed walk's choices. C3 is listed on the right only: the named
    /// search for a left C3 falls back to the right one from anywhere on 28R, as
    /// <c>Issue276ErThenExitTaxiwayTests.NamedTaxiwayWithUnsupportedSide_FallsBackToOtherSide</c> pins.
    /// </summary>
    [Fact]
    public void HopExits_AreListed()
    {
        if (StageRollout("SFO", "28R", "B738", 0, 30, null) is not { } staged)
        {
            return;
        }

        IReadOnlyList<ExitAheadDto> list = staged.List();
        Assert.True(Lists(list, "Q", ExitSide.Left), $"the left Q is not listed: {string.Join(", ", list)}");
        Assert.True(Lists(list, "C3", ExitSide.Right), $"the right C3 is not listed: {string.Join(", ", list)}");
        Assert.False(Lists(list, "C3", ExitSide.Left), $"a left C3 is listed: {string.Join(", ", list)}");
    }

    /// <summary>SFO 28R/28L forbid turning off onto L and P: a controller naming one is obeyed, but neither is ever listed.</summary>
    [Theory]
    [InlineData("28R")]
    [InlineData("28L")]
    public void NoTurnoffTaxiways_AreAcceptedByName_ButNeverListed(string designator)
    {
        if (StageRollout("SFO", designator, "B738", 0, 30, null) is not { } staged)
        {
            return;
        }

        IReadOnlyList<ExitAheadDto> list = staged.List();
        foreach (string taxiway in new[] { "L", "P" })
        {
            Assert.Contains(taxiway, NoTurnoff(staged.Layout, designator));
            bool accepted = staged.Accepts(taxiway, ExitSide.Left) || staged.Accepts(taxiway, ExitSide.Right);
            Assert.True(accepted, $"{taxiway} is refused by name on {designator}");
            Assert.DoesNotContain(list, row => string.Equals(row.Taxiway, taxiway, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>(g) On the rollout the exit the aircraft is braking for (its committed candidate) is the one planned row.</summary>
    [Fact]
    public void Rollout_FlagsTheCommittedCandidateAsPlanned()
    {
        if (StageRollout("OAK", "30", "B738", 1500, 120, null) is not { } staged)
        {
            return;
        }

        staged.Phase.OnTick(staged.Ctx);
        ResolvedExitInfo candidate = Assert.IsType<ResolvedExitInfo>(staged.Phase.CandidateExit);

        ExitAheadDto planned = Assert.Single(staged.List(), row => row.Planned);
        Assert.Equal(candidate.TaxiwayName, planned.Taxiway, ignoreCase: true);
    }

    /// <summary>
    /// (g) The planned row is the committed candidate's own taxiway and side, the side its exit search found: on SFO 28R's D, which
    /// crosses the runway with a bar on each side, the requested side's row is planned and the other side's is not.
    /// </summary>
    [Theory]
    [InlineData(ExitSide.Left)]
    [InlineData(ExitSide.Right)]
    public void Rollout_PlannedMark_IsTheCandidatesOwnSide(ExitSide side)
    {
        if (StageRollout("SFO", "28R", "B738", 4000, 60, null) is not { } staged)
        {
            return;
        }

        staged.Aircraft.Phases!.RequestedExit = new ExitPreference { Side = side, Taxiway = "D" };
        staged.Phase.OnTick(staged.Ctx);
        ResolvedExitInfo candidate = Assert.IsType<ResolvedExitInfo>(staged.Phase.CandidateExit);
        Assert.Equal(("D", (ExitSide?)side), (candidate.TaxiwayName, candidate.Side));

        IReadOnlyList<ExitAheadDto> list = staged.List();
        Assert.True(Lists(list, "D", ExitSide.Left) && Lists(list, "D", ExitSide.Right), $"D is not listed on both sides: {string.Join(", ", list)}");
        ExitAheadDto planned = Assert.Single(list, row => row.Planned);
        Assert.Equal(("D", side), (planned.Taxiway, planned.Side));
    }

    /// <summary>(h) No list with no layout, under a forced landing (<c>CLANDF</c>), or before touchdown.</summary>
    [Fact]
    public void Rollout_NoList_WithoutLayout_UnderClandf_OrBeforeTouchdown()
    {
        if (StageRollout("OAK", "30", "B738", 1500, 100, null) is not { } staged)
        {
            return;
        }

        Assert.NotNull(staged.Phase.ListExitsAhead(staged.Aircraft));

        staged.Aircraft.Phases!.ForceLanding = true;
        Assert.Null(staged.Phase.ListExitsAhead(staged.Aircraft));
        staged.Aircraft.Phases.ForceLanding = false;

        staged.Aircraft.Ground.Layout = null;
        Assert.Null(staged.Phase.ListExitsAhead(staged.Aircraft));
        staged.Aircraft.Ground.Layout = staged.Layout;

        staged.Aircraft.IsOnGround = false;
        staged.Aircraft.Altitude = staged.Runway.ElevationFt + 20;
        var airborne = new LandingPhase();
        airborne.OnStart(staged.Ctx);
        Assert.NotEqual(LandingPhase.State.Rollout, airborne.CurrentState);
        Assert.Null(airborne.ListExitsAhead(staged.Aircraft));
    }

    /// <summary>
    /// (j) Each exit is judged at its own turn-off speed: placed where a high-speed exit is makeable at its high-speed turn-off speed
    /// but would not be at a standard one, it is listed; placed where a 90° exit would be makeable at the high-speed speed but not at
    /// its own standard one, it is not.
    /// </summary>
    [Fact]
    public void HighSpeedAndStandardExits_AreJudgedAtTheirOwnTurnOffSpeeds()
    {
        if (StageRollout("OAK", "30", "B738", 0, 60, null) is not { } probe)
        {
            return;
        }

        LatLon threshold = LandingThreshold.Resolve(probe.Runway, probe.Layout);
        List<AirportGroundLayout.CenterlineExitResult> ahead = ConnectionsAhead(probe.Layout, probe.Runway, probe.Aircraft.Position);
        double BranchAlongFt(AirportGroundLayout.CenterlineExitResult exit) =>
            GeoMath.AlongTrackDistanceNm(exit.Path[0].Position, threshold, probe.Runway.TrueHeading) * GeoMath.FeetPerNm;

        const double GapFt = 1000;
        AirportGroundLayout.CenterlineExitResult highSpeed = ahead.First(exit => (exit.ExitAngle <= 46) && (BranchAlongFt(exit) > GapFt));
        AirportGroundLayout.CenterlineExitResult standard = ahead.First(exit => (exit.ExitAngle >= 60) && (BranchAlongFt(exit) > GapFt));

        double highSpeedTurnOff = CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, highSpeed.ExitAngle);
        double standardTurnOff = CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, standard.ExitAngle);
        Assert.True(highSpeedTurnOff > standardTurnOff);
        double firm = CategoryPerformance.FirmBrakingRate(AircraftCategory.Jet);

        // Between the two turn-off speeds' firm-braking entry speeds over the gap: makeable at the high-speed turn-off, not the standard.
        static double FpsToKts(double fps) => fps * 3600.0 / GeoMath.FeetPerNm;
        static double KtsToFps(double kts) => kts * GeoMath.FeetPerNm / 3600.0;
        double firmFps2 = KtsToFps(firm);
        double entryKts = FpsToKts(
            Math.Sqrt(((Math.Pow(KtsToFps(highSpeedTurnOff), 2) + Math.Pow(KtsToFps(standardTurnOff), 2)) / 2.0) + (2.0 * firmFps2 * GapFt))
        );

        Staged atHighSpeed = StageRollout("OAK", "30", "B738", BranchAlongFt(highSpeed) - GapFt, entryKts, null)!;
        Assert.True(
            Lists(atHighSpeed.List(), highSpeed.Taxiway, highSpeed.Side),
            $"high-speed {highSpeed.Taxiway} at {entryKts:F1} kt is not listed"
        );

        Staged atStandard = StageRollout("OAK", "30", "B738", BranchAlongFt(standard) - GapFt, entryKts, null)!;
        Assert.False(Lists(atStandard.List(), standard.Taxiway, standard.Side), $"90° {standard.Taxiway} at {entryKts:F1} kt is listed");
    }

    /// <summary>The exit search's memoized parking distance equals the direct computation for every hold-short bar at SFO.</summary>
    [Fact]
    public void MemoizedParkingDistance_EqualsTheDirectComputation_ForEveryBar()
    {
        if (new TestAirportGroundData().GetLayout("SFO") is not { } layout)
        {
            return;
        }

        List<GroundNode> bars = [.. layout.Nodes.Values.Where(node => node.Type == GroundNodeType.RunwayHoldShort)];
        Assert.NotEmpty(bars);
        foreach (GroundNode bar in bars)
        {
            double direct = layout.ComputeAverageNearestParkingDistanceNm(bar, 3);
            Assert.Equal(direct, layout.AverageNearestParkingDistanceNm(bar, 3));
            Assert.Equal(direct, layout.AverageNearestParkingDistanceNm(bar, 3));
        }
    }

    // --- Final ---

    /// <summary>(k) The projected touchdown point lies within 300 ft of where the flown landing touches down.</summary>
    [Theory]
    [InlineData("B738", 140.0)]
    [InlineData("C172", 70.0)]
    [InlineData("DH8D", 130.0)]
    public void ProjectedTouchdown_IsWithin300FtOfTheFlownTouchdown(string aircraftType, double ias)
    {
        if (SpawnOnFinal("OAK", "30", aircraftType, 3.0, ias) is not { } final)
        {
            return;
        }

        TouchdownForecast forecast = Assert.IsType<TouchdownForecast>(
            FinalApproachExitForecast.ProjectTouchdown(final.Aircraft, final.Layout, null, 0)
        );
        TickUntil(final.Engine, () => final.Aircraft.Phases?.CurrentPhase is LandingPhase { TouchdownPosition: not null }, 180, "touchdown");

        var landing = (LandingPhase)final.Aircraft.Phases!.CurrentPhase!;
        double flownFt =
            GeoMath.AlongTrackDistanceNm(landing.TouchdownPosition!.Value, final.Threshold, final.Runway.TrueHeading) * GeoMath.FeetPerNm;
        Assert.True(Math.Abs(forecast.AlongFt - flownFt) <= 300, $"projected {forecast.AlongFt:F0} ft, flown {flownFt:F0} ft");
    }

    /// <summary>
    /// (l) The set forecast at 3 NM is the set the first rollout tick lists, leaving out exits within 0.25 kt/s of the firm limit from
    /// either reference.
    /// </summary>
    [Theory]
    [InlineData("OAK", "30", "B738", 140.0)]
    [InlineData("SFO", "28R", "B738", 140.0)]
    [InlineData("OAK", "30", "DH8D", 130.0)]
    public void ForecastAt3Nm_MatchesTheFirstRolloutList(string airport, string designator, string aircraftType, double ias)
    {
        if (SpawnOnFinal(airport, designator, aircraftType, 3.0, ias) is not { } final)
        {
            return;
        }

        AircraftCategory category = AircraftCategorization.Categorize(aircraftType);

        IReadOnlyList<ExitAheadDto> forecast = Assert.IsAssignableFrom<IReadOnlyList<ExitAheadDto>>(final.Forecast());
        TouchdownForecast touchdown = FinalApproachExitForecast.ProjectTouchdown(final.Aircraft, final.Layout, null, 0)!;
        TickUntil(final.Engine, () => IsRollingOut(final.Aircraft), 180, "the rollout");
        var landing = (LandingPhase)final.Aircraft.Phases!.CurrentPhase!;
        IReadOnlyList<ExitAheadDto> rollout = Assert.IsAssignableFrom<IReadOnlyList<ExitAheadDto>>(landing.ListExitsAhead(final.Aircraft));

        double firm = CategoryPerformance.FirmBrakingRate(category);
        bool Borderline((string Taxiway, ExitSide Side) exit)
        {
            if (FirstConnectionAhead(final.Layout, final.Runway, touchdown.Position, exit) is not { } connection)
            {
                return true;
            }

            double turnOff = CategoryPerformance.ExitTurnOffSpeed(category, connection.ExitAngle);
            double fromTouchdownNm = GeoMath.AlongTrackDistanceNm(connection.Path[0].Position, touchdown.Position, final.Runway.TrueHeading);
            double fromRolloutNm = GeoMath.AlongTrackDistanceNm(connection.Path[0].Position, final.Aircraft.Position, final.Runway.TrueHeading);
            double forecastRate = RolloutBraking.RequiredDecelKtsPerSec(touchdown.WheelSpeedKts, turnOff, fromTouchdownNm, category);
            double rolloutRate = RolloutBraking.RequiredDecelKtsPerSec(final.Aircraft.GroundSpeed, turnOff, fromRolloutNm, category);
            return (Math.Abs(forecastRate - firm) < 0.25) || (Math.Abs(rolloutRate - firm) < 0.25);
        }

        HashSet<(string, ExitSide)> forecastKeys = Keys(forecast);
        HashSet<(string, ExitSide)> rolloutKeys = Keys(rollout);
        List<(string, ExitSide)> disagreements =
        [
            .. forecastKeys.Union(rolloutKeys).Where(key => forecastKeys.Contains(key) != rolloutKeys.Contains(key)).Where(key => !Borderline(key)),
        ];

        Assert.True(disagreements.Count == 0, $"forecast {string.Join(", ", forecast)}; rollout {string.Join(", ", rollout)}");
        Assert.NotEmpty(forecast);
    }

    /// <summary>(m) Listed more than 1.0 NM and at most 5.0 NM from the landing threshold, not outside that window.</summary>
    [Fact]
    public void Final_ListsOnlyBetweenOneAndFiveMiles()
    {
        if (SpawnOnFinal("OAK", "30", "B738", 3.0, 140) is not { } final)
        {
            return;
        }

        final.MoveTo(5.5);
        Assert.Null(final.Forecast());
        final.MoveTo(4.9);
        Assert.NotNull(final.Forecast());
        final.MoveTo(1.1);
        Assert.NotNull(final.Forecast());
        final.MoveTo(0.9);
        Assert.Null(final.Forecast());
    }

    /// <summary>(n) On final only the controller's exit instruction (<see cref="PhaseList.RequestedExit"/>) is marked planned.</summary>
    [Fact]
    public void Final_MarksOnlyTheRequestedExitAsPlanned()
    {
        if (SpawnOnFinal("OAK", "30", "B738", 3.0, 140) is not { } final)
        {
            return;
        }

        IReadOnlyList<ExitAheadDto> unrequested = Assert.IsAssignableFrom<IReadOnlyList<ExitAheadDto>>(final.Forecast());
        Assert.DoesNotContain(unrequested, row => row.Planned);
        ExitAheadDto pick = unrequested[^1];

        final.Aircraft.Phases!.RequestedExit = new ExitPreference { Side = pick.Side, Taxiway = pick.Taxiway };

        ExitAheadDto planned = Assert.Single(final.Forecast()!, row => row.Planned);
        Assert.Equal((pick.Taxiway, pick.Side), (planned.Taxiway, planned.Side));
    }

    /// <summary>
    /// (n) A bare <c>EXIT &lt;twy&gt;</c> on a taxiway that crosses the runway marks the one row the crew would take: on the side it
    /// expects to turn off (the inferred side), since the taxiway is listed there.
    /// </summary>
    [Fact]
    public void Final_BareExitOnACrossingTaxiway_MarksOnlyTheInferredSide()
    {
        if (SpawnOnFinal("SFO", "28R", "B738", 3.0, 140) is not { } final)
        {
            return;
        }

        IReadOnlyList<ExitAheadDto> unrequested = final.Forecast()!;
        ExitSide inferred = Assert.IsType<ExitSide>(final.Layout.InferPreferredExitSide("28R", final.Runway.TrueHeading));
        string crossing = unrequested
            .Select(row => row.Taxiway)
            .First(taxiway => Lists(unrequested, taxiway, ExitSide.Left) && Lists(unrequested, taxiway, ExitSide.Right));

        final.Aircraft.Phases!.RequestedExit = new ExitPreference { Taxiway = crossing };

        ExitAheadDto planned = Assert.Single(final.Forecast()!, row => row.Planned);
        Assert.Equal((crossing, inferred), (planned.Taxiway, planned.Side));
    }

    /// <summary>(o) No list for a touch-and-go, the option, a forced landing, a helicopter, or an aircraft not aligned on final.</summary>
    [Theory]
    [InlineData("TG")]
    [InlineData("COPT")]
    [InlineData("CLANDF")]
    public void Final_NoList_ForAnythingButAFullStopLanding(string command)
    {
        if (SpawnOnFinal("OAK", "30", "B738", 3.0, 140) is not { } final)
        {
            return;
        }

        Assert.NotNull(final.Forecast());
        CommandResult result = final.Engine.SendCommand(final.Aircraft.Callsign, command);
        Assert.True(result.Success, $"{command}: {result.Message}");

        Assert.Null(final.Forecast());
    }

    [Fact]
    public void Final_NoList_ForAHelicopter_OrWhenNotAligned()
    {
        if (SpawnOnFinal("OAK", "30", "EC35", 3.0, 70) is not { } helicopter)
        {
            return;
        }

        Assert.Null(helicopter.Forecast());

        OnFinal jet = SpawnOnFinal("OAK", "30", "B738", 3.0, 140)!;
        Assert.NotNull(jet.Forecast());
        jet.Aircraft.TrueHeading = new TrueHeading((jet.Runway.TrueHeading.Degrees + 60) % 360);
        jet.Aircraft.TrueTrack = jet.Aircraft.TrueHeading;
        Assert.Null(jet.Forecast());
    }

    /// <summary>(p) A LAHSO clearance on final leaves out the exits past the hold-short point, measured from the threshold.</summary>
    [Fact]
    public void Final_Lahso_RemovesTheExitsBeyondTheHoldShortPoint()
    {
        if (SpawnOnFinal("SFO", "28R", "B738", 3.0, 140) is not { } final)
        {
            return;
        }

        List<ExitAheadDto> open = [.. final.Forecast()!.OrderBy(row => row.DistanceFt)];
        ExitAheadDto nearest = open[0];
        ExitAheadDto farther = open.First(row => row.DistanceFt > nearest.DistanceFt + 400);
        double limitFt = (nearest.DistanceFt + farther.DistanceFt) / 2.0;
        double holdShortFt = limitFt + 50.0 + (AircraftLength.ResolveFt("B738") / 2.0);
        LatLon holdShortPoint = GeoMath.ProjectPoint(final.Threshold, final.Runway.TrueHeading, holdShortFt / GeoMath.FeetPerNm);
        final.Aircraft.Phases!.LahsoHoldShort = new LahsoTarget
        {
            Lat = holdShortPoint.Lat,
            Lon = holdShortPoint.Lon,
            DistFromThresholdNm = holdShortFt / GeoMath.FeetPerNm,
            CrossingRunwayId = "1L",
        };

        IReadOnlyList<ExitAheadDto> held = final.Forecast()!;

        Assert.True(Lists(held, nearest.Taxiway, nearest.Side), $"{nearest.Taxiway} fits before the hold-short point but is not listed");
        Assert.False(Lists(held, farther.Taxiway, farther.Side), $"{farther.Taxiway} lies past the hold-short point but is listed");
        Assert.All(held, row => Assert.True(row.DistanceFt <= limitFt + 50, $"{row.Taxiway} at {row.DistanceFt} ft"));
    }

    // --- Carrier ---

    /// <summary>
    /// (q) The <c>Situation</c> step stores the list: the forecast inside the final window, nothing inside 1 NM before touchdown,
    /// the rollout list once rolling out, and nothing once <see cref="RunwayExitPhase"/> has taken over.
    /// </summary>
    [Fact]
    public void SituationStep_StoresTheExitsAhead_OnFinalAndOnTheRollout()
    {
        if (SpawnOnFinal("OAK", "30", "B738", 3.0, 140) is not { } final)
        {
            return;
        }

        AircraftState aircraft = final.Aircraft;
        final.Engine.TickOneSecond();
        Assert.NotNull(aircraft.Situation.ExitsAhead);

        TickUntil(final.Engine, () => aircraft.Phases?.CurrentPhase is LandingPhase, 180, "the landing phase");
        Assert.Null(aircraft.Situation.ExitsAhead);

        TickUntil(final.Engine, () => IsRollingOut(aircraft), 60, "the rollout");
        var landing = (LandingPhase)aircraft.Phases!.CurrentPhase!;
        IReadOnlyList<ExitAheadDto> stored = Assert.IsAssignableFrom<IReadOnlyList<ExitAheadDto>>(aircraft.Situation.ExitsAhead);
        Assert.Equal(landing.ListExitsAhead(aircraft)!, stored);

        TickUntil(final.Engine, () => aircraft.Phases?.CurrentPhase is RunwayExitPhase, 180, "the runway exit");
        final.Engine.TickSituation();
        Assert.Null(aircraft.Situation.ExitsAhead);
    }

    /// <summary>(q) The stored list survives a snapshot round-trip; a snapshot written before the field restores no list.</summary>
    [Fact]
    public void Snapshot_RoundTripsTheExitsAhead_AndAnOldSnapshotRestoresNone()
    {
        var aircraft = new AircraftState
        {
            Callsign = "UAL463",
            AircraftType = "B738",
            IsOnGround = true,
        };
        aircraft.Situation.ExitsAhead =
        [
            new ExitAheadDto("W2", ExitSide.Right, 1800, true, 45),
            new ExitAheadDto("W3", ExitSide.Right, 3600, false, 90),
        ];

        string json = JsonSerializer.Serialize(aircraft.ToSnapshot(), RecordingJsonOptions.Default);
        AircraftSnapshotDto dto = JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!;
        var restored = AircraftState.FromSnapshot(dto, null);

        Assert.Equal(aircraft.Situation.ExitsAhead, restored.Situation.ExitsAhead!);

        AircraftSituationStateDto old = JsonSerializer.Deserialize<AircraftSituationStateDto>(
            """{"Current":5,"WasOnGround":true}""",
            RecordingJsonOptions.Default
        )!;
        Assert.Null(AircraftSituationState.FromSnapshot(old).ExitsAhead);

        aircraft.Situation.ExitsAhead = null;
        string empty = JsonSerializer.Serialize(aircraft.Situation.ToSnapshot(), RecordingJsonOptions.Default);
        Assert.DoesNotContain("ExitsAhead", empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (r) A committed recording with a landing (LXJ574 onto SFO 28R with <c>ER</c> then <c>EXIT D</c>, issue #276): replayed to
    /// mid-rollout and restored from its own snapshot into a second engine, the two engines tick on second for second to the same
    /// aircraft snapshot, the exits-ahead list included.
    /// </summary>
    [Fact]
    public void LandingRecording_RestoredMidRollout_TicksOnByteForByte()
    {
        SessionRecording? recording = RecordingLoader.Load(ReplayRecordingPath);
        if (recording is null)
        {
            return;
        }

        var original = new SimulationEngine(RecordingGroundData.Evergreen(GroundDataWhy), PushPlanStore.CreateShipped());
        original.Replay(recording, 430);
        for (int t = 0; (t < 150) && !((original.FindAircraft(ReplayCallsign) is { } rolling) && IsRollingOut(rolling)); t++)
        {
            original.ReplayOneSecond();
        }

        Assert.True(IsRollingOut(original.FindAircraft(ReplayCallsign)!), $"{ReplayCallsign} never reached its rollout");
        original.ReplayOneSecond();
        original.ReplayOneSecond();
        Assert.NotEmpty(original.FindAircraft(ReplayCallsign)!.Situation.ExitsAhead!);

        string snapshotJson = JsonSerializer.Serialize(original.CaptureSnapshot(), RecordingJsonOptions.Default);
        var restored = new SimulationEngine(RecordingGroundData.Evergreen(GroundDataWhy), PushPlanStore.CreateShipped());
        restored.Replay(recording, original.Scenario!.ElapsedSeconds);
        restored.RestoreFromSnapshot(JsonSerializer.Deserialize<StateSnapshotDto>(snapshotJson, RecordingJsonOptions.Default)!);

        Assert.Equal(Json(original.FindAircraft(ReplayCallsign)!.ToSnapshot()), Json(restored.FindAircraft(ReplayCallsign)!.ToSnapshot()));

        for (int t = 1; t <= 20; t++)
        {
            original.ReplayOneSecond();
            restored.ReplayOneSecond();
            AircraftState originalAircraft = Assert.IsType<AircraftState>(original.FindAircraft(ReplayCallsign));
            AircraftState restoredAircraft = Assert.IsType<AircraftState>(restored.FindAircraft(ReplayCallsign));
            Assert.Equal(Json(originalAircraft.ToSnapshot()), Json(restoredAircraft.ToSnapshot()));
        }
    }

    /// <summary>
    /// (r) The list changes nothing the recording does: LXJ574 hands its rollout to the runway exit at the same second, on the same
    /// taxiway and bar, as before the list existed (the right-side D, issue #276).
    /// </summary>
    [Fact]
    public void LandingRecording_TakesTheSameExitAtTheSameSecond()
    {
        SessionRecording? recording = RecordingLoader.Load(ReplayRecordingPath);
        if (recording is null)
        {
            return;
        }

        var engine = new SimulationEngine(RecordingGroundData.Evergreen(GroundDataWhy), PushPlanStore.CreateShipped());
        engine.Replay(recording, 430);
        ResolvedExitInfo? lastCandidate = null;
        for (int t = 0; t < 200; t++)
        {
            engine.ReplayOneSecond();
            AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(ReplayCallsign));
            if (aircraft.Phases?.CurrentPhase is LandingPhase landing)
            {
                lastCandidate = landing.CandidateExit;
                continue;
            }

            if (aircraft.Phases?.CurrentPhase is RunwayExitPhase)
            {
                ResolvedExitInfo exit = Assert.IsType<ResolvedExitInfo>(lastCandidate);
                Assert.Equal(
                    (ExitHandoffSecond, ExitTaxiway, ExitHoldShortId),
                    (engine.Scenario!.ElapsedSeconds, exit.TaxiwayName, exit.HoldShortNode.Id)
                );
                return;
            }
        }

        Assert.Fail($"{ReplayCallsign} never handed off to the runway exit");
    }
}
