using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression: a <see cref="DepartureProcedurePhase"/> writes <c>TargetAltitude</c> from the ceiling it
/// resolved once in <c>OnStart</c>, so a lateral vector issued while the aircraft sits under a SID climb
/// window (an ARINC-424 "at or below" / "between" crossing) must not strand the climb at the window
/// ceiling — and a controller altitude issued before or during that window must survive every one of the
/// phase's own altitude writes.
///
/// COAST9 RWY30 out of KOAK opens with a VD leg "heading 296° to OAK 4.0 DME, between 1400 and 2000".
/// <see cref="DepartureProcedurePhase.ApplyLegAltitudeCap"/> caps <c>TargetAltitude</c> at 2000 while that
/// leg is active. A controller "maintain 5,000" is additive
/// (<see cref="Phase.IsAdditiveAirborneAdjustment"/> is <see cref="CommandAcceptance.Allowed"/>): it writes
/// <c>Targets.AssignedAltitude</c> + <c>TargetAltitude</c> and leaves the SID flying laterally, so the
/// phase must stop re-capping the target and must carry the assignment out through the vector override and
/// Finish.
///
/// 7110.65 §4-3-2.c.1 / AIM §5-2-9.h.3: a vector off a SID cancels the SID's published altitude
/// restrictions and the pilot flies the altitude ATC assigned; a plain "maintain" interrupting the SID's
/// vertical navigation takes priority over the published restrictions. With no controller altitude, the
/// SID's published initial altitude stands as a floor the lateral vector cannot descend below.
/// </summary>
public class DepartureProcedureVectorClimbStallTests
{
    private const double WindowCeilingFt = 2000.0;
    private const double LevelToleranceFt = 50.0;
    private const int ControllerAltitudeFt = 5000;

    private readonly ITestOutputHelper _output;

    public DepartureProcedureVectorClimbStallTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    /// <summary>A cleared COAST9 RWY30 departure under way, with the OAK fix that locates its climb window.</summary>
    private sealed record Departure(AircraftState Aircraft, PhaseContext Context, LatLon Oak);

    private sealed record FlightResult(double MinAltitude, double MaxAltitude, bool SawDepartureProcedure);

    [Fact]
    public void VectorOffSidInsideClimbWindow_ResumesClimb_DoesNotStallAtWindowCeiling()
    {
        Departure departure = FlyIntoClimbWindow(null);
        AircraftState ac = departure.Aircraft;
        Assert.True(
            ac.Altitude <= WindowCeilingFt + 100,
            $"precondition: expected the aircraft leveled at ~{WindowCeilingFt:F0} ft, was {ac.Altitude:F0}"
        );

        // Controller vectors the aircraft off the SID — a lateral-only instruction.
        Dispatch(ac, "FH 270");

        // After the vector the SID restriction is gone: the aircraft must resume its climb well clear of the
        // 2000 ft window ceiling, not stall at it.
        FlightResult flight = FlyFor(departure, 180);
        _output.WriteLine($"maxAltAfterVector={flight.MaxAltitude:F0} finalTgtAlt={ac.Targets.TargetAltitude?.ToString() ?? "-"}");

        Assert.True(
            flight.MaxAltitude > WindowCeilingFt + 1000,
            $"Aircraft stalled at {flight.MaxAltitude:F0} ft after being vectored off COAST9 — the SID climb "
                + $"window ({WindowCeilingFt:F0} ft) was never released, so the vector silently capped the climb."
        );
    }

    [Fact]
    public void VectorOffSid_AfterClimbToFiveThousand_LevelsAtFiveThousand()
    {
        Departure departure = FlyIntoClimbWindow(null);
        AircraftState ac = departure.Aircraft;

        Dispatch(ac, "CM 050");
        Dispatch(ac, "FH 270");

        FlightResult flight = FlyFor(departure, 240);
        _output.WriteLine($"maxAlt={flight.MaxAltitude:F0} finalAlt={ac.Altitude:F0}");

        Assert.True(
            flight.MaxAltitude < ControllerAltitudeFt + LevelToleranceFt,
            $"the controller's {ControllerAltitudeFt} ft assignment was replaced by the SID climb ceiling — "
                + $"the aircraft reached {flight.MaxAltitude:F0}"
        );
        Assert.InRange(ac.Altitude, ControllerAltitudeFt - LevelToleranceFt, ControllerAltitudeFt + LevelToleranceFt);
    }

    [Fact]
    public void VectorWithBundledClimb_KeepsBundledAltitude()
    {
        Departure departure = FlyIntoClimbWindow(null);
        AircraftState ac = departure.Aircraft;

        DispatchLine(ac, "FH 270, CM 050");

        FlightResult flight = FlyFor(departure, 240);
        _output.WriteLine($"maxAlt={flight.MaxAltitude:F0} finalAlt={ac.Altitude:F0}");

        Assert.True(
            flight.MaxAltitude < ControllerAltitudeFt + LevelToleranceFt,
            $"the {ControllerAltitudeFt} ft climb bundled behind the vector was lost — the aircraft reached {flight.MaxAltitude:F0}"
        );
        Assert.InRange(ac.Altitude, ControllerAltitudeFt - LevelToleranceFt, ControllerAltitudeFt + LevelToleranceFt);
    }

    [Fact]
    public void ClimbDuringSid_HoldsAssignedAltitudeThroughCrossingWindow()
    {
        Departure departure = FlyIntoClimbWindow(null);
        AircraftState ac = departure.Aircraft;

        Dispatch(ac, "CM 050");

        FlightResult flight = FlyFor(departure, 180);
        _output.WriteLine($"maxAlt={flight.MaxAltitude:F0} finalAlt={ac.Altitude:F0}");

        Assert.True(flight.SawDepartureProcedure, "the aircraft should still be flying the SID laterally after the climb");
        Assert.True(
            flight.MaxAltitude < ControllerAltitudeFt + LevelToleranceFt,
            $"the {ControllerAltitudeFt} ft assignment was re-capped by the SID crossing window — the aircraft reached {flight.MaxAltitude:F0}"
        );
        Assert.InRange(ac.Altitude, ControllerAltitudeFt - LevelToleranceFt, ControllerAltitudeFt + LevelToleranceFt);
    }

    /// <summary>
    /// A climb issued during the takeoff/initial climb, before the departure-procedure phase exists, owns the
    /// altitude: the phase must pick the assignment up at <c>OnStart</c> instead of its resolved ceiling and
    /// must not let the first coded leg's crossing window re-cap it.
    /// </summary>
    [Fact]
    public void ClimbDuringInitialClimb_BeforeProcedurePhase_HoldsAssignedAltitude()
    {
        Departure departure = StartCoast9Departure(null);
        AircraftState ac = departure.Aircraft;
        Assert.IsType<InitialClimbPhase>(ac.Phases!.CurrentPhase);

        Dispatch(ac, "CM 050");

        FlightResult flight = FlyFor(departure, 240);
        _output.WriteLine($"maxAlt={flight.MaxAltitude:F0} finalAlt={ac.Altitude:F0}");

        Assert.True(flight.SawDepartureProcedure, "the departure-procedure phase should have flown the SID");
        Assert.True(
            flight.MaxAltitude < ControllerAltitudeFt + LevelToleranceFt,
            $"the climb assigned during the initial climb was replaced by the SID climb ceiling — reached {flight.MaxAltitude:F0}"
        );
        Assert.InRange(ac.Altitude, ControllerAltitudeFt - LevelToleranceFt, ControllerAltitudeFt + LevelToleranceFt);
    }

    /// <summary>
    /// The controller's assignment outlives the coded legs: when the phase completes through <c>Finish</c>
    /// (not a lateral override) the aircraft holds the assigned altitude instead of the resolved ceiling.
    /// </summary>
    [Fact]
    public void ClimbDuringSid_HoldsAssignedAltitudeAfterProcedureFinishes()
    {
        Departure departure = FlyIntoClimbWindow(null);
        AircraftState ac = departure.Aircraft;
        DepartureProcedurePhase phase = Assert.IsType<DepartureProcedurePhase>(ac.Phases!.CurrentPhase);

        Dispatch(ac, "CM 050");

        // COAST9 RWY30's coded legs are VD → VM, and the heading-to-manual leg holds until a controller
        // vector, so this phase never reaches Finish unaided. Rewind its own snapshot to "every coded leg
        // flown" and let the phase list run the real completion: Finish, then the fix-to-fix route.
        _output.WriteLine($"legs={phase.Legs.Count} activeLeg={phase.ActiveLegIndex} types={string.Join(",", phase.Legs.Select(l => l.Type))}");
        DepartureProcedurePhaseDto snapshot = Assert.IsType<DepartureProcedurePhaseDto>(phase.ToSnapshot());
        Assert.False(snapshot.Overridden, "setup: the aircraft must still be flying the coded procedure");
        var allLegsFlown = DepartureProcedurePhase.FromSnapshot(EveryLegFlown(snapshot));
        int index = ac.Phases.Phases.IndexOf(phase);
        Assert.True(index >= 0, "the departure-procedure phase must be in the phase list");
        ac.Phases.Phases[index] = allLegsFlown;

        FlightResult after = FlyFor(departure, 180);
        _output.WriteLine($"afterFinishMaxAlt={after.MaxAltitude:F0} finalAlt={ac.Altitude:F0}");

        Assert.False(ReferenceEquals(allLegsFlown, ac.Phases.CurrentPhase), "the phase must complete through Finish");
        Assert.False(
            Assert.IsType<DepartureProcedurePhaseDto>(allLegsFlown.ToSnapshot()).Overridden,
            "the phase must complete through Finish, not a lateral override"
        );
        Assert.True(
            after.MaxAltitude < ControllerAltitudeFt + LevelToleranceFt,
            $"the {ControllerAltitudeFt} ft assignment did not survive Finish — the aircraft reached {after.MaxAltitude:F0}"
        );
        Assert.InRange(ac.Altitude, ControllerAltitudeFt - LevelToleranceFt, ControllerAltitudeFt + LevelToleranceFt);
    }

    /// <summary>The phase snapshot with every coded leg marked flown, so the next tick runs <c>Finish</c>.</summary>
    private static DepartureProcedurePhaseDto EveryLegFlown(DepartureProcedurePhaseDto snapshot) =>
        new()
        {
            Status = snapshot.Status,
            ElapsedSeconds = snapshot.ElapsedSeconds,
            Requirements = snapshot.Requirements,
            Legs = snapshot.Legs,
            PostRoute = snapshot.PostRoute,
            AssignedAltitude = snapshot.AssignedAltitude,
            CruiseAltitude = snapshot.CruiseAltitude,
            LegIndex = snapshot.Legs.Count,
            Overridden = false,
            LegEntryPosition = snapshot.LegEntryPosition,
            PreviousSignedCrossTrack = snapshot.PreviousSignedCrossTrack,
            LegElapsedSeconds = snapshot.LegElapsedSeconds,
            ControllerAltitude = snapshot.ControllerAltitude,
        };

    /// <summary>
    /// The controller-altitude flag survives the phase snapshot while set, and a null field — what an absent
    /// field in an older snapshot deserializes to — reads as unset: the clean default, needing no migration.
    /// </summary>
    [Fact]
    public void ControllerAltitudeFlag_RoundTripsThroughSnapshot()
    {
        Departure departure = FlyIntoClimbWindow(null);
        DepartureProcedurePhase phase = Assert.IsType<DepartureProcedurePhase>(departure.Aircraft.Phases!.CurrentPhase);

        DepartureProcedurePhaseDto unset = Assert.IsType<DepartureProcedurePhaseDto>(phase.ToSnapshot());
        Assert.Null(unset.ControllerAltitude);
        Assert.Null(Assert.IsType<DepartureProcedurePhaseDto>(DepartureProcedurePhase.FromSnapshot(unset).ToSnapshot()).ControllerAltitude);

        Dispatch(departure.Aircraft, "CM 050");
        DepartureProcedurePhaseDto set = Assert.IsType<DepartureProcedurePhaseDto>(phase.ToSnapshot());
        Assert.True(set.ControllerAltitude);
        Assert.True(Assert.IsType<DepartureProcedurePhaseDto>(DepartureProcedurePhase.FromSnapshot(set).ToSnapshot()).ControllerAltitude);
    }

    /// <summary>
    /// A vector with no controller altitude leaves the departure holding the SID's published initial altitude
    /// (OAK publishes 2,000 ft), not the filed cruise altitude the phase resolved as its climb ceiling.
    /// </summary>
    [Fact]
    public void VectorOffSid_NoAssignment_HoldsSidInitialAltitude()
    {
        ArtccConfigRoot? artccConfig = TestArtccConfig.LoadZoa();
        if (artccConfig is null)
        {
            _output.WriteLine("ZOA ARTCC config snapshot not available — skipping.");
            return;
        }

        Departure departure = FlyIntoClimbWindow(artccConfig);
        AircraftState ac = departure.Aircraft;
        int sidInitial = Assert.NotNull(ac.Procedure.SidInitialAltitudeFt);
        _output.WriteLine($"SidInitialAltitudeFt={sidInitial}");

        Dispatch(ac, "FH 270");

        FlightResult flight = FlyFor(departure, 180);
        _output.WriteLine($"maxAlt={flight.MaxAltitude:F0} finalAlt={ac.Altitude:F0}");

        Assert.True(
            flight.MaxAltitude < sidInitial + 100,
            $"a vector with no controller altitude must not climb past the SID's published {sidInitial} ft, " + $"but reached {flight.MaxAltitude:F0}"
        );
        Assert.InRange(ac.Altitude, sidInitial - 100, sidInitial + 100);
    }

    /// <summary>
    /// An aircraft already above the SID's published initial altitude, vectored with no controller altitude,
    /// levels off where it is: the lateral vector cannot command a descent back to the published altitude.
    /// </summary>
    [Fact]
    public void VectorOffSid_NoAssignment_AboveSidInitial_NeverDescends()
    {
        ArtccConfigRoot? artccConfig = TestArtccConfig.LoadZoa();
        if (artccConfig is null)
        {
            _output.WriteLine("ZOA ARTCC config snapshot not available — skipping.");
            return;
        }

        Departure departure = FlyIntoClimbWindow(artccConfig);
        AircraftState ac = departure.Aircraft;
        int sidInitial = Assert.NotNull(ac.Procedure.SidInitialAltitudeFt);

        // Fly on past the window: with no assignment only the published initial altitude stands behind the
        // lateral path, and the coded legs climb past it.
        FlyUntilAbove(departure, sidInitial + 500, 180);
        double altitudeAtVector = ac.Altitude;

        Dispatch(ac, "FH 270");

        FlightResult flight = FlyFor(departure, 180);
        _output.WriteLine($"altAtVector={altitudeAtVector:F0} minAfter={flight.MinAltitude:F0} finalAlt={ac.Altitude:F0}");

        Assert.True(
            flight.MinAltitude >= altitudeAtVector - LevelToleranceFt,
            $"a lateral vector sent the aircraft down from {altitudeAtVector:F0} ft to {flight.MinAltitude:F0} ft"
        );
        Assert.InRange(ac.Altitude, altitudeAtVector - 100, altitudeAtVector + 100);
    }

    private static RunwayInfo? Koak30()
    {
        RunwayInfo? phys = NavigationDatabase
            .Instance.GetRunways("KOAK")
            .FirstOrDefault(r => r.Id.End1 == "30" || r.Id.End2 == "30" || r.Designator.Contains("30", StringComparison.Ordinal));
        if (phys is null)
        {
            return null;
        }
        return new RunwayInfo
        {
            AirportId = "KOAK",
            Id = phys.Id,
            Designator = "30",
            Lat1 = phys.Lat1,
            Lon1 = phys.Lon1,
            Elevation1Ft = phys.Elevation1Ft,
            TrueHeading1 = phys.TrueHeading1,
            Lat2 = phys.Lat2,
            Lon2 = phys.Lon2,
            Elevation2Ft = phys.Elevation2Ft,
            TrueHeading2 = phys.TrueHeading2,
            WidthFt = phys.WidthFt,
        };
    }

    /// <summary>
    /// Clears a COAST9 RWY30 departure for takeoff with no commanded altitude and puts it airborne just past
    /// the DER, in its initial climb. NavData and CIFP are hard preconditions, so a missing runway, fix or
    /// SID fails rather than skips.
    /// </summary>
    private static Departure StartCoast9Departure(ArtccConfigRoot? artccConfig)
    {
        RunwayInfo rwy = Assert.IsType<RunwayInfo>(Koak30());
        (double Lat, double Lon) oakPos = Assert.NotNull(NavigationDatabase.Instance.GetFixPosition("OAK"));
        Assert.NotNull(NavigationDatabase.Instance.GetSid("KOAK", "COAST9"));
        var oak = new LatLon(oakPos.Lat, oakPos.Lon);

        var ac = new AircraftState
        {
            Callsign = "SWA1822",
            AircraftType = "B738",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            Altitude = rwy.ElevationFt,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KSAN",
                Route = "COAST9 MCKEY LAX COMIX2",
                Altitude = PlannedAltitude.Ifr(39000),
                FlightRules = "IFR",
            },
            Phases = new PhaseList { AssignedRunway = rwy },
        };

        // The dispatcher caches this when CTO goes through CommandDispatcher (issue #187); this fixture clears
        // the aircraft through the handler directly, so resolve it here from the same TDLS config.
        if (artccConfig is not null)
        {
            DepartureClearanceHandler.StoreSidInitialAltitude(ac, artccConfig);
        }

        var holding = new HoldingInPositionPhase();
        ac.Phases.Add(holding);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        CommandResult cto = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            assignedAltitude: null,
            TestDispatch.Context(Random.Shared),
            NullLogger.Instance
        );
        Assert.True(cto.Success, cto.Message);

        // Airborne just past the DER, above the 400 ft AGL turn floor.
        ac.IsOnGround = false;
        ac.Position = GeoMath.ProjectPoint(new LatLon(rwy.EndLatitude, rwy.EndLongitude), rwy.TrueHeading, 0.2);
        ac.TrueHeading = rwy.TrueHeading;
        ac.Altitude = rwy.ElevationFt + 450;
        ac.IndicatedAirspeed = 170;

        AircraftCategory cat = AircraftCategorization.Categorize(ac.AircraftType);
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = cat,
            DeltaSeconds = 1.0,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };
        ac.Phases.SkipTo<InitialClimbPhase>(ctx);
        return new Departure(ac, ctx, oak);
    }

    private Departure FlyIntoClimbWindow(ArtccConfigRoot? artccConfig)
    {
        Departure departure = StartCoast9Departure(artccConfig);
        FlyToWindow(departure);
        return departure;
    }

    /// <summary>
    /// Flies until the aircraft is established in the COAST9 first climb window: inside OAK 4.0 DME and
    /// leveled at/near the 2,000 ft ceiling of the active VD "between 1400 and 2000" leg.
    /// </summary>
    private void FlyToWindow(Departure departure)
    {
        AircraftState ac = departure.Aircraft;
        for (int t = 0; t < 360; t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);

            double dme = GeoMath.DistanceNm(ac.Position, departure.Oak);
            bool inProcedure = ac.Phases!.CurrentPhase is DepartureProcedurePhase;
            bool cappedAtWindow = (ac.Targets.TargetAltitude ?? double.MaxValue) <= WindowCeilingFt + LevelToleranceFt;
            if (inProcedure && dme < 3.5 && ac.Altitude >= WindowCeilingFt - 150 && cappedAtWindow)
            {
                _output.WriteLine($"reached window at t={t}: alt={ac.Altitude:F0} dmeOAK={dme:F2} tgtAlt={ac.Targets.TargetAltitude:F0}");
                return;
            }
        }

        Assert.Fail("aircraft never established in the COAST9 climb window under the DepartureProcedurePhase");
    }

    /// <summary>Flies until the aircraft is above an altitude, failing the test if it never gets there.</summary>
    private void FlyUntilAbove(Departure departure, double altitudeFt, int maxSeconds)
    {
        AircraftState ac = departure.Aircraft;
        for (int t = 0; t < maxSeconds && ac.Altitude <= altitudeFt; t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.True(ac.Altitude > altitudeFt, $"the aircraft never climbed above {altitudeFt:F0} ft: {ac.Altitude:F0}");
    }

    private static FlightResult FlyFor(Departure departure, int seconds)
    {
        AircraftState ac = departure.Aircraft;
        double minAltitude = ac.Altitude;
        double maxAltitude = ac.Altitude;
        bool sawDepartureProcedure = false;
        for (int t = 0; t < seconds; t++)
        {
            PhaseRunner.Tick(ac, departure.Context);
            FlightPhysics.Update(ac, 1.0);
            minAltitude = Math.Min(minAltitude, ac.Altitude);
            maxAltitude = Math.Max(maxAltitude, ac.Altitude);
            sawDepartureProcedure |= ac.Phases!.CurrentPhase is DepartureProcedurePhase;
        }
        return new FlightResult(minAltitude, maxAltitude, sawDepartureProcedure);
    }

    /// <summary>Dispatch one command line; asserts it parsed and was accepted.</summary>
    private static void Dispatch(AircraftState aircraft, string text)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.Dispatch(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared));
        Assert.True(result.Success, $"'{text}' was refused: {result.Message}");
    }

    /// <summary>Dispatch one line holding several chained blocks (e.g. <c>FH 270, CM 050</c>).</summary>
    private static void DispatchLine(AircraftState aircraft, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        CommandResult result = CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(Random.Shared));
        Assert.True(result.Success, $"'{text}' was refused: {result.Message}");
    }
}
