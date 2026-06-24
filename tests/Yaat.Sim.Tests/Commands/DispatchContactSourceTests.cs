using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A successful command against a ground aircraft marks <see cref="AircraftState.HasMadeInitialContact"/>
/// (the pilot read back a clearance the controller spoke), which suppresses the redundant
/// post-takeoff airborne check-in. A scenario-scripted preset is NOT the controller, so a scripted
/// ground clearance must NOT mark contact — otherwise a runway-spawn CTO-preset departure handed to
/// the student via auto-track never makes its check-in call.
/// Regression: "Z | S3-NCTA-1 | Area A Familiarization" bug bundle (QXE2274 departing KSJC).
/// </summary>
public class DispatchContactSourceTests
{
    public DispatchContactSourceTests() => TestVnasData.EnsureInitialized();

    private static RunwayInfo Oak28R() =>
        TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: 280,
            elevationFt: 9
        );

    private static AircraftState MakeLinedUpIfrDeparture(RunwayInfo runway)
    {
        var ac = new AircraftState
        {
            Callsign = "AAL123",
            AircraftType = "B738",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                CruiseAltitude = 5000,
                FlightRules = "IFR",
                HasFlightPlan = true,
            },
        };

        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(new LinedUpAndWaitingPhase());
        phases.Add(new TakeoffPhase());
        phases.Add(new InitialClimbPhase { Departure = new DefaultDeparture(), CruiseAltitude = 5000 });

        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        return ac;
    }

    private static CompoundCommand Cto()
    {
        var parsed = CommandParser.ParseCompound("CTO");
        Assert.True(parsed.IsSuccess, $"Parse failed: {parsed.Reason}");
        return parsed.Value!;
    }

    [Fact]
    public void ScriptedPresetCto_OnGround_DoesNotMarkInitialContact()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var ac = MakeLinedUpIfrDeparture(Oak28R());

        var result = CommandDispatcher.DispatchCompound(Cto(), ac, TestDispatch.Context(new SerializableRandom(42), isScenarioScripted: true));

        Assert.True(result.Success, $"Dispatch failed: {result.Message}");
        Assert.False(ac.HasMadeInitialContact, "A scripted preset clearance must not establish student contact.");
    }

    [Fact]
    public void LiveControllerCto_OnGround_MarksInitialContact()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var ac = MakeLinedUpIfrDeparture(Oak28R());

        var result = CommandDispatcher.DispatchCompound(Cto(), ac, TestDispatch.Context(new SerializableRandom(42), isScenarioScripted: false));

        Assert.True(result.Success, $"Dispatch failed: {result.Message}");
        Assert.True(
            ac.HasMadeInitialContact,
            "A live controller clearance to a ground aircraft establishes contact (no redundant airborne check-in)."
        );
    }

    [Fact]
    public void ScriptedPresetWaitCto_DefersAsScripted_DoesNotMarkInitialContactOnReFire()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var ac = MakeLinedUpIfrDeparture(Oak28R());

        // Leading WAIT defers the CTO payload; the deferral must inherit the scripted origin.
        var waitCto = CommandParser.ParseCompound("WAIT 5; CTO").Value!;
        var deferResult = CommandDispatcher.DispatchCompound(waitCto, ac, TestDispatch.Context(new SerializableRandom(42), isScenarioScripted: true));

        Assert.True(deferResult.Success, $"Defer failed: {deferResult.Message}");
        Assert.False(ac.HasMadeInitialContact, "Nothing has executed yet — only the WAIT deferred.");
        var deferred = Assert.Single(ac.DeferredDispatches);
        Assert.True(deferred.IsScenarioScripted, "A preset WAIT deferral must carry the scripted origin.");

        // Re-fire the payload exactly as ProcessDeferredDispatches does (scripted inherited, conditionals preserved).
        var reFire = CommandDispatcher.DispatchCompound(
            deferred.Payload,
            ac,
            TestDispatch.Context(new SerializableRandom(42), isScenarioScripted: deferred.IsScenarioScripted, preserveConditionals: true)
        );

        Assert.True(reFire.Success, $"Re-fire failed: {reFire.Message}");
        Assert.False(ac.HasMadeInitialContact, "A scripted preset payload firing on the ground must not establish student contact.");
    }

    [Fact]
    public void LiveWaitCto_DefersAsNonScripted_MarksInitialContactOnReFire()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var ac = MakeLinedUpIfrDeparture(Oak28R());

        var waitCto = CommandParser.ParseCompound("WAIT 5; CTO").Value!;
        CommandDispatcher.DispatchCompound(waitCto, ac, TestDispatch.Context(new SerializableRandom(42), isScenarioScripted: false));

        var deferred = Assert.Single(ac.DeferredDispatches);
        Assert.False(deferred.IsScenarioScripted, "A live controller WAIT deferral stays non-scripted.");

        var reFire = CommandDispatcher.DispatchCompound(
            deferred.Payload,
            ac,
            TestDispatch.Context(new SerializableRandom(42), isScenarioScripted: deferred.IsScenarioScripted, preserveConditionals: true)
        );

        Assert.True(reFire.Success, $"Re-fire failed: {reFire.Message}");
        Assert.True(ac.HasMadeInitialContact, "A live controller payload firing on the ground establishes contact.");
    }

    [Fact]
    public void DeferredDispatch_IsScenarioScripted_SurvivesSnapshotRoundTrip()
    {
        var payload = CommandParser.ParseCompound("FH 270").Value!;
        var deferred = new DeferredDispatch(5, payload) { SourceText = "WAIT 5; FH 270", IsScenarioScripted = true };

        var restored = DeferredDispatch.FromSnapshot(deferred.ToSnapshot());

        Assert.NotNull(restored);
        Assert.True(restored!.IsScenarioScripted);
    }

    /// <summary>
    /// End-to-end outcome: a scripted CTO leaves <see cref="AircraftState.HasMadeInitialContact"/>
    /// false, so once the departure is airborne in solo mode the proactive airborne check-in fires.
    /// </summary>
    [Fact]
    public void ScriptedCtoDeparture_FiresAirborneCheckIn_WhenAirborneInSolo()
    {
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var runway = Oak28R();
        var ac = MakeLinedUpIfrDeparture(runway);

        var result = CommandDispatcher.DispatchCompound(Cto(), ac, TestDispatch.Context(new SerializableRandom(42), isScenarioScripted: true));
        Assert.True(result.Success, $"Dispatch failed: {result.Message}");
        Assert.False(ac.HasMadeInitialContact);

        // Simulate the departure climbing out.
        ac.IsOnGround = false;
        ac.Altitude = 2500;

        var scenario = new SimScenarioState
        {
            ScenarioId = "test",
            ScenarioName = "test",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            SoloTrainingMode = true,
            StudentPositionType = "APP",
        };
        var airportPos = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);

        PilotProactive.TickAirborneCheckIn(ac, scenario, _ => airportPos);

        Assert.NotEmpty(ac.PendingPilotTransmissions);
        Assert.True(ac.HasMadeInitialContact, "The airborne check-in marks contact once it has been spoken.");
    }
}
