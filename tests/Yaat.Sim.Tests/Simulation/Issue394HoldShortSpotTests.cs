using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Speech;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #394: <c>TAXI … HS $spot</c> never held short of the spot.
///
/// Recording: S2-SFO-3 "High Intensity" (trimmed to t≤720). N619TC (C525, parked SIG3) spawns at
/// t=330 with the scenario preset <c>TAXI T421 C Z B M1 1L HS $17</c> — taxi via C, hold short of
/// spot 17 (a spot node on Z just west of the C/Z junction) for west-end coordination, then on to
/// 1L. The parser consumed <c>$17</c> as the taxi *destination* because the <c>$</c>-prefix check ran
/// before the <c>HS</c>-clause check, so no hold-short was created and the aircraft taxied all the
/// way to the 1L bar at M1. No commands were issued to it.
///
/// The fix makes an <c>HS</c> clause own every following token (<c>$17</c> is a spot hold-short
/// target, bound to the spot node like a taxiway hold-short) and moves the client's destination
/// token ahead of <c>HS</c>. The scripted tests run on the committed SFO layout; the replay test
/// proves it against the reported session.
/// </summary>
public class Issue394HoldShortSpotTests(ITestOutputHelper output)
{
    private const string RecordingPath = "TestData/issue394-sfo-hs-spot17-recording.zip";
    private const string SpotName = "17";
    private const string SpotTargetName = "$17";
    private const int SpawnSeconds = 330;
    private const double TickSeconds = 0.25;

    private static AirportGroundLayout? LoadSfo()
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout("SFO");
    }

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    /// <summary>The C-side neighbor of the C/Z junction: on C, one hop short of Z.</summary>
    private static GroundNode? FindStartNodeOnCShortOfZ(AirportGroundLayout layout)
    {
        var junction = layout.FindIntersectionNode("C", "Z");
        if (junction is null)
        {
            return null;
        }

        foreach (var edge in junction.Edges)
        {
            if (edge.MatchesTaxiway("C") && !edge.MatchesTaxiway("Z"))
            {
                return edge.OtherNode(junction);
            }
        }

        return null;
    }

    private static AircraftState MakeAircraftAt(GroundNode node, double headingTrue)
    {
        var ac = new AircraftState
        {
            Callsign = "N394HS",
            AircraftType = "C525",
            Position = node.Position,
            TrueHeading = new TrueHeading(headingTrue),
            TrueTrack = new TrueHeading(headingTrue),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KSFO" },
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static PhaseContext Context(AircraftState aircraft, AirportGroundLayout layout)
    {
        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = TickSeconds,
            GroundLayout = layout,
            AircraftLookup = null,
            Logger = NullLogger.Instance,
        };
    }

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    private static TaxiCommand ParseTaxi(string input)
    {
        var parsed = CommandParser.Parse(input);
        Assert.True(parsed.IsSuccess, $"'{input}' failed to parse: {parsed.Reason}");
        return Assert.IsType<TaxiCommand>(parsed.Value);
    }

    private void LogRoute(string label, TaxiRoute? route)
    {
        if (route is null)
        {
            output.WriteLine($"[{label}] route=<null>");
            return;
        }

        var taxiways = route.Segments.Select(s => s.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase);
        output.WriteLine($"[{label}] taxiways=[{string.Join(", ", taxiways)}] segments={route.Segments.Count}");
        output.WriteLine($"[{label}] warnings=[{string.Join(" | ", route.Warnings)}]");
        foreach (var hs in route.HoldShortPoints)
        {
            output.WriteLine($"[{label}]   HS node={hs.NodeId} target={hs.TargetName} reason={hs.Reason} cleared={hs.IsCleared}");
        }
    }

    /// <summary>
    /// Ticks phases + physics (the engine's split) until the aircraft holds short of <paramref name="targetName"/>
    /// or the budget runs out. Returns the holding phase, or null when it never arrived.
    /// </summary>
    private HoldingShortPhase? TickUntilHoldingShort(AircraftState ac, PhaseContext ctx, string targetName, int maxSeconds, GroundNode reference)
    {
        int ticks = (int)(maxSeconds / TickSeconds);
        for (int i = 0; i < ticks; i++)
        {
            PhaseRunner.Tick(ac, ctx);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);

            if (i % 200 == 0)
            {
                output.WriteLine(
                    $"t={i * TickSeconds:F0}s phase={ac.Phases?.CurrentPhase?.Name} ias={ac.IndicatedAirspeed:F1} "
                        + $"segIdx={ac.Ground.AssignedTaxiRoute?.CurrentSegmentIndex} distRef={DistanceFt(ac.Position, reference.Position):F0}ft"
                );
            }

            if (ac.Phases?.CurrentPhase is HoldingShortPhase holding)
            {
                if (string.Equals(holding.HoldShort.TargetName, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine(
                        $"t={i * TickSeconds:F1}s holding short of {targetName} at {DistanceFt(ac.Position, reference.Position):F0} ft from reference"
                    );
                    return holding;
                }

                Assert.Fail($"held short of '{holding.HoldShort.TargetName}' before reaching the '{targetName}' hold at t={i * TickSeconds:F1}s");
            }
        }

        return null;
    }

    // --- Route binding ---

    /// <summary>
    /// The core fix: <c>HS $17</c> binds an explicit hold-short to the spot-17 node on the route,
    /// alongside the 1L destination hold, and no longer hijacks the taxi destination.
    /// </summary>
    [Fact]
    public void TaxiCzbm1_HsSpot17_BindsSpotNode()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var spot = layout.FindSpotNodeByName(SpotName);
        Assert.NotNull(spot);
        var start = FindStartNodeOnCShortOfZ(layout);
        Assert.NotNull(start);
        var junction = layout.FindIntersectionNode("C", "Z");
        Assert.NotNull(junction);

        var ac = MakeAircraftAt(start, GeoMath.BearingTo(start.Position, junction.Position));
        var taxi = ParseTaxi("TAXI C Z B M1 1L HS $17");
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout);
        var route = ac.Ground.AssignedTaxiRoute;
        LogRoute("HS $17", route);
        Assert.True(result.Success, $"TAXI C Z B M1 1L HS $17 failed: {result.Message}");
        Assert.NotNull(route);

        Assert.Null(route.DestinationSpot);

        var hs = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.ExplicitHoldShort);
        Assert.Equal(SpotTargetName, hs.TargetName);
        Assert.Equal(spot.Id, hs.NodeId);
        Assert.False(hs.IsCleared);
        Assert.NotNull(hs.Latitude);
        Assert.NotNull(hs.Longitude);
        // A spot is a painted point: nose at the mark, i.e. half the aircraft length back (C525 ≈ 20 ft) —
        // not the taxiway setback (length + 30 ft), which would put a widebody back in the C/Z junction.
        double stopOffsetFt = DistanceFt(new LatLon(hs.Latitude.Value, hs.Longitude.Value), spot.Position);
        Assert.True(stopOffsetFt is > 5 and < 45, $"stop position is {stopOffsetFt:F0} ft from the spot node — expected nose-at-the-mark (½ length)");

        Assert.Contains(route.HoldShortPoints, h => (h.Reason == HoldShortReason.DestinationRunway) && (h.TargetName == "1L"));
        Assert.DoesNotContain(route.Warnings, w => w.Contains("not applied", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A spot the route never touches is reported, not silently dropped.</summary>
    [Fact]
    public void TaxiCzbm1_HsSpotOffRoute_Warns()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var start = FindStartNodeOnCShortOfZ(layout);
        Assert.NotNull(start);
        var junction = layout.FindIntersectionNode("C", "Z");
        Assert.NotNull(junction);

        var ac = MakeAircraftAt(start, GeoMath.BearingTo(start.Position, junction.Position));
        var taxi = ParseTaxi("TAXI C Z B M1 1L HS $8");
        var result = GroundCommandHandler.TryTaxi(ac, taxi, layout);
        var route = ac.Ground.AssignedTaxiRoute;
        LogRoute("HS $8", route);
        Assert.True(result.Success, $"TAXI C Z B M1 1L HS $8 failed: {result.Message}");
        Assert.NotNull(route);

        Assert.DoesNotContain(route.HoldShortPoints, h => h.TargetName == "$8");
        Assert.Contains(route.Warnings, w => w.Contains("HS $8 not applied", StringComparison.OrdinalIgnoreCase) && w.Contains("spot 8"));
    }

    /// <summary>Post-hoc <c>HS $17</c> on an aircraft already taxiing a route through the spot.</summary>
    [Fact]
    public void PostHoc_HsSpot17_AddsHoldAtSpotNode()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var spot = layout.FindSpotNodeByName(SpotName);
        Assert.NotNull(spot);
        var start = FindStartNodeOnCShortOfZ(layout);
        Assert.NotNull(start);
        var junction = layout.FindIntersectionNode("C", "Z");
        Assert.NotNull(junction);

        var ac = MakeAircraftAt(start, GeoMath.BearingTo(start.Position, junction.Position));
        var taxiResult = GroundCommandHandler.TryTaxi(ac, ParseTaxi("TAXI C Z B M1 1L"), layout);
        Assert.True(taxiResult.Success, $"setup taxi failed: {taxiResult.Message}");

        var parsed = CommandParser.Parse("HS $17");
        Assert.True(parsed.IsSuccess, $"'HS $17' failed to parse: {parsed.Reason}");
        var hsCmd = Assert.IsType<HoldShortCommand>(parsed.Value);

        var result = GroundCommandHandler.TryHoldShort(ac, hsCmd, layout);
        var route = ac.Ground.AssignedTaxiRoute;
        LogRoute("post-hoc $17", route);
        Assert.True(result.Success, $"HS $17 failed: {result.Message}");
        Assert.NotNull(route);

        var hs = Assert.Single(route.HoldShortPoints, h => h.Reason == HoldShortReason.ExplicitHoldShort);
        Assert.Equal(SpotTargetName, hs.TargetName);
        Assert.Equal(spot.Id, hs.NodeId);
    }

    // --- Phase behavior ---

    /// <summary>
    /// The aircraft stops short of spot 17 (not at the 1L bar), announces the spot in its phase
    /// name, and continues to the 1L hold after <c>RES</c>.
    /// </summary>
    [Fact]
    public void Taxi_HsSpot17_HoldsAtSpot_ThenResumesTo1L()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var spot = layout.FindSpotNodeByName(SpotName);
        Assert.NotNull(spot);
        var start = FindStartNodeOnCShortOfZ(layout);
        Assert.NotNull(start);
        var junction = layout.FindIntersectionNode("C", "Z");
        Assert.NotNull(junction);

        var ac = MakeAircraftAt(start, GeoMath.BearingTo(start.Position, junction.Position));
        var result = GroundCommandHandler.TryTaxi(ac, ParseTaxi("TAXI C Z B M1 1L HS $17"), layout);
        Assert.True(result.Success, $"taxi failed: {result.Message}");
        LogRoute("phase", ac.Ground.AssignedTaxiRoute);
        var ctx = Context(ac, layout);

        var holding = TickUntilHoldingShort(ac, ctx, SpotTargetName, maxSeconds: 300, spot);
        Assert.NotNull(holding);
        Assert.Equal("Holding Short spot 17", holding.Name);
        Assert.True(ac.IndicatedAirspeed < 1, $"still rolling at {ac.IndicatedAirspeed:F1} kt while holding short");

        // The holding announcement (warning lane here — no RPO speech in this context) names the spot, not the sigil.
        string announcement = Assert.Single(ac.PendingWarnings, w => w.Contains("holding short", StringComparison.OrdinalIgnoreCase));
        output.WriteLine($"announcement: {announcement}");
        Assert.Contains("holding short of spot 17", announcement);
        Assert.DoesNotContain("$", announcement);

        double distToSpotFt = DistanceFt(ac.Position, spot.Position);
        Assert.True(distToSpotFt is > 5 and < 60, $"holding {distToSpotFt:F0} ft from spot 17 — expected nose at the mark (½ length back)");

        // Bare RES from an explicit hold-short: the dispatcher applies any modifiers, then satisfies the hold.
        var resume = GroundCommandHandler.TryApplyRouteCrossingsAndHoldShorts(ac, layout, [], []);
        Assert.True(resume.Success, $"RES failed: {resume.Message}");
        holding.SatisfyClearance(ClearanceType.RunwayCrossing);

        var bar1L = TestLayoutNodes.RunwayHoldShortOnTaxiway(layout, "1L", "M1");
        Assert.NotNull(bar1L);
        var holding1L = TickUntilHoldingShort(ac, ctx, "1L", maxSeconds: 900, bar1L);
        Assert.NotNull(holding1L);
        Assert.Equal(HoldShortReason.DestinationRunway, holding1L.HoldShort.Reason);
    }

    // --- Recording replay ---

    /// <summary>
    /// Full replay of the reported session: N619TC spawns at t=330 with the preset and must hold
    /// short of spot 17 — never reaching the 1L bar first.
    /// </summary>
    [Fact]
    public void N619TC_Replay_HoldsShortOfSpot17()
    {
        var recording = RecordingLoader.Load(RecordingPath);
        var engine = BuildEngine();
        if (recording is null || engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        var spot = layout.FindSpotNodeByName(SpotName);
        Assert.NotNull(spot);

        engine.Replay(recording, SpawnSeconds);
        var aircraft = engine.FindAircraft("N619TC");
        Assert.NotNull(aircraft);
        LogRoute("replay", aircraft.Ground.AssignedTaxiRoute);

        for (int t = 1; t <= 900; t++)
        {
            engine.ReplayOneSecond();
            aircraft = engine.FindAircraft("N619TC");
            Assert.NotNull(aircraft);

            if (t % 30 == 0)
            {
                output.WriteLine(
                    $"t={SpawnSeconds + t} phase={aircraft.Phases?.CurrentPhase?.Name} ias={aircraft.IndicatedAirspeed:F1} "
                        + $"distSpot={DistanceFt(aircraft.Position, spot.Position):F0}ft"
                );
            }

            if (aircraft.Phases?.CurrentPhase is HoldingShortPhase holding)
            {
                Assert.True(
                    string.Equals(holding.HoldShort.TargetName, SpotTargetName, StringComparison.OrdinalIgnoreCase),
                    $"N619TC held short of '{holding.HoldShort.TargetName}' at t={SpawnSeconds + t} — expected spot 17 first"
                );

                double distToSpotFt = DistanceFt(aircraft.Position, spot.Position);
                output.WriteLine($"t={SpawnSeconds + t}: holding short of spot 17, {distToSpotFt:F0} ft from the spot node");
                Assert.True(distToSpotFt < 60, $"holding {distToSpotFt:F0} ft from spot 17 — expected nose at the mark");
                return;
            }
        }

        Assert.Fail("N619TC never held short of spot 17 within 900 s of spawning");
    }

    // --- Readback and STT ---

    /// <summary>A spot target is read back as a spot — never as runway or bare "one seven".</summary>
    [Theory]
    [InlineData("HS $17", "hold short of spot one seven")]
    [InlineData("TAXI C Z HS $17", "taxi via charlie, zulu, hold short of spot one seven")]
    [InlineData("RES HS $17", "resume taxi, hold short of spot one seven")]
    public void Readback_SpotTarget_SaysSpot(string command, string expected)
    {
        var parsed = CommandParser.Parse(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        Assert.Equal(expected, PhraseologyVerbalizer.Verbalize(parsed.Value!));
    }

    /// <summary>A misheard spot word must not become <c>HS $HARRIET</c> — it falls through to the LLM fallback.</summary>
    [Fact]
    public void Stt_ImplausibleSpotWord_IsRejected()
    {
        var result = PhraseologyMapper.Map("hold short of spot harriet", MapContext.Empty);
        Assert.True(result is null || !result.CanonicalCommand.Contains("$HARRIET"), $"mapped to {result?.CanonicalCommand}");
    }

    /// <summary>
    /// With the layout's parking/spot names in context, a spot the airport doesn't have (a
    /// mis-transcribed number) is rejected like an unknown taxiway; a known spot still maps.
    /// </summary>
    [Theory]
    [InlineData("hold short of spot one seven", "HS $17")]
    [InlineData("hold short of spot nine nine", null)]
    public void Stt_SpotHoldShort_ValidatesAgainstLayoutSpots(string transcript, string? expectedCanonical)
    {
        var ctx = MapContext.Empty with { DestinationNames = new HashSet<string>(["17", "7A", "8", "SIG3"], StringComparer.OrdinalIgnoreCase) };
        var result = PhraseologyMapper.Map(transcript, ctx);
        if (expectedCanonical is null)
        {
            Assert.True(result is null || !result.CanonicalCommand.Contains("$99"), $"mapped to {result?.CanonicalCommand}");
            return;
        }

        Assert.NotNull(result);
        Assert.Equal(expectedCanonical, result.CanonicalCommand);
    }

    [Theory]
    [InlineData("hold short of spot one seven", "HS $17")]
    [InlineData("hold short spot seventeen", "HS $17")]
    [InlineData("taxi via charlie zulu hold short of spot one seven", "TAXI C Z HS $17")]
    public void Stt_SpotHoldShort_MapsToCanonical(string transcript, string expectedCanonical)
    {
        var ctx = MapContext.Empty with { TaxiwayNames = new HashSet<string>(["C", "Z", "B", "M1"], StringComparer.OrdinalIgnoreCase) };
        var result = PhraseologyMapper.Map(transcript, ctx);
        Assert.NotNull(result);
        Assert.Equal(expectedCanonical, result.CanonicalCommand);
    }
}
