using Xunit;
using Yaat.Client.Models;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for the context-rich info text produced by <c>AircraftModel.ComputePhaseStatus()</c>.
/// Covers pattern-entry kinds, runway-exit taxiway, VFR-follow callsign, and heading suppression.
/// Fabricates AircraftModel state directly — no simulation, no recording replay.
/// </summary>
public class AircraftModelPhaseStatusTests
{
    private static AircraftModel CreateModel()
    {
        return new AircraftModel { Callsign = "N123AB", AircraftType = "C172" };
    }

    // --- Pattern entry by kind -------------------------------------------------

    [Fact]
    public void PatternEntry_DirectDownwind()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Pattern Entry";
        ac.PatternDirection = "Left";
        ac.PatternEntryKind = "Direct";
        ac.AssignedRunway = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Direct left downwind 28R", ac.SmartStatus);
    }

    [Fact]
    public void PatternEntry_FortyFive()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Pattern Entry";
        ac.PatternDirection = "Right";
        ac.PatternEntryKind = "FortyFive";
        ac.AssignedRunway = "10L";
        ac.ComputeSmartStatus();

        Assert.Equal("45 to right downwind 10L", ac.SmartStatus);
    }

    [Fact]
    public void PatternEntry_Crosswind()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Pattern Entry";
        ac.PatternDirection = "Left";
        ac.PatternEntryKind = "Crosswind";
        ac.AssignedRunway = "28L";
        ac.ComputeSmartStatus();

        Assert.Equal("Crosswind to left downwind 28L", ac.SmartStatus);
    }

    [Fact]
    public void PatternEntry_Upwind()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Pattern Entry";
        ac.PatternDirection = "Left";
        ac.PatternEntryKind = "Upwind";
        ac.AssignedRunway = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Upwind entry 28R", ac.SmartStatus);
    }

    [Fact]
    public void PatternEntry_Base()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Pattern Entry";
        ac.PatternDirection = "Right";
        ac.PatternEntryKind = "Base";
        ac.AssignedRunway = "19";
        ac.ComputeSmartStatus();

        Assert.Equal("Right base entry 19", ac.SmartStatus);
    }

    [Fact]
    public void PatternEntry_Final()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Pattern Entry";
        ac.PatternEntryKind = "Final";
        ac.AssignedRunway = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Straight-in 28R", ac.SmartStatus);
    }

    [Fact]
    public void PatternEntry_HeadingAssigned_IsSuppressed()
    {
        // For a direct downwind entry, the heading is implied; suppress the suffix.
        var ac = CreateModel();
        ac.CurrentPhase = "Pattern Entry";
        ac.PatternDirection = "Left";
        ac.PatternEntryKind = "Direct";
        ac.AssignedRunway = "28R";
        ac.AssignedHeading = 100;
        ac.ComputeSmartStatus();

        Assert.Equal("Direct left downwind 28R", ac.SmartStatus);
    }

    // --- Runway exit -----------------------------------------------------------

    [Fact]
    public void RunwayExit_WithRunwayAndTaxiway()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Runway Exit";
        ac.ExitingRunwayId = "28R";
        ac.CurrentTaxiway = "T";
        ac.ComputeSmartStatus();

        Assert.Equal("Exiting runway 28R via T", ac.SmartStatus);
    }

    [Fact]
    public void RunwayExit_WithRunwayOnly()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Runway Exit";
        ac.ExitingRunwayId = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Exiting runway 28R", ac.SmartStatus);
    }

    [Fact]
    public void RunwayExit_FallsBackToAssignedRunway()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Runway Exit";
        ac.AssignedRunway = "10L";
        ac.CurrentTaxiway = "D";
        ac.ComputeSmartStatus();

        Assert.Equal("Exiting runway 10L via D", ac.SmartStatus);
    }

    [Fact]
    public void RunwayExit_NoContext_FallsBack()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Runway Exit";
        ac.ComputeSmartStatus();

        Assert.Equal("Exiting runway", ac.SmartStatus);
    }

    // --- Holding after exit ----------------------------------------------------

    [Fact]
    public void HoldingAfterExit_WithRunwayAndTaxiway()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Holding After Exit";
        ac.ExitingRunwayId = "28R";
        ac.CurrentTaxiway = "T";
        ac.ComputeSmartStatus();

        Assert.Equal("Clear of runway 28R via T", ac.SmartStatus);
    }

    [Fact]
    public void HoldingAfterExit_WithRunwayOnly()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Holding After Exit";
        ac.ExitingRunwayId = "10R";
        ac.ComputeSmartStatus();

        Assert.Equal("Clear of runway 10R", ac.SmartStatus);
    }

    [Fact]
    public void HoldingAfterExit_NoContext_FallsBack()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Holding After Exit";
        ac.ComputeSmartStatus();

        Assert.Equal("Clear of runway", ac.SmartStatus);
    }

    // --- VFR follow ------------------------------------------------------------

    [Fact]
    public void VfrFollow_WithTargetCallsign()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "VFR Follow";
        ac.FollowingCallsign = "SWA123";
        ac.ComputeSmartStatus();

        Assert.Equal("Following SWA123", ac.SmartStatus);
    }

    [Fact]
    public void VfrFollow_WithoutTargetCallsign()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "VFR Follow";
        ac.ComputeSmartStatus();

        Assert.Equal("VFR follow", ac.SmartStatus);
    }

    // --- AirTaxi / Crossing Runway with runway context -------------------------

    [Fact]
    public void AirTaxi_WithAssignedRunway()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "AirTaxi";
        ac.AssignedRunway = "28R";
        ac.ComputeSmartStatus();

        Assert.Equal("Air taxi to 28R", ac.SmartStatus);
    }

    [Fact]
    public void CrossingRunway_WithAssignedRunway()
    {
        var ac = CreateModel();
        ac.CurrentPhase = "Crossing Runway";
        ac.AssignedRunway = "10L";
        ac.ComputeSmartStatus();

        Assert.Equal("Crossing runway 10L", ac.SmartStatus);
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
        var ac = CreateModel();
        ac.CurrentPhase = phase;
        ac.AssignedRunway = "28R";
        ac.ClearedRunway = "28R";
        ac.DepartureRunway = "28R";
        ac.PatternDirection = "Left";
        ac.AssignedHeading = 123;
        ac.ComputeSmartStatus();

        Assert.DoesNotContain("hdg", ac.SmartStatus);
    }

    [Theory]
    [InlineData("ProceedToFix")]
    [InlineData("InterceptCourse")]
    [InlineData("HoldingPattern")]
    [InlineData("HoldingAtFix")]
    [InlineData("TurnL90")]
    public void HeadingSuffix_Kept_ForVectorPhases(string phase)
    {
        var ac = CreateModel();
        ac.CurrentPhase = phase;
        ac.AssignedHeading = 270;
        ac.ComputeSmartStatus();

        Assert.Contains("hdg 270", ac.SmartStatus);
    }
}
