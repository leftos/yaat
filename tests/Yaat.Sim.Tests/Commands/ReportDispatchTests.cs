using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Dispatch contract for the <c>REPORT</c> verb (GitHub issue #211): arming sets the per-aircraft
/// flags, the reject cases fail with a clear message, cancel clears (all or one leg), and the
/// command is phase-transparent (does not wipe an active pattern phase).
/// </summary>
[Collection("NavDbMutator")]
public class ReportDispatchTests
{
    public ReportDispatchTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void ArmPatternLeg_SetsFlag_AndKeepsPhase()
    {
        AircraftState ac = MakeAircraft();
        var downwind = new DownwindPhase();
        ac.Phases = new PhaseList();
        ac.Phases.Add(downwind);

        CommandResult result = CommandDispatcher.Dispatch(new ReportCommand(ReportTrigger.Base), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(ac.Approach.ReportArmedBase);
        Assert.Contains("turning base", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(downwind, ac.Phases.CurrentPhase);
    }

    [Fact]
    public void ArmPatternLeg_Rejected_WhenNotInPattern()
    {
        AircraftState ac = MakeAircraft();

        CommandResult result = CommandDispatcher.Dispatch(new ReportCommand(ReportTrigger.Base), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("not in the pattern", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ac.Approach.ReportArmedBase);
    }

    [Fact]
    public void ArmMileFinal_SetsTarget_WhenRunwayAssigned()
    {
        AircraftState ac = MakeAircraft();
        ac.Phases = new PhaseList { AssignedRunway = MakeRunway() };

        CommandResult result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.MileFinal, DistanceNm: 5),
            ac,
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(result.Success);
        Assert.Equal(5, ac.Approach.ReportFinalMileTarget);
    }

    [Fact]
    public void ArmMileFinal_Rejected_WhenNoRunway()
    {
        AircraftState ac = MakeAircraft();

        CommandResult result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.MileFinal, DistanceNm: 5),
            ac,
            TestDispatch.Context(Random.Shared)
        );

        Assert.False(result.Success);
        Assert.Contains("no runway", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ac.Approach.ReportFinalMileTarget);
    }

    [Fact]
    public void ArmAtFix_Rejected_WhenFixUnknown()
    {
        AircraftState ac = MakeAircraft();

        CommandResult result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.AtFix, FixName: "QXQXQ"),
            ac,
            TestDispatch.Context(Random.Shared)
        );

        Assert.False(result.Success);
        Assert.Contains("nav database", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ac.Approach.ReportAtFixName);
    }

    [Fact]
    public void ArmAtFix_ResolvesCoordinates_ForKnownFix()
    {
        if (NavigationDatabase.Instance.GetFixPosition("SUNOL") is null)
        {
            return; // nav data unavailable — silent skip
        }

        AircraftState ac = MakeAircraft();
        CommandResult result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.AtFix, FixName: "SUNOL"),
            ac,
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(result.Success);
        Assert.Equal("SUNOL", ac.Approach.ReportAtFixName);
        Assert.NotNull(ac.Approach.ReportAtFixLat);
        Assert.NotNull(ac.Approach.ReportAtFixLon);
    }

    [Fact]
    public void CancelAll_ClearsEveryArm()
    {
        AircraftState ac = MakeAircraft();
        ac.Approach.ReportArmedBase = true;
        ac.Approach.ReportArmedFinal = true;
        ac.Approach.ReportFinalMileTarget = 5;
        ac.Approach.ReportAtFixName = "SUNOL";

        CommandResult result = CommandDispatcher.Dispatch(new ReportCommand(ReportTrigger.Cancel), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.False(ac.Approach.ReportArmedBase);
        Assert.False(ac.Approach.ReportArmedFinal);
        Assert.Null(ac.Approach.ReportFinalMileTarget);
        Assert.Null(ac.Approach.ReportAtFixName);
    }

    [Fact]
    public void CancelSpecificLeg_ClearsOnlyThatLeg()
    {
        AircraftState ac = MakeAircraft();
        ac.Approach.ReportArmedBase = true;
        ac.Approach.ReportArmedFinal = true;

        CommandResult result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.Cancel, CancelTarget: ReportTrigger.Base),
            ac,
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(result.Success);
        Assert.False(ac.Approach.ReportArmedBase);
        Assert.True(ac.Approach.ReportArmedFinal);
    }

    [Fact]
    public void CancelMileFinal_ClearsOnlyTheMileReport()
    {
        AircraftState ac = MakeAircraft();
        ac.Approach.ReportArmedBase = true;
        ac.Approach.ReportFinalMileTarget = 5;
        ac.Approach.ReportAtFixName = "SUNOL";

        CommandResult result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.Cancel, DistanceNm: 3, CancelTarget: ReportTrigger.MileFinal),
            ac,
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(result.Success);
        Assert.Equal("Cancelled 5-mile final report", result.Message);
        Assert.Null(ac.Approach.ReportFinalMileTarget);
        Assert.True(ac.Approach.ReportArmedBase);
        Assert.Equal("SUNOL", ac.Approach.ReportAtFixName);
    }

    [Fact]
    public void CancelMileFinal_NoneArmed_IsRefused()
    {
        AircraftState ac = MakeAircraft();

        CommandResult result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.Cancel, DistanceNm: 3, CancelTarget: ReportTrigger.MileFinal),
            ac,
            TestDispatch.Context(Random.Shared)
        );

        Assert.False(result.Success);
        Assert.Equal("Unable, N123AB is not reporting a mile final", result.Message);
        Assert.Null(ac.Approach.ReportFinalMileTarget);
    }

    [Fact]
    public void CancelFix_MatchingName_ClearsOnlyTheFix()
    {
        AircraftState ac = MakeAircraft();
        ac.Approach.ReportAtFixName = "SUNOL";
        ac.Approach.ReportAtFixLat = 37.6;
        ac.Approach.ReportAtFixLon = -121.9;
        ac.Approach.ReportFinalMileTarget = 5;

        CommandResult result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.Cancel, FixName: "SUNOL", CancelTarget: ReportTrigger.AtFix),
            ac,
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(result.Success);
        Assert.Equal("Cancelled report passing SUNOL", result.Message);
        Assert.Null(ac.Approach.ReportAtFixName);
        Assert.Null(ac.Approach.ReportAtFixLat);
        Assert.Null(ac.Approach.ReportAtFixLon);
        Assert.Equal(5, ac.Approach.ReportFinalMileTarget);
    }

    [Fact]
    public void CancelFix_OtherFixArmed_IsRefused()
    {
        AircraftState ac = MakeAircraft();
        ac.Approach.ReportAtFixName = "SUNOL";

        CommandResult result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.Cancel, FixName: "MENLO", CancelTarget: ReportTrigger.AtFix),
            ac,
            TestDispatch.Context(Random.Shared)
        );

        Assert.False(result.Success);
        Assert.Equal("Unable, N123AB is not reporting passing MENLO", result.Message);
        Assert.Equal("SUNOL", ac.Approach.ReportAtFixName);
    }

    [Fact]
    public void CancelFix_NoneArmed_IsRefused()
    {
        AircraftState ac = MakeAircraft();

        CommandResult result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.Cancel, FixName: "MENLO", CancelTarget: ReportTrigger.AtFix),
            ac,
            TestDispatch.Context(Random.Shared)
        );

        Assert.False(result.Success);
        Assert.Equal("Unable, N123AB is not reporting passing MENLO", result.Message);
    }

    [Fact]
    public void ReportAtLongFixName_Arms()
    {
        AircraftState ac = MakeAircraft();

        CommandResult result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.AtFix, FixName: "OAK30NUM"),
            ac,
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(result.Success);
        Assert.Equal("Will report passing OAK30NUM", result.Message);
        Assert.Equal("OAK30NUM", ac.Approach.ReportAtFixName);
        Assert.NotNull(ac.Approach.ReportAtFixLat);
        Assert.NotNull(ac.Approach.ReportAtFixLon);
    }

    [Fact]
    public void ArmedReports_SurviveSnapshotRoundTrip()
    {
        var approach = new AircraftApproachState
        {
            ReportArmedCrosswind = true,
            ReportArmedBase = true,
            ReportFinalMileTarget = 5,
            ReportAtFixName = "SUNOL",
            ReportAtFixLat = 37.6,
            ReportAtFixLon = -121.9,
        };

        var restored = AircraftApproachState.FromSnapshot(approach.ToSnapshot());

        Assert.True(restored.ReportArmedCrosswind);
        Assert.False(restored.ReportArmedDownwind);
        Assert.True(restored.ReportArmedBase);
        Assert.False(restored.ReportArmedFinal);
        Assert.Equal(5, restored.ReportFinalMileTarget);
        Assert.Equal("SUNOL", restored.ReportAtFixName);
        Assert.Equal(37.6, restored.ReportAtFixLat);
        Assert.Equal(-121.9, restored.ReportAtFixLon);
    }

    [Fact]
    public void ClearArmedReports_ResetsEverything()
    {
        var approach = new AircraftApproachState
        {
            ReportArmedBase = true,
            ReportArmedFinal = true,
            ReportFinalMileTarget = 5,
            ReportAtFixName = "SUNOL",
            ReportAtFixLat = 37.6,
            ReportAtFixLon = -121.9,
        };

        approach.ClearArmedReports();

        Assert.False(approach.ReportArmedBase);
        Assert.False(approach.ReportArmedFinal);
        Assert.Null(approach.ReportFinalMileTarget);
        Assert.Null(approach.ReportAtFixName);
        Assert.Null(approach.ReportAtFixLat);
        Assert.Null(approach.ReportAtFixLon);
    }

    [Fact]
    public void ArmMidfield_SetsFlag_KeepsPhase_AndEchoesMidfieldDownwind()
    {
        AircraftState ac = MakeAircraft();
        var downwind = new DownwindPhase();
        ac.Phases = new PhaseList();
        ac.Phases.Add(downwind);

        CommandResult result = CommandDispatcher.Dispatch(new ReportCommand(ReportTrigger.Midfield), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(ac.Approach.ReportArmedMidfield);
        Assert.Equal("Will report midfield downwind", result.Message);
        Assert.Same(downwind, ac.Phases.CurrentPhase);
    }

    [Fact]
    public void ArmMidfield_ReadbackIsWillReportMidfieldDownwind()
    {
        AircraftState ac = MakeAircraft();
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound("REPORT MIDFIELD");
        Assert.True(parsed.IsSuccess, parsed.Reason);

        PilotSpeechText? readback = PilotResponder.BuildReadback(parsed.Value!, ac, ActiveRunways.Empty);

        Assert.NotNull(readback);
        Assert.Contains("will report midfield downwind", readback.Terminal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("turning", readback.Terminal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArmMidfield_Rejected_WhenNotInPattern()
    {
        AircraftState ac = MakeAircraft();

        CommandResult result = CommandDispatcher.Dispatch(new ReportCommand(ReportTrigger.Midfield), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Equal("Unable, N123AB is not in the pattern", result.Message);
        Assert.False(ac.Approach.ReportArmedMidfield);
    }

    [Fact]
    public void ArmMidfield_Accepted_ForInboundVfrAircraftWithDownwindEntryQueued()
    {
        RunwayInfo? rwy = NavigationDatabase.Instance.GetRunway("KOAK", "28R");
        Assert.NotNull(rwy);

        // VFR inbound 6 nm southeast of the field, no pattern phase yet.
        (double lat, double lon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, new TrueHeading(135), 6.0);
        var ac = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(315),
            TrueTrack = new TrueHeading(315),
            Altitude = 2500,
            IndicatedAirspeed = 100,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "VFR",
                Destination = "KOAK",
                HasFlightPlan = true,
            },
        };

        CommandResult eld = CommandDispatcher.Dispatch(
            new EnterLeftDownwindCommand("28R"),
            ac,
            TestDispatch.Context(new Random(0), validateDctFixes: false)
        );
        Assert.True(eld.Success, eld.Message);

        CommandResult result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.Midfield),
            ac,
            TestDispatch.Context(new Random(0), validateDctFixes: false)
        );

        Assert.True(result.Success, result.Message);
        Assert.True(ac.Approach.ReportArmedMidfield);
    }

    [Fact]
    public void CancelMidfield_ClearsOnlyMidfield()
    {
        AircraftState ac = MakeAircraft();
        ac.Approach.ReportArmedMidfield = true;
        ac.Approach.ReportArmedDownwind = true;

        CommandResult result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.Cancel, CancelTarget: ReportTrigger.Midfield),
            ac,
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(result.Success);
        Assert.Equal("Cancelled midfield downwind report", result.Message);
        Assert.False(ac.Approach.ReportArmedMidfield);
        Assert.True(ac.Approach.ReportArmedDownwind);
    }

    [Fact]
    public void CancelAll_ClearsMidfield()
    {
        AircraftState ac = MakeAircraft();
        ac.Approach.ReportArmedMidfield = true;

        CommandResult result = CommandDispatcher.Dispatch(new ReportCommand(ReportTrigger.Cancel), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.False(ac.Approach.ReportArmedMidfield);
    }

    [Fact]
    public void ArmedMidfield_SurvivesSnapshotRoundTrip()
    {
        var approach = new AircraftApproachState { ReportArmedMidfield = true };

        var restored = AircraftApproachState.FromSnapshot(approach.ToSnapshot());

        Assert.True(restored.ReportArmedMidfield);
        Assert.False(AircraftApproachState.FromSnapshot(new AircraftApproachState().ToSnapshot()).ReportArmedMidfield);
    }

    private static AircraftState MakeAircraft() =>
        new()
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Position = new LatLon(37.75, -122.221),
            TrueHeading = new TrueHeading(180),
            TrueTrack = new TrueHeading(180),
            Altitude = 1500,
            IndicatedAirspeed = 90,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
        };

    private static RunwayInfo MakeRunway() =>
        new()
        {
            AirportId = "KOAK",
            Id = new RunwayIdentifier("28R", "10L"),
            Designator = "28R",
            Lat1 = 37.721,
            Lon1 = -122.221,
            Elevation1Ft = 9,
            TrueHeading1 = new TrueHeading(284),
            Lat2 = 37.73,
            Lon2 = -122.18,
            Elevation2Ft = 9,
            TrueHeading2 = new TrueHeading(104),
            WidthFt = 150,
        };
}
