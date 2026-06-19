using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
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
        var ac = MakeAircraft();
        var downwind = new DownwindPhase();
        ac.Phases = new PhaseList();
        ac.Phases.Add(downwind);

        var result = CommandDispatcher.Dispatch(new ReportCommand(ReportTrigger.Base), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.True(ac.Approach.ReportArmedBase);
        Assert.Contains("turning base", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(downwind, ac.Phases.CurrentPhase);
    }

    [Fact]
    public void ArmPatternLeg_Rejected_WhenNotInPattern()
    {
        var ac = MakeAircraft();

        var result = CommandDispatcher.Dispatch(new ReportCommand(ReportTrigger.Base), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("not in the pattern", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ac.Approach.ReportArmedBase);
    }

    [Fact]
    public void ArmMileFinal_SetsTarget_WhenRunwayAssigned()
    {
        var ac = MakeAircraft();
        ac.Phases = new PhaseList { AssignedRunway = MakeRunway() };

        var result = CommandDispatcher.Dispatch(new ReportCommand(ReportTrigger.MileFinal, DistanceNm: 5), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(5, ac.Approach.ReportFinalMileTarget);
    }

    [Fact]
    public void ArmMileFinal_Rejected_WhenNoRunway()
    {
        var ac = MakeAircraft();

        var result = CommandDispatcher.Dispatch(new ReportCommand(ReportTrigger.MileFinal, DistanceNm: 5), ac, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("no runway", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ac.Approach.ReportFinalMileTarget);
    }

    [Fact]
    public void ArmAtFix_Rejected_WhenFixUnknown()
    {
        var ac = MakeAircraft();

        var result = CommandDispatcher.Dispatch(new ReportCommand(ReportTrigger.AtFix, FixName: "QXQXQ"), ac, TestDispatch.Context(Random.Shared));

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

        var ac = MakeAircraft();
        var result = CommandDispatcher.Dispatch(new ReportCommand(ReportTrigger.AtFix, FixName: "SUNOL"), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal("SUNOL", ac.Approach.ReportAtFixName);
        Assert.NotNull(ac.Approach.ReportAtFixLat);
        Assert.NotNull(ac.Approach.ReportAtFixLon);
    }

    [Fact]
    public void CancelAll_ClearsEveryArm()
    {
        var ac = MakeAircraft();
        ac.Approach.ReportArmedBase = true;
        ac.Approach.ReportArmedFinal = true;
        ac.Approach.ReportFinalMileTarget = 5;
        ac.Approach.ReportAtFixName = "SUNOL";

        var result = CommandDispatcher.Dispatch(new ReportCommand(ReportTrigger.Cancel), ac, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.False(ac.Approach.ReportArmedBase);
        Assert.False(ac.Approach.ReportArmedFinal);
        Assert.Null(ac.Approach.ReportFinalMileTarget);
        Assert.Null(ac.Approach.ReportAtFixName);
    }

    [Fact]
    public void CancelSpecificLeg_ClearsOnlyThatLeg()
    {
        var ac = MakeAircraft();
        ac.Approach.ReportArmedBase = true;
        ac.Approach.ReportArmedFinal = true;

        var result = CommandDispatcher.Dispatch(
            new ReportCommand(ReportTrigger.Cancel, CancelTarget: ReportTrigger.Base),
            ac,
            TestDispatch.Context(Random.Shared)
        );

        Assert.True(result.Success);
        Assert.False(ac.Approach.ReportArmedBase);
        Assert.True(ac.Approach.ReportArmedFinal);
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
            LengthFt = 10000,
            WidthFt = 150,
        };
}
