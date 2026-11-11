using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// YAAT-2044: an aircraft flying closed traffic under MRT/MLT is in the climb-out of a touch-and-go
/// (TouchAndGoPhase, lifted off, below 400 ft AGL). Its next circuit is not built until that phase
/// completes, so <c>EXT DOWNWIND</c> / <c>EXT CROSSWIND</c> found no pending leg and were refused with
/// "No upcoming downwind leg to extend" until the controller re-issued MRT (which splices the next circuit
/// in at once). EXT / EXT UPWIND already pre-arm the next circuit through <c>AircraftPattern.ExtendNextUpwind</c>;
/// the later legs must reach the next circuit the same way and fly extended when the aircraft gets there.
/// <c>SA</c> and <c>MNA</c> queue for that circuit too, and a queued <c>MNA</c> cancels a queued extension.
/// </summary>
public class ExtLegDuringTouchAndGoClimbOutTests(ITestOutputHelper output)
{
    private const string Callsign = "TST2044";

    [Theory]
    [InlineData("ERD 28R", "MRT", 37.78, -122.21, 180.0, "EXT DW", PatternEntryLeg.Downwind)]
    [InlineData("ELD 28L", "MLT", 37.68, -122.21, 0.0, "EXT DW", PatternEntryLeg.Downwind)]
    [InlineData("ERD 28R", "MRT", 37.78, -122.21, 180.0, "EXT CW", PatternEntryLeg.Crosswind)]
    public void ExtLeg_DuringTouchAndGoClimbOut_ExtendsThatLegOfTheNextCircuit(
        string entry,
        string direction,
        double lat,
        double lon,
        double headingDeg,
        string ext,
        PatternEntryLeg leg
    )
    {
        SimulationEngine? engine = BuildEngineInPattern(entry, direction, lat, lon, headingDeg);
        Assert.NotNull(engine);
        AircraftState ac = FlyToClimbOut(engine!);

        Assert.IsType<TouchAndGoPhase>(ac.Phases?.CurrentPhase);
        // The next circuit is not built yet: this is the window the controller hit.
        Assert.DoesNotContain(ac.Phases!.Phases, p => (p is CrosswindPhase or DownwindPhase) && (p.Status == PhaseStatus.Pending));

        CommandResult result = engine!.SendCommand(Callsign, ext);
        output.WriteLine($"{ext}: Success={result.Success}, Message={result.Message}");
        Assert.True(result.Success, $"{ext} during the touch-and-go climb-out under {direction} should be accepted, got: {result.Message}");

        // The extension must survive into the next circuit and hold on the requested leg.
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            AircraftState current = engine.FindAircraft(Callsign)!;
            Phase? phase = current.Phases?.CurrentPhase;
            bool onLeg = leg == PatternEntryLeg.Downwind ? phase is DownwindPhase : phase is CrosswindPhase;
            if (!onLeg)
            {
                continue;
            }

            bool extended = phase switch
            {
                DownwindPhase dw => dw.IsExtended,
                CrosswindPhase cw => cw.IsExtended,
                _ => false,
            };
            Assert.True(extended, $"The next circuit's {leg} should fly extended after {ext} during the climb-out");
            return;
        }

        Assert.Fail($"The aircraft never reached the next circuit's {leg}");
    }

    [Fact]
    public void Sa_DuringTouchAndGoClimbOut_FliesTheNextCircuitShort()
    {
        SimulationEngine? engine = BuildEngineInPattern("ERD 28R", "MRT", 37.78, -122.21, 180.0);
        Assert.NotNull(engine);
        AircraftState ac = FlyToClimbOut(engine!);
        Assert.IsType<TouchAndGoPhase>(ac.Phases?.CurrentPhase);

        CommandResult result = engine!.SendCommand(Callsign, "SA");
        output.WriteLine($"SA: Success={result.Success}, Message={result.Message}");
        Assert.True(result.Success, $"SA during the touch-and-go climb-out should be accepted, got: {result.Message}");

        (DownwindPhase Downwind, BasePhase Base)? nextCircuit = AwaitNextCircuitPendingLegs(engine);
        Assert.NotNull(nextCircuit);
        Assert.True(nextCircuit!.Value.Downwind.ShortApproachArmed, "The next circuit's downwind should be armed for a short approach");

        double shortFinalNm = CategoryPerformance.MinShortApproachFinalNm(AircraftCategorization.Categorize("C172"));
        Assert.Equal(shortFinalNm, nextCircuit.Value.Base.FinalDistanceNm ?? -1);
    }

    /// <summary>
    /// The owner's wider window: <c>SA</c> on short final before a planned touch-and-go applies to the lap
    /// the touch-and-go begins, not to the approach already committed.
    /// </summary>
    [Fact]
    public void Sa_OnFinalBeforePlannedTouchAndGo_AppliesToTheNextLap()
    {
        SimulationEngine? engine = BuildEngineInPattern("ERD 28R", "MRT", 37.78, -122.21, 180.0);
        Assert.NotNull(engine);
        AircraftState ac = FlyToFinal(engine!);
        Assert.IsType<FinalApproachPhase>(ac.Phases?.CurrentPhase);

        CommandResult result = engine!.SendCommand(Callsign, "SA");
        output.WriteLine($"SA on final: Success={result.Success}, Message={result.Message}");
        Assert.True(result.Success, $"SA on final before a planned touch-and-go should be accepted, got: {result.Message}");

        (DownwindPhase Downwind, BasePhase Base)? nextCircuit = AwaitNextCircuitPendingLegs(engine);
        Assert.NotNull(nextCircuit);
        Assert.True(nextCircuit!.Value.Downwind.ShortApproachArmed, "The next lap's downwind should be armed for a short approach");

        double shortFinalNm = CategoryPerformance.MinShortApproachFinalNm(AircraftCategorization.Categorize("C172"));
        Assert.Equal(shortFinalNm, nextCircuit.Value.Base.FinalDistanceNm ?? -1);
    }

    [Fact]
    public void Mna_DuringTouchAndGoClimbOut_CancelsAQueuedExtension()
    {
        SimulationEngine? engine = BuildEngineInPattern("ERD 28R", "MRT", 37.78, -122.21, 180.0);
        Assert.NotNull(engine);
        AircraftState ac = FlyToClimbOut(engine!);
        Assert.IsType<TouchAndGoPhase>(ac.Phases?.CurrentPhase);

        CommandResult ext = engine!.SendCommand(Callsign, "EXT DW");
        output.WriteLine($"EXT DW: Success={ext.Success}, Message={ext.Message}");
        Assert.True(ext.Success, $"EXT DW during the touch-and-go climb-out should be accepted, got: {ext.Message}");

        CommandResult mna = engine.SendCommand(Callsign, "MNA");
        output.WriteLine($"MNA: Success={mna.Success}, Message={mna.Message}");
        Assert.True(mna.Success, $"MNA during the touch-and-go climb-out should be accepted, got: {mna.Message}");

        (DownwindPhase Downwind, BasePhase Base)? nextCircuit = AwaitNextCircuitPendingLegs(engine);
        Assert.NotNull(nextCircuit);
        Assert.False(nextCircuit!.Value.Downwind.IsExtended, "MNA should cancel the EXT DW queued for the next circuit");
        Assert.False(nextCircuit.Value.Downwind.ShortApproachArmed);
    }

    /// <summary>
    /// <c>MNA</c> in the same window cancels a queued <c>EXT UPWIND</c> too — the behaviour the one-shot
    /// <c>ExtendNextUpwind</c> flag exists for, observed on the leg it would have extended.
    /// </summary>
    [Fact]
    public void Mna_DuringTouchAndGoClimbOut_CancelsAQueuedUpwindExtension()
    {
        SimulationEngine? engine = BuildEngineInPattern("ERD 28R", "MRT", 37.78, -122.21, 180.0);
        Assert.NotNull(engine);
        AircraftState ac = FlyToClimbOut(engine!);
        Assert.IsType<TouchAndGoPhase>(ac.Phases?.CurrentPhase);

        CommandResult ext = engine!.SendCommand(Callsign, "EXT UW");
        output.WriteLine($"EXT UW: Success={ext.Success}, Message={ext.Message}");
        Assert.True(ext.Success, $"EXT UW during the touch-and-go climb-out should be accepted, got: {ext.Message}");

        CommandResult mna = engine.SendCommand(Callsign, "MNA");
        output.WriteLine($"MNA: Success={mna.Success}, Message={mna.Message}");
        Assert.True(mna.Success, $"MNA during the touch-and-go climb-out should be accepted, got: {mna.Message}");

        UpwindPhase? upwind = AwaitNextCircuitUpwind(engine);
        Assert.NotNull(upwind);
        Assert.False(upwind!.IsExtended, "MNA should cancel the EXT UW queued for the next circuit");
    }

    /// <summary>
    /// A modifier armed against a queued pattern entry belongs to the circuit that entry builds, not to the
    /// lap the touch-and-go auto-cycle appends: <c>OTG ERD 28R</c> then <c>EXT DW</c> extends the downwind
    /// the entry installs.
    /// </summary>
    [Fact]
    public void OtgEntryThenExtDw_DuringTouchAndGo_ExtendsTheEntrysDownwind()
    {
        SimulationEngine? engine = BuildEngineInPattern("ERD 28R", "MRT", 37.78, -122.21, 180.0);
        Assert.NotNull(engine);
        AircraftState ac = FlyToClimbOut(engine!);
        Assert.IsType<TouchAndGoPhase>(ac.Phases?.CurrentPhase);

        CommandResult otg = engine!.SendCommand(Callsign, "OTG ERD 28R");
        output.WriteLine($"OTG ERD 28R: Success={otg.Success}, Message={otg.Message}");
        Assert.True(otg.Success, otg.Message);

        CommandResult ext = engine.SendCommand(Callsign, "EXT DW");
        output.WriteLine($"EXT DW: Success={ext.Success}, Message={ext.Message}");
        Assert.True(ext.Success, $"EXT DW behind the OTG entry should be accepted, got: {ext.Message}");

        Assert.True(FlownExtendedDownwindAfterPatternEntry(engine), "The downwind of the circuit ERD builds should fly extended");
    }

    /// <summary>
    /// An entry issued directly during the climb-out builds the next circuit whichever way it is reached:
    /// the extension queued before it must land on that circuit, not wait for a lap after it.
    /// </summary>
    [Fact]
    public void ExtDwThenDirectErd_DuringTouchAndGoClimbOut_ExtendsTheEntryCircuitsDownwind()
    {
        SimulationEngine? engine = BuildEngineInPattern("ERD 28R", "MRT", 37.78, -122.21, 180.0);
        Assert.NotNull(engine);
        AircraftState ac = FlyToClimbOut(engine!);
        Assert.IsType<TouchAndGoPhase>(ac.Phases?.CurrentPhase);

        CommandResult ext = engine!.SendCommand(Callsign, "EXT DW");
        output.WriteLine($"EXT DW: Success={ext.Success}, Message={ext.Message}");
        Assert.True(ext.Success, $"EXT DW during the touch-and-go climb-out should be accepted, got: {ext.Message}");

        CommandResult erd = engine.SendCommand(Callsign, "ERD 28R");
        output.WriteLine($"ERD 28R: Success={erd.Success}, Message={erd.Message}");
        Assert.True(erd.Success, $"ERD 28R during the touch-and-go climb-out should be accepted, got: {erd.Message}");

        Assert.True(FlownExtendedDownwind(engine), "The downwind of the circuit ERD builds should fly extended");
    }

    /// <summary>
    /// A bare <c>EXT</c>/<c>EXT UPWIND</c> queued in the climb-out must survive a re-issued <c>MRT</c>: the
    /// spliced circuit's upwind is the next lap, so it flies extended.
    /// </summary>
    [Fact]
    public void ExtUwThenMrt_DuringTouchAndGoClimbOut_ExtendsTheInsertedCircuitsUpwind()
    {
        SimulationEngine? engine = BuildEngineInPattern("ERD 28R", "MRT", 37.78, -122.21, 180.0);
        Assert.NotNull(engine);
        AircraftState ac = FlyToClimbOut(engine!);
        Assert.IsType<TouchAndGoPhase>(ac.Phases?.CurrentPhase);

        CommandResult ext = engine!.SendCommand(Callsign, "EXT UW");
        output.WriteLine($"EXT UW: Success={ext.Success}, Message={ext.Message}");
        Assert.True(ext.Success, $"EXT UW during the touch-and-go climb-out should be accepted, got: {ext.Message}");

        CommandResult mrt = engine.SendCommand(Callsign, "MRT");
        output.WriteLine($"MRT: Success={mrt.Success}, Message={mrt.Message}");
        Assert.True(mrt.Success, $"MRT during the touch-and-go climb-out should be accepted, got: {mrt.Message}");

        UpwindPhase? upwind = AwaitNextCircuitUpwind(engine);
        Assert.NotNull(upwind);
        Assert.True(upwind!.IsExtended, "The upwind of the circuit MRT inserted should fly extended");
    }

    /// <summary>
    /// Re-issuing <c>MRT</c> during the climb-out splices the next circuit in at once; the extension queued
    /// for the auto-cycle lap must land on that inserted circuit instead of waiting for a lap that never comes.
    /// </summary>
    [Fact]
    public void ExtDwThenMrt_DuringTouchAndGoClimbOut_ExtendsTheInsertedCircuitsDownwind()
    {
        SimulationEngine? engine = BuildEngineInPattern("ERD 28R", "MRT", 37.78, -122.21, 180.0);
        Assert.NotNull(engine);
        AircraftState ac = FlyToClimbOut(engine!);
        Assert.IsType<TouchAndGoPhase>(ac.Phases?.CurrentPhase);

        CommandResult ext = engine!.SendCommand(Callsign, "EXT DW");
        output.WriteLine($"EXT DW: Success={ext.Success}, Message={ext.Message}");
        Assert.True(ext.Success, $"EXT DW during the touch-and-go climb-out should be accepted, got: {ext.Message}");

        CommandResult mrt = engine.SendCommand(Callsign, "MRT");
        output.WriteLine($"MRT: Success={mrt.Success}, Message={mrt.Message}");
        Assert.True(mrt.Success, $"MRT during the touch-and-go climb-out should be accepted, got: {mrt.Message}");

        PhaseList phases = engine.FindAircraft(Callsign)!.Phases!;
        Assert.Contains(phases.Phases, p => p is UpwindPhase && (p.Status is PhaseStatus.Pending or PhaseStatus.Active));

        Assert.True(FlownExtendedDownwind(engine), "The downwind of the circuit MRT inserted should fly extended");
    }

    /// <summary>
    /// A pattern go-around re-enters the pattern, so a modifier given during the go-around climb rides into
    /// the appended circuit the same way it does after a touch-and-go.
    /// </summary>
    [Fact]
    public void Ga_ThenExtDw_DuringGoAroundClimb_ExtendsTheAppendedCircuitsDownwind()
    {
        SimulationEngine? engine = BuildEngineInPattern("ERD 28R", "MRT", 37.78, -122.21, 180.0);
        Assert.NotNull(engine);
        AircraftState ac = FlyToFinal(engine!);
        Assert.IsType<FinalApproachPhase>(ac.Phases?.CurrentPhase);

        CommandResult ga = engine!.SendCommand(Callsign, "GA");
        output.WriteLine($"GA: Success={ga.Success}, Message={ga.Message}");
        Assert.True(ga.Success, $"GA on final should be accepted, got: {ga.Message}");
        Assert.IsType<GoAroundPhase>(engine.FindAircraft(Callsign)!.Phases?.CurrentPhase);

        CommandResult ext = engine.SendCommand(Callsign, "EXT DW");
        output.WriteLine($"EXT DW: Success={ext.Success}, Message={ext.Message}");
        Assert.True(ext.Success, $"EXT DW during the go-around climb should be accepted, got: {ext.Message}");

        Assert.True(FlownExtendedDownwind(engine), "The downwind of the circuit the go-around appends should fly extended");
    }

    private static SimulationEngine? BuildEngineInPattern(string entry, string direction, double lat, double lon, double headingDeg)
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "ext-leg-tg-climb-out",
                ScenarioName = "EXT leg during touch-and-go climb-out",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        engine.World.AddAircraft(
            new AircraftState
            {
                Callsign = Callsign,
                AircraftType = "C172",
                Position = new LatLon(lat, lon),
                TrueHeading = new TrueHeading(headingDeg),
                TrueTrack = new TrueHeading(headingDeg),
                Altitude = 1500,
                IndicatedAirspeed = 100,
                IsOnGround = false,
                FlightPlan = new AircraftFlightPlan
                {
                    Departure = "KOAK",
                    Destination = "KOAK",
                    FlightRules = "VFR",
                    Altitude = PlannedAltitude.Vfr(1500),
                    CruiseSpeed = 110,
                },
            }
        );

        Assert.True(engine.SendCommand(Callsign, entry).Success, entry);
        Assert.True(engine.SendCommand(Callsign, direction).Success, direction);
        Assert.True(engine.SendCommand(Callsign, "TG").Success, "TG");
        return engine;
    }

    /// <summary>Flies the circuit into the touch-and-go climb-out: lifted off, still in TouchAndGoPhase (below 400 ft AGL).</summary>
    private AircraftState FlyToClimbOut(SimulationEngine engine)
    {
        AircraftState? ac = null;
        for (int t = 1; t <= 600; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            if (ac.Phases?.CurrentPhase is TouchAndGoPhase { HasLiftedOff: true } && (ac.Altitude > 200))
            {
                output.WriteLine($"t={t}: climb-out at {ac.Altitude:F0} ft MSL");
                break;
            }
        }

        Assert.NotNull(ac);
        return ac!;
    }

    /// <summary>Flies the circuit to its final approach, the phase before the planned touch-and-go.</summary>
    private AircraftState FlyToFinal(SimulationEngine engine)
    {
        AircraftState? ac = null;
        for (int t = 1; t <= 600; t++)
        {
            engine.TickOneSecond();
            ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            if (ac.Phases?.CurrentPhase is FinalApproachPhase)
            {
                output.WriteLine($"t={t}: final approach at {ac.Altitude:F0} ft MSL");
                break;
            }
        }

        Assert.NotNull(ac);
        return ac!;
    }

    /// <summary>
    /// Ticks until PhaseRunner appends the next circuit, then returns that circuit's still-pending
    /// downwind and base — where the queued SA / EXT pre-arm lands.
    /// </summary>
    private (DownwindPhase Downwind, BasePhase Base)? AwaitNextCircuitPendingLegs(SimulationEngine engine)
    {
        for (int t = 1; t <= 600; t++)
        {
            engine.TickOneSecond();
            PhaseList? phases = engine.FindAircraft(Callsign)?.Phases;
            DownwindPhase? downwind = phases?.Phases.OfType<DownwindPhase>().FirstOrDefault(p => p.Status == PhaseStatus.Pending);
            BasePhase? basePhase = phases?.Phases.OfType<BasePhase>().FirstOrDefault(p => p.Status == PhaseStatus.Pending);
            if (downwind is not null && basePhase is not null)
            {
                return (downwind, basePhase);
            }
        }

        return null;
    }

    /// <summary>Ticks until the next circuit's upwind becomes the current phase.</summary>
    private UpwindPhase? AwaitNextCircuitUpwind(SimulationEngine engine)
    {
        for (int t = 1; t <= 600; t++)
        {
            engine.TickOneSecond();
            if (engine.FindAircraft(Callsign)?.Phases?.CurrentPhase is UpwindPhase upwind)
            {
                return upwind;
            }
        }

        return null;
    }

    /// <summary>Ticks until a downwind becomes the current phase and reports whether it is extended.</summary>
    private bool FlownExtendedDownwind(SimulationEngine engine)
    {
        for (int t = 1; t <= 600; t++)
        {
            engine.TickOneSecond();
            if (engine.FindAircraft(Callsign)?.Phases?.CurrentPhase is DownwindPhase downwind)
            {
                return downwind.IsExtended;
            }
        }

        output.WriteLine("The aircraft never reached a downwind leg");
        return false;
    }

    /// <summary>
    /// Ticks past the circuit installed by a pattern entry, then reports whether the first downwind flown
    /// after that entry is extended.
    /// </summary>
    private bool FlownExtendedDownwindAfterPatternEntry(SimulationEngine engine)
    {
        bool entrySeen = false;
        for (int t = 1; t <= 600; t++)
        {
            engine.TickOneSecond();
            Phase? phase = engine.FindAircraft(Callsign)?.Phases?.CurrentPhase;
            entrySeen |= phase is PatternEntryPhase;
            if (entrySeen && phase is DownwindPhase downwind)
            {
                return downwind.IsExtended;
            }
        }

        output.WriteLine("The aircraft never flew the entry circuit's downwind");
        return false;
    }
}
