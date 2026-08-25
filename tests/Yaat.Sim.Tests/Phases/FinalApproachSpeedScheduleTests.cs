using Xunit;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>Unit tests for the uncontrolled final-approach speed schedule (<see cref="FinalApproachSpeedSchedule"/>).</summary>
public class FinalApproachSpeedScheduleTests
{
    public FinalApproachSpeedScheduleTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("B744", AircraftCategory.Jet, 157, 240)] // heavy: Vref+70 = 227 < 240 cap
    [InlineData("A388", AircraftCategory.Jet, 145, 240)] // super: same cap as heavy
    [InlineData("B738", AircraftCategory.Jet, 144, 220)] // large narrowbody: 214 < 220 cap
    [InlineData("E75L", AircraftCategory.Jet, 126, 210)] // regional: 196 < 210 cap
    [InlineData("DH8D", AircraftCategory.Turboprop, 125, 200)] // turboprop: Vref+55 = 180
    [InlineData("C172", AircraftCategory.Piston, 65, 120)] // piston: Vref+35 = 100
    public void CleanSpeed_IsAdditiveOnVref_AndNeverAboveTheCategoryCap(string type, AircraftCategory category, double vref, double cap)
    {
        double clean = FinalApproachSpeedSchedule.CleanSpeedKts(type, category, vref, "TST1");
        double additive = category switch
        {
            AircraftCategory.Jet => FinalApproachSpeedSchedule.JetCleanAdditiveKts,
            AircraftCategory.Turboprop => FinalApproachSpeedSchedule.TurbopropCleanAdditiveKts,
            _ => FinalApproachSpeedSchedule.PistonCleanAdditiveKts,
        };
        double expected = Math.Min(vref + additive, cap);
        Assert.InRange(clean, expected - FinalApproachSpeedSchedule.CleanJitterKts, expected + FinalApproachSpeedSchedule.CleanJitterKts);
        Assert.True(clean < 250, $"{type} clean {clean:F0} kt would violate the 250-kt below-10k limit");
    }

    [Fact]
    public void CleanSpeed_JitterVariesByCallsign_WithinHalfWidth()
    {
        var speeds = new[] { "UAL1", "DAL2", "SWA3", "AAL4", "JBU5", "ASA6", "SKW7", "QXE8" }
            .Select(cs => FinalApproachSpeedSchedule.CleanSpeedKts("B738", AircraftCategory.Jet, 144, cs))
            .ToList();
        Assert.True(speeds.Max() - speeds.Min() > 2, "jitter should spread clean speeds across callsigns");
        Assert.All(speeds, s => Assert.InRange(s, 214 - 5, 214 + 5));
    }

    [Theory]
    [InlineData(157, 227, 204.1)] // B744: min(227-25, 157+45) = 202, floored at 1.3·Vref = 204.1
    [InlineData(144, 214, 189)] // B738: min(189, 189) = 189
    [InlineData(126, 196, 171)] // E75L: min(171, 171) = 171
    [InlineData(100, 110, 130)] // slow type: never below the 1.3·Vref configuration speed
    public void ApproachFlapSpeed_SitsBetweenCleanAndConfigurationSpeed(double vref, double clean, double expected)
    {
        Assert.Equal(expected, FinalApproachSpeedSchedule.ApproachFlapSpeedKts(vref, clean), 3);
    }

    [Fact]
    public void ApproachFlapReachGate_JetsAndTurbopropsHaveVariety_PistonsHaveNoStage()
    {
        var callsigns = new[] { "UAL1", "DAL2", "SWA3", "AAL4", "JBU5", "ASA6", "SKW7", "QXE8", "N123AB", "N9225L" };
        var jetGates = callsigns.Select(cs => FinalApproachSpeedSchedule.ApproachFlapReachGateNm(AircraftCategory.Jet, cs)!.Value).ToList();
        Assert.All(jetGates, g => Assert.InRange(g, 7.5, 10.5));
        Assert.True(jetGates.Max() - jetGates.Min() > 1.0, "jet flap gates should spread across callsigns");

        var tpGates = callsigns.Select(cs => FinalApproachSpeedSchedule.ApproachFlapReachGateNm(AircraftCategory.Turboprop, cs)!.Value).ToList();
        Assert.All(tpGates, g => Assert.InRange(g, 7.0, 9.0));

        Assert.Null(FinalApproachSpeedSchedule.ApproachFlapReachGateNm(AircraftCategory.Piston, "N123AB"));
        Assert.Null(FinalApproachSpeedSchedule.ApproachFlapReachGateNm(AircraftCategory.Helicopter, "N123AB"));
    }

    [Fact]
    public void SpeedAtDistance_IsMonotoneNonIncreasingTowardTheThreshold()
    {
        double prev = double.MaxValue;
        foreach (double dist in new[] { 14.0, 10.5, 10.4, 6.1, 6.0, 4.1, 4.0, 1.0 })
        {
            double s = FinalApproachSpeedSchedule.SpeedAtDistanceKts("B738", AircraftCategory.Jet, 144, "UAL880", dist);
            Assert.True(s <= prev, $"speed rose from {prev:F0} to {s:F0} at {dist} nm");
            prev = s;
        }

        Assert.Equal(
            144 + FinalApproachSpeedSchedule.ShortFinalSpawnAdditiveKts,
            FinalApproachSpeedSchedule.SpeedAtDistanceKts("B738", AircraftCategory.Jet, 144, "UAL880", 3.0),
            3
        );
        Assert.Equal(144 * 1.3, FinalApproachSpeedSchedule.SpeedAtDistanceKts("B738", AircraftCategory.Jet, 144, "UAL880", 5.0), 3);
    }

    [Fact]
    public void SpeedAtDistance_PistonSkipsTheApproachFlapStage()
    {
        double clean = FinalApproachSpeedSchedule.CleanSpeedKts("C172", AircraftCategory.Piston, 65, "N123AB");
        Assert.Equal(clean, FinalApproachSpeedSchedule.SpeedAtDistanceKts("C172", AircraftCategory.Piston, 65, "N123AB", 8.0), 3);
    }
}
