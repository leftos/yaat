using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="ArmedReportsDto.From"/> projects the armed reports on <see cref="AircraftApproachState"/> for the wire: the five leg
/// flags fold into <see cref="ArmedReportLegs"/>, the mile final and the fix pass through, and nothing armed is null.
/// </summary>
public class ArmedReportsDtoTests
{
    [Fact]
    public void From_NothingArmed_IsNull() => Assert.Null(ArmedReportsDto.From(new AircraftApproachState()));

    [Fact]
    public void From_BaseAndMidfield_FoldsTheFlags()
    {
        var approach = new AircraftApproachState { ReportArmedBase = true, ReportArmedMidfield = true };

        ArmedReportsDto armed = Assert.IsType<ArmedReportsDto>(ArmedReportsDto.From(approach));

        Assert.Equal(ArmedReportLegs.Base | ArmedReportLegs.Midfield, armed.Legs);
        Assert.Null(armed.FinalMiles);
        Assert.Null(armed.Fix);
    }

    [Fact]
    public void From_MileFinalOnly_HasNoLegs()
    {
        var approach = new AircraftApproachState { ReportFinalMileTarget = 5 };

        ArmedReportsDto armed = Assert.IsType<ArmedReportsDto>(ArmedReportsDto.From(approach));

        Assert.Equal(ArmedReportLegs.None, armed.Legs);
        Assert.Equal(5, armed.FinalMiles);
        Assert.Null(armed.Fix);
    }

    [Fact]
    public void From_FixOnly_CarriesTheFixName()
    {
        var approach = new AircraftApproachState
        {
            ReportAtFixName = "SUNOL",
            ReportAtFixLat = 37.6,
            ReportAtFixLon = -121.9,
        };

        ArmedReportsDto armed = Assert.IsType<ArmedReportsDto>(ArmedReportsDto.From(approach));

        Assert.Equal(ArmedReportLegs.None, armed.Legs);
        Assert.Null(armed.FinalMiles);
        Assert.Equal("SUNOL", armed.Fix);
    }

    [Fact]
    public void From_EverythingArmed_CarriesEveryFlagAndBothValues()
    {
        var approach = new AircraftApproachState
        {
            ReportArmedCrosswind = true,
            ReportArmedDownwind = true,
            ReportArmedBase = true,
            ReportArmedFinal = true,
            ReportArmedMidfield = true,
            ReportFinalMileTarget = 3,
            ReportAtFixName = "OAK30NUM",
            ReportAtFixLat = 37.7,
            ReportAtFixLon = -122.2,
        };

        ArmedReportsDto armed = Assert.IsType<ArmedReportsDto>(ArmedReportsDto.From(approach));

        ArmedReportLegs all =
            ArmedReportLegs.Crosswind | ArmedReportLegs.Downwind | ArmedReportLegs.Midfield | ArmedReportLegs.Base | ArmedReportLegs.Final;
        Assert.Equal(all, armed.Legs);
        Assert.Equal(3, armed.FinalMiles);
        Assert.Equal("OAK30NUM", armed.Fix);
    }
}
