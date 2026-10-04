using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Situation;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

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
        var engine = new SimulationEngine(new TestAirportGroundData());
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
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
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

    /// <summary>A point <paramref name="alongNm"/> out the final and <paramref name="rightNm"/> right of the landing direction.</summary>
    private static LatLon OffFinal(RunwayInfo rwy, double alongNm, double rightNm) =>
        GeoMath.ProjectPoint(
            GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading.ToReciprocal(), alongNm),
            rwy.TrueHeading + 90.0,
            rightNm
        );
}
