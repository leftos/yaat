using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// KOAK radar-vectors SIDs (NIMI*, OAK6): CIFP heading extraction, version fallback, CTO propagation, and RV hold semantics.
/// </summary>
/// <summary>
/// E2E tests for NIMI departure (KOAK) — a radar-vectors SID published with a
/// 315° departure heading on every runway transition. Validates that:
///   (1) YAAT extracts the published 315° heading from the CIFP VM leg
///   (2) The base-name fallback in NavigationDatabase.GetSid lets a flight
///       plan filed on an older revision (e.g. NIMI5) resolve to whatever
///       NIMI revision is currently in the CIFP cycle
///   (3) The heading propagates end-to-end into the InitialClimbPhase that
///       drives the aircraft after Vr, so the pilot actually flies 315°
///       rather than running straight off the runway heading.
///
/// Context: NIMI was renumbered from NIMI5 → NIMI6 mid-2026 and ATCTrainer
/// regressed by flying departures straight out. YAAT's CIFP-based resolution
/// + base-name fallback survives the renumbering automatically.
/// </summary>
public class NimiRadarVectorsSidTests
{
    private const double ExpectedRvHeading = 315.0;
    private static readonly ILogger Logger = new NullLogger<NimiRadarVectorsSidTests>();

    public NimiRadarVectorsSidTests()
    {
        // Pin the real CIFP-backed singleton before any test method runs to avoid
        // racing with other test classes that initialize on demand.
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeOakDeparture(string sidRoute, string runwayDesignator, double runwayHeading)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(37.728, -122.218),
            TrueHeading = new TrueHeading(runwayHeading),
            Altitude = 6,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KSAC",
                Route = sidRoute,
                CruiseAltitude = 11000,
                FlightRules = "IFR",
            },
        };
        ac.Phases = new PhaseList
        {
            AssignedRunway = TestRunwayFactory.Make(designator: runwayDesignator, airportId: "OAK", heading: runwayHeading, elevationFt: 6),
        };
        return ac;
    }

    [Theory]
    [InlineData("28R", 280.0)]
    [InlineData("28L", 280.0)]
    [InlineData("30", 300.0)]
    public void NIMI5_FromCifp_ExtractsPublished315Heading(string runwayDesignator, double runwayHeading)
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);
        var ac = MakeOakDeparture("NIMI5 OAK V6 SAC", runwayDesignator, runwayHeading);

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(ac);

        Assert.NotNull(result);
        Assert.Equal(ExpectedRvHeading, result.DepartureHeadingMagnetic);
        // Radar-vectors SID — no published lateral routing in the body; controller vectors.
        // (Enroute transition body fixes get appended separately by AppendPostSidEnrouteFixes
        // and are deduped against the departure airport, so we don't assert on Targets here.)
    }

    [Fact]
    public void NIMI6_BaseNameFallback_ResolvesToSameProcedureAsNIMI5()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        // Real-world failure mode: flight plan filed on NIMI5, FAA amendment cycle
        // publishes NIMI6 (or vice versa). NavigationDatabase.GetSid's
        // StripTrailingDigits fallback must catch the version skew and return the
        // current procedure with the same 315° heading.
        var ac5 = MakeOakDeparture("NIMI5 OAK V6 SAC", "28R", 280.0);
        var ac6 = MakeOakDeparture("NIMI6 OAK V6 SAC", "28R", 280.0);

        var r5 = DepartureClearanceHandler.TryResolveSidFromCifp(ac5);
        var r6 = DepartureClearanceHandler.TryResolveSidFromCifp(ac6);

        Assert.NotNull(r5);
        Assert.NotNull(r6);
        Assert.Equal(r5.DepartureHeadingMagnetic, r6.DepartureHeadingMagnetic);
        Assert.Equal(ExpectedRvHeading, r6.DepartureHeadingMagnetic);
    }

    [Fact]
    public void NIMI_HeadingSurvivesAcrossManyVersionSkews()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        // The fallback must not be brittle to which specific number is currently
        // in the cycle: any "NIMIn" should resolve to whichever NIMI exists.
        foreach (var name in new[] { "NIMI1", "NIMI4", "NIMI5", "NIMI6", "NIMI9" })
        {
            var ac = MakeOakDeparture($"{name} OAK V6 SAC", "28R", 280.0);
            var result = DepartureClearanceHandler.TryResolveSidFromCifp(ac);
            Assert.NotNull(result);
            Assert.Equal(ExpectedRvHeading, result.DepartureHeadingMagnetic);
        }
    }

    [Theory]
    [InlineData("28R", 280.0, 278.2)]
    public void OAK6_FromCifp_ExtractsPublishedRvHeadingOn28R(string runwayDesignator, double runwayHeading, double expectedHeading)
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);
        var ac = MakeOakDeparture("OAK6 OAK SYRAH", runwayDesignator, runwayHeading);

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(ac);

        Assert.NotNull(result);
        Assert.Equal(expectedHeading, result.DepartureHeadingMagnetic);
    }

    [Fact]
    public void OAK6_ClearedForTakeoff_PropagatesHeadingToInitialClimbPhase()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);
        var ac = MakeOakDeparture("OAK6 OAK SYRAH", "28R", 280.0);

        var holding = new HoldingInPositionPhase();
        ac.Phases!.Add(holding);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            assignedAltitude: 5000,
            Logger
        );

        Assert.True(result.Success);

        var initialClimb = ac.Phases.Phases.OfType<InitialClimbPhase>().FirstOrDefault();
        Assert.NotNull(initialClimb);
        Assert.Equal(278.2, initialClimb.SidDepartureHeadingMagnetic);
        Assert.Null(initialClimb.DepartureSidId);
    }

    [Fact]
    public void NIMI5_ClearedForTakeoff_PropagatesHeadingToInitialClimbPhase()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        using var _ = NavigationDatabase.ScopedOverride(TestVnasData.NavigationDb);

        // Full clearance pipeline: CTO from holding-in-position → InsertTowerPhasesAfterCurrent
        // → ResolveDepartureRoute → InitialClimbPhase.SidDepartureHeadingMagnetic = 315.
        // The HoldingInPositionPhase path is used here (rather than HoldingShortPhase)
        // because it consumes the pre-set AssignedRunway directly and skips the
        // navDB runway-lookup-by-target-name dance, keeping the test focused on
        // the SID-resolution + heading-propagation behavior under test.
        var ac = MakeOakDeparture("NIMI5 OAK V6 SAC", "28R", 280.0);

        var holding = new HoldingInPositionPhase();
        ac.Phases!.Add(holding);
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        var result = DepartureClearanceHandler.TryDepartureClearance(
            ac,
            holding,
            ClearanceType.ClearedForTakeoff,
            new DefaultDeparture(),
            assignedAltitude: 5000,
            Logger
        );

        Assert.True(result.Success);

        var initialClimb = ac.Phases.Phases.OfType<InitialClimbPhase>().FirstOrDefault();
        Assert.NotNull(initialClimb);
        Assert.Equal(ExpectedRvHeading, initialClimb.SidDepartureHeadingMagnetic);
        // SidId is null for RV SIDs — the procedure ID is intentionally not threaded
        // through because there's no published lateral path to follow against it.
        Assert.Null(initialClimb.DepartureSidId);
    }

    // -------------------------------------------------------------------------
    // RV SID heading-hold release semantics
    //
    // Track ownership ≠ comms (FAA 7110.65 §7-6-11). An auto-track or HOO does
    // not put the pilot on departure's frequency, so the RV SID heading hold
    // must NOT release until the controller actually issues CT/FCA (which sets
    // AircraftState.HasLeftStudentFrequency). Until then the aircraft must hold
    // 315° regardless of who owns the radar track.
    // -------------------------------------------------------------------------

    private static (InitialClimbPhase Phase, AircraftState Aircraft, PhaseContext Ctx) BuildRvSidClimbHarness()
    {
        const double fieldElev = 6.0;
        var runway = TestRunwayFactory.Make(designator: "28R", airportId: "OAK", heading: 280.0, elevationFt: fieldElev);
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(37.728, -122.218),
            TrueHeading = new TrueHeading(280),
            Altitude = fieldElev + 1500, // above the deferred-turn floor so OnStart engages heading
            IndicatedAirspeed = 180,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KSAC",
                Route = "NIMI5 OAK V6 SAC",
                CruiseAltitude = 11000,
                FlightRules = "IFR",
            },
        };
        ac.Phases = new PhaseList { AssignedRunway = runway };

        // Mirror what InsertTowerPhasesAfterCurrent would build: route fixes + RV heading
        // populated from the CIFP. Use a single placeholder fix to satisfy the
        // (DepartureRoute is { Count: > 0 }) precondition for _rvSidActive.
        var climb = new InitialClimbPhase
        {
            Departure = new DefaultDeparture(),
            AssignedAltitude = 5000,
            DepartureRoute = [new NavigationTarget { Name = "FESIK", Position = new LatLon(38.2, -121.5) }],
            DepartureSidId = null,
            SidDepartureHeadingMagnetic = ExpectedRvHeading,
            IsVfr = false,
            CruiseAltitude = 11000,
        };
        var ctx = new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = fieldElev,
            Logger = NullLogger.Instance,
        };
        climb.OnStart(ctx);
        return (climb, ac, ctx);
    }

    [Fact]
    public void RvSid_NoFreqHandoff_HoldsHeadingFor60Seconds()
    {
        // HasLeftStudentFrequency stays false — controller never issued CT. Aircraft
        // must hold 315° and not load NavigationRoute, regardless of how long passes.
        var (phase, ac, ctx) = BuildRvSidClimbHarness();
        Assert.False(ac.HasLeftStudentFrequency);

        for (int i = 0; i < 60; i++)
        {
            phase.OnTick(ctx);
        }

        Assert.Equal(ExpectedRvHeading, ctx.Targets.TargetTrueHeading?.Degrees);
        Assert.Empty(ctx.Targets.NavigationRoute);
    }

    [Fact]
    public void RvSid_TrackOwnerChangedButNoFreqHandoff_StillHoldsHeading()
    {
        // The exact concern: an auto-track to a non-tower TCP MUST NOT release the
        // heading hold, because comms haven't been transferred. Pre-fix this would
        // start a 5s timer based purely on Track.Owner; post-fix the timer is gated
        // on HasLeftStudentFrequency.
        var (phase, ac, ctx) = BuildRvSidClimbHarness();
        ac.Track.Owner = TrackOwner.CreateNonNas("OAK_APP");
        ac.Track.HandoffAccepted = true;
        Assert.False(ac.HasLeftStudentFrequency);

        for (int i = 0; i < 30; i++)
        {
            phase.OnTick(ctx);
        }

        Assert.Equal(ExpectedRvHeading, ctx.Targets.TargetTrueHeading?.Degrees);
        Assert.Empty(ctx.Targets.NavigationRoute);
    }

    [Fact]
    public void RvSid_AfterCT_HoldsHeadingFor5SecondsThenLoadsRoute()
    {
        // Controller issues CT (HasLeftStudentFrequency = true). 5s grace period:
        // aircraft still holds 315° (simulates pilot retuning the radio). After 5s
        // elapses the route loads and FlightPhysics takes over to fly to FESIK.
        var (phase, ac, ctx) = BuildRvSidClimbHarness();

        ac.HasLeftStudentFrequency = true;

        // Ticks 1-4: still in post-CT delay, heading still being asserted, route NOT loaded.
        for (int i = 0; i < 4; i++)
        {
            phase.OnTick(ctx);
            Assert.Equal(ExpectedRvHeading, ctx.Targets.TargetTrueHeading?.Degrees);
            Assert.Empty(ctx.Targets.NavigationRoute);
        }

        // Tick 5: timer crosses the 5s threshold, route loads.
        phase.OnTick(ctx);
        Assert.Single(ctx.Targets.NavigationRoute);
        Assert.Equal("FESIK", ctx.Targets.NavigationRoute[0].Name);
    }
}
