using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
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
