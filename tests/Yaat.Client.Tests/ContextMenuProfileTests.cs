using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class ContextMenuProfileTests
{
    private static readonly MenuGroup[] AlwaysVisibleGroups =
    [
        MenuGroup.Tracking,
        MenuGroup.DataBlock,
        MenuGroup.Squawk,
        MenuGroup.Coordination,
        MenuGroup.Assignment,
        MenuGroup.DatablockToggle,
        MenuGroup.Delete,
    ];

    private static readonly MenuGroup[] PhaseGroups =
    [
        MenuGroup.Heading,
        MenuGroup.Altitude,
        MenuGroup.Speed,
        MenuGroup.Navigation,
        MenuGroup.DrawRoute,
        MenuGroup.Hold,
        MenuGroup.Approach,
        MenuGroup.Procedures,
        MenuGroup.Tower,
        MenuGroup.Pattern,
    ];

    [Fact]
    public void AlwaysVisibleGroups_NeverHidden()
    {
        // Test with a variety of phases
        string[] phases =
        [
            "",
            "At Parking",
            "Taxiing",
            "Takeoff",
            "InitialClimb",
            "Downwind",
            "FinalApproach",
            "ApproachNav",
            "HoldingPattern",
            "Landing",
            "GoAround",
            "S-Turns",
        ];

        foreach (string phase in phases)
        {
            ContextMenuProfile profile = ContextMenuProfileService.GetProfile(phase, false);
            foreach (MenuGroup group in AlwaysVisibleGroups)
            {
                Assert.DoesNotContain(group, profile.HiddenGroups);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FreeFlight_ShowsAllFlightCommands(string? phase)
    {
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile(phase, false);

        Assert.Contains(MenuGroup.Heading, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Altitude, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Speed, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Navigation, profile.PrimaryGroups);
        Assert.True(profile.HiddenGroups.Count == 0, "Expected no hidden groups");
    }

    [Theory]
    [InlineData("At Parking")]
    [InlineData("Pushback")]
    [InlineData("Holding After Pushback")]
    [InlineData("Taxiing")]
    [InlineData("Holding In Position")]
    [InlineData("Crossing Runway")]
    [InlineData("Runway Exit")]
    [InlineData("Holding After Exit")]
    [InlineData("AirTaxi")]
    [InlineData("Holding Short 28L")]
    [InlineData("Holding Short")]
    [InlineData("Following UAL123")]
    [InlineData("LiningUp")]
    [InlineData("LinedUpAndWaiting")]
    public void GroundPhases_HideFlightCommands(string phase)
    {
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile(phase, true);

        Assert.Contains(MenuGroup.Tower, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Heading, profile.HiddenGroups);
        Assert.Contains(MenuGroup.Altitude, profile.HiddenGroups);
        Assert.Contains(MenuGroup.Speed, profile.HiddenGroups);
        Assert.Contains(MenuGroup.Navigation, profile.HiddenGroups);
        Assert.Contains(MenuGroup.Pattern, profile.HiddenGroups);
    }

    [Fact]
    public void Takeoff_OnGround_HidesFlightCommands()
    {
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile("Takeoff", true);

        Assert.Contains(MenuGroup.Tower, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Heading, profile.HiddenGroups);
    }

    [Fact]
    public void Takeoff_Airborne_ShowsFlightCommands()
    {
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile("Takeoff", false);

        Assert.Contains(MenuGroup.Heading, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Altitude, profile.PrimaryGroups);
        Assert.DoesNotContain(MenuGroup.Heading, profile.HiddenGroups);
    }

    [Theory]
    [InlineData("Pattern Entry")]
    [InlineData("Upwind")]
    [InlineData("Crosswind")]
    [InlineData("Downwind")]
    [InlineData("Base")]
    [InlineData("MidfieldCrossing")]
    public void PatternPhases_ShowTowerAndPattern(string phase)
    {
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile(phase, false);

        Assert.Contains(MenuGroup.Tower, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Pattern, profile.PrimaryGroups);
        Assert.True(profile.HiddenGroups.Count == 0, "Expected no hidden groups");
    }

    [Fact]
    public void FinalApproach_ShowsTower()
    {
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile("FinalApproach", false);

        Assert.Contains(MenuGroup.Tower, profile.PrimaryGroups);
        Assert.True(profile.HiddenGroups.Count == 0, "Expected no hidden groups");
    }

    [Theory]
    [InlineData("ApproachNav")]
    [InlineData("InterceptCourse")]
    public void ApproachNavPhases_ShowSpeedAltitudeTower(string phase)
    {
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile(phase, false);

        Assert.Contains(MenuGroup.Speed, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Altitude, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Tower, profile.PrimaryGroups);
    }

    [Theory]
    [InlineData("HoldingPattern")]
    [InlineData("HPP-L")]
    [InlineData("HPP-R")]
    [InlineData("HPP")]
    [InlineData("HoldingAtFix")]
    [InlineData("ProceedToFix")]
    public void HoldingPhases_ShowFlightCommands(string phase)
    {
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile(phase, false);

        Assert.Contains(MenuGroup.Heading, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Navigation, profile.PrimaryGroups);
        Assert.True(profile.HiddenGroups.Count == 0, "Expected no hidden groups");
    }

    [Theory]
    [InlineData("Landing")]
    [InlineData("Landing-H")]
    public void LandingPhases_HideFlightCommands(string phase)
    {
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile(phase, true);

        Assert.Contains(MenuGroup.Tower, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Heading, profile.HiddenGroups);
    }

    [Fact]
    public void GoAround_ShowsTowerAndFlight()
    {
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile("GoAround", false);

        Assert.Contains(MenuGroup.Tower, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Heading, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Altitude, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Speed, profile.PrimaryGroups);
    }

    [Theory]
    [InlineData("TurnL090")]
    [InlineData("TurnR180")]
    [InlineData("S-Turns")]
    public void TurnPhases_ShowHeadingAltitudeSpeed(string phase)
    {
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile(phase, false);

        Assert.Contains(MenuGroup.Heading, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Altitude, profile.PrimaryGroups);
        Assert.Contains(MenuGroup.Speed, profile.PrimaryGroups);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("At Parking", true)]
    [InlineData("FinalApproach", false)]
    [InlineData("Downwind", false)]
    [InlineData("HoldingPattern", false)]
    public void PrimaryPlusSecondary_CoversAllNonHiddenPhaseGroups(string? phase, bool onGround)
    {
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile(phase, onGround);

        var allPresent = new HashSet<MenuGroup>(profile.PrimaryGroups);
        foreach (MenuGroup g in profile.SecondaryGroups)
        {
            allPresent.Add(g);
        }
        foreach (MenuGroup g in profile.HiddenGroups)
        {
            allPresent.Add(g);
        }

        foreach (MenuGroup group in PhaseGroups)
        {
            Assert.Contains(group, allPresent);
        }
    }
}
