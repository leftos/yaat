using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Fuzzy traffic-advisory matching: best-candidate-by-weighted-error within aviation-reviewed tolerance
/// gates, plus accuracy grading. See docs/solo-training-evaluation.md (Advisory matching) and FAA 7110.65
/// §2-1-21 / AIM §4-1-15.
/// </summary>
public sealed class TrafficAdvisoryMatcherTests
{
    public TrafficAdvisoryMatcherTests() => TestVnasData.EnsureInitialized();

    [Fact]
    public void UserScenario_OffByOneMileAndOneBucket_ResolvesIntendedTargetAsExact()
    {
        // The S2-OAK-3 case: controller calls "11 o'clock, 2 miles, southbound, Cessna, 2,000" for a
        // target actually at 11 o'clock, 3 NM, southbound, 2,100. A second southbound Cessna is also near.
        var recipient = Recipient(headingTrue: 150);
        var intended = TargetAt("N9225L", recipient, clock: 11, distanceNm: 3.0, trackTrue: 180, altitude: 2100);
        var other = TargetAt("N346G", recipient, clock: 12, distanceNm: 4.0, trackTrue: 180, altitude: 2500);

        var match = TrafficAdvisoryMatcher.ResolveStructuredTrafficTarget(
            recipient,
            new TrafficAdvisoryDetails(11, 2, "S", "C172", 2000),
            [recipient, intended, other],
            out string error
        );

        Assert.NotNull(match);
        Assert.Equal("N9225L", match.Target.Callsign);
        Assert.Equal(AdvisoryMatchQuality.Exact, match.Quality); // +/-1 NM and +/-100 ft are correct calls
        Assert.Equal("", error);
    }

    [Fact]
    public void ImpreciseCall_WithinGateButBeyondExact_GradesImprecise()
    {
        var recipient = Recipient(headingTrue: 150);
        var target = TargetAt("N9225L", recipient, clock: 9, distanceNm: 3.0, trackTrue: 180, altitude: 2100);

        // clock off 2 (9 vs 11), altitude off 300 ft (2100 vs 1800) — both within the gate, beyond Exact.
        var match = TrafficAdvisoryMatcher.ResolveStructuredTrafficTarget(
            recipient,
            new TrafficAdvisoryDetails(11, 2, "S", "C172", 1800),
            [recipient, target],
            out _
        );

        Assert.NotNull(match);
        Assert.Equal("N9225L", match.Target.Callsign);
        Assert.Equal(AdvisoryMatchQuality.Imprecise, match.Quality);
        Assert.Contains("clock off 2", match.ImpreciseDetail);
        Assert.Contains("altitude off 300", match.ImpreciseDetail);
    }

    [Fact]
    public void OutOfTolerance_NoCandidate_ReturnsNullWithReason()
    {
        var recipient = Recipient(headingTrue: 150);
        var target = TargetAt("N9225L", recipient, clock: 6, distanceNm: 10.0, trackTrue: 0, altitude: 6000);

        var match = TrafficAdvisoryMatcher.ResolveStructuredTrafficTarget(
            recipient,
            new TrafficAdvisoryDetails(11, 2, "S", "C172", 2000),
            [recipient, target],
            out string error
        );

        Assert.Null(match);
        Assert.Contains("no aircraft matches", error);
    }

    [Fact]
    public void OmittedAltitude_ExcludedFromMatchAndGrade()
    {
        var recipient = Recipient(headingTrue: 150);
        var target = TargetAt("N9225L", recipient, clock: 11, distanceNm: 2.0, trackTrue: 180, altitude: 7500);

        // No altitude stated (4-field form). Altitude must not gate or penalize even though it is way off.
        var match = TrafficAdvisoryMatcher.ResolveStructuredTrafficTarget(
            recipient,
            new TrafficAdvisoryDetails(11, 2, "S", "C172", null),
            [recipient, target],
            out _
        );

        Assert.NotNull(match);
        Assert.Equal("N9225L", match.Target.Callsign);
        Assert.Equal(AdvisoryMatchQuality.Exact, match.Quality);
    }

    [Fact]
    public void ManeuveringRecipient_WidensClockGate()
    {
        // Target at 8 o'clock; controller called 11 (3 sectors off). Beyond the stable gate (2) but within
        // the maneuvering gate (4). A circling recipient's instantaneous heading is unreliable (the user's
        // boundary-hold scenario), so a banked recipient still resolves the call.
        var stable = Recipient(headingTrue: 150, bankDeg: 0);
        var banked = Recipient(headingTrue: 150, bankDeg: 13);
        var targetForStable = TargetAt("N9225L", stable, clock: 8, distanceNm: 3.0, trackTrue: 180, altitude: 2100);
        var targetForBanked = TargetAt("N9225L", banked, clock: 8, distanceNm: 3.0, trackTrue: 180, altitude: 2100);
        var details = new TrafficAdvisoryDetails(11, 2, "S", "C172", 2000);

        Assert.Null(TrafficAdvisoryMatcher.ResolveStructuredTrafficTarget(stable, details, [stable, targetForStable], out _));
        Assert.NotNull(TrafficAdvisoryMatcher.ResolveStructuredTrafficTarget(banked, details, [banked, targetForBanked], out _));
    }

    // ── VFR relative-position form ──────────────────────────────────────────

    [Fact]
    public void Relative_NoseRight_ResolvesTargetInOctant()
    {
        var recipient = Recipient(headingTrue: 150);
        var noseRight = TargetAt("N111", recipient, clock: 1, distanceNm: 2.0, trackTrue: 180, altitude: 2000);
        var behind = TargetAt("N222", recipient, clock: 6, distanceNm: 2.0, trackTrue: 0, altitude: 2000);

        var match = TrafficAdvisoryMatcher.ResolveRelativeTrafficTarget(
            recipient,
            new TrafficRelativeDetails("NR", 2, "C172"),
            [recipient, noseRight, behind],
            out string error
        );

        Assert.NotNull(match);
        Assert.Equal("N111", match.Target.Callsign);
        Assert.Equal(AdvisoryMatchQuality.Exact, match.Quality);
        Assert.Equal("", error);
    }

    [Fact]
    public void Relative_OutsideOctantGate_ReturnsNull()
    {
        var recipient = Recipient(headingTrue: 150);
        var behind = TargetAt("N222", recipient, clock: 6, distanceNm: 2.0, trackTrue: 0, altitude: 2000);

        Assert.Null(
            TrafficAdvisoryMatcher.ResolveRelativeTrafficTarget(recipient, new TrafficRelativeDetails("NR", 2, "C172"), [recipient, behind], out _)
        );
    }

    // ── VFR pattern-leg form ────────────────────────────────────────────────

    [Fact]
    public void Pattern_RightBase_ResolvesCandidateOnThatLeg()
    {
        var runway = TestRunwayFactory.Make(designator: "28R", heading: 280);
        var recipient = Recipient(headingTrue: 280);
        var onBase = PatternCandidate("N111", runway, MakeBasePhase(runway, PatternDirection.Right), distanceNm: 2.0);
        var onFinal = PatternCandidate("N222", runway, new FinalApproachPhase(), distanceNm: 5.0);

        var match = TrafficAdvisoryMatcher.ResolvePatternTrafficTarget(
            recipient,
            new TrafficPatternDetails(PatternEntryLeg.Base, PatternDirection.Right, 2, "28R", "C172"),
            [recipient, onBase, onFinal],
            out string error
        );

        Assert.NotNull(match);
        Assert.Equal("N111", match.Target.Callsign);
        Assert.Equal(AdvisoryMatchQuality.Exact, match.Quality);
        Assert.Equal("", error);
    }

    [Fact]
    public void Pattern_SideMismatch_ReturnsNull()
    {
        var runway = TestRunwayFactory.Make(designator: "28R", heading: 280);
        var recipient = Recipient(headingTrue: 280);
        var onLeftBase = PatternCandidate("N333", runway, MakeBasePhase(runway, PatternDirection.Left), distanceNm: 2.0);

        Assert.Null(
            TrafficAdvisoryMatcher.ResolvePatternTrafficTarget(
                recipient,
                new TrafficPatternDetails(PatternEntryLeg.Base, PatternDirection.Right, 2, "28R", "C172"),
                [recipient, onLeftBase],
                out _
            )
        );
    }

    [Fact]
    public void Pattern_DistanceBeyondGate_ReturnsNull()
    {
        var runway = TestRunwayFactory.Make(designator: "28R", heading: 280);
        var recipient = Recipient(headingTrue: 280);
        var farBase = PatternCandidate("N444", runway, MakeBasePhase(runway, PatternDirection.Right), distanceNm: 6.0);

        Assert.Null(
            TrafficAdvisoryMatcher.ResolvePatternTrafficTarget(
                recipient,
                new TrafficPatternDetails(PatternEntryLeg.Base, PatternDirection.Right, 2, "28R", "C172"),
                [recipient, farBase],
                out _
            )
        );
    }

    // ── VFR landmark form ───────────────────────────────────────────────────

    [Fact]
    public void Landmark_NearestWithinRadius_ResolvesAsExact()
    {
        var recipient = Recipient(headingTrue: 150);
        var landmark = new LatLon(37.7516, -122.2005);
        var near = AircraftAt("N111", GeoMath.ProjectPoint(landmark, new TrueHeading(0), 0.5));
        var far = AircraftAt("N222", GeoMath.ProjectPoint(landmark, new TrueHeading(0), 5.0));

        var match = TrafficAdvisoryMatcher.ResolveLandmarkTrafficTarget(recipient, landmark, "C172", [recipient, near, far], out string error);

        Assert.NotNull(match);
        Assert.Equal("N111", match.Target.Callsign);
        Assert.Equal(AdvisoryMatchQuality.Exact, match.Quality);
        Assert.Equal("", error);
    }

    [Fact]
    public void Landmark_NoneWithinRadius_ReturnsNull()
    {
        var recipient = Recipient(headingTrue: 150);
        var landmark = new LatLon(37.7516, -122.2005);
        var far = AircraftAt("N222", GeoMath.ProjectPoint(landmark, new TrueHeading(0), 5.0));

        Assert.Null(TrafficAdvisoryMatcher.ResolveLandmarkTrafficTarget(recipient, landmark, "C172", [recipient, far], out _));
    }

    private static BasePhase MakeBasePhase(RunwayInfo runway, PatternDirection side) =>
        new() { Waypoints = PatternGeometry.Compute(runway, AircraftCategory.Piston, side, null, null, null) };

    private static AircraftState PatternCandidate(string callsign, RunwayInfo runway, Phase phase, double distanceNm)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        var ac = AircraftAt(callsign, GeoMath.ProjectPoint(threshold, new TrueHeading(60), distanceNm));
        ac.Phases = new PhaseList { AssignedRunway = runway };
        ac.Phases.Add(phase);
        return ac;
    }

    private static AircraftState AircraftAt(string callsign, LatLon position) =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            TrueHeading = new TrueHeading(100),
            TrueTrack = new TrueHeading(100),
            Altitude = 1500,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" },
        };

    private static AircraftState Recipient(double headingTrue, double bankDeg = 0) =>
        new()
        {
            Callsign = "N436MS",
            AircraftType = "C172",
            Position = new LatLon(37.75, -122.30),
            TrueHeading = new TrueHeading(headingTrue),
            TrueTrack = new TrueHeading(headingTrue),
            Altitude = 2500,
            IndicatedAirspeed = 90,
            BankAngle = bankDeg,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" },
        };

    private static AircraftState TargetAt(string callsign, AircraftState recipient, int clock, double distanceNm, double trackTrue, double altitude)
    {
        double relativeBearing = (clock % 12) * 30.0; // clock 12 -> straight ahead
        double trueBearing = (recipient.TrueHeading.Degrees + relativeBearing) % 360.0;
        var position = GeoMath.ProjectPoint(recipient.Position, new TrueHeading(trueBearing), distanceNm);
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = position,
            TrueHeading = new TrueHeading(trackTrue),
            TrueTrack = new TrueHeading(trackTrue),
            Altitude = altitude,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" },
        };
    }
}
