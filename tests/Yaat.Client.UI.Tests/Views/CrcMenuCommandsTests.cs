using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Scope.FieldMenus;
using Yaat.Client.Views.Scope.FieldMenus.Crc;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// What each CRC FDB menu pick does (<see cref="CrcMenuCommands"/>): on ERAM the exact entries CRC's menus send, on PILOT the
/// pilot command the detailed popups' PILOT panels send, or none where no pilot command matches.
/// </summary>
public class CrcMenuCommandsTests
{
    private const int None = 0;

    // UAL1 with the ERAM altitudes in hundreds of feet (0: not set) on a plan of `rules`, at 5000 ft.
    private static AircraftModel Aircraft(int interim, int local, int procedure, string rules) =>
        new()
        {
            Callsign = "UAL1",
            FlightRules = (rules == "OTP") ? "VFR" : rules,
            IsVfrOnTop = rules == "OTP",
            Altitude = 5000,
            EramInterimAltitude = (interim == None) ? null : interim,
            EramLocalInterimAltitude = (local == None) ? null : local,
            EramProcedureAltitude = (procedure == None) ? null : procedure,
        };

    [Theory]
    [InlineData(CrcAltitudePickArea.Altitude, 350, None, None, None, "IFR", "QZ 350 UAL1")]
    [InlineData(CrcAltitudePickArea.Altitude, 55, None, None, None, "VFR", "QZ VFR/055 UAL1")]
    [InlineData(CrcAltitudePickArea.Altitude, 55, None, None, None, "OTP", "QZ OTP/055 UAL1")]
    [InlineData(CrcAltitudePickArea.T, 240, None, None, None, "IFR", "QQ 240 UAL1")]
    [InlineData(CrcAltitudePickArea.LocalTalt, 240, None, None, None, "IFR", "QQ L240 UAL1")]
    [InlineData(CrcAltitudePickArea.Procedure, 240, None, None, None, "IFR", "QQ P240 UAL1")]
    [InlineData(CrcAltitudePickArea.Fp, 350, 240, None, None, "IFR", "QQ UAL1")]
    [InlineData(CrcAltitudePickArea.Fp, 350, None, None, 240, "IFR", "QQ UAL1")]
    [InlineData(CrcAltitudePickArea.Fp, 350, None, 240, None, "IFR", "QQ L UAL1")]
    [InlineData(CrcAltitudePickArea.Fp, 350, 240, 200, None, "IFR", "QQ UAL1|QQ L UAL1")]
    [InlineData(CrcAltitudePickArea.InterimT, 240, 240, 200, None, "IFR", "QQ L UAL1")]
    public void EramAltitude_SendsCrcsEntries(
        CrcAltitudePickArea area,
        int altitude,
        int interim,
        int local,
        int procedure,
        string rules,
        string expected
    )
    {
        AircraftModel ac = Aircraft(interim, local, procedure, rules);

        IReadOnlyList<string> entries = CrcMenuCommands.EramAltitude(new CrcAltitudePick(altitude, area, false), ac);

        Assert.Equal(expected, string.Join('|', entries));
    }

    [Theory]
    [InlineData(CrcAltitudePickArea.Altitude, 350, 5000.0, -1.0, "CM 35000")]
    [InlineData(CrcAltitudePickArea.T, 30, 5000.0, -1.0, "DM 3000")]
    [InlineData(CrcAltitudePickArea.Altitude, 50, 5000.0, -1.0, "CM 5000")]
    [InlineData(CrcAltitudePickArea.Altitude, 50, 5000.0, 3000.0, "DM 5000")]
    [InlineData(CrcAltitudePickArea.Altitude, 50, 5000.0, 9000.0, "CM 5000")]
    [InlineData(CrcAltitudePickArea.Fp, 350, 5000.0, -1.0, null)]
    [InlineData(CrcAltitudePickArea.InterimT, 240, 5000.0, -1.0, null)]
    [InlineData(CrcAltitudePickArea.LocalTalt, 240, 5000.0, -1.0, null)]
    [InlineData(CrcAltitudePickArea.Procedure, 240, 5000.0, -1.0, null)]
    public void PilotAltitude_ClimbsOrDescendsAsTheMaintainPanel(
        CrcAltitudePickArea area,
        int altitude,
        double current,
        double assigned,
        string? expected
    ) => Assert.Equal(expected, CrcMenuCommands.PilotAltitude(new CrcAltitudePick(altitude, area, false), current, (assigned < 0) ? null : assigned));

    [Theory]
    [InlineData(CrcHeadingPickArea.Heading, 270, "QS H270 UAL1", "FH 270")]
    [InlineData(CrcHeadingPickArea.Heading, 5, "QS H005 UAL1", "FH 005")]
    [InlineData(CrcHeadingPickArea.Lt, 20, "QS 20L UAL1", "LT 20")]
    [InlineData(CrcHeadingPickArea.Rt, 35, "QS 35R UAL1", "RT 35")]
    [InlineData(CrcHeadingPickArea.Ph, 0, "QS PH UAL1", "FPH")]
    [InlineData(CrcHeadingPickArea.Delete, 0, "QS */ UAL1", null)]
    public void Heading_SendsCrcsEntry_OrThePilotCommand(CrcHeadingPickArea area, int heading, string eram, string? pilot)
    {
        var pick = new CrcHeadingPick(heading, area, false);

        Assert.Equal(eram, CrcMenuCommands.EramHeading(pick, "UAL1"));
        Assert.Equal(pilot, CrcMenuCommands.PilotHeading(pick));
    }

    [Theory]
    [InlineData(CrcSpeedPickArea.Kt, 250, false, "QS /S250 UAL1", "SPD 250")]
    [InlineData(CrcSpeedPickArea.Kt, 79, true, "QS /M79 UAL1", "MACH .79")]
    [InlineData(CrcSpeedPickArea.Plus, 250, false, "QS /250+ UAL1", "SPD 250+")]
    [InlineData(CrcSpeedPickArea.Minus, 250, false, "QS /250- UAL1", "SPD 250-")]
    [InlineData(CrcSpeedPickArea.Plus, 79, true, "QS /M79+ UAL1", "MACH .79")]
    [InlineData(CrcSpeedPickArea.Minus, 79, true, "QS /M79- UAL1", "MACH .79")]
    [InlineData(CrcSpeedPickArea.Del, 0, false, "QS /* UAL1", "RNS")]
    public void Speed_SendsCrcsEntry_OrThePilotCommand(CrcSpeedPickArea area, int speed, bool isMach, string eram, string pilot)
    {
        var pick = new CrcSpeedPick(speed, isMach, area, false);

        Assert.Equal(eram, CrcMenuCommands.EramSpeed(pick, "UAL1"));
        Assert.Equal(pilot, CrcMenuCommands.PilotSpeed(pick));
    }
}
