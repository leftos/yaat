using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Scope;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins <see cref="EramLine4.Text"/>, the ERAM line 4 in CRC <c>FdbRenderObject.GetFieldF</c> order (the HSF free text,
/// heading and speed, then the type or destination fallback), and the HSF indicator the presenter puts after the status.
/// </summary>
public class EramLine4Tests
{
    private static AircraftModel Plain()
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.AircraftType = "";
        ac.FiledAircraftType = "";
        ac.EquipmentSuffix = "";
        ac.Destination = "";
        return ac;
    }

    private static string Line4(AircraftModel ac) => EramLine4.Text(ac, showType: true, showDestination: true);

    [Fact]
    public void FreeText_WinsOverHeadingAndSpeed()
    {
        AircraftModel ac = Plain();
        ac.EramFreeText = "WX DEV";
        ac.EramAssignedHeading = "270";
        ac.EramAssignedSpeed = "250";

        Assert.Equal("WX DEV", Line4(ac));
    }

    [Fact]
    public void HeadingAndSpeed_HeadingPaddedToFourThenTheSpeed()
    {
        AircraftModel ac = Plain();
        ac.EramAssignedHeading = "270";
        ac.EramAssignedSpeed = "250";

        Assert.Equal("270  250", Line4(ac));
    }

    [Fact]
    public void HeadingAlone()
    {
        AircraftModel ac = Plain();
        ac.EramAssignedHeading = "270";

        Assert.Equal("270", Line4(ac));
    }

    [Fact]
    public void SpeedAlone_SitsInTheSpeedColumn()
    {
        AircraftModel ac = Plain();
        ac.EramAssignedSpeed = "250";

        Assert.Equal("     250", Line4(ac));
    }

    [Fact]
    public void TypeWithSuffix()
    {
        AircraftModel ac = Plain();
        ac.FiledAircraftType = "B738";
        ac.EquipmentSuffix = "L";
        ac.Destination = "KSFO";

        Assert.Equal("B738/L", Line4(ac));
    }

    [Fact]
    public void PhysicalTypeCarryingTheSuffix_ShowsTheSuffixOnce()
    {
        AircraftModel ac = Plain();
        ac.AircraftType = "B738/L";
        ac.EquipmentSuffix = "L";

        Assert.Equal("B738/L", Line4(ac));
    }

    [Fact]
    public void FiledTypeWithPrefixAndSuffix_ShowsTheBareTypeThenTheSuffix()
    {
        AircraftModel ac = Plain();
        ac.FiledAircraftType = "H/A306/L";
        ac.EquipmentSuffix = "L";

        Assert.Equal("A306/L", Line4(ac));
    }

    [Fact]
    public void TypeWithoutSuffix()
    {
        AircraftModel ac = Plain();
        ac.FiledAircraftType = "B738";

        Assert.Equal("B738", Line4(ac));
    }

    [Fact]
    public void Destination_WhenNoType()
    {
        AircraftModel ac = Plain();
        ac.Destination = "KSFO";

        Assert.Equal("KSFO", Line4(ac));
    }

    [Fact]
    public void Destination_OfFiveCharacters_IsNotShown()
    {
        AircraftModel ac = Plain();
        ac.Destination = "KSFOX";

        Assert.Equal("", Line4(ac));
    }

    [Fact]
    public void BothFlagsOff_IsEmpty()
    {
        AircraftModel ac = Plain();
        ac.FiledAircraftType = "B738";
        ac.Destination = "KSFO";

        Assert.Equal("", EramLine4.Text(ac, showType: false, showDestination: false));
    }

    [Fact]
    public void TypeFlagOff_ShowsTheDestination()
    {
        AircraftModel ac = Plain();
        ac.FiledAircraftType = "B738";
        ac.Destination = "KSFO";

        Assert.Equal("KSFO", EramLine4.Text(ac, showType: false, showDestination: true));
    }

    [Theory]
    [InlineData("270", null, null)]
    [InlineData(null, "250", null)]
    [InlineData(null, null, "WX DEV")]
    public void HsfIndicator_PresentWhenAnyHsfStringIsSet(string? heading, string? speed, string? freeText)
    {
        AircraftModel ac = Plain();
        ac.EramAssignedHeading = heading;
        ac.EramAssignedSpeed = speed;
        ac.EramFreeText = freeText;

        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EramId));

        ScopeField indicator = Assert.Single(p.Fields, f => f.Slot == ScopeFieldSlot.HsfIndicator);
        Assert.Equal(TrackPresenter.EramArrowCell, indicator.Text);
        Assert.Equal(ScopeFieldStyle.HsfIndicator, indicator.Style);
        Assert.Equal(ScopeColorToken.Default, indicator.Token);
    }

    [Fact]
    public void HsfIndicator_AbsentWithoutHsf()
    {
        AircraftModel ac = Plain();
        ac.EramFreeText = "";

        TrackPresentation p = TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EramId));

        Assert.Equal("", Assert.Single(p.Fields, f => f.Slot == ScopeFieldSlot.HsfIndicator).Text);
    }

    [Theory]
    [InlineData(null, "    ")]
    [InlineData("4", "H-4 ")]
    [InlineData("L250", "HL250")]
    public void HsfIndicator_Present_PadsTheStatusToFourWithoutTruncating(string? peer, string expected)
    {
        AircraftModel ac = Plain();
        ac.EramAssignedHeading = "270";
        if (peer is not null)
        {
            ac.HandoffPeerSectorCode = "C40";
            ac.EramHandoffPeerFieldE = peer;
        }

        ScopeField status = Assert.Single(TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EramId)).Fields, IsStatus);

        Assert.Equal(expected, status.Text);
        Assert.Equal(expected, status.StableText);
    }

    [Fact]
    public void HsfIndicator_Absent_LeavesTheStatusUnpadded()
    {
        AircraftModel ac = Plain();

        ScopeField status = Assert.Single(TrackPresenter.Present(ScopeTestAircraft.Input(ac, ScopeTemplates.EramId)).Fields, IsStatus);

        Assert.Equal("", status.Text);
    }

    private static bool IsStatus(ScopeField f) => f.Slot == ScopeFieldSlot.StatusLine3;
}
