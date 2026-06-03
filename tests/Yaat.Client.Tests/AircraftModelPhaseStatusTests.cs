using Xunit;
using Yaat.Sim;
using static Yaat.Sim.AircraftStatusDescriber;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for the context-rich info text produced by <see cref="AircraftStatusDescriber"/>.
/// Covers pattern-entry kinds, runway-exit taxiway, VFR-follow callsign, and heading suppression.
/// Builds an <see cref="AircraftStatusView"/> directly — no simulation, no recording replay.
/// </summary>
public class AircraftModelPhaseStatusTests
{
    private static string Text(AircraftStatusView v) => Describe(v).Text;

    // --- Pattern entry by kind -------------------------------------------------

    [Fact]
    public void PatternEntry_DirectDownwind()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Pattern Entry",
            PatternDirection = "Left",
            PatternEntryKind = "Direct",
            AssignedRunway = "28R",
        };
        Assert.Equal("Direct left downwind 28R", Text(v));
    }

    [Fact]
    public void PatternEntry_FortyFive()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Pattern Entry",
            PatternDirection = "Right",
            PatternEntryKind = "FortyFive",
            AssignedRunway = "10L",
        };
        Assert.Equal("45 to right downwind 10L", Text(v));
    }

    [Fact]
    public void PatternEntry_Crosswind()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Pattern Entry",
            PatternDirection = "Left",
            PatternEntryKind = "Crosswind",
            AssignedRunway = "28L",
        };
        Assert.Equal("Crosswind to left downwind 28L", Text(v));
    }

    [Fact]
    public void PatternEntry_Upwind()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Pattern Entry",
            PatternDirection = "Left",
            PatternEntryKind = "Upwind",
            AssignedRunway = "28R",
        };
        Assert.Equal("Upwind entry 28R", Text(v));
    }

    [Fact]
    public void PatternEntry_Base()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Pattern Entry",
            PatternDirection = "Right",
            PatternEntryKind = "Base",
            AssignedRunway = "19",
        };
        Assert.Equal("Right base entry 19", Text(v));
    }

    [Fact]
    public void PatternEntry_Final()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Pattern Entry",
            PatternEntryKind = "Final",
            AssignedRunway = "28R",
        };
        Assert.Equal("Straight-in 28R", Text(v));
    }

    [Fact]
    public void PatternEntry_HeadingAssigned_IsSuppressed()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Pattern Entry",
            PatternDirection = "Left",
            PatternEntryKind = "Direct",
            AssignedRunway = "28R",
            AssignedHeading = new MagneticHeading(100),
        };
        Assert.Equal("Direct left downwind 28R", Text(v));
    }

    // --- Runway exit -----------------------------------------------------------

    [Fact]
    public void RunwayExit_WithRunwayAndTaxiway()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Runway Exit",
            ExitingRunwayId = "28R",
            CurrentTaxiway = "T",
        };
        Assert.Equal("Exiting runway 28R via T", Text(v));
    }

    [Fact]
    public void RunwayExit_WithRunwayOnly()
    {
        var v = new AircraftStatusView { CurrentPhase = "Runway Exit", ExitingRunwayId = "28R" };
        Assert.Equal("Exiting runway 28R", Text(v));
    }

    [Fact]
    public void RunwayExit_FallsBackToAssignedRunway()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Runway Exit",
            AssignedRunway = "10L",
            CurrentTaxiway = "D",
        };
        Assert.Equal("Exiting runway 10L via D", Text(v));
    }

    [Fact]
    public void RunwayExit_NoContext_FallsBack()
    {
        Assert.Equal("Exiting runway", Text(new AircraftStatusView { CurrentPhase = "Runway Exit" }));
    }

    // --- Holding after exit ----------------------------------------------------

    [Fact]
    public void HoldingAfterExit_WithRunwayAndTaxiway()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Holding After Exit",
            ExitingRunwayId = "28R",
            CurrentTaxiway = "T",
        };
        Assert.Equal("Clear of runway 28R via T", Text(v));
    }

    [Fact]
    public void HoldingAfterExit_WithRunwayOnly()
    {
        var v = new AircraftStatusView { CurrentPhase = "Holding After Exit", ExitingRunwayId = "10R" };
        Assert.Equal("Clear of runway 10R", Text(v));
    }

    [Fact]
    public void HoldingAfterExit_NoContext_FallsBack()
    {
        Assert.Equal("Clear of runway", Text(new AircraftStatusView { CurrentPhase = "Holding After Exit" }));
    }

    // --- VFR follow ------------------------------------------------------------

    [Fact]
    public void VfrFollow_WithTargetCallsign()
    {
        var v = new AircraftStatusView { CurrentPhase = "VFR Follow", FollowingCallsign = "SWA123" };
        Assert.Equal("Following SWA123", Text(v));
    }

    [Fact]
    public void VfrFollow_WithoutTargetCallsign()
    {
        Assert.Equal("VFR follow", Text(new AircraftStatusView { CurrentPhase = "VFR Follow" }));
    }

    // --- AirTaxi / Crossing Runway with runway context -------------------------

    [Fact]
    public void AirTaxi_WithAssignedRunway()
    {
        var v = new AircraftStatusView { CurrentPhase = "AirTaxi", AssignedRunway = "28R" };
        Assert.Equal("Air taxi to 28R", Text(v));
    }

    [Fact]
    public void CrossingRunway_UsesCrossingRunwayId_NotAssignedRunway()
    {
        // Bug fixture: aircraft taxiing to runway 30 for departure, currently crossing 28L.
        // The status must reflect the runway being crossed, not the departure runway.
        var v = new AircraftStatusView
        {
            CurrentPhase = "Crossing Runway",
            AssignedRunway = "30",
            CrossingRunwayId = "28L",
        };
        Assert.Equal("Crossing runway 28L", Text(v));
    }

    [Fact]
    public void CrossingRunway_FallsBackToAssignedRunway_WhenCrossingRunwayIdMissing()
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = "Crossing Runway",
            AssignedRunway = "10L",
            CrossingRunwayId = null,
        };
        Assert.Equal("Crossing runway 10L", Text(v));
    }

    [Fact]
    public void CrossingRunway_NoRunway()
    {
        Assert.Equal("Crossing runway", Text(new AircraftStatusView { CurrentPhase = "Crossing Runway" }));
    }

    // --- Heading suppression matrix --------------------------------------------

    [Theory]
    [InlineData("InitialClimb")]
    [InlineData("ApproachNav")]
    [InlineData("FinalApproach")]
    [InlineData("Downwind")]
    [InlineData("Base")]
    [InlineData("Upwind")]
    [InlineData("Crosswind")]
    [InlineData("Landing")]
    [InlineData("Taxiing")]
    [InlineData("AirTaxi")]
    [InlineData("Runway Exit")]
    [InlineData("Pattern Entry")]
    [InlineData("MidfieldCrossing")]
    public void HeadingSuffix_Suppressed_ForImpliedOrGroundPhases(string phase)
    {
        var v = new AircraftStatusView
        {
            CurrentPhase = phase,
            AssignedRunway = "28R",
            ClearedRunway = "28R",
            DepartureRunway = "28R",
            PatternDirection = "Left",
            AssignedHeading = new MagneticHeading(123),
        };
        Assert.DoesNotContain("hdg", Text(v));
    }

    [Theory]
    [InlineData("ProceedToFix")]
    [InlineData("InterceptCourse")]
    [InlineData("HoldingPattern")]
    [InlineData("HoldingAtFix")]
    [InlineData("TurnL90")]
    public void HeadingSuffix_Kept_ForVectorPhases(string phase)
    {
        var v = new AircraftStatusView { CurrentPhase = phase, AssignedHeading = new MagneticHeading(270) };
        Assert.Contains("hdg 270", Text(v));
    }
}
