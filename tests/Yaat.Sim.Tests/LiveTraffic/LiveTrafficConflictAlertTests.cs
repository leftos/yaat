using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.LiveTraffic;

/// <summary>
/// Conflict-alert policy for live-traffic shadows: never shadow↔shadow; shadow↔simulated only when the shadow is
/// IFR, not coasting, and not in an approach corridor; <c>CASUP</c> suppresses one pair from either side.
/// </summary>
public class LiveTrafficConflictAlertTests
{
    private static readonly LatLon A = new(37.80, -122.00);
    private static readonly LatLon B = new(37.81, -122.00);

    public LiveTrafficConflictAlertTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState Simulated(string callsign, LatLon pos) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = pos,
            Altitude = 4_500,
            IndicatedAirspeed = 250,
            TrueHeading = new TrueHeading(0),
            TrueTrack = new TrueHeading(0),
            Transponder = new AircraftTransponder
            {
                Code = 4521,
                AssignedCode = 4521,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan { HasFlightPlan = true, FlightRules = "IFR" },
            Track = new AircraftTrack(),
        };

    private static AircraftState Shadow(string callsign, LatLon pos, bool ifr) =>
        LiveTrafficKinematics.CreateShadow(
            callsign,
            "B738",
            new LiveTrafficSample(0, pos.Lat, pos.Lon, 4_500, 240, 0, 0, LiveTrafficSource.Stars, ifr ? 4522u : 1200u),
            ifr
                ? new AircraftFlightPlan { HasFlightPlan = true, FlightRules = "IFR" }
                : new AircraftFlightPlan { HasFlightPlan = false, FlightRules = "VFR" }
        );

    private static List<ConflictAlertDetector.ConflictPair> Detect(params AircraftState[] aircraft) =>
        ConflictAlertDetector.Detect([.. aircraft], new ConflictAlertContext([], []));

    [Fact]
    public void TwoShadows_NeverAlert()
    {
        Assert.Empty(Detect(Shadow("LIVE1", A, ifr: true), Shadow("LIVE2", B, ifr: true)));
        Assert.Empty(EramConflictDetector.Detect([Shadow("LIVE1", A, ifr: true), Shadow("LIVE2", B, ifr: true)], new HashSet<string>()));
    }

    [Fact]
    public void IfrShadowAndSimulated_Alert()
    {
        var pair = Assert.Single(Detect(Shadow("LIVE1", B, ifr: true), Simulated("SIM1", A)));
        Assert.Equal("LIVE1", pair.CallsignA);
    }

    [Fact]
    public void VfrShadow_DoesNotAlert()
    {
        Assert.Empty(Detect(Shadow("LIVE1", B, ifr: false), Simulated("SIM1", A)));
    }

    [Fact]
    public void CoastingShadow_DoesNotAlert()
    {
        var shadow = Shadow("LIVE1", B, ifr: true);
        shadow.LiveTraffic!.IsCoasting = true;

        Assert.Empty(Detect(shadow, Simulated("SIM1", A)));
    }

    [Fact]
    public void ShadowWithAnOldObservation_DoesNotAlert()
    {
        // An en-route observation delivered ~50 s behind: the projection error rivals the separation standard.
        var shadow = Shadow("LIVE1", B, ifr: true);
        shadow.LiveTraffic!.SecondsSinceSample = ConflictAlertDetector.ShadowCaMaxSampleAgeSeconds + 1;

        Assert.Empty(Detect(shadow, Simulated("SIM1", A)));
    }

    [Fact]
    public void Casup_SuppressesThePair_FromEitherSide_AndToggles()
    {
        var shadow = Shadow("LIVE1", B, ifr: true);
        var sim = Simulated("SIM1", A);
        var scenario = new SimScenarioState
        {
            ScenarioId = "t",
            ScenarioName = "t",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
        };

        var parsed = CommandParser.Parse("CASUP LIVE1");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var result = TrackEngine.Dispatch(parsed.Value!, sim, identity: null, scenario, redirect: null);
        Assert.True(result!.Success, result.Message);
        Assert.Empty(Detect(shadow, sim));
        Assert.Empty(EramConflictDetector.Detect([shadow, sim], new HashSet<string>()));

        var restored = AircraftState.FromSnapshot(sim.ToSnapshot(), null);
        Assert.Equal(["LIVE1"], restored.Stars.CaSuppressedWith);

        TrackEngine.Dispatch(parsed.Value!, sim, identity: null, scenario, redirect: null);
        Assert.Empty(sim.Stars.CaSuppressedWith);
        Assert.Single(Detect(shadow, sim));

        TrackEngine.Dispatch(new SuppressConflictAlertCommand("SIM1"), shadow, identity: null, scenario, redirect: null);
        Assert.Empty(Detect(shadow, sim));
    }

    [Fact]
    public void Casup_OnItself_IsRejected()
    {
        var sim = Simulated("SIM1", B);
        var scenario = new SimScenarioState
        {
            ScenarioId = "t",
            ScenarioName = "t",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
        };

        var result = TrackEngine.Dispatch(new SuppressConflictAlertCommand("SIM1"), sim, identity: null, scenario, redirect: null);

        Assert.False(result!.Success);
    }

    [Fact]
    public void Casup_IsAllowedOnAShadow_ThroughTheTrackPath()
    {
        Assert.True(TrackEngine.IsTrackCommand(new SuppressConflictAlertCommand("X")));
        Assert.False(
            CommandDispatcher
                .Dispatch(new SuppressConflictAlertCommand("X"), Shadow("LIVE1", A, ifr: true), TestDispatch.Context(new Random(1)))
                .Success
        );
    }
}
