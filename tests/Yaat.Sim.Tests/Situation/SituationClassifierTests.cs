using System.Reflection;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Situation;

namespace Yaat.Sim.Tests.Situation;

public sealed class SituationClassifierTests
{
    /// <summary>
    /// Every concrete phase and the situation it gives the representative aircraft of
    /// <see cref="Classify_EveryConcretePhase_MatchesTheExpectedMap"/> (airborne IFR, no destination, 100 NM out).
    /// </summary>
    private static readonly Dictionary<Type, AircraftSituation> ExpectedByPhase = new()
    {
        [typeof(AtParkingPhase)] = AircraftSituation.AtParking,
        [typeof(PushbackPhase)] = AircraftSituation.PushingBack,
        [typeof(HoldingInPositionPhase)] = AircraftSituation.HoldingOnGround,
        [typeof(HoldingAfterExitPhase)] = AircraftSituation.HoldingOnGround,
        [typeof(HoldingAfterPushbackPhase)] = AircraftSituation.HoldingOnGround,
        [typeof(TaxiingPhase)] = AircraftSituation.Taxiing,
        [typeof(FollowingPhase)] = AircraftSituation.Taxiing,
        [typeof(AirTaxiPhase)] = AircraftSituation.Taxiing,
        [typeof(CrossingRunwayPhase)] = AircraftSituation.Taxiing,
        [typeof(HoldingShortPhase)] = AircraftSituation.HoldingShort,
        [typeof(RunwayHoldingPhase)] = AircraftSituation.HoldingShort,
        [typeof(LineUpPhase)] = AircraftSituation.LinedUp,
        [typeof(LinedUpAndWaitingPhase)] = AircraftSituation.LinedUp,
        [typeof(TakeoffPhase)] = AircraftSituation.Departing,
        [typeof(HelicopterTakeoffPhase)] = AircraftSituation.Departing,
        [typeof(InitialClimbPhase)] = AircraftSituation.Departing,
        [typeof(DepartureProcedurePhase)] = AircraftSituation.Departing,
        [typeof(FinalApproachPhase)] = AircraftSituation.Final,
        [typeof(HelicopterApproachPhase)] = AircraftSituation.Final,
        [typeof(HelicopterLandingPhase)] = AircraftSituation.Final,
        [typeof(LandingPhase)] = AircraftSituation.RolloutExit,
        [typeof(RunwayExitPhase)] = AircraftSituation.RolloutExit,
        [typeof(ClearRunwayPhase)] = AircraftSituation.RolloutExit,
        [typeof(RejectedTakeoffPhase)] = AircraftSituation.RolloutExit,
        [typeof(GoAroundPhase)] = AircraftSituation.GoAround,
        [typeof(LowApproachPhase)] = AircraftSituation.GoAround,
        [typeof(ApproachNavigationPhase)] = AircraftSituation.Approach,
        [typeof(InterceptCoursePhase)] = AircraftSituation.Approach,
        [typeof(ProcedureTurnPhase)] = AircraftSituation.Approach,
        [typeof(HoldingPatternPhase)] = AircraftSituation.Holding,
        [typeof(VfrHoldPhase)] = AircraftSituation.Holding,
        [typeof(AirspaceBoundaryHoldPhase)] = AircraftSituation.Holding,
        [typeof(AerialRefuelingAnchorPhase)] = AircraftSituation.Holding,
        [typeof(UpwindPhase)] = AircraftSituation.Pattern,
        [typeof(CrosswindPhase)] = AircraftSituation.Pattern,
        [typeof(DownwindPhase)] = AircraftSituation.Pattern,
        [typeof(BasePhase)] = AircraftSituation.Pattern,
        [typeof(PatternEntryPhase)] = AircraftSituation.Pattern,
        [typeof(MidfieldCrossingPhase)] = AircraftSituation.Pattern,
        [typeof(TeardropReentryPhase)] = AircraftSituation.Pattern,
        [typeof(VfrFollowPhase)] = AircraftSituation.Pattern,
        [typeof(TouchAndGoPhase)] = AircraftSituation.Pattern,
        [typeof(StopAndGoPhase)] = AircraftSituation.Pattern,
        [typeof(PatternExitPhase)] = AircraftSituation.VfrDeparting,
        // Classified by the flight-rules predicates (a turn with nothing after it, or a military training route):
        // the representative aircraft is airborne IFR with no destination, so enroute.
        [typeof(MakeTurnPhase)] = AircraftSituation.IfrEnroute,
        [typeof(STurnPhase)] = AircraftSituation.IfrEnroute,
        [typeof(MilitaryRoutePhase)] = AircraftSituation.IfrEnroute,
    };

    public SituationClassifierTests() => TestVnasData.EnsureInitialized();

    // --- Phase-type situations ---

    [Theory]
    [InlineData("AtParking", true, AircraftSituation.AtParking)]
    [InlineData("Pushback", true, AircraftSituation.PushingBack)]
    [InlineData("HoldingInPosition", true, AircraftSituation.HoldingOnGround)]
    [InlineData("HoldingAfterExit", true, AircraftSituation.HoldingOnGround)]
    [InlineData("Taxiing", true, AircraftSituation.Taxiing)]
    [InlineData("HoldingShort", true, AircraftSituation.HoldingShort)]
    [InlineData("RunwayHolding", true, AircraftSituation.HoldingShort)]
    [InlineData("LinedUpAndWaiting", true, AircraftSituation.LinedUp)]
    [InlineData("Takeoff", true, AircraftSituation.LinedUp)]
    [InlineData("Takeoff", false, AircraftSituation.Departing)]
    [InlineData("InitialClimb", false, AircraftSituation.Departing)]
    [InlineData("ApproachNavigation", false, AircraftSituation.Approach)]
    [InlineData("HoldingPattern", false, AircraftSituation.Holding)]
    [InlineData("Downwind", false, AircraftSituation.Pattern)]
    [InlineData("FinalApproach", false, AircraftSituation.Final)]
    [InlineData("Landing", true, AircraftSituation.RolloutExit)]
    [InlineData("GoAround", false, AircraftSituation.GoAround)]
    [InlineData("PatternExit", false, AircraftSituation.VfrDeparting)]
    public void Classify_PhaseType_MapsToItsSituation(string phaseName, bool onGround, AircraftSituation expected)
    {
        AircraftState ac = OnPhase(BuildPhase(phaseName), onGround);

        Assert.Equal(expected, SituationClassifier.Classify(ac));
    }

    [Fact]
    public void Classify_HeldTaxi_StaysTaxiing()
    {
        AircraftState ac = OnPhase(new TaxiingPhase(), onGround: true);
        ac.Ground.Hold = HoldDirective.HoldPosition;

        Assert.Equal(AircraftSituation.Taxiing, SituationClassifier.Classify(ac));
    }

    [Fact]
    public void Classify_GroundedWithNoPhase_IsHoldingOnGround()
    {
        AircraftState ac = OnPhase(null, onGround: true);

        Assert.Equal(AircraftSituation.HoldingOnGround, SituationClassifier.Classify(ac));
    }

    [Fact]
    public void Classify_LiveTrafficShadow_WinsOverPhase()
    {
        AircraftState ac = OnPhase(new FinalApproachPhase(), onGround: false);
        ac.LiveTraffic = new AircraftLiveTraffic();

        Assert.Equal(AircraftSituation.LiveTraffic, SituationClassifier.Classify(ac));
    }

    // --- Turns take the phase they interrupt ---

    [Fact]
    public void Classify_360OnDownwind_IsPattern()
    {
        // The predicates alone would call this aircraft (VFR, 3 NM from its destination, closing) inbound.
        AircraftState ac = Airborne("VFR", "SAC", "OAK", Radial.Toward(180, 3));
        ac.Phases = PhasesOf(new MakeTurnPhase { Direction = TurnDirection.Left, TargetDegrees = 360 }, new DownwindPhase());

        Assert.Equal(AircraftSituation.Pattern, SituationClassifier.Classify(ac));
    }

    [Fact]
    public void Classify_STurnsOnFinal_IsFinal()
    {
        // The predicates alone would call this aircraft (IFR, 8 NM from its destination, closing) an arrival.
        AircraftState ac = Airborne("IFR", "LAX", "OAK", Radial.Toward(180, 8));
        ac.Phases = PhasesOf(new STurnPhase { InitialDirection = TurnDirection.Left }, new FinalApproachPhase());

        Assert.Equal(AircraftSituation.Final, SituationClassifier.Classify(ac));
    }

    [Fact]
    public void Classify_StandaloneTurn_FallsToFlightRules()
    {
        AircraftState ac = Airborne("VFR", "SAC", "OAK", Radial.Toward(180, 10));
        ac.Phases = PhasesOf(new MakeTurnPhase { Direction = TurnDirection.Left, TargetDegrees = 360 });

        Assert.Equal(AircraftSituation.VfrArrivalInbound, SituationClassifier.Classify(ac));
    }

    // --- IFR inbound predicates ---

    [Fact]
    public void Classify_IfrWithActiveStar_IsArrivalFarFromDestination()
    {
        AircraftState ac = Airborne("IFR", "LAX", "OAK", Radial.Toward(150, 200));
        ac.Procedure.ActiveStarId = "SERFR4";

        Assert.Equal(AircraftSituation.IfrArrival, SituationClassifier.Classify(ac));
    }

    [Fact]
    public void Classify_IfrWithDestinationRunway_IsArrival()
    {
        AircraftState ac = Airborne("IFR", "LAX", "OAK", Radial.Away(150, 200));
        ac.Procedure.DestinationRunway = "30";

        Assert.Equal(AircraftSituation.IfrArrival, SituationClassifier.Classify(ac));
    }

    [Theory]
    [InlineData(39.0, 0.0, AircraftSituation.IfrArrival)]
    [InlineData(41.0, 0.0, AircraftSituation.IfrEnroute)]
    [InlineData(20.0, 180.0, AircraftSituation.IfrEnroute)]
    [InlineData(30.0, 55.0, AircraftSituation.IfrArrival)]
    [InlineData(30.0, 65.0, AircraftSituation.IfrEnroute)]
    public void Classify_IfrByDistance_ArrivalOnlyInside40NmAndClosing(double distanceNm, double trackOffsetDeg, AircraftSituation expected)
    {
        AircraftState ac = Airborne("IFR", "LAX", "OAK", new Radial("OAK", 180, distanceNm, trackOffsetDeg));

        Assert.Equal(expected, SituationClassifier.Classify(ac));
    }

    [Theory]
    [InlineData(60.0, -1500.0, 9000.0, AircraftSituation.IfrArrival)]
    [InlineData(60.0, 0.0, 9000.0, AircraftSituation.IfrEnroute)]
    [InlineData(68.0, -1500.0, 9000.0, AircraftSituation.IfrArrival)]
    [InlineData(72.0, -1500.0, 9000.0, AircraftSituation.IfrEnroute)]
    [InlineData(60.0, -600.0, 9000.0, AircraftSituation.IfrArrival)]
    [InlineData(60.0, -400.0, 9000.0, AircraftSituation.IfrEnroute)]
    [InlineData(60.0, -1500.0, 1100.0, AircraftSituation.IfrArrival)]
    [InlineData(60.0, -1500.0, 900.0, AircraftSituation.IfrEnroute)]
    public void Classify_IfrClosingAndDescending_IsArrivalInsideDescentRange(
        double distanceNm,
        double verticalSpeedFpm,
        double altitudeToLoseFt,
        AircraftSituation expected
    )
    {
        // At 20,000 ft over OAK (elevation ~9 ft) the descent range is ~70 NM (3 NM per 1,000 ft + 10); every row is
        // beyond the 40 NM distance test.
        AircraftState ac = Airborne("IFR", "LAX", "OAK", Radial.Toward(180, distanceNm));
        ac.Altitude = 20000;
        ac.VerticalSpeed = verticalSpeedFpm;
        ac.Targets.TargetAltitude = 20000 - altitudeToLoseFt;

        Assert.Equal(expected, SituationClassifier.Classify(ac));
    }

    [Fact]
    public void Classify_DescentRange_CountsHeightAboveTheDestinationField()
    {
        double elevationFt =
            NavigationDatabase.Instance.GetAirportElevation("DEN") ?? throw new InvalidOperationException("DEN missing from NavData");
        Assert.True(elevationFt > 5000, $"DEN elevation {elevationFt} ft");
        double aboveFieldRangeNm = (3.0 * (20000 - elevationFt) / 1000.0) + 10.0;
        double mslRangeNm = (3.0 * 20.0) + 10.0;

        // Between the two ranges: inside the one an MSL altitude would give, outside the one above the field gives.
        AircraftState between = DescendingInto("DEN", (aboveFieldRangeNm + mslRangeNm) / 2.0);
        AircraftState inside = DescendingInto("DEN", aboveFieldRangeNm - 2.0);

        Assert.Equal(AircraftSituation.IfrEnroute, SituationClassifier.Classify(between));
        Assert.Equal(AircraftSituation.IfrArrival, SituationClassifier.Classify(inside));
    }

    [Fact]
    public void Classify_LocalIfrClosingInside40Nm_IsNotArrivalByDistance()
    {
        AircraftState ac = Airborne("IFR", "OAK", "OAK", Radial.Toward(180, 30));

        Assert.Equal(AircraftSituation.IfrEnroute, SituationClassifier.Classify(ac));
    }

    [Fact]
    public void Classify_LocalIfrClosingAndDescending_IsArrival()
    {
        AircraftState ac = Airborne("IFR", "OAK", "OAK", Radial.Toward(180, 30));
        ac.Altitude = 8000;
        ac.VerticalSpeed = -1000;
        ac.Targets.TargetAltitude = 3000;

        Assert.Equal(AircraftSituation.IfrArrival, SituationClassifier.Classify(ac));
    }

    [Fact]
    public void Classify_NoDestination_IsNeverArrivalByDistanceOrDescent()
    {
        AircraftState ac = Airborne("IFR", "LAX", "", Radial.Toward(180, 10));
        ac.VerticalSpeed = -1500;
        ac.Targets.TargetAltitude = 3000;

        Assert.Equal(AircraftSituation.IfrEnroute, SituationClassifier.Classify(ac));
    }

    [Fact]
    public void Classify_NoDestinationWithStar_IsArrival()
    {
        AircraftState ac = Airborne("IFR", "LAX", "", Radial.Toward(180, 10));
        ac.Procedure.ActiveStarId = "SERFR4";

        Assert.Equal(AircraftSituation.IfrArrival, SituationClassifier.Classify(ac));
    }

    // --- VFR predicates ---

    [Theory]
    [InlineData(19.0, AircraftSituation.VfrArrivalInbound)]
    [InlineData(21.0, AircraftSituation.VfrFlightFollowing)]
    public void Classify_VfrClosingOnDestination_IsInboundOnlyInside20Nm(double distanceNm, AircraftSituation expected)
    {
        AircraftState ac = Airborne("VFR", "SAC", "OAK", Radial.Toward(180, distanceNm));

        Assert.Equal(expected, SituationClassifier.Classify(ac));
    }

    [Theory]
    [InlineData(35.0, AircraftSituation.VfrFlightFollowing)]
    [InlineData(45.0, AircraftSituation.VfrArrivalInbound)]
    public void Classify_VfrClosingOnDestination_NeedsMoreThan40KtGroundSpeed(double indicatedAirspeedKts, AircraftSituation expected)
    {
        AircraftState ac = Airborne("VFR", "SAC", "OAK", Radial.Toward(180, 10));
        ac.Altitude = 1000;
        ac.IndicatedAirspeed = indicatedAirspeedKts;
        Assert.Equal(expected == AircraftSituation.VfrArrivalInbound, ac.GroundSpeed > 40.0);

        Assert.Equal(expected, SituationClassifier.Classify(ac));
    }

    [Theory]
    [InlineData(9.0, 180.0, AircraftSituation.VfrDeparting)]
    [InlineData(11.0, 180.0, AircraftSituation.VfrFlightFollowing)]
    [InlineData(5.0, 125.0, AircraftSituation.VfrDeparting)]
    [InlineData(5.0, 115.0, AircraftSituation.VfrFlightFollowing)]
    public void Classify_VfrHeadingAwayFromOrigin_IsDepartingInside10NmAnd120Deg(double distanceNm, double trackOffsetDeg, AircraftSituation expected)
    {
        // North-east of OAK bound for SAC, still more than 20 NM from SAC in every row.
        AircraftState ac = Airborne("VFR", "OAK", "SAC", new Radial("OAK", 45, distanceNm, trackOffsetDeg));

        Assert.Equal(expected, SituationClassifier.Classify(ac));
    }

    [Fact]
    public void Classify_VfrWithNoOrigin_IsFlightFollowing()
    {
        AircraftState ac = Airborne("VFR", "", "SAC", Radial.Away(45, 5));

        Assert.Equal(AircraftSituation.VfrFlightFollowing, SituationClassifier.Classify(ac));
    }

    // --- Every phase has a situation ---

    [Fact]
    public void Classify_EveryConcretePhase_MatchesTheExpectedMap()
    {
        List<Type> phaseTypes = [.. typeof(Phase).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Phase)) && !t.IsAbstract)];
        Assert.Equal(ExpectedByPhase.Keys.Select(t => t.Name).Order(), phaseTypes.Select(t => t.Name).Order());

        List<string> mismatches = [];
        foreach (Type type in phaseTypes)
        {
            AircraftState ac = Airborne("IFR", "LAX", "", Radial.Away(180, 100));
            ac.Phases = PhasesOf(ConstructWithDefaults(type));

            AircraftSituation actual = SituationClassifier.Classify(ac);
            if (actual != ExpectedByPhase[type])
            {
                mismatches.Add($"{type.Name}: expected {ExpectedByPhase[type]}, got {actual}");
            }
        }

        Assert.Empty(mismatches);

        AircraftState rolling = OnPhase(ConstructWithDefaults(typeof(TakeoffPhase)), onGround: true);
        Assert.Equal(AircraftSituation.LinedUp, SituationClassifier.Classify(rolling));
    }

    /// <summary>Builds a phase through its simplest public constructor, passing each parameter's default value.</summary>
    private static Phase ConstructWithDefaults(Type type)
    {
        ConstructorInfo ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        object?[] args = [.. ctor.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)];
        return (Phase)ctor.Invoke(args);
    }

    private static Phase BuildPhase(string name) =>
        name switch
        {
            "AtParking" => new AtParkingPhase(),
            "Pushback" => new PushbackPhase
            {
                Move = TugMove.Straight(PushbackLegKind.Push, TugMovePlanner.SimplePushbackFt("B738")),
                PlannedEnd = AirportPosition("OAK"),
                StartsAtStand = true,
                ContinuesIntoNextMove = false,
                ContinuesStandPushOff = false,
            },
            "HoldingInPosition" => new HoldingInPositionPhase(),
            "HoldingAfterExit" => new HoldingAfterExitPhase(),
            "Taxiing" => new TaxiingPhase(),
            "HoldingShort" => new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = 10,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = "30",
                }
            ),
            "RunwayHolding" => new RunwayHoldingPhase("28R"),
            "LinedUpAndWaiting" => new LinedUpAndWaitingPhase(),
            "Takeoff" => new TakeoffPhase(),
            "InitialClimb" => new InitialClimbPhase(),
            "ApproachNavigation" => new ApproachNavigationPhase { Fixes = [] },
            "HoldingPattern" => new HoldingPatternPhase
            {
                FixName = "SUNOL",
                FixLat = 37.6,
                FixLon = -121.9,
                InboundCourse = 270,
                LegLength = 1,
                IsMinuteBased = true,
                Direction = TurnDirection.Right,
            },
            "Downwind" => new DownwindPhase(),
            "FinalApproach" => new FinalApproachPhase(),
            "Landing" => new LandingPhase(),
            "GoAround" => new GoAroundPhase(),
            "PatternExit" => new PatternExitPhase(),
            _ => throw new ArgumentException($"No test phase named {name}", nameof(name)),
        };

    private static PhaseList PhasesOf(params Phase[] phases)
    {
        var list = new PhaseList();
        foreach (Phase phase in phases)
        {
            list.Add(phase);
        }

        return list;
    }

    private static AircraftState OnPhase(Phase? phase, bool onGround)
    {
        var ac = new AircraftState
        {
            Callsign = "UAL100",
            AircraftType = "B738",
            Position = AirportPosition("OAK"),
            IsOnGround = onGround,
            Altitude = onGround ? 9 : 3000,
        };
        if (phase is not null)
        {
            ac.Phases = PhasesOf(phase);
        }

        return ac;
    }

    /// <summary>An IFR arrival from LAX to <paramref name="airport"/>, closing from the south at 20,000 ft and descending to 11,000.</summary>
    private static AircraftState DescendingInto(string airport, double distanceNm)
    {
        AircraftState ac = Airborne("IFR", "LAX", airport, new Radial(airport, 180, distanceNm, 0));
        ac.Altitude = 20000;
        ac.VerticalSpeed = -1500;
        ac.Targets.TargetAltitude = 11000;
        return ac;
    }

    /// <summary>An airborne aircraft at <paramref name="at"/>, level at 10,000 ft and 250 kt.</summary>
    private static AircraftState Airborne(string rules, string departure, string destination, Radial at)
    {
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "B738",
            Position = GeoMath.ProjectPoint(AirportPosition(at.Airport), new TrueHeading(at.BearingFromAirportDeg), at.DistanceNm),
            TrueHeading = new TrueHeading(at.TrackDeg),
            TrueTrack = new TrueHeading(at.TrackDeg),
            Altitude = 10000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
        };
        ac.FlightPlan.FlightRules = rules;
        ac.FlightPlan.Departure = departure;
        ac.FlightPlan.Destination = destination;
        return ac;
    }

    private static LatLon AirportPosition(string airport)
    {
        (double lat, double lon) =
            NavigationDatabase.Instance.GetAirportPosition(airport) ?? throw new InvalidOperationException($"{airport} missing from NavData");
        return new LatLon(lat, lon);
    }

    /// <summary>
    /// A point on a radial from an airport, and the aircraft's track there as an offset from the bearing back to the
    /// airport: 0 tracks straight at it, 180 straight away.
    /// </summary>
    private readonly record struct Radial(string Airport, double BearingFromAirportDeg, double DistanceNm, double TrackOffsetDeg)
    {
        public double TrackDeg => (BearingFromAirportDeg + 180.0 + TrackOffsetDeg) % 360.0;

        public static Radial Toward(double bearingFromOakDeg, double distanceNm) => new("OAK", bearingFromOakDeg, distanceNm, 0);

        public static Radial Away(double bearingFromOakDeg, double distanceNm) => new("OAK", bearingFromOakDeg, distanceNm, 180);
    }
}
