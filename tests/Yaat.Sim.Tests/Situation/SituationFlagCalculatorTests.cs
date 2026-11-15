using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Situation;
using Yaat.Sim.Tests.ControllerAi;
using static Yaat.Sim.Tests.Helpers.FollowTestHelpers;

namespace Yaat.Sim.Tests.Situation;

/// <summary>
/// The situation flags <see cref="SituationFlagCalculator"/> computes, on real KOAK navdata and the real KOAK ground
/// layout: taxi routes come from <c>TAXIAUTO</c>, landings are flown from the on-final fixture.
/// </summary>
public class SituationFlagCalculatorTests
{
    private const int TaxiBudgetSeconds = 900;
    private const int LandingBudgetSeconds = 600;

    private readonly ArtccConfigRoot? _zoa = LoadZoa();

    private static ArtccConfigRoot? LoadZoa()
    {
        TestVnasData.EnsureInitialized();
        return TestArtccConfig.LoadZoa();
    }

    private static readonly string ParkedAt29 = AiTestFixture.ParkedAtOak.Replace("\"parking\": \"SIG1\"", "\"parking\": \"29\"");

    // --- NearingDepartureHoldLine ---

    [Fact]
    public void Nearing_TaxiingWithin1200FtOfDepartureBar_IsSet()
    {
        if (Taxiing(ParkedAt29, "TAXIAUTO 30") is not { } taxi)
        {
            return;
        }

        AircraftState ac = TickUntilDistance(taxi, d => d <= SituationFlagCalculator.NearingDepartureHoldLineFt);

        Assert.True(Has(Compute(taxi.Engine, ac, SituationFlags.None), SituationFlags.NearingDepartureHoldLine));
    }

    [Fact]
    public void Nearing_TaxiingFartherThan1200Ft_IsClear()
    {
        if (Taxiing(ParkedAt29, "TAXIAUTO 30") is not { } taxi)
        {
            return;
        }

        AircraftState ac = TickUntilDistance(
            taxi,
            d => (d > SituationFlagCalculator.NearingDepartureHoldLineFt) && (d <= SituationFlagCalculator.NearingDepartureHoldLineLeaveFt)
        );

        Assert.False(Has(Compute(taxi.Engine, ac, SituationFlags.None), SituationFlags.NearingDepartureHoldLine));
    }

    [Fact]
    public void Nearing_PreviouslySet_StaysSetTo1300Ft()
    {
        if (Taxiing(ParkedAt29, "TAXIAUTO 30") is not { } taxi)
        {
            return;
        }

        AircraftState ac = TickUntilDistance(
            taxi,
            d => (d > SituationFlagCalculator.NearingDepartureHoldLineFt) && (d <= SituationFlagCalculator.NearingDepartureHoldLineLeaveFt)
        );

        Assert.True(Has(Compute(taxi.Engine, ac, SituationFlags.NearingDepartureHoldLine), SituationFlags.NearingDepartureHoldLine));
    }

    [Fact]
    public void Nearing_PreviouslySet_ClearsBeyond1300Ft()
    {
        if (Taxiing(ParkedAt29, "TAXIAUTO 30") is not { } taxi)
        {
            return;
        }

        AircraftState ac = TickUntilDistance(taxi, d => d > SituationFlagCalculator.NearingDepartureHoldLineLeaveFt);

        Assert.False(Has(Compute(taxi.Engine, ac, SituationFlags.NearingDepartureHoldLine), SituationFlags.NearingDepartureHoldLine));
    }

    /// <summary>
    /// From SIG1 the route to runway 28L crosses a runway shortly before its departure bar. The crossings are cleared
    /// (as a <c>CROSS</c> would), so the along-route distance to the departure bar is known; inside 1,200 ft of it, with a
    /// crossing bar still ahead, the flag stays clear (7110.65 §3-9-10.d).
    /// </summary>
    [Fact]
    public void Nearing_RunwayCrossingBeforeDepartureBar_IsClear()
    {
        if (Taxiing(AiTestFixture.ParkedAtOak, "TAXIAUTO 28L") is not { } taxi)
        {
            return;
        }

        TaxiRoute route = taxi.Aircraft.Ground.AssignedTaxiRoute!;
        List<HoldShortPoint> crossings = [.. route.HoldShortPoints.Where(h => h.Reason == HoldShortReason.RunwayCrossing)];
        Assert.Contains(crossings, c => (c.TargetName ?? "").Contains("28R", StringComparison.OrdinalIgnoreCase));
        foreach (HoldShortPoint crossing in crossings)
        {
            crossing.IsCleared = true;
        }

        AircraftState ac = AiTestFixture.TickUntil(
            taxi.Engine,
            AiTestFixture.Callsign,
            a =>
                (a.Phases?.CurrentPhase is TaxiingPhase)
                && (DistanceToBar(taxi.Engine, a) is <= SituationFlagCalculator.NearingDepartureHoldLineFt)
                && crossings.Any(c => IsAhead(route, c)),
            TaxiBudgetSeconds
        );

        Assert.False(Has(Compute(taxi.Engine, ac, SituationFlags.None), SituationFlags.NearingDepartureHoldLine));
        Assert.False(Has(Compute(taxi.Engine, ac, SituationFlags.NearingDepartureHoldLine), SituationFlags.NearingDepartureHoldLine));
    }

    /// <summary>
    /// Past the 28R bar on the way to 28L but still on 28R's pavement: the runway is not yet crossed (7110.65 §3-9-10.d),
    /// and the route carries no far-side bar to say so. On this KOAK route the aircraft is still in its runway-crossing
    /// phase for every second on 28R's pavement and only resumes taxiing once off it, so this checks the outcome; the
    /// calculator's on-another-runway test is the guard for a taxi phase that is still on the pavement.
    /// </summary>
    [Fact]
    public void Nearing_OnTheCrossedRunwayPastItsBar_IsClear()
    {
        if (Taxiing(AiTestFixture.ParkedAtOak, "TAXIAUTO 28L") is not { } taxi)
        {
            return;
        }

        TaxiRoute route = taxi.Aircraft.Ground.AssignedTaxiRoute!;
        List<HoldShortPoint> crossings = [.. route.HoldShortPoints.Where(h => h.Reason == HoldShortReason.RunwayCrossing)];
        Assert.NotEmpty(crossings);
        foreach (HoldShortPoint crossing in crossings)
        {
            crossing.IsCleared = true;
        }

        RunwayInfo crossed = Runway("28R");
        AircraftState ac = AiTestFixture.TickUntil(
            taxi.Engine,
            AiTestFixture.Callsign,
            a => (!crossings.Any(c => IsAhead(route, c))) && RunwayOccupancy.IsOnPavement(a, crossed),
            TaxiBudgetSeconds
        );

        Assert.False(Has(Compute(taxi.Engine, ac, SituationFlags.None), SituationFlags.NearingDepartureHoldLine));
        Assert.False(Has(Compute(taxi.Engine, ac, SituationFlags.NearingDepartureHoldLine), SituationFlags.NearingDepartureHoldLine));

        // Once taxiing again off the pavement the flag is set; the same taxi state put back on 28R's pavement reads clear.
        LatLon onCrossedRunway = ac.Position;
        AircraftState taxiing = AiTestFixture.TickUntil(
            taxi.Engine,
            AiTestFixture.Callsign,
            a => (a.Phases?.CurrentPhase is TaxiingPhase) && (DistanceToBar(taxi.Engine, a) is <= SituationFlagCalculator.NearingDepartureHoldLineFt),
            TaxiBudgetSeconds
        );
        Assert.True(Has(Compute(taxi.Engine, taxiing, SituationFlags.None), SituationFlags.NearingDepartureHoldLine));

        taxiing.Position = onCrossedRunway;
        Assert.True(RunwayOccupancy.IsOnPavement(taxiing, crossed));
        Assert.True(DistanceToBar(taxi.Engine, taxiing) is <= SituationFlagCalculator.NearingDepartureHoldLineLeaveFt);
        Assert.False(Has(Compute(taxi.Engine, taxiing, SituationFlags.NearingDepartureHoldLine), SituationFlags.NearingDepartureHoldLine));
    }

    /// <summary>
    /// <c>HS 28R</c> on the way to 28L turns the 28R crossing bar into an explicit hold-short. Cleared, it still lies
    /// before the departure bar, so inside 1,200 ft of that bar the flag stays clear.
    /// </summary>
    [Fact]
    public void Nearing_ExplicitHoldShortOfARunwayBeforeDepartureBar_IsClear()
    {
        if (Taxiing(AiTestFixture.ParkedAtOak, "TAXIAUTO 28L") is not { } taxi)
        {
            return;
        }

        CommandResult result = taxi.Engine.SendCommand(AiTestFixture.Callsign, "HS 28R");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = taxi.Aircraft.Ground.AssignedTaxiRoute!;
        Assert.DoesNotContain(route.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing);
        List<HoldShortPoint> explicitBars = [.. route.HoldShortPoints.Where(h => h.Reason == HoldShortReason.ExplicitHoldShort)];
        Assert.NotEmpty(explicitBars);
        foreach (HoldShortPoint bar in explicitBars)
        {
            bar.IsCleared = true;
        }

        AircraftState ac = AiTestFixture.TickUntil(
            taxi.Engine,
            AiTestFixture.Callsign,
            a =>
                (a.Phases?.CurrentPhase is TaxiingPhase)
                && (DistanceToBar(taxi.Engine, a) is <= SituationFlagCalculator.NearingDepartureHoldLineFt)
                && explicitBars.Any(b => IsAhead(route, b)),
            TaxiBudgetSeconds
        );

        Assert.False(Has(Compute(taxi.Engine, ac, SituationFlags.None), SituationFlags.NearingDepartureHoldLine));
    }

    /// <summary>The departure bar cleared inside 1,200 ft of it (a takeoff or line-up-and-wait clearance): clear.</summary>
    [Fact]
    public void Nearing_DepartureBarCleared_IsClear()
    {
        if (Taxiing(ParkedAt29, "TAXIAUTO 30") is not { } taxi)
        {
            return;
        }

        AircraftState ac = TickUntilDistance(taxi, d => d <= SituationFlagCalculator.NearingDepartureHoldLineFt);
        Assert.True(Has(Compute(taxi.Engine, ac, SituationFlags.None), SituationFlags.NearingDepartureHoldLine));

        HoldShortPoint departureBar = ac.Ground.AssignedTaxiRoute!.HoldShortPoints.Single(h => h.Reason == HoldShortReason.DestinationRunway);
        departureBar.IsCleared = true;

        Assert.False(Has(Compute(taxi.Engine, ac, SituationFlags.None), SituationFlags.NearingDepartureHoldLine));
        Assert.False(Has(Compute(taxi.Engine, ac, SituationFlags.NearingDepartureHoldLine), SituationFlags.NearingDepartureHoldLine));
    }

    [Fact]
    public void Nearing_NotTaxiing_IsClear()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = AiTestFixture.Load(ParkedAt29, _zoa, 7, []);
        AircraftState ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.IsNotType<TaxiingPhase>(ac.Phases?.CurrentPhase);

        Assert.False(Has(Compute(engine, ac, SituationFlags.NearingDepartureHoldLine), SituationFlags.NearingDepartureHoldLine));
    }

    // --- HoldShortIsDepartureRunway ---

    [Fact]
    public void HoldShort_DestinationRunwayBar_IsSet()
    {
        if (HoldingShort("TAXIAUTO 28R", HoldShortReason.DestinationRunway) is not { } held)
        {
            return;
        }

        Assert.True(Has(Compute(held.Engine, held.Aircraft, SituationFlags.None), SituationFlags.HoldShortIsDepartureRunway));
    }

    [Fact]
    public void HoldShort_RunwayCrossingBar_IsClear()
    {
        if (HoldingShort("TAXIAUTO 30", HoldShortReason.RunwayCrossing) is not { } held)
        {
            return;
        }

        Assert.NotNull(held.Aircraft.Phases!.AssignedRunway);
        Assert.False(Has(Compute(held.Engine, held.Aircraft, SituationFlags.None), SituationFlags.HoldShortIsDepartureRunway));
    }

    /// <summary>
    /// An intersection departure: runway 28R assigned, then <c>TAXI D G HS 28R</c> from SIG1, so the aircraft holds at
    /// the 28R bar on G as an explicit hold-short with no departure-runway bar on its route. Set for the assigned runway,
    /// clear once another runway is assigned.
    /// </summary>
    [Fact]
    public void HoldShort_ExplicitHoldShortOfAssignedRunway_IsSet()
    {
        if (Taxiing(AiTestFixture.ParkedAtOak, "RWY 28R") is not { } taxi)
        {
            return;
        }

        CommandResult result = taxi.Engine.SendCommand(AiTestFixture.Callsign, "TAXI D G HS 28R");
        Assert.True(result.Success, result.Message);
        AircraftState ac = HoldUntilHoldingShort(taxi, HoldShortReason.ExplicitHoldShort);
        Assert.DoesNotContain(ac.Ground.AssignedTaxiRoute!.HoldShortPoints, h => h.Reason == HoldShortReason.DestinationRunway);
        Assert.True(Has(Compute(taxi.Engine, ac, SituationFlags.None), SituationFlags.HoldShortIsDepartureRunway));

        ac.Phases!.AssignedRunway = Runway("30");
        Assert.False(Has(Compute(taxi.Engine, ac, SituationFlags.None), SituationFlags.HoldShortIsDepartureRunway));
    }

    /// <summary>
    /// Holding at an explicit hold-short of the assigned runway with the route's departure-runway bar still ahead: the
    /// aircraft is crossing that runway on the way to its departure bar, so clear. <c>HS 28R</c> on the way to 28L makes
    /// the explicit bar; 28R is then assigned so the bar is the assigned runway's.
    /// </summary>
    [Fact]
    public void HoldShort_ExplicitHoldShortWithDepartureBarAhead_IsClear()
    {
        if (Taxiing(AiTestFixture.ParkedAtOak, "TAXIAUTO 28L") is not { } taxi)
        {
            return;
        }

        CommandResult result = taxi.Engine.SendCommand(AiTestFixture.Callsign, "HS 28R");
        Assert.True(result.Success, result.Message);
        AircraftState ac = HoldUntilHoldingShort(taxi, HoldShortReason.ExplicitHoldShort);
        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        Assert.Contains(route.HoldShortPoints, h => (h.Reason == HoldShortReason.DestinationRunway) && IsAhead(route, h));
        ac.Phases!.AssignedRunway = Runway("28R");

        Assert.False(Has(Compute(taxi.Engine, ac, SituationFlags.None), SituationFlags.HoldShortIsDepartureRunway));
    }

    [Fact]
    public void HoldShort_NoAssignedRunway_IsClear()
    {
        if (HoldingShort("TAXIAUTO 28R", HoldShortReason.DestinationRunway) is not { } held)
        {
            return;
        }

        held.Aircraft.Phases!.AssignedRunway = null;

        Assert.False(Has(Compute(held.Engine, held.Aircraft, SituationFlags.None), SituationFlags.HoldShortIsDepartureRunway));
    }

    // --- InsideFinalApproachFix ---

    [Fact]
    public void InsideFaf_OnFinalInside_IsSet()
    {
        RunwayInfo runway = Runway("28R");
        AircraftState ac = OnApproachPhase(new FinalApproachPhase(), runway, OffFinal(runway, 2.0, 0.0));

        Assert.True(
            Has(
                SituationFlagCalculator.Compute(ac, AircraftSituation.Final, SituationFlags.None, null, OakLayout()),
                SituationFlags.InsideFinalApproachFix
            )
        );
    }

    [Fact]
    public void InsideFaf_PreviouslySet_StaysSetWithinOneNmCrossTrack()
    {
        RunwayInfo runway = Runway("28R");
        AircraftState ac = OnApproachPhase(new FinalApproachPhase(), runway, OffFinal(runway, 2.0, 0.8));
        Assert.False(
            Has(
                SituationFlagCalculator.Compute(ac, AircraftSituation.Final, SituationFlags.None, null, OakLayout()),
                SituationFlags.InsideFinalApproachFix
            )
        );

        SituationFlags flags = SituationFlagCalculator.Compute(ac, AircraftSituation.Final, SituationFlags.InsideFinalApproachFix, null, OakLayout());

        Assert.True(Has(flags, SituationFlags.InsideFinalApproachFix));
    }

    [Fact]
    public void InsideFaf_PreviouslySet_ClearsBeyondOneNmCrossTrack()
    {
        RunwayInfo runway = Runway("28R");
        AircraftState ac = OnApproachPhase(new FinalApproachPhase(), runway, OffFinal(runway, 2.0, 1.2));

        SituationFlags flags = SituationFlagCalculator.Compute(ac, AircraftSituation.Final, SituationFlags.InsideFinalApproachFix, null, OakLayout());

        Assert.False(Has(flags, SituationFlags.InsideFinalApproachFix));
    }

    /// <summary>Latched, within 1 NM cross-track but outside the on-final test, once the situation leaves Approach/Final: clear.</summary>
    [Theory]
    [InlineData(AircraftSituation.Pattern)]
    [InlineData(AircraftSituation.GoAround)]
    public void InsideFaf_PreviouslySet_ClearsOnSituationChange(AircraftSituation situation)
    {
        RunwayInfo runway = Runway("28R");
        AircraftState ac = OnApproachPhase(new FinalApproachPhase(), runway, OffFinal(runway, 2.0, 0.8));
        Assert.False(FinalApproachFix.IsInside(ac, runway, OakLayout()));

        SituationFlags flags = SituationFlagCalculator.Compute(ac, situation, SituationFlags.InsideFinalApproachFix, null, OakLayout());

        Assert.False(Has(flags, SituationFlags.InsideFinalApproachFix));
    }

    /// <summary>On the published missed approach for I28R, at a point inside the FAF on final: clear, latched or not.</summary>
    [Fact]
    public void InsideFaf_OnMissedApproach_IsClear()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        var ac = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            Position = new LatLon(37.75, -122.35),
            TrueHeading = new TrueHeading(280),
            Altitude = 3000,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };
        engine.World.AddAircraft(ac);
        CommandResult result = engine.SendCommand(ac.Callsign, "CAPP I28R");
        Assert.True(result.Success, result.Message);

        PhaseList cleared = ac.Phases!;
        RunwayInfo runway = cleared.AssignedRunway!;
        List<Phase> missed = ApproachCommandHandler.BuildMissedApproachPhases(ac);
        ac.Phases = new PhaseList { AssignedRunway = runway, ActiveApproach = cleared.ActiveApproach };
        foreach (Phase phase in missed)
        {
            ac.Phases.Add(phase);
        }

        ac.Position = OffFinal(runway, 2.0, 0.0);
        ac.TrueHeading = runway.TrueHeading;
        ac.Altitude = 800;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
        Assert.True(Assert.IsType<ApproachNavigationPhase>(ac.Phases.CurrentPhase).IsMissedApproach);
        Assert.True(FinalApproachFix.IsInside(ac, runway, OakLayout()));

        Assert.False(
            Has(
                SituationFlagCalculator.Compute(ac, AircraftSituation.GoAround, SituationFlags.None, null, OakLayout()),
                SituationFlags.InsideFinalApproachFix
            )
        );
        Assert.False(
            Has(
                SituationFlagCalculator.Compute(ac, AircraftSituation.GoAround, SituationFlags.InsideFinalApproachFix, null, OakLayout()),
                SituationFlags.InsideFinalApproachFix
            )
        );
    }

    [Fact]
    public void InsideFaf_PatternPhase_IsClear()
    {
        RunwayInfo runway = Runway("28R");
        AircraftState ac = OnApproachPhase(new DownwindPhase(), runway, OffFinal(runway, 2.0, 0.0));
        Assert.True(FinalApproachFix.IsInside(ac, runway, OakLayout()));

        Assert.False(
            Has(
                SituationFlagCalculator.Compute(ac, AircraftSituation.Pattern, SituationFlags.InsideFinalApproachFix, null, OakLayout()),
                SituationFlags.InsideFinalApproachFix
            )
        );
    }

    // --- RolloutDecelerating ---

    [Fact]
    public void Rollout_JustAfterTouchdown_IsClear()
    {
        if (RollingOut() is not { } rollout)
        {
            return;
        }

        rollout.Aircraft.Position = rollout.Phase.TouchdownPosition!.Value;
        rollout.Aircraft.IndicatedAirspeed = rollout.Phase.TouchdownGroundSpeedKts!.Value;

        Assert.False(Has(Compute(rollout.Engine, rollout.Aircraft, SituationFlags.None), SituationFlags.RolloutDecelerating));
    }

    [Fact]
    public void Rollout_TenKnotsBelowTouchdown_IsSet()
    {
        if (RollingOut() is not { } rollout)
        {
            return;
        }

        double touchdownKts = rollout.Phase.TouchdownGroundSpeedKts!.Value;
        rollout.Aircraft.Position = rollout.Phase.TouchdownPosition!.Value;
        rollout.Aircraft.IndicatedAirspeed = touchdownKts - 9.0;
        Assert.False(Has(Compute(rollout.Engine, rollout.Aircraft, SituationFlags.None), SituationFlags.RolloutDecelerating));

        rollout.Aircraft.IndicatedAirspeed = touchdownKts - 10.0;

        Assert.True(Has(Compute(rollout.Engine, rollout.Aircraft, SituationFlags.None), SituationFlags.RolloutDecelerating));
    }

    [Fact]
    public void Rollout_ThousandFeetPastTouchdown_IsSet()
    {
        if (RollingOut() is not { } rollout)
        {
            return;
        }

        LatLon touchdown = rollout.Phase.TouchdownPosition!.Value;
        TrueHeading runwayHeading = rollout.Aircraft.Phases!.AssignedRunway!.TrueHeading;
        rollout.Aircraft.IndicatedAirspeed = rollout.Phase.TouchdownGroundSpeedKts!.Value;
        rollout.Aircraft.Position = GeoMath.ProjectPoint(touchdown, runwayHeading, 990.0 / GeoMath.FeetPerNm);
        Assert.False(Has(Compute(rollout.Engine, rollout.Aircraft, SituationFlags.None), SituationFlags.RolloutDecelerating));

        rollout.Aircraft.Position = GeoMath.ProjectPoint(touchdown, runwayHeading, 1010.0 / GeoMath.FeetPerNm);

        Assert.True(Has(Compute(rollout.Engine, rollout.Aircraft, SituationFlags.None), SituationFlags.RolloutDecelerating));
    }

    /// <summary>Latched: still at the touchdown point and speed, the rollout keeps the flag.</summary>
    [Fact]
    public void Rollout_PreviouslySet_StaysSet()
    {
        if (RollingOut() is not { } rollout)
        {
            return;
        }

        rollout.Aircraft.Position = rollout.Phase.TouchdownPosition!.Value;
        rollout.Aircraft.IndicatedAirspeed = rollout.Phase.TouchdownGroundSpeedKts!.Value;
        Assert.False(Has(Compute(rollout.Engine, rollout.Aircraft, SituationFlags.None), SituationFlags.RolloutDecelerating));

        Assert.True(Has(Compute(rollout.Engine, rollout.Aircraft, SituationFlags.RolloutDecelerating), SituationFlags.RolloutDecelerating));
    }

    [Fact]
    public void Rollout_StopAndGo_IsClear()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, _zoa, 7, []);
        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "SG");
        Assert.True(result.Success, result.Message);
        AircraftState ac = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            a => (a.Phases?.CurrentPhase is StopAndGoPhase) && a.IsOnGround,
            LandingBudgetSeconds
        );
        ac.IndicatedAirspeed = Math.Max(0.0, ac.IndicatedAirspeed - 20.0);

        Assert.False(Has(Compute(engine, ac, SituationFlags.None), SituationFlags.RolloutDecelerating));
        Assert.False(Has(Compute(engine, ac, SituationFlags.RolloutDecelerating), SituationFlags.RolloutDecelerating));
    }

    [Fact]
    public void Rollout_TouchAndGo_IsClear()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, _zoa, 7, []);
        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "TG");
        Assert.True(result.Success, result.Message);
        AircraftState ac = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            a => (a.Phases?.CurrentPhase is TouchAndGoPhase) && a.IsOnGround,
            LandingBudgetSeconds
        );
        ac.IndicatedAirspeed = Math.Max(0.0, ac.IndicatedAirspeed - 20.0);

        Assert.False(Has(Compute(engine, ac, SituationFlags.None), SituationFlags.RolloutDecelerating));
        Assert.False(Has(Compute(engine, ac, SituationFlags.RolloutDecelerating), SituationFlags.RolloutDecelerating));
    }

    [Fact]
    public void Rollout_RunwayExitPhase_IsSet()
    {
        if (_zoa is null)
        {
            return;
        }

        SimulationEngine engine = ClearedToLand(_zoa);
        AircraftState ac = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            a => a.Phases?.CurrentPhase is RunwayExitPhase,
            LandingBudgetSeconds
        );

        Assert.True(Has(Compute(engine, ac, SituationFlags.None), SituationFlags.RolloutDecelerating));
    }

    // --- HasReportedFieldInSight / HasReportedTrafficInSight ---

    [Fact]
    public void FieldInSight_Ifr_MirrorsTheReport()
    {
        AircraftState ac = Reporting("IFR");
        Assert.False(
            Has(
                SituationFlagCalculator.Compute(ac, AircraftSituation.Approach, SituationFlags.None, null, null),
                SituationFlags.HasReportedFieldInSight
            )
        );

        ac.Approach.HasReportedFieldInSight = true;

        SituationFlags flags = SituationFlagCalculator.Compute(ac, AircraftSituation.Approach, SituationFlags.None, null, null);
        Assert.True(Has(flags, SituationFlags.HasReportedFieldInSight));
        Assert.False(Has(flags, SituationFlags.HasReportedTrafficInSight));
    }

    [Fact]
    public void FieldInSight_Vfr_IsClear()
    {
        AircraftState ac = Reporting("VFR");
        ac.Approach.HasReportedFieldInSight = true;
        ac.Approach.HasReportedTrafficInSight = true;

        SituationFlags flags = SituationFlagCalculator.Compute(ac, AircraftSituation.Approach, SituationFlags.HasReportedFieldInSight, null, null);

        Assert.False(Has(flags, SituationFlags.HasReportedFieldInSight));
        Assert.False(Has(flags, SituationFlags.HasReportedTrafficInSight));
    }

    [Fact]
    public void TrafficInSight_Ifr_MirrorsTheReport()
    {
        AircraftState ac = Reporting("IFR");
        ac.Approach.HasReportedTrafficInSight = true;

        SituationFlags flags = SituationFlagCalculator.Compute(ac, AircraftSituation.Approach, SituationFlags.None, null, null);
        Assert.True(Has(flags, SituationFlags.HasReportedTrafficInSight));
        Assert.False(Has(flags, SituationFlags.HasReportedFieldInSight));

        ac.Approach.HasReportedTrafficInSight = false;

        Assert.False(
            Has(
                SituationFlagCalculator.Compute(ac, AircraftSituation.Approach, SituationFlags.HasReportedTrafficInSight, null, null),
                SituationFlags.HasReportedTrafficInSight
            )
        );
    }

    // --- NextCrossingRunway ---

    /// <summary>
    /// From SIG1 the route to runway 28L crosses a runway before its departure bar, so the first uncleared bar ahead is a
    /// crossing (7110.65 §3-7-2): named, as one of that runway's ends, in the taxiing, holding-on-ground and rollout/exit
    /// situations; null in any other.
    /// </summary>
    [Theory]
    [InlineData(AircraftSituation.Taxiing, true)]
    [InlineData(AircraftSituation.HoldingOnGround, true)]
    [InlineData(AircraftSituation.RolloutExit, true)]
    [InlineData(AircraftSituation.HoldingShort, false)]
    [InlineData(AircraftSituation.LinedUp, false)]
    public void NextCrossing_CrossingAhead_NamedOnlyInTheGroundMovementSituations(AircraftSituation situation, bool named)
    {
        if (Taxiing(AiTestFixture.ParkedAtOak, "TAXIAUTO 28L") is not { } taxi)
        {
            return;
        }

        AircraftState ac = TickUntilTaxiing(taxi);
        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        HoldShortPoint next = route
            .Segments.Skip(route.CurrentSegmentIndex)
            .Select(s => route.GetHoldShortAt(s.ToNodeId))
            .First(h => h is { IsCleared: false })!;
        Assert.Equal(HoldShortReason.RunwayCrossing, next.Reason);

        string? runway = NextCrossing(taxi.Engine, ac, situation);

        if (named)
        {
            Assert.NotNull(runway);
            Assert.True(RunwayIdentifier.Parse(next.TargetName!).Contains(runway), $"{runway} is not an end of {next.TargetName}");
        }
        else
        {
            Assert.Null(runway);
        }
    }

    /// <summary>The crossings cleared (as a <c>CROSS</c> would), the next uncleared bar is the departure runway's: null.</summary>
    [Fact]
    public void NextCrossing_CrossingsCleared_IsNull()
    {
        if (Taxiing(AiTestFixture.ParkedAtOak, "TAXIAUTO 28L") is not { } taxi)
        {
            return;
        }

        AircraftState ac = TickUntilTaxiing(taxi);
        foreach (HoldShortPoint crossing in ac.Ground.AssignedTaxiRoute!.HoldShortPoints.Where(h => h.Reason == HoldShortReason.RunwayCrossing))
        {
            crossing.IsCleared = true;
        }

        Assert.Null(NextCrossing(taxi.Engine, ac, AircraftSituation.Taxiing));
    }

    /// <summary>An explicit hold-short of a runway on the way (<c>HS 28R</c>) is a runway bar too: named.</summary>
    [Fact]
    public void NextCrossing_ExplicitHoldShortOfARunwayAhead_IsNamed()
    {
        if (Taxiing(AiTestFixture.ParkedAtOak, "TAXIAUTO 28L") is not { } taxi)
        {
            return;
        }

        CommandResult result = taxi.Engine.SendCommand(AiTestFixture.Callsign, "HS 28R");
        Assert.True(result.Success, result.Message);
        AircraftState ac = TickUntilTaxiing(taxi);
        Assert.DoesNotContain(ac.Ground.AssignedTaxiRoute!.HoldShortPoints, h => h.Reason == HoldShortReason.RunwayCrossing);

        Assert.Contains(NextCrossing(taxi.Engine, ac, AircraftSituation.Taxiing), (string[])["28R", "10L"]);
    }

    /// <summary>Taxiing to runway 30 from parking 29 crosses no runway: the next bar is the departure bar, null.</summary>
    [Fact]
    public void NextCrossing_NoCrossingOnTheRoute_IsNull()
    {
        if (Taxiing(ParkedAt29, "TAXIAUTO 30") is not { } taxi)
        {
            return;
        }

        AircraftState ac = TickUntilTaxiing(taxi);

        Assert.Null(NextCrossing(taxi.Engine, ac, AircraftSituation.Taxiing));
    }

    /// <summary>
    /// SIG1 to runway 30 crosses two runways. With the first crossing cleared but not yet crossed, no second crossing is
    /// offered (7110.65 §3-7-2.c); once the first bar is behind the aircraft, the second runway is named.
    /// </summary>
    [Fact]
    public void NextCrossing_PreviousCrossingClearedButNotCrossed_IsNullUntilPastIt()
    {
        if (Taxiing(AiTestFixture.ParkedAtOak, "TAXIAUTO 30") is not { } taxi)
        {
            return;
        }

        AircraftState ac = TickUntilTaxiing(taxi);
        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        List<HoldShortPoint> crossings = [.. route.HoldShortPoints.Where(h => h.Reason == HoldShortReason.RunwayCrossing)];
        Assert.True(crossings.Count >= 2, $"expected two crossings, got {crossings.Count}");
        crossings[0].IsCleared = true;
        Assert.True(IsAhead(route, crossings[0]));

        Assert.Null(NextCrossing(taxi.Engine, ac, AircraftSituation.Taxiing));

        ac = AiTestFixture.TickUntil(
            taxi.Engine,
            AiTestFixture.Callsign,
            a => (a.Phases?.CurrentPhase is TaxiingPhase) && (!IsAhead(route, crossings[0])),
            TaxiBudgetSeconds
        );

        string? runway = NextCrossing(taxi.Engine, ac, AircraftSituation.Taxiing);
        Assert.NotNull(runway);
        Assert.True(RunwayIdentifier.Parse(crossings[1].TargetName!).Contains(runway), $"{runway} is not an end of {crossings[1].TargetName}");
    }

    /// <summary>
    /// A 28R arrival on the rollout given a taxi to runway 30: the TAXI stores its route behind the rollout's exit, and the controller
    /// sees that route from the moment it is accepted. The route starts at the exit's hold-short, so leaving the runway the
    /// aircraft is on is no crossing on it, and the runway named is the stored route's first uncleared crossing.
    /// </summary>
    [Fact]
    public void NextCrossing_OnTheRolloutWithAStoredRoute_NamesItsFirstUnclearedCrossing()
    {
        if (RollingOut() is not { } rollout)
        {
            return;
        }

        CommandResult result = rollout.Engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 30");
        Assert.True(result.Success, result.Message);
        AircraftState ac = rollout.Aircraft;
        Assert.IsType<LandingPhase>(ac.Phases?.CurrentPhase);
        Assert.Null(ac.Ground.AssignedTaxiRoute);
        TaxiRoute route = ac.Phases!.TaxiRoute ?? throw new Xunit.Sdk.XunitException("no taxi route stored on the phase list");
        List<HoldShortPoint> bars =
        [
            .. route.Segments.Skip(route.CurrentSegmentIndex).Select(s => route.GetHoldShortAt(s.ToNodeId)).OfType<HoldShortPoint>(),
        ];
        string dump = string.Join(", ", bars.Select(b => $"{b.Reason} {b.TargetName} cleared={b.IsCleared}"));
        HoldShortPoint next = bars.First(b => !b.IsCleared);

        string? runway = NextCrossing(rollout.Engine, ac, AircraftSituation.RolloutExit);

        Assert.True(runway is not null, $"no next crossing on the rollout: {dump}");
        Assert.True(RunwayIdentifier.Parse(next.TargetName!).Contains(runway), $"{runway} is not an end of {next.TargetName}: {dump}");
    }

    /// <summary>
    /// SIG1 to runway 30 crosses two runways. While the aircraft is on the first one's pavement, its bar behind it, the
    /// crossing is not finished, so no second crossing is offered (7110.65 §3-7-2.c); once off that pavement the second
    /// runway is named.
    /// </summary>
    [Fact]
    public void NextCrossing_MidCrossingOnAnotherRunwaysPavement_IsNullUntilOffIt()
    {
        if (MidCrossing() is not { } mid)
        {
            return;
        }

        Assert.Null(NextCrossing(mid.Taxi.Engine, mid.Aircraft, AircraftSituation.Taxiing));

        AircraftState ac = AiTestFixture.TickUntil(
            mid.Taxi.Engine,
            AiTestFixture.Callsign,
            a => (a.Phases?.CurrentPhase is TaxiingPhase) && (!RunwayOccupancy.IsOnPavement(a, mid.Crossed)),
            TaxiBudgetSeconds
        );

        string? runway = NextCrossing(mid.Taxi.Engine, ac, AircraftSituation.Taxiing);
        Assert.NotNull(runway);
        Assert.True(RunwayIdentifier.Parse(mid.Next.TargetName!).Contains(runway), $"{runway} is not an end of {mid.Next.TargetName}");
    }

    /// <summary>
    /// SIG1 to runway 30, cleared across the first runway and ticked onto its pavement, past its bar; null when the OAK
    /// data is missing.
    /// </summary>
    private MidRunwayCrossing? MidCrossing()
    {
        if (Taxiing(AiTestFixture.ParkedAtOak, "TAXIAUTO 30") is not { } taxi)
        {
            return null;
        }

        AircraftState ac = TickUntilTaxiing(taxi);
        TaxiRoute route = ac.Ground.AssignedTaxiRoute!;
        List<HoldShortPoint> crossings = [.. route.HoldShortPoints.Where(h => h.Reason == HoldShortReason.RunwayCrossing)];
        Assert.True(crossings.Count >= 2, $"expected two crossings, got {crossings.Count}");
        crossings[0].IsCleared = true;
        RunwayInfo crossed = OakRunwayOf(crossings[0].TargetName!);

        ac = AiTestFixture.TickUntil(
            taxi.Engine,
            AiTestFixture.Callsign,
            a => (!IsAhead(route, crossings[0])) && RunwayOccupancy.IsOnPavement(a, crossed),
            TaxiBudgetSeconds
        );
        return new MidRunwayCrossing(taxi, ac, crossed, crossings[1]);
    }

    private sealed record MidRunwayCrossing(EngineAircraft Taxi, AircraftState Aircraft, RunwayInfo Crossed, HoldShortPoint Next);

    /// <summary>
    /// The 28R rollout given a taxi to runway 30, restaged in a phase that stands on the aircraft's own runway: a rejected
    /// takeoff slowed to taxi speed, clearing the runway, or stopped on it. The route starts on 28R, so no bar of it lies
    /// behind the aircraft: leaving its own runway is not crossing another, and the runway after 28R's bar is still named.
    /// </summary>
    [Theory]
    [InlineData("Rejected Takeoff", AircraftSituation.RolloutExit)]
    [InlineData("Clearing Runway", AircraftSituation.RolloutExit)]
    [InlineData("Holding In Position", AircraftSituation.HoldingOnGround)]
    public void NextCrossing_OnItsOwnAssignedRunway_NamesTheNextRunway(string phaseName, AircraftSituation situation)
    {
        if (OnOwnRunwayTaxiingTo30(phaseName) is not { } rollout)
        {
            return;
        }

        AircraftState ac = rollout.Aircraft;
        Restage(ac, OwnRunwayPhase(phaseName), ac.Phases!.AssignedRunway!);
        ac.IndicatedAirspeed = 20;
        Assert.True(RunwayOccupancy.IsOnPavement(ac, Runway("28R")), "the rollout is not on 28R's pavement");
        HoldShortPoint next = ac.Ground.AssignedTaxiRoute!.HoldShortPoints.First(h => !h.IsCleared);

        string? runway = NextCrossing(rollout.Engine, ac, situation);

        Assert.NotNull(runway);
        Assert.True(RunwayIdentifier.Parse(next.TargetName!).Contains(runway), $"{runway} is not an end of {next.TargetName}");
    }

    /// <summary>
    /// Clearing a runway the aircraft is crossing (its bar behind it on the route, its pavement underfoot) is not having
    /// crossed it: no next crossing (§3-7-2.c).
    /// </summary>
    [Fact]
    public void NextCrossing_ClearingARunwayItIsCrossing_IsNull()
    {
        if (MidCrossing() is not { } mid)
        {
            return;
        }

        Restage(mid.Aircraft, new ClearRunwayPhase(0, 0), mid.Aircraft.Phases!.AssignedRunway!);

        Assert.Null(NextCrossing(mid.Taxi.Engine, mid.Aircraft, AircraftSituation.RolloutExit));
    }

    /// <summary>
    /// A departure lined up on runway 30 and then stopped there in Holding In Position: its own runway, but no uncleared bar
    /// remains on the route, so no crossing is offered.
    /// </summary>
    [Fact]
    public void NextCrossing_HoldingInPositionOnItsDepartureRunwayWithTheRouteDone_IsNull()
    {
        if (Taxiing(ParkedAt29, "TAXIAUTO 30") is not { } taxi)
        {
            return;
        }

        AiTestFixture.TickUntil(taxi.Engine, AiTestFixture.Callsign, a => a.Phases?.CurrentPhase is HoldingShortPhase, TaxiBudgetSeconds);
        CommandResult result = taxi.Engine.SendCommand(AiTestFixture.Callsign, "LUAW");
        Assert.True(result.Success, result.Message);
        AircraftState ac = AiTestFixture.TickUntil(
            taxi.Engine,
            AiTestFixture.Callsign,
            a => a.Phases?.CurrentPhase is LinedUpAndWaitingPhase,
            TaxiBudgetSeconds
        );
        RunwayInfo departure = ac.Phases!.AssignedRunway!;
        Restage(ac, new HoldingInPositionPhase(), departure);
        Assert.True(RunwayOccupancy.IsOnPavement(ac, departure), "the departure is not on runway 30's pavement");

        Assert.Null(NextCrossing(taxi.Engine, ac, AircraftSituation.HoldingOnGround));
    }

    /// <summary>
    /// The 28R arrival, out of its rollout and staged in <paramref name="phaseName"/> on 28R at taxi speed, given a taxi to runway
    /// 30: the TAXI routes it from 28R's centerline, so the active route starts on 28R and crosses a runway after 28R's own bar.
    /// </summary>
    private Rollout? OnOwnRunwayTaxiingTo30(string phaseName)
    {
        if (RollingOut() is not { } rollout)
        {
            return null;
        }

        AircraftState ac = rollout.Aircraft;
        Restage(ac, OwnRunwayPhase(phaseName), ac.Phases!.AssignedRunway!);
        ac.IndicatedAirspeed = 20;
        CommandResult result = rollout.Engine.SendCommand(AiTestFixture.Callsign, "TAXIAUTO 30");
        Assert.True(result.Success, result.Message);
        return rollout;
    }

    private static Phase OwnRunwayPhase(string phaseName) =>
        phaseName switch
        {
            "Rejected Takeoff" => new RejectedTakeoffPhase(0),
            "Clearing Runway" => new ClearRunwayPhase(0, 0),
            "Holding In Position" => new HoldingInPositionPhase(),
            _ => throw new ArgumentOutOfRangeException(nameof(phaseName), phaseName, "no phase staged under this name"),
        };

    /// <summary>Replaces the aircraft's phases with <paramref name="phase"/> alone, on <paramref name="assigned"/>.</summary>
    private static void Restage(AircraftState ac, Phase phase, RunwayInfo assigned)
    {
        var phases = new PhaseList { AssignedRunway = assigned };
        phases.Add(phase);
        ac.Phases = phases;
    }

    /// <summary>The OAK runway a crossing bar's combined target (<c>10L/28R</c>) names.</summary>
    private static RunwayInfo OakRunwayOf(string target) =>
        NavigationDatabase.Instance.GetRunways("OAK").First(r => r.Id.Overlaps(RunwayIdentifier.Parse(target)));

    /// <summary>
    /// An explicit hold-short of the assigned runway with no departure bar after it is an intersection departure, not a crossing:
    /// null.
    /// </summary>
    [Fact]
    public void NextCrossing_IntersectionDepartureHoldShort_IsNull()
    {
        if (Taxiing(AiTestFixture.ParkedAtOak, "RWY 28R") is not { } taxi)
        {
            return;
        }

        CommandResult result = taxi.Engine.SendCommand(AiTestFixture.Callsign, "TAXI D G HS 28R");
        Assert.True(result.Success, result.Message);
        AircraftState ac = TickUntilTaxiing(taxi);
        Assert.DoesNotContain(ac.Ground.AssignedTaxiRoute!.HoldShortPoints, h => h.Reason == HoldShortReason.DestinationRunway);

        Assert.Null(NextCrossing(taxi.Engine, ac, AircraftSituation.Taxiing));

        ac.Phases!.AssignedRunway = Runway("30");
        Assert.NotNull(NextCrossing(taxi.Engine, ac, AircraftSituation.Taxiing));
    }

    /// <summary>Without a ground layout an explicit hold-short cannot be placed on a runway: null.</summary>
    [Fact]
    public void NextCrossing_ExplicitRunwayHoldShortWithoutALayout_IsNull()
    {
        if (Taxiing(AiTestFixture.ParkedAtOak, "TAXIAUTO 28L") is not { } taxi)
        {
            return;
        }

        Assert.True(taxi.Engine.SendCommand(AiTestFixture.Callsign, "HS 28R").Success);
        AircraftState ac = TickUntilTaxiing(taxi);

        Assert.Null(SituationFlagCalculator.NextCrossingRunway(ac, AircraftSituation.Taxiing, null, ActiveRunways.Empty));
    }

    // --- HasTakeoffClearance ---

    [Fact]
    public void TakeoffClearance_ClearedForTakeoff_IsSet()
    {
        AircraftState ac = OnRunway30(new TakeoffPhase(), true, 0);
        ac.Phases!.DepartureClearance = new DepartureClearanceInfo { Type = ClearanceType.ClearedForTakeoff, Departure = new DefaultDeparture() };

        Assert.True(Has(ComputeAs(ac, AircraftSituation.LinedUp), SituationFlags.HasTakeoffClearance));
    }

    /// <summary>A line-up-and-wait clearance is not a takeoff clearance, and no clearance reads clear.</summary>
    [Fact]
    public void TakeoffClearance_LineUpAndWaitOrNone_IsClear()
    {
        AircraftState ac = OnRunway30(new LinedUpAndWaitingPhase(), true, 0);
        Assert.False(Has(ComputeAs(ac, AircraftSituation.LinedUp), SituationFlags.HasTakeoffClearance));

        ac.Phases!.DepartureClearance = new DepartureClearanceInfo { Type = ClearanceType.LineUpAndWait, Departure = new DefaultDeparture() };

        Assert.False(Has(ComputeAs(ac, AircraftSituation.LinedUp), SituationFlags.HasTakeoffClearance));
    }

    // --- PastV1 ---

    [Fact]
    public void PastV1_RollingAtOrAboveV1_IsSet()
    {
        AircraftState ac = OnRunway30(new TakeoffPhase(), true, B738V1() + 5);

        Assert.True(Has(ComputeAs(ac, AircraftSituation.LinedUp), SituationFlags.PastV1));
    }

    /// <summary>
    /// One knot either side of V1 on the roll. The exact boundary is not asserted: the on-ground airspeed field carries
    /// groundspeed, which is converted to indicated (<c>GroundFrame.IasForGroundSpeed</c>) before the compare, so V1 itself
    /// lands a rounding hair either side of the gate.
    /// </summary>
    [Theory]
    [InlineData(1.0, true)]
    [InlineData(-1.0, false)]
    public void PastV1_RollingJustAboveOrBelowV1_IsSetOnlyAbove(double offsetKts, bool isSet)
    {
        AircraftState ac = OnRunway30(new TakeoffPhase(), true, B738V1() + offsetKts);

        Assert.Equal(isSet, Has(ComputeAs(ac, AircraftSituation.LinedUp), SituationFlags.PastV1));
    }

    [Fact]
    public void PastV1_AirborneInTakeoffPhase_IsSet()
    {
        AircraftState ac = OnRunway30(new TakeoffPhase(), false, 160);

        Assert.True(Has(ComputeAs(ac, AircraftSituation.Departing), SituationFlags.PastV1));
    }

    /// <summary>Below V1 on the roll, and past V1 speed outside the takeoff phase (an initial climb), read clear.</summary>
    [Fact]
    public void PastV1_BelowV1OrNotTakingOff_IsClear()
    {
        AircraftState rolling = OnRunway30(new TakeoffPhase(), true, B738V1() - 20);
        Assert.False(Has(ComputeAs(rolling, AircraftSituation.LinedUp), SituationFlags.PastV1));

        AircraftState climbing = OnRunway30(new InitialClimbPhase(), false, 180);
        Assert.False(Has(ComputeAs(climbing, AircraftSituation.Departing), SituationFlags.PastV1));
    }

    // --- ApproachClearedForDescent ---

    [Fact]
    public void ApproachClearedForDescent_FullApproachClearance_IsSet()
    {
        AircraftState ac = Reporting("IFR");
        ac.Phases = new PhaseList { ActiveApproach = ClearanceFor28R(lateralInterceptOnly: false) };

        Assert.True(Has(ComputeAs(ac, AircraftSituation.Approach), SituationFlags.ApproachClearedForDescent));
    }

    /// <summary>
    /// A go-around, the missed approach and its published hold end the approach clearance: a second approach needs a new
    /// one (§4-8-9.a), so the flag clears although the clearance is still on the aircraft.
    /// </summary>
    [Fact]
    public void ApproachClearedForDescent_GoneAroundOrMissed_IsClear()
    {
        RunwayInfo runway = Runway("28R");
        LatLon position = OffFinal(runway, 0.5, 0);
        var missedHold = new MissedApproachHold("OAK", position.Lat, position.Lon, 280, 1, true, TurnDirection.Right);
        Phase[] ended =
        [
            new GoAroundPhase(),
            new ApproachNavigationPhase { Fixes = [], IsMissedApproach = true },
            new HoldingPatternPhase
            {
                FixName = missedHold.FixName,
                FixLat = missedHold.FixLat,
                FixLon = missedHold.FixLon,
                InboundCourse = missedHold.InboundCourse,
                LegLength = missedHold.LegLength,
                IsMinuteBased = missedHold.IsMinuteBased,
                Direction = missedHold.Direction,
            },
        ];

        foreach (Phase phase in ended)
        {
            AircraftState ac = OnApproachPhase(phase, runway, position);
            ac.Phases!.ActiveApproach = new ApproachClearance
            {
                TransitionName = null,
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = runway.TrueHeading,
                MapHold = missedHold,
            };

            Assert.False(Has(ComputeAs(ac, AircraftSituation.GoAround), SituationFlags.ApproachClearedForDescent), phase.Name);
        }
    }

    /// <summary>
    /// The go-around keeps the clearance on the aircraft, and stays ended once the climb is over: vectored off it (the phase
    /// list cleared), the flag stays clear until a new approach clearance, which starts a new phase list, sets it again.
    /// </summary>
    [Fact]
    public void ApproachClearedForDescent_AfterTheGoAroundEnds_StaysClearUntilANewClearance()
    {
        AircraftState ac = OnApproachPhase(new GoAroundPhase(), Runway("28R"), OffFinal(Runway("28R"), 0.5, 0));
        ac.Phases!.ActiveApproach = ClearanceFor28R(lateralInterceptOnly: false);
        PhaseContext ctx = CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty);
        ac.Phases.Start(ctx);
        ac.Phases.Clear(ctx);
        Assert.Null(ac.Phases.CurrentPhase);

        Assert.False(Has(ComputeAs(ac, AircraftSituation.IfrArrival), SituationFlags.ApproachClearedForDescent));

        ac.Phases = new PhaseList { AssignedRunway = Runway("28R"), ActiveApproach = ClearanceFor28R(lateralInterceptOnly: false) };

        Assert.True(Has(ComputeAs(ac, AircraftSituation.IfrArrival), SituationFlags.ApproachClearedForDescent));
    }

    /// <summary>A lateral intercept (JFAC/JLOC) is not a clearance to descend on the approach, and no clearance reads clear.</summary>
    [Fact]
    public void ApproachClearedForDescent_LateralInterceptOnlyOrNone_IsClear()
    {
        AircraftState ac = Reporting("IFR");
        ac.Phases = new PhaseList();
        Assert.False(Has(ComputeAs(ac, AircraftSituation.Approach), SituationFlags.ApproachClearedForDescent));

        ac.Phases.ActiveApproach = ClearanceFor28R(lateralInterceptOnly: true);

        Assert.False(Has(ComputeAs(ac, AircraftSituation.Approach), SituationFlags.ApproachClearedForDescent));
    }

    // --- EstablishedOnExpectedFinal / ExpectedApproachAdvised / ApproachClearancePending ---

    /// <summary>Distance (NM) out from the threshold, along the final approach course, the established cases fly at.</summary>
    private const double EstablishedOutNm = 10.0;

    /// <summary>The KOAK I28R segment fix between an aircraft 10 NM out and the FAF; the established cases fly its altitude.</summary>
    private const string KoakNextFix = "URZAF";

    /// <summary>KOAK, I28R expected, on its final course 10 NM out, tracking the course at the next fix's altitude: set.</summary>
    [Fact]
    public void EstablishedFinal_OnLocalizerOutsideFaf_IsSet()
    {
        ExpectedFinal final = Final("OAK", "I28R");
        double nextFixOutNm = OutNm(final, FixPosition(final, KoakNextFix));
        Assert.True((nextFixOutNm < EstablishedOutNm) && (nextFixOutNm > FafOutNm(final)), $"{KoakNextFix} is {nextFixOutNm:F1} NM out");

        Assert.True(Established(OnKoakI28R(EstablishedOutNm, 0.0)));
    }

    [Fact]
    public void EstablishedFinal_InsideFaf_IsClear()
    {
        ExpectedFinal final = Final("OAK", "I28R");

        Assert.False(Established(OnKoakI28R(FafOutNm(final) - 1.0, 0.0)));
    }

    [Fact]
    public void EstablishedFinal_PastLateralTolerance_IsClear() => Assert.False(Established(OnKoakI28R(EstablishedOutNm, 0.6)));

    [Fact]
    public void EstablishedFinal_CrossingTheCourse_IsClear()
    {
        AircraftState ac = OnKoakI28R(EstablishedOutNm, 0.0);
        ac.TrueTrack += 90.0;
        ac.TrueHeading = ac.TrueTrack;

        Assert.False(Established(ac));
    }

    [Fact]
    public void EstablishedFinal_OutboundOnTheCourse_IsClear()
    {
        AircraftState ac = OnKoakI28R(EstablishedOutNm, 0.0);
        ac.TrueTrack = ac.TrueTrack.ToReciprocal();
        ac.TrueHeading = ac.TrueTrack;

        Assert.False(Established(ac));
    }

    [Fact]
    public void EstablishedFinal_BeyondEighteenMiles_IsClear()
    {
        AircraftState ac = OnKoakI28R(25.0, 0.0);
        ac.Altitude = 6000;

        Assert.False(Established(ac));
    }

    [Fact]
    public void EstablishedFinal_AboveTheGlideslope_IsSet()
    {
        AircraftState ac = OnKoakI28R(EstablishedOutNm, 0.0);
        ac.Altitude += 3000;

        Assert.True(Established(ac));
    }

    [Fact]
    public void EstablishedFinal_BelowTheSegmentAltitude_IsClear()
    {
        AircraftState ac = OnKoakI28R(EstablishedOutNm, 0.0);
        ac.Altitude -= 300;

        Assert.False(Established(ac));
    }

    /// <summary>
    /// KOAK I28R codes its IF, GROVE, with a glideslope-intercept altitude. 1.5 NM outside GROVE on the course, GROVE is
    /// the nearest fix ahead: 300 ft below its altitude reads clear, at it set.
    /// </summary>
    [Fact]
    public void EstablishedFinal_BelowTheGlideSlopeInterceptAltitude_IsClear()
    {
        ExpectedFinal final = Final("OAK", "I28R");
        CifpLeg grove = final.Procedure.CommonLegs.First(l => l.FixIdentifier == "GROVE");
        CifpAltitudeRestriction gsi = FixRestriction(final, "GROVE");
        Assert.Equal(CifpAltitudeRestrictionType.GlideSlopeIntercept, gsi.Type);
        double outNm = OutNm(final, LegPosition(grove)) + 1.5;
        Assert.True((outNm < ExpectedFinalGeometry.MaxRangeNm) && (outNm > FafOutNm(final)), $"{outNm:F1} NM out");
        AircraftState ac = Place(final, outNm, 0.0);

        ac.Altitude = gsi.Altitude1Ft - 300;
        Assert.False(Established(ac));

        ac.Altitude = gsi.Altitude1Ft;
        Assert.True(Established(ac));
    }

    /// <summary>
    /// KSFO, I28R expected, on 28L's extended centerline 10 NM out: within I28R's lateral tolerance (the parallels are 750 ft
    /// apart), but 28L's is the nearest final, so clear.
    /// </summary>
    [Fact]
    public void EstablishedFinal_OnTheCloseParallel_IsClear()
    {
        ExpectedFinal final = Final("SFO", "I28R");
        RunwayInfo left = NavigationDatabase.Instance.GetRunway("SFO", "28L") ?? throw new InvalidOperationException("SFO 28L missing from NavData");
        var leftThreshold = new LatLon(left.ThresholdLatitude, left.ThresholdLongitude);
        LatLon position = GeoMath.ProjectPoint(leftThreshold, left.TrueHeading.ToReciprocal(), EstablishedOutNm);
        AircraftState ac = Arrival(final, position, left.TrueHeading);
        double crossTrackNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(position, Anchor(final), final.Fac.Course));
        Assert.True(crossTrackNm <= AirborneFollowHelper.OnFinalMaxCrossTrackNm, $"{crossTrackNm:F2} NM off I28R's course");

        Assert.False(Established(ac));
    }

    /// <summary>KCCR, X19R (LDA) expected, on the LDA's published course, not 19R's centerline, 10 NM out: set.</summary>
    [Fact]
    public void EstablishedFinal_OffsetLda_UsesThePublishedCourse()
    {
        ExpectedFinal final = Final("CCR", "X19R");
        Assert.True(final.Fac.Course.AbsAngleTo(final.Runway.TrueHeading) > 1.0, "X19R's course is 19R's centerline");

        Assert.True(Established(Place(final, EstablishedOutNm, 0.0)));
    }

    /// <summary>KAJO, VOR-A expected (circling only, no runway), inbound on its final course outside its FAF: clear.</summary>
    [Fact]
    public void EstablishedFinal_CirclingOnly_IsClear()
    {
        CifpApproachProcedure procedure =
            NavigationDatabase.Instance.GetApproach("AJO", "VOR-A") ?? throw new InvalidOperationException("KAJO VOR-A missing");
        Assert.Null(procedure.Runway);
        LatLon faf = LegPosition(procedure.CommonLegs.First(l => l.FixRole == CifpFixRole.FAF));
        LatLon map = LegPosition(procedure.CommonLegs.First(l => l.FixRole == CifpFixRole.MAP));
        var course = new TrueHeading(GeoMath.BearingTo(faf, map));
        var ac = new AircraftState
        {
            Callsign = "UAL100",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(faf, course.ToReciprocal(), 3.0),
            TrueHeading = course,
            TrueTrack = course,
            Altitude = 4000,
            IndicatedAirspeed = 180,
            IsOnGround = false,
        };
        ac.FlightPlan.FlightRules = "IFR";
        ac.FlightPlan.Destination = "AJO";
        ac.Approach.Expected = "VOR-A";

        Assert.False(Established(ac));
    }

    [Fact]
    public void EstablishedFinal_VisualExpectation_IsClear()
    {
        AircraftState ac = OnKoakI28R(EstablishedOutNm, 0.0);
        ac.Approach.Expected = "VIS28R";

        Assert.False(Established(ac));
    }

    [Fact]
    public void EstablishedFinal_Vfr_IsClear()
    {
        AircraftState ac = OnKoakI28R(EstablishedOutNm, 0.0);
        ac.FlightPlan.FlightRules = "VFR";

        Assert.False(Established(ac));
    }

    [Fact]
    public void EstablishedFinal_ClearedForDescent_IsClear()
    {
        AircraftState ac = OnKoakI28R(EstablishedOutNm, 0.0);
        ac.Phases = new PhaseList { ActiveApproach = ClearanceFor28R(lateralInterceptOnly: false) };

        SituationFlags flags = ComputeAs(ac, AircraftSituation.Approach);
        Assert.True(Has(flags, SituationFlags.ApproachClearedForDescent));
        Assert.False(Has(flags, SituationFlags.EstablishedOnExpectedFinal));
    }

    /// <summary>A JFAC/JLOC lateral join is not an approach clearance: still uncleared, so set.</summary>
    [Fact]
    public void EstablishedFinal_LateralJoinOnly_IsSet()
    {
        AircraftState ac = OnKoakI28R(EstablishedOutNm, 0.0);
        ac.Phases = new PhaseList { ActiveApproach = ClearanceFor28R(lateralInterceptOnly: true) };

        Assert.True(Established(ac));
    }

    [Fact]
    public void EstablishedFinal_PendingClearance_IsClear()
    {
        AircraftState ac = OnKoakI28R(EstablishedOutNm, 0.0);
        Assert.False(Has(ComputeAs(ac, AircraftSituation.Approach), SituationFlags.ApproachClearancePending));
        ac.Approach.PendingClearance = new PendingApproachInfo
        {
            Clearance = ClearanceFor28R(lateralInterceptOnly: false),
            AssignedRunway = Runway("28R"),
            ConnectingFix = null,
        };

        SituationFlags flags = ComputeAs(ac, AircraftSituation.Approach);
        Assert.False(Has(flags, SituationFlags.EstablishedOnExpectedFinal));
        Assert.True(Has(flags, SituationFlags.ApproachClearancePending));
    }

    [Fact]
    public void ExpectedApproachAdvised_FollowsTheStamp()
    {
        AircraftState ac = Reporting("IFR");
        ac.Approach.Expected = "I28R";
        Assert.False(Has(ComputeAs(ac, AircraftSituation.IfrArrival), SituationFlags.ExpectedApproachAdvised));

        ac.ExpectedApproachAdvisedAtSeconds = 30.0;
        Assert.True(Has(ComputeAs(ac, AircraftSituation.IfrArrival), SituationFlags.ExpectedApproachAdvised));

        ac.ExpectedApproachAdvisedAtSeconds = null;
        Assert.False(Has(ComputeAs(ac, AircraftSituation.IfrArrival), SituationFlags.ExpectedApproachAdvised));
    }

    /// <summary>
    /// KCCR, S19R expected (a VOR final offset from 19R's centerline), on its published course 1 NM outside FAWNE at FAWNE's
    /// altitude: set, though a centerline wedge for 19R would not hold it.
    /// </summary>
    [Fact]
    public void EstablishedFinal_OffsetFinalPastTheCenterlineWedge_IsSet()
    {
        ExpectedFinal final = Final("CCR", "S19R");
        double outNm = OutNm(final, FixPosition(final, "FAWNE")) + 1.0;
        Assert.True(outNm < ExpectedFinalGeometry.MaxRangeNm, $"{outNm:F1} NM out");
        AircraftState ac = Place(final, outNm, 0.0);
        ac.Altitude = FixRestriction(final, "FAWNE").Altitude1Ft;

        Assert.True(Established(ac));
    }

    /// <summary>KCCR, X19R (LDA) expected, on its published course 16 NM out at JOANS's altitude: set.</summary>
    [Fact]
    public void EstablishedFinal_OffsetLdaFarOut_IsSet()
    {
        ExpectedFinal final = Final("CCR", "X19R");
        Assert.True(OutNm(final, FixPosition(final, "JOANS")) < 16.0, "JOANS is not ahead of 16 NM out");
        AircraftState ac = Place(final, 16.0, 0.0);
        ac.Altitude = FixRestriction(final, "JOANS").Altitude1Ft;

        Assert.True(Established(ac));
    }

    /// <summary>KSFO, I28R expected, on 28R's own course 10 NM out with 28L's final 750 ft beside it: set.</summary>
    [Fact]
    public void EstablishedFinal_OnTheExpectedRunwaysOwnFinalBesideAParallel_IsSet()
    {
        ExpectedFinal final = Final("SFO", "I28R");
        AircraftState ac = Place(final, EstablishedOutNm, 0.0);
        ac.Altitude = FixRestriction(final, "CEPIN").Altitude1Ft;

        Assert.True(Established(ac));
    }

    /// <summary>KOAK I28R, at 4,000 ft (above GROVE's glideslope-intercept altitude ahead): set inside 18 NM out, clear past it.</summary>
    [Theory]
    [InlineData(17.5, true)]
    [InlineData(18.5, false)]
    public void EstablishedFinal_RangeBoundary(double outNm, bool established)
    {
        AircraftState ac = OnKoakI28R(outNm, 0.0);
        ac.Altitude = 4000;
        (double Lat, double Lon) oak =
            NavigationDatabase.Instance.GetAirportPosition("OAK") ?? throw new InvalidOperationException("OAK has no position");
        double fromFieldNm = GeoMath.DistanceNm(ac.Position, new LatLon(oak.Lat, oak.Lon));
        Assert.True(
            fromFieldNm < (ExpectedFinalGeometry.MaxRangeNm + ExpectedFinalGeometry.AirportGateMarginNm),
            $"{fromFieldNm:F1} NM from the field: the destination range gate, not the 18 NM rule, would decide"
        );

        Assert.Equal(established, Established(ac));
    }

    /// <summary>KOAK I28R 10 NM out: within the 100 ft margin below the next fix's altitude set, past it clear.</summary>
    [Theory]
    [InlineData(-90.0, true)]
    [InlineData(-110.0, false)]
    public void EstablishedFinal_SegmentAltitudeMarginBoundary(double belowFt, bool established)
    {
        AircraftState ac = OnKoakI28R(EstablishedOutNm, 0.0);
        ac.Altitude += belowFt;

        Assert.Equal(established, Established(ac));
    }

    [Fact]
    public void EstablishedFinal_WithinLateralTolerance_IsSet() => Assert.True(Established(OnKoakI28R(EstablishedOutNm, 0.4)));

    /// <summary>KOAK I28R 10 NM out on the course: tracking 25° off it set, 35° off clear.</summary>
    [Theory]
    [InlineData(25.0, true)]
    [InlineData(35.0, false)]
    public void EstablishedFinal_TrackBoundary(double offDeg, bool established)
    {
        AircraftState ac = OnKoakI28R(EstablishedOutNm, 0.0);
        ac.TrueTrack += offDeg;
        ac.TrueHeading = ac.TrueTrack;

        Assert.Equal(established, Established(ac));
    }

    /// <summary>
    /// KAPC R19R's common legs turn onto the final: BAAHH, restricted at or above, lies off the final course but projects
    /// ahead of an aircraft 1 NM outside it on the final. Below BAAHH's altitude but above the on-course FAF's, the off-course
    /// fix does not set the floor: set.
    /// </summary>
    [Fact]
    public void EstablishedFinal_OffCourseFixAhead_DoesNotSetTheFloor()
    {
        ExpectedFinal final = Final("APC", "R19R");
        LatLon baahh = FixPosition(final, "BAAHH");
        Assert.True(Math.Abs(CrossTrackNm(final, baahh)) > AirborneFollowHelper.OnFinalMaxCrossTrackNm, "BAAHH is on the final course");
        LatLon faf = LegPosition(final.Procedure.CommonLegs.First(l => l.FixRole == CifpFixRole.FAF));
        Assert.True(Math.Abs(CrossTrackNm(final, faf)) <= AirborneFollowHelper.OnFinalMaxCrossTrackNm, "the FAF is off the final course");
        double outNm = OutNm(final, baahh) + 1.0;
        Assert.True((outNm > FafOutNm(final)) && (outNm < ExpectedFinalGeometry.MaxRangeNm), $"{outNm:F1} NM out");
        int fafFt =
            final.Procedure.CommonLegs.First(l => l.FixRole == CifpFixRole.FAF).Altitude?.Altitude1Ft
            ?? throw new InvalidOperationException("KAPC R19R FAF carries no altitude");
        int baahhFt = FixRestriction(final, "BAAHH").Altitude1Ft;
        Assert.True(baahhFt - fafFt > 400, $"BAAHH {baahhFt} ft, FAF {fafFt} ft");
        AircraftState ac = Place(final, outNm, 0.0);
        ac.Altitude = (fafFt + baahhFt) / 2;

        Assert.True(Established(ac));
    }

    /// <summary>
    /// 40 NM out on the final with an expected approach: clear. This covers the outcome only; the 18 NM rule and the
    /// destination range gate both reject it, and the test cannot tell which did.
    /// </summary>
    [Fact]
    public void EstablishedFinal_FarOut_IsClear()
    {
        AircraftState ac = OnKoakI28R(40.0, 0.0);
        ac.Altitude = 6000;

        Assert.False(Established(ac));
    }

    /// <summary>An expected approach resolved as CAPP resolves it, with its runway and its extracted final approach course.</summary>
    private sealed record ExpectedFinal(
        string Airport,
        string Expected,
        CifpApproachProcedure Procedure,
        RunwayInfo Runway,
        FinalApproachCourseResult Fac
    );

    private static ExpectedFinal Final(string airport, string expected)
    {
        NavigationDatabase navDb = NavigationDatabase.Instance;
        string id = navDb.ResolveApproachCandidates(airport, expected).Single();
        CifpApproachProcedure procedure = navDb.GetApproach(airport, id) ?? throw new InvalidOperationException($"{airport} {id} missing");
        string runwayId = procedure.Runway ?? throw new InvalidOperationException($"{airport} {id} names no runway");
        RunwayInfo runway = navDb.GetRunway(airport, runwayId) ?? throw new InvalidOperationException($"{airport} {runwayId} missing");
        return new ExpectedFinal(airport, expected, procedure, runway, FinalApproachCourseExtractor.Extract(procedure, runway, navDb));
    }

    private static bool Established(AircraftState ac) => Has(ComputeAs(ac, AircraftSituation.Approach), SituationFlags.EstablishedOnExpectedFinal);

    /// <summary>
    /// KOAK, I28R expected, <paramref name="outNm"/> out on its course and <paramref name="offNm"/> right of it, at the next
    /// fix's altitude.
    /// </summary>
    private static AircraftState OnKoakI28R(double outNm, double offNm)
    {
        ExpectedFinal final = Final("OAK", "I28R");
        AircraftState ac = Place(final, outNm, offNm);
        ac.Altitude = FixRestriction(final, KoakNextFix).Altitude1Ft;
        return ac;
    }

    /// <summary>The altitude restriction coded on <paramref name="fix"/>'s common leg.</summary>
    private static CifpAltitudeRestriction FixRestriction(ExpectedFinal final, string fix) =>
        final.Procedure.CommonLegs.First(l => l.FixIdentifier == fix).Altitude
        ?? throw new InvalidOperationException($"{final.Procedure.ApproachId} {fix} carries no altitude");

    /// <summary>
    /// On <paramref name="final"/>'s course <paramref name="outNm"/> from the threshold, <paramref name="offNm"/> right of it,
    /// tracking it.
    /// </summary>
    private static AircraftState Place(ExpectedFinal final, double outNm, double offNm)
    {
        LatLon anchor = Anchor(final);
        TrueHeading course = final.Fac.Course;
        LatLon onCourse = GeoMath.ProjectPoint(anchor, course.ToReciprocal(), outNm - OutNm(final, anchor));
        return Arrival(final, GeoMath.ProjectPoint(onCourse, course + 90.0, offNm), course);
    }

    private static AircraftState Arrival(ExpectedFinal final, LatLon position, TrueHeading track)
    {
        var ac = new AircraftState
        {
            Callsign = "UAL100",
            AircraftType = "B738",
            Position = position,
            TrueHeading = track,
            TrueTrack = track,
            Altitude = 3000,
            IndicatedAirspeed = 180,
            IsOnGround = false,
        };
        ac.FlightPlan.FlightRules = "IFR";
        ac.FlightPlan.Destination = final.Airport;
        ac.Approach.Expected = final.Expected;
        return ac;
    }

    private static LatLon Threshold(ExpectedFinal final) => new(final.Runway.ThresholdLatitude, final.Runway.ThresholdLongitude);

    private static LatLon Anchor(ExpectedFinal final) =>
        ((final.Fac.AnchorLat is { } lat) && (final.Fac.AnchorLon is { } lon)) ? new LatLon(lat, lon) : Threshold(final);

    /// <summary>How far <paramref name="point"/> is out from the threshold along the final approach course.</summary>
    private static double OutNm(ExpectedFinal final, LatLon point) => -GeoMath.AlongTrackDistanceNm(point, Threshold(final), final.Fac.Course);

    /// <summary>How far <paramref name="point"/> is right of the final approach course, measured from its anchor.</summary>
    private static double CrossTrackNm(ExpectedFinal final, LatLon point) =>
        GeoMath.SignedCrossTrackDistanceNm(point, Anchor(final), final.Fac.Course);

    private static double FafOutNm(ExpectedFinal final) =>
        OutNm(final, LegPosition(final.Procedure.CommonLegs.First(l => l.FixRole == CifpFixRole.FAF)));

    private static LatLon FixPosition(ExpectedFinal final, string fix) => LegPosition(final.Procedure.CommonLegs.First(l => l.FixIdentifier == fix));

    private static LatLon LegPosition(CifpLeg leg) =>
        leg.ResolveFixPosition(NavigationDatabase.Instance) is { } pos
            ? new LatLon(pos.Lat, pos.Lon)
            : throw new InvalidOperationException($"{leg.FixIdentifier} unresolved");

    // --- Helpers ---

    private sealed record EngineAircraft(SimulationEngine Engine, AircraftState Aircraft);

    private sealed record Rollout(SimulationEngine Engine, AircraftState Aircraft, LandingPhase Phase);

    private static bool Has(SituationFlags flags, SituationFlags flag) => (flags & flag) == flag;

    private static SituationFlags Compute(SimulationEngine engine, AircraftState ac, SituationFlags previous) =>
        SituationFlagCalculator.Compute(ac, ac.Situation.Current, previous, ac.Ground.Layout ?? engine.ResolveGroundLayout(ac), null);

    private EngineAircraft? Taxiing(string scenarioJson, string taxiCommand)
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(scenarioJson, _zoa, 7, []);
        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, taxiCommand);
        Assert.True(result.Success, result.Message);
        return new EngineAircraft(engine, engine.FindAircraft(AiTestFixture.Callsign)!);
    }

    private EngineAircraft? HoldingShort(string taxiCommand, HoldShortReason reason)
    {
        if (Taxiing(AiTestFixture.ParkedAtOak, taxiCommand) is not { } taxi)
        {
            return null;
        }

        return taxi with
        {
            Aircraft = HoldUntilHoldingShort(taxi, reason),
        };
    }

    /// <summary>Ticks until the aircraft holds short, and checks the bar's reason.</summary>
    private static AircraftState HoldUntilHoldingShort(EngineAircraft taxi, HoldShortReason reason)
    {
        AircraftState ac = AiTestFixture.TickUntil(
            taxi.Engine,
            AiTestFixture.Callsign,
            a => a.Phases?.CurrentPhase is HoldingShortPhase,
            TaxiBudgetSeconds
        );
        Assert.Equal(reason, ((HoldingShortPhase)ac.Phases!.CurrentPhase!).HoldShort.Reason);
        return ac;
    }

    private static AircraftState TickUntilDistance(EngineAircraft taxi, Func<double, bool> wanted) =>
        AiTestFixture.TickUntil(
            taxi.Engine,
            AiTestFixture.Callsign,
            a => (a.Phases?.CurrentPhase is TaxiingPhase) && (DistanceToBar(taxi.Engine, a) is { } d) && wanted(d),
            TaxiBudgetSeconds
        );

    private static double? DistanceToBar(SimulationEngine engine, AircraftState ac) =>
        TaxiRouteProgress.DistanceToDestinationBarFt(ac, ac.Ground.Layout ?? engine.ResolveGroundLayout(ac));

    private static bool IsAhead(TaxiRoute route, HoldShortPoint bar) =>
        route.Segments.Skip(route.CurrentSegmentIndex).Any(s => s.ToNodeId == bar.NodeId);

    private static string? NextCrossing(SimulationEngine engine, AircraftState ac, AircraftSituation situation) =>
        SituationFlagCalculator.NextCrossingRunway(ac, situation, ac.Ground.Layout ?? engine.ResolveGroundLayout(ac), engine.Scenario!.ActiveRunways);

    private static SituationFlags ComputeAs(AircraftState ac, AircraftSituation situation) =>
        SituationFlagCalculator.Compute(ac, situation, SituationFlags.None, null, null);

    private static AircraftState TickUntilTaxiing(EngineAircraft taxi) =>
        AiTestFixture.TickUntil(taxi.Engine, AiTestFixture.Callsign, a => a.Phases?.CurrentPhase is TaxiingPhase, TaxiBudgetSeconds);

    private static double B738V1() => AircraftPerformance.DecisionSpeed("B738", AircraftCategorization.Categorize("B738"));

    /// <summary>A B738 on runway 30's threshold in <paramref name="phase"/>, at <paramref name="indicatedKts"/>.</summary>
    private static AircraftState OnRunway30(Phase phase, bool onGround, double indicatedKts)
    {
        RunwayInfo runway = Runway("30");
        var ac = new AircraftState
        {
            Callsign = "UAL100",
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = onGround ? runway.ElevationFt : runway.ElevationFt + 400,
            IndicatedAirspeed = indicatedKts,
            IsOnGround = onGround,
        };
        ac.FlightPlan.Departure = "OAK";
        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(phase);
        ac.Phases = phases;
        return ac;
    }

    private static ApproachClearance ClearanceFor28R(bool lateralInterceptOnly) =>
        new()
        {
            TransitionName = null,
            ApproachId = "I28R",
            AirportCode = "OAK",
            RunwayId = "28R",
            FinalApproachCourse = Runway("28R").TrueHeading,
            LateralInterceptOnly = lateralInterceptOnly,
        };

    /// <summary>The on-final fixture, cleared to land (without the clearance it goes around into the pattern).</summary>
    private static SimulationEngine ClearedToLand(ArtccConfigRoot zoa)
    {
        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.OnFinalAtOak, zoa, 7, []);
        CommandResult result = engine.SendCommand(AiTestFixture.Callsign, "CLAND");
        Assert.True(result.Success, result.Message);
        return engine;
    }

    /// <summary>The on-final fixture flown down to its first rollout second after a recorded touchdown.</summary>
    private Rollout? RollingOut()
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = ClearedToLand(_zoa);
        AircraftState ac = AiTestFixture.TickUntil(
            engine,
            AiTestFixture.Callsign,
            a => a.Phases?.CurrentPhase is LandingPhase { CurrentState: LandingPhase.State.Rollout, TouchdownGroundSpeedKts: not null },
            LandingBudgetSeconds
        );
        Assert.NotNull(ac.Phases!.AssignedRunway);
        return new Rollout(engine, ac, (LandingPhase)ac.Phases.CurrentPhase!);
    }

    private static AircraftState OnApproachPhase(Phase phase, RunwayInfo runway, LatLon position)
    {
        var ac = new AircraftState
        {
            Callsign = "UAL100",
            AircraftType = "B738",
            Position = position,
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = 1200,
            IndicatedAirspeed = 150,
            IsOnGround = false,
        };
        ac.FlightPlan.Destination = "OAK";
        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(phase);
        ac.Phases = phases;
        return ac;
    }

    private static AircraftState Reporting(string rules)
    {
        var ac = new AircraftState
        {
            Callsign = "UAL100",
            AircraftType = "B738",
            Position = new LatLon(37.75, -122.35),
            Altitude = 5000,
            IndicatedAirspeed = 210,
            IsOnGround = false,
        };
        ac.FlightPlan.FlightRules = rules;
        ac.FlightPlan.Destination = "OAK";
        return ac;
    }

    private static RunwayInfo Runway(string designator) =>
        NavigationDatabase.Instance.GetRunway("OAK", designator) ?? throw new InvalidOperationException($"OAK {designator} missing from NavData");

    private static AirportGroundLayout OakLayout() =>
        new TestAirportGroundData().GetLayout("OAK") ?? throw new InvalidOperationException("KOAK layout missing from TestData");
}
