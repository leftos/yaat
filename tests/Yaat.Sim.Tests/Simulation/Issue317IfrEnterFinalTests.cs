using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// GitHub issue #317: "IFR arrivals that are visual should be easy to change runway."
///
/// Most SFO arrivals are visuals, and moving one to the parallel runway used to mean
/// <c>CIFR</c> → <c>EF</c> → <c>CL</c>, because the dispatcher refused every VFR-only command
/// for an IFR aircraft. The restriction is now a client-side controller preference
/// (<see cref="VfrCommandsForIfr"/>), so the simulation accepts <c>EF</c> from an IFR arrival and
/// the aircraft keeps its IFR flight plan while doing it.
///
/// Uses real KOAK runway data via <see cref="TestVnasData"/> — no synthetic navdata.
/// </summary>
public class Issue317IfrEnterFinalTests(ITestOutputHelper output)
{
    /// <summary>Far enough out, and high enough, to clear the sidestep floor and the short-final guard.</summary>
    private const double FinalDistanceNm = 6.0;
    private const double ApproachAglFt = 2000.0;

    private static RunwayInfo? Runway(string designator)
    {
        TestVnasData.EnsureInitialized();
        return TestVnasData.NavigationDb?.GetRunway("KOAK", designator);
    }

    private AircraftState? BuildIfrArrival(RunwayInfo runway)
    {
        SimLogBuilder.CreateForTest(output).EnableCategory("PatternCommandHandler", LogLevel.Debug).InitializeSimLog();

        var (lat, lon) = GeoMath.ProjectPoint(
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            runway.TrueHeading.ToReciprocal(),
            FinalDistanceNm
        );

        var aircraft = new AircraftState
        {
            Callsign = "UAL238",
            AircraftType = "B738",
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "IFR",
                Destination = "KOAK",
                Altitude = PlannedAltitude.Ifr(35000),
            },
            Position = new LatLon(lat, lon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + ApproachAglFt,
            IndicatedAirspeed = 170,
            IsOnGround = false,
        };

        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(new FinalApproachPhase());
        phases.Start(
            new PhaseContext
            {
                Aircraft = aircraft,
                Targets = aircraft.Targets,
                Category = AircraftCategory.Jet,
                DeltaSeconds = 1.0,
                Runway = runway,
                FieldElevation = runway.ElevationFt,
                Logger = NullLogger.Instance,
            }
        );

        return aircraft;
    }

    /// <summary>
    /// The headline case: an IFR arrival on final for 28R is moved to the parallel 28L with a bare
    /// <c>EF 28L</c> — no <c>CIFR</c> first — and stays IFR while doing it.
    /// </summary>
    [Fact]
    public void IfrArrival_EnterFinalOnParallelRunway_AcceptedAndStaysIfr()
    {
        var rwy28R = Runway("28R");
        var rwy28L = Runway("28L");
        if (rwy28R is null || rwy28L is null)
        {
            return;
        }

        var aircraft = BuildIfrArrival(rwy28R);
        Assert.NotNull(aircraft);
        Assert.False(aircraft.FlightPlan.IsVfr, "Fixture invariant: the arrival is IFR.");

        var result = CommandDispatcher.Dispatch(new EnterFinalCommand("28L"), aircraft, TestDispatch.Context(new Random(0), validateDctFixes: false));

        output.WriteLine($"EF 28L -> Success={result.Success} Message='{result.Message}'");

        Assert.True(result.Success, $"EF must be accepted for an IFR arrival: '{result.Message}'");
        Assert.Equal("28L", aircraft.Phases?.AssignedRunway?.Designator);

        // The whole point of staying IFR: no implicit CIFR, and the filed altitude survives.
        Assert.False(aircraft.FlightPlan.IsVfr);
        Assert.Equal("IFR", aircraft.FlightPlan.FlightRules);
        Assert.Equal(35000, aircraft.FlightPlan.Altitude.CruiseFeet);
    }

    /// <summary>
    /// An IFR aircraft stays on an IFR flight plan until it lands or cancels (7110.65 §7-4-1), so it
    /// must not be left flying a final with no approach clearance. <c>EF</c> is the controller
    /// putting it on a visual, so a visual clearance for the new runway is granted the way
    /// <c>CVAF</c> grants one — the field-in-sight report is assumed, not required.
    /// </summary>
    [Fact]
    public void IfrArrival_EnterFinal_GrantsAVisualClearanceForTheNewRunway()
    {
        var rwy28R = Runway("28R");
        if (rwy28R is null)
        {
            return;
        }

        var aircraft = BuildIfrArrival(rwy28R);
        Assert.NotNull(aircraft);
        Assert.False(aircraft.Approach.HasReportedFieldInSight, "Fixture invariant: the pilot has not reported the field.");

        var result = CommandDispatcher.Dispatch(new EnterFinalCommand("28L"), aircraft, TestDispatch.Context(new Random(0), validateDctFixes: false));
        Assert.True(result.Success, $"EF must be accepted for an IFR arrival: '{result.Message}'");

        var clearance = aircraft.Phases?.ActiveApproach;
        Assert.NotNull(clearance);
        Assert.Equal("VIS28L", clearance.ApproachId);
        Assert.Equal("28L", clearance.RunwayId);
        Assert.Equal(rwy28R.AirportId, clearance.AirportCode);
        Assert.True(aircraft.Approach.HasReportedFieldInSight, "The visual clearance is granted CVAF-style, so the report is assumed.");
    }

    /// <summary>A VFR aircraft flies the pattern with no approach clearance — that concept is IFR-only.</summary>
    [Fact]
    public void VfrArrival_EnterFinal_GetsNoApproachClearance()
    {
        var rwy28R = Runway("28R");
        if (rwy28R is null)
        {
            return;
        }

        var aircraft = BuildIfrArrival(rwy28R);
        Assert.NotNull(aircraft);
        aircraft.FlightPlan.FlightRules = "VFR";
        aircraft.AircraftType = "C172";

        var result = CommandDispatcher.Dispatch(new EnterFinalCommand("28L"), aircraft, TestDispatch.Context(new Random(0), validateDctFixes: false));
        Assert.True(result.Success, $"EF must be accepted for a VFR arrival: '{result.Message}'");

        Assert.Null(aircraft.Phases?.ActiveApproach);
        Assert.False(aircraft.Approach.HasReportedFieldInSight);
    }

    /// <summary>
    /// The controller no longer has to cancel IFR first, and the old workaround must not be
    /// required: dispatch must never come back asking for <c>CIFR</c>.
    /// </summary>
    [Fact]
    public void IfrArrival_EnterFinal_NeverAsksForCifr()
    {
        var rwy28R = Runway("28R");
        if (rwy28R is null)
        {
            return;
        }

        var aircraft = BuildIfrArrival(rwy28R);
        Assert.NotNull(aircraft);

        var result = CommandDispatcher.Dispatch(new EnterFinalCommand("28L"), aircraft, TestDispatch.Context(new Random(0), validateDctFixes: false));

        if (!result.Success)
        {
            Assert.DoesNotContain("CIFR", result.Message!);
        }
    }

    /// <summary>
    /// The circuit-leg entries reach the same handler for an IFR aircraft — the dispatcher does not
    /// gate on flight rules at all. What keeps them off an IFR arrival by default is the client
    /// policy, asserted here so the two halves stay in step.
    /// </summary>
    [Fact]
    public void IfrArrival_CircuitLegEntry_NotGatedByDispatcherButBlockedByDefaultPolicy()
    {
        var rwy28R = Runway("28R");
        if (rwy28R is null)
        {
            return;
        }

        var aircraft = BuildIfrArrival(rwy28R);
        Assert.NotNull(aircraft);

        var eld = new EnterLeftDownwindCommand("28L");
        Assert.False(VfrCommandPolicy.AllowsForIfr(eld, VfrCommandsForIfr.EnterFinalOnly));
        Assert.True(VfrCommandPolicy.AllowsForIfr(eld, VfrCommandsForIfr.All));

        var result = CommandDispatcher.Dispatch(eld, aircraft, TestDispatch.Context(new Random(0), validateDctFixes: false));

        output.WriteLine($"ELD 28L -> Success={result.Success} Message='{result.Message}'");

        if (!result.Success)
        {
            Assert.DoesNotContain("CIFR", result.Message!);
        }
    }

    /// <summary>
    /// After the runway change the aircraft keeps flying: ticking forward must leave it airborne,
    /// still IFR, and still assigned the new runway rather than faulting or reverting.
    /// </summary>
    [Fact]
    public void IfrArrival_AfterEnterFinal_ContinuesTowardTheNewRunway()
    {
        var rwy28R = Runway("28R");
        var rwy28L = Runway("28L");
        if (rwy28R is null || rwy28L is null)
        {
            return;
        }

        var aircraft = BuildIfrArrival(rwy28R);
        Assert.NotNull(aircraft);

        var result = CommandDispatcher.Dispatch(new EnterFinalCommand("28L"), aircraft, TestDispatch.Context(new Random(0), validateDctFixes: false));
        Assert.True(result.Success, $"EF must be accepted for an IFR arrival: '{result.Message}'");

        double startDistanceNm = GeoMath.DistanceNm(aircraft.Position, new LatLon(rwy28L.ThresholdLatitude, rwy28L.ThresholdLongitude));

        // Mirror SimulationEngine.PreTick: rebuild the context each sub-tick and drive the phase
        // list through PhaseRunner, at the production 4-per-second cadence.
        const double dt = 0.25;
        for (int t = 0; t < 4 * 60; t++)
        {
            if (aircraft.Phases is null || aircraft.Phases.IsComplete)
            {
                break;
            }

            var ctx = new PhaseContext
            {
                Aircraft = aircraft,
                Targets = aircraft.Targets,
                Category = AircraftCategory.Jet,
                DeltaSeconds = dt,
                Runway = aircraft.Phases.AssignedRunway,
                FieldElevation = aircraft.Phases.AssignedRunway?.ElevationFt ?? 0,
                Logger = NullLogger.Instance,
            };
            PhaseRunner.Tick(aircraft, ctx);
            FlightPhysics.Update(aircraft, dt, null);
        }

        double endDistanceNm = GeoMath.DistanceNm(aircraft.Position, new LatLon(rwy28L.ThresholdLatitude, rwy28L.ThresholdLongitude));
        output.WriteLine(
            $"distance to 28L: {startDistanceNm:F2} nm -> {endDistanceNm:F2} nm, alt={aircraft.Altitude:F0}, rules={aircraft.FlightPlan.FlightRules}"
        );

        Assert.True(endDistanceNm < startDistanceNm, $"Aircraft should be closing on 28L ({startDistanceNm:F2} -> {endDistanceNm:F2} nm).");
        Assert.Equal("28L", aircraft.Phases?.AssignedRunway?.Designator);
        Assert.Equal("IFR", aircraft.FlightPlan.FlightRules);
    }
}
