using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// Unit tests for the canonical ASDE-X commands routed via <see cref="TrackEngine"/>.
/// Covers parser → ParsedCommand record → handler → AircraftStarsState mutation.
/// </summary>
public class AsdexCommandTests
{
    private static AircraftState MakeAircraft() =>
        new()
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan { HasFlightPlan = true, Destination = "KSFO" },
        };

    // ── Parser ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ASDXSP1 ABC", AsdexEditField.Scratchpad1, "ABC")]
    [InlineData("ASDXSP2 XY", AsdexEditField.Scratchpad2, "XY")]
    [InlineData("ASDXCS UAL238", AsdexEditField.Callsign, "UAL238")]
    [InlineData("ASDXBCN 4577", AsdexEditField.BeaconCode, "4577")]
    [InlineData("ASDXCAT B", AsdexEditField.Category, "B")]
    [InlineData("ASDXTYPE B738", AsdexEditField.AircraftType, "B738")]
    [InlineData("ASDXFIX SEGUL", AsdexEditField.Fix, "SEGUL")]
    public void Parser_AsdexEditCommands_ProduceCorrectFieldAndText(string input, AsdexEditField expectedField, string expectedText)
    {
        var result = CommandParser.Parse(input);

        Assert.True(result.IsSuccess, result.Reason);
        var asdexEdit = Assert.IsType<AsdexEditCommand>(result.Value);
        Assert.Equal(expectedField, asdexEdit.Field);
        Assert.Equal(expectedText, asdexEdit.Text);
    }

    [Theory]
    [InlineData("ASDXSP1", AsdexEditField.Scratchpad1)]
    [InlineData("ASDXFIX", AsdexEditField.Fix)]
    public void Parser_AsdexEditCommandsWithNoArg_ProduceEmptyText(string input, AsdexEditField expectedField)
    {
        var result = CommandParser.Parse(input);

        Assert.True(result.IsSuccess, result.Reason);
        var asdexEdit = Assert.IsType<AsdexEditCommand>(result.Value);
        Assert.Equal(expectedField, asdexEdit.Field);
        Assert.Equal("", asdexEdit.Text);
    }

    [Theory]
    [InlineData("ASDXTAG", AsdexVerb.Tag)]
    [InlineData("ASDXTERM", AsdexVerb.Terminate)]
    [InlineData("ASDXSUSP", AsdexVerb.Suspend)]
    [InlineData("ASDXUSUS", AsdexVerb.Unsuspend)]
    [InlineData("ASDXINHIB", AsdexVerb.InhibitAlerts)]
    public void Parser_AsdexVerbCommands_ProduceCorrectVerb(string input, AsdexVerb expectedVerb)
    {
        var result = CommandParser.Parse(input);

        Assert.True(result.IsSuccess, result.Reason);
        var asdexVerb = Assert.IsType<AsdexVerbCommand>(result.Value);
        Assert.Equal(expectedVerb, asdexVerb.Verb);
    }

    [Fact]
    public void Parser_AsdexEnableAllAlerts_ProducesGlobalCommand()
    {
        var result = CommandParser.Parse("ASDXALERTS");

        Assert.True(result.IsSuccess, result.Reason);
        Assert.IsType<AsdexEnableAllAlertsCommand>(result.Value);
    }

    // ── HandleAsdexEdit ───────────────────────────────────────────────────────

    [Fact]
    public void HandleAsdexEdit_Scratchpad1_SetsAndClearsAsdexFieldOnly()
    {
        var ac = MakeAircraft();
        ac.Stars.Scratchpad1 = "STARS"; // STARS scratchpad must NOT be touched

        TrackEngine.HandleAsdexEdit(ac, AsdexEditField.Scratchpad1, "ASDX");

        Assert.Equal("ASDX", ac.Stars.AsdexScratchpad1);
        Assert.Equal("STARS", ac.Stars.Scratchpad1);

        TrackEngine.HandleAsdexEdit(ac, AsdexEditField.Scratchpad1, "");

        Assert.Null(ac.Stars.AsdexScratchpad1);
        Assert.Equal("STARS", ac.Stars.Scratchpad1);
    }

    [Theory]
    [InlineData(AsdexEditField.Callsign, "UAL238")]
    [InlineData(AsdexEditField.BeaconCode, "4577")]
    [InlineData(AsdexEditField.Category, "B")]
    [InlineData(AsdexEditField.AircraftType, "B738")]
    [InlineData(AsdexEditField.Fix, "SEGUL")]
    public void HandleAsdexEdit_AllFields_RoundTripThroughOverrideSlots(AsdexEditField field, string text)
    {
        var ac = MakeAircraft();

        TrackEngine.HandleAsdexEdit(ac, field, text);

        switch (field)
        {
            case AsdexEditField.Callsign:
                Assert.Equal(text, ac.Stars.AsdexCallsignOverride);
                break;
            case AsdexEditField.BeaconCode:
                Assert.Equal(text, ac.Stars.AsdexBeaconCodeOverride);
                break;
            case AsdexEditField.Category:
                Assert.Equal(text, ac.Stars.AsdexCategoryOverride);
                break;
            case AsdexEditField.AircraftType:
                Assert.Equal(text, ac.Stars.AsdexAircraftTypeOverride);
                break;
            case AsdexEditField.Fix:
                Assert.Equal(text, ac.Stars.AsdexFixOverride);
                break;
        }
    }

    // ── HandleAsdexVerb ───────────────────────────────────────────────────────

    [Fact]
    public void HandleAsdexVerb_Suspend_FlipsBitOnAircraftStarsState()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Suspend);

        Assert.True(ac.Stars.AsdexSuspended);

        TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Unsuspend);

        Assert.False(ac.Stars.AsdexSuspended);
    }

    [Fact]
    public void HandleAsdexVerb_Tag_ClearsTerminatedBit()
    {
        var ac = MakeAircraft();
        ac.Stars.AsdexTerminated = true;

        TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Tag);

        Assert.False(ac.Stars.AsdexTerminated);
    }

    [Fact]
    public void HandleAsdexVerb_Terminate_SetsTerminatedBit()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleAsdexVerb(ac, AsdexVerb.Terminate);

        Assert.True(ac.Stars.AsdexTerminated);
    }

    [Fact]
    public void HandleAsdexVerb_InhibitAlerts_FlipsBit()
    {
        var ac = MakeAircraft();

        TrackEngine.HandleAsdexVerb(ac, AsdexVerb.InhibitAlerts);

        Assert.True(ac.Stars.AsdexAlertsInhibited);
    }
}
