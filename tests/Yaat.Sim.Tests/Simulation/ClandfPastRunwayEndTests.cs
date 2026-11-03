using System;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// CLANDF on SFO 28L, real navdata and layout. An aircraft that has overflown the runway on its approach — on final, in
/// its landing phase, or in the go-around CLANDF itself reverses — is refused past the far end ("unable, past the end
/// of runway 28L") and nothing about it changes; a pattern leg past the end is not refused. The forced-landing override
/// belongs to the approach: an airborne phase clear lifts it, while a completion into the landing phase keeps it.
/// </summary>
public sealed class ClandfPastRunwayEndTests(ITestOutputHelper output)
{
    private const string Callsign = "TSTAC";
    private const string GroundDataWhy = "CLANDF past the runway end and the phase clear that follows it";

    private static (RunwayInfo Runway, AirportGroundLayout Layout, LatLon Threshold, double LandingDistanceFt) Sfo28L()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("SFO", "28L");
        Assert.NotNull(runway);
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        Assert.NotNull(layout);
        return (runway, layout, LandingThreshold.Resolve(runway, layout), ForcedLandingProfile.LandingDistanceFt(runway, layout));
    }

    /// <summary>
    /// A CRJ7 on the 28L centerline <paramref name="alongFt"/> ft past the landing threshold at 500 ft AGL, on an active
    /// final approach for the runway with its landing still pending, positioned at or a hundredth of a foot past that
    /// along-track distance (the project/measure pair is flat-earth, so the placement iterates its own rounding out and
    /// never lands short of the request).
    /// </summary>
    private static AircraftState OnFinalAt(RunwayInfo runway, AirportGroundLayout layout, LatLon threshold, double alongFt)
    {
        double offsetFt = alongFt;
        for (int i = 0; i < 6; i++)
        {
            double measuredFt = ForcedLandingProfile.AlongTrackFt(
                GeoMath.ProjectPoint(threshold, runway.TrueHeading, offsetFt / GeoMath.FeetPerNm),
                threshold,
                runway.TrueHeading
            );
            double deficitFt = alongFt - measuredFt;
            if (Math.Abs(deficitFt) < 0.001)
            {
                break;
            }

            offsetFt += deficitFt;
        }

        LatLon position = GeoMath.ProjectPoint(threshold, runway.TrueHeading, (offsetFt + 0.01) / GeoMath.FeetPerNm);
        Assert.InRange(ForcedLandingProfile.AlongTrackFt(position, threshold, runway.TrueHeading), alongFt, alongFt + 0.05);

        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "CRJ7",
            Position = position,
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt + 500,
            IndicatedAirspeed = 150,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
            Phases = new PhaseList { AssignedRunway = runway },
        };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        return aircraft;
    }

    /// <summary>An aircraft past the end whose current phase is the travel downwind, with its landing still to come.</summary>
    private static AircraftState OnDownwindAt(RunwayInfo runway, AirportGroundLayout layout, LatLon threshold, double alongFt)
    {
        AircraftState aircraft = OnFinalAt(runway, layout, threshold, alongFt);
        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(new DownwindPhase());
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        return aircraft;
    }

    /// <summary>The aircraft in a one-aircraft world, so a tick runs the production phase loop over it.</summary>
    private static SimulationEngine Ticking(AircraftState aircraft, AirportGroundLayout layout)
    {
        var engine = new SimulationEngine(RecordingGroundData.Evergreen(GroundDataWhy), PushPlanStore.CreateShipped());
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-sfo-clandf",
            ScenarioName = "SFO CLANDF Test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
        };
        return engine;
    }

    /// <summary>The position note each case-table row prints, so a failure names where the aircraft sat.</summary>
    private void NoteAlong(AircraftState aircraft, RunwayInfo runway, LatLon threshold, double lengthFt)
    {
        double alongFt = ForcedLandingProfile.AlongTrackFt(aircraft.Position, threshold, runway.TrueHeading);
        output.WriteLine($"runway {lengthFt:F0} ft, aircraft {alongFt:F2} ft along");
    }

    private static CommandResult SendClandf(AircraftState aircraft, AirportGroundLayout layout) =>
        PatternCommandHandler.TryForceLanding(aircraft, TestDispatch.Context(new Random(1), groundLayout: layout));

    /// <summary>A controller command through the production dispatcher, the route a user command takes.</summary>
    private static CommandResult Dispatch(AircraftState aircraft, AirportGroundLayout layout, string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return CommandDispatcher.DispatchCompound(parsed.Value!, aircraft, TestDispatch.Context(new Random(2), groundLayout: layout));
    }

    [Fact]
    public void Clandf_AtOrWithinAHairPastRunwayEnd_Refused()
    {
        (RunwayInfo runway, AirportGroundLayout layout, LatLon threshold, double lengthFt) = Sfo28L();
        AircraftState aircraft = OnFinalAt(runway, layout, threshold, lengthFt);
        NoteAlong(aircraft, runway, threshold, lengthFt);

        CommandResult result = SendClandf(aircraft, layout);

        Assert.False(result.Success);
        Assert.Equal("Unable, past the end of runway 28L", result.Message);
        Assert.False(aircraft.Phases!.ForceLanding);
    }

    [Fact]
    public void Clandf_PastRunwayEnd_RefusedAndChangesNothing()
    {
        (RunwayInfo runway, AirportGroundLayout layout, LatLon threshold, double lengthFt) = Sfo28L();
        AircraftState aircraft = OnFinalAt(runway, layout, threshold, lengthFt + 500);
        Phase? currentPhase = aircraft.Phases!.CurrentPhase;
        int phaseCount = aircraft.Phases.Phases.Count;
        NoteAlong(aircraft, runway, threshold, lengthFt);

        CommandResult result = SendClandf(aircraft, layout);

        Assert.False(result.Success);
        Assert.Equal("Unable, past the end of runway 28L", result.Message);
        Assert.False(aircraft.Phases.ForceLanding);
        Assert.Null(aircraft.Phases.LandingClearance);
        Assert.Null(aircraft.Phases.ClearedRunwayId);
        Assert.Same(currentPhase, aircraft.Phases.CurrentPhase);
        Assert.Equal(phaseCount, aircraft.Phases.Phases.Count);
        Assert.Null(aircraft.Targets.DesiredDecelRate);
    }

    /// <summary>Five feet inside the far end, an aircraft that has overflown nothing yet still gets its forced landing.</summary>
    [Fact]
    public void Clandf_JustInsideRunwayEnd_Accepted()
    {
        (RunwayInfo runway, AirportGroundLayout layout, LatLon threshold, double lengthFt) = Sfo28L();
        AircraftState aircraft = OnFinalAt(runway, layout, threshold, lengthFt - 1);
        NoteAlong(aircraft, runway, threshold, lengthFt);

        CommandResult result = SendClandf(aircraft, layout);

        Assert.True(result.Success, result.Message);
        Assert.True(aircraft.Phases!.ForceLanding);
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);
        Assert.Equal("28L", aircraft.Phases.ClearedRunwayId);
    }

    /// <summary>
    /// A pattern leg past the end with its landing still to come is not "past the end" of anything: it has not overflown
    /// the runway on an approach, and it keeps its forced landing.
    /// </summary>
    [Fact]
    public void Clandf_PastRunwayEndOnDownwind_NotRefused()
    {
        (RunwayInfo runway, AirportGroundLayout layout, LatLon threshold, double lengthFt) = Sfo28L();
        AircraftState aircraft = OnDownwindAt(runway, layout, threshold, lengthFt + 500);
        NoteAlong(aircraft, runway, threshold, lengthFt);

        CommandResult result = SendClandf(aircraft, layout);

        Assert.True(result.Success, result.Message);
        Assert.True(aircraft.Phases!.ForceLanding);
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);
    }

    /// <summary>Base leg with the final still pending: the approach has not begun, so it is not refused.</summary>
    [Fact]
    public void Clandf_PastRunwayEndWithFinalStillPending_NotRefused()
    {
        (RunwayInfo runway, AirportGroundLayout layout, LatLon threshold, double lengthFt) = Sfo28L();
        AircraftState aircraft = OnFinalAt(runway, layout, threshold, lengthFt + 500);
        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(new BasePhase());
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        Assert.IsType<BasePhase>(aircraft.Phases.CurrentPhase);

        CommandResult result = SendClandf(aircraft, layout);

        Assert.True(result.Success, result.Message);
        Assert.True(aircraft.Phases.ForceLanding);
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);
    }

    /// <summary>
    /// CLANDF reverses a go-around by putting the aircraft straight onto a final behind it, so a go-around already past
    /// the far end is past the end too: refused, and nothing about the go-around changes.
    /// </summary>
    [Fact]
    public void Clandf_PastRunwayEndInGoAround_Refused()
    {
        (RunwayInfo runway, AirportGroundLayout layout, LatLon threshold, double lengthFt) = Sfo28L();
        AircraftState aircraft = OnFinalAt(runway, layout, threshold, lengthFt + 500);
        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(new GoAroundPhase { ReenterPattern = true, NextLandingFullStop = false });
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        Assert.IsType<GoAroundPhase>(aircraft.Phases.CurrentPhase);

        CommandResult result = SendClandf(aircraft, layout);

        Assert.False(result.Success);
        Assert.Equal("Unable, past the end of runway 28L", result.Message);
        Assert.False(aircraft.Phases.ForceLanding);
        Assert.Null(aircraft.Phases.LandingClearance);
        Assert.IsType<GoAroundPhase>(aircraft.Phases.CurrentPhase);
    }

    /// <summary>
    /// A final approach that completes normally hands the aircraft to its landing phase with the override intact: the
    /// forced landing is what is being flown, and clearing it on the handoff would let the landing go around.
    /// </summary>
    [Fact]
    public void Clandf_CompletingIntoLanding_KeepsForceLanding()
    {
        (RunwayInfo runway, AirportGroundLayout layout, LatLon threshold, _) = Sfo28L();
        AircraftState aircraft = OnFinalAt(runway, layout, threshold, -3.0 * GeoMath.FeetPerNm);

        CommandResult forced = SendClandf(aircraft, layout);
        Assert.True(forced.Success, forced.Message);

        aircraft.Phases!.AdvanceToNext(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));

        Assert.IsType<LandingPhase>(aircraft.Phases.CurrentPhase);
        Assert.True(aircraft.Phases.ForceLanding);
    }

    /// <summary>
    /// A heading command takes the aircraft off the approach. The phase chain clears, and the CLANDF forced-landing
    /// override and its firm airborne deceleration go with it — otherwise the vectored aircraft keeps flying a forced
    /// landing it was never vectored for.
    /// </summary>
    [Fact]
    public void HeadingVectoredOffFinal_ClearsForceLanding()
    {
        (RunwayInfo runway, AirportGroundLayout layout, LatLon threshold, _) = Sfo28L();
        AircraftState aircraft = OnFinalAt(runway, layout, threshold, -3.0 * GeoMath.FeetPerNm);
        SimulationEngine engine = Ticking(aircraft, layout);

        CommandResult forced = SendClandf(aircraft, layout);
        Assert.True(forced.Success, forced.Message);
        engine.TickOneSecond();
        Assert.Equal(ForcedLandingProfile.AirborneDecelKtsPerSec, aircraft.Targets.DesiredDecelRate);

        CommandResult result = Dispatch(aircraft, layout, "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.False(aircraft.Phases?.ForceLanding == true);
        Assert.Null(aircraft.Targets.DesiredDecelRate);
    }

    /// <summary>
    /// The same clear from a pattern leg: CLANDF is accepted on the downwind, and the heading command that clears the
    /// leg takes the override with the chain.
    /// </summary>
    [Fact]
    public void HeadingVectoredOffDownwind_ClearsForceLanding()
    {
        (RunwayInfo runway, AirportGroundLayout layout, LatLon threshold, _) = Sfo28L();
        AircraftState aircraft = OnDownwindAt(runway, layout, threshold, -3.0 * GeoMath.FeetPerNm);
        Assert.IsType<DownwindPhase>(aircraft.Phases!.CurrentPhase);

        CommandResult forced = SendClandf(aircraft, layout);
        Assert.True(forced.Success, forced.Message);
        Assert.True(aircraft.Phases.ForceLanding);

        CommandResult result = Dispatch(aircraft, layout, "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.False(aircraft.Phases?.ForceLanding == true);
        Assert.Null(aircraft.Targets.DesiredDecelRate);
    }
}
