using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// Pins <see cref="EramStatusLine.Build"/>, the ERAM line-3 status text: one text, the first link of SPC, handoff
/// <c>H</c>, recently accepted <c>O</c>/<c>K</c>, beacon mismatch, <c>NONE</c>, <c>FRZN</c> and <c>CST</c> that applies,
/// with CRC's <c>FdbRenderObject.GetFieldE</c> texts and blink rule; and the viewer's ERAM sector it compares against.
/// </summary>
public class EramStatusLineTests
{
    private static readonly ScopeViewer Viewer = new(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44");

    private static AircraftModel Plain() => ScopeTestAircraft.CreateModel();

    private static (string Text, bool Blinks) Build(AircraftModel ac) => EramStatusLine.Build(ac, Viewer);

    private static void Handoff(AircraftModel ac, string? token)
    {
        ac.HandoffPeerSectorCode = "C40";
        ac.EramHandoffPeerFieldE = token;
    }

    private static void RecentlyAccepted(AircraftModel ac, string facility, string sector, bool forced)
    {
        ac.EramRecentHandoffFromFacilityId = facility;
        ac.EramRecentHandoffFromSectorId = sector;
        ac.EramRecentHandoffWasForced = forced;
        ac.EramRecentHandoffPeerFieldE = "40";
    }

    private static void Mismatch(AircraftModel ac)
    {
        ac.EramIsCorrelated = true;
        ac.TransponderMode = "C";
        ac.BeaconCode = 1234;
        ac.AssignedBeaconCode = 4321;
    }

    private static void NoBeacon(AircraftModel ac)
    {
        ac.EramIsCorrelated = true;
        ac.TransponderMode = "Standby";
        ac.AssignedBeaconCode = 4321;
    }

    [Theory]
    [InlineData(7500u, "HIJK")]
    [InlineData(7600u, "RDOF")]
    [InlineData(7700u, "EMRG")]
    [InlineData(1276u, "ADIZ")]
    [InlineData(7777u, "AFIO")]
    [InlineData(7400u, "LLNK")]
    public void Spc_ShowsItsLabel_BlinkingWhenCorrelatedAndInItsWindow(uint code, string label)
    {
        AircraftModel ac = Plain();
        ac.BeaconCode = code;
        ac.EramIsCorrelated = true;
        ac.EramSpcBlinking = true;

        Assert.Equal((label, true), Build(ac));
    }

    [Fact]
    public void Spc_CorrelatedOutsideItsWindow_IsSteady()
    {
        AircraftModel ac = Plain();
        ac.BeaconCode = 7700;
        ac.EramIsCorrelated = true;

        Assert.Equal(("EMRG", false), Build(ac));
    }

    [Fact]
    public void Spc_TransponderOnStandby_ReportsNoCode()
    {
        AircraftModel ac = Plain();
        ac.BeaconCode = 7700;
        ac.TransponderMode = "Standby";

        Assert.Equal(("", false), Build(ac));
    }

    [Fact]
    public void Handoff_TwoCharacterToken_TakesAHyphen()
    {
        AircraftModel ac = Plain();
        Handoff(ac, "40");

        Assert.Equal(("H-40", true), Build(ac));
    }

    [Fact]
    public void Handoff_ThreeCharacterToken_TakesNoHyphen()
    {
        AircraftModel ac = Plain();
        Handoff(ac, "L25");

        Assert.Equal(("HL25", true), Build(ac));
    }

    [Fact]
    public void Handoff_NoToken_IsUnk()
    {
        AircraftModel ac = Plain();
        Handoff(ac, null);

        Assert.Equal(("HUNK", true), Build(ac));
    }

    [Fact]
    public void RecentlyAccepted_ByTheViewersSector_ShowsO()
    {
        AircraftModel ac = Plain();
        RecentlyAccepted(ac, "zoa", "44", forced: false);

        Assert.Equal(("O-40", false), Build(ac));
    }

    [Fact]
    public void RecentlyAccepted_Forced_ShowsK()
    {
        AircraftModel ac = Plain();
        RecentlyAccepted(ac, "ZOA", "44", forced: true);

        Assert.Equal(("K-40", false), Build(ac));
    }

    [Fact]
    public void RecentlyAccepted_ViewerOfAnotherFacility_IsHidden()
    {
        AircraftModel ac = Plain();
        RecentlyAccepted(ac, "ZLA", "44", forced: false);

        Assert.Equal(("", false), Build(ac));
    }

    [Fact]
    public void RecentlyAccepted_ViewerOfAnotherSector_IsHidden()
    {
        AircraftModel ac = Plain();
        RecentlyAccepted(ac, "ZOA", "45", forced: false);

        Assert.Equal(("", false), Build(ac));
    }

    [Fact]
    public void Mismatch_ShowsTheReportedCode()
    {
        AircraftModel ac = Plain();
        Mismatch(ac);

        Assert.Equal(("1234", false), Build(ac));
    }

    [Fact]
    public void None_CorrelatedWithAnAssignedCodeAndNoBeacon()
    {
        AircraftModel ac = Plain();
        NoBeacon(ac);

        Assert.Equal(("NONE", false), Build(ac));
    }

    [Fact]
    public void Frozen_ShowsFrzn()
    {
        AircraftModel ac = Plain();
        ac.EramIsFrozen = true;

        Assert.Equal(("FRZN", false), Build(ac));
    }

    [Fact]
    public void CoastTrack_ShowsCst()
    {
        AircraftModel ac = Plain();
        ac.EramIsCoastTrack = true;

        Assert.Equal((" CST", true), Build(ac));
    }

    [Fact]
    public void CoverageCoasting_ShowsCst()
    {
        AircraftModel ac = Plain();
        ac.EramIsCoverageCoasting = true;

        Assert.Equal((" CST", true), Build(ac));
    }

    [Fact]
    public void Nothing_IsEmpty() => Assert.Equal(("", false), Build(Plain()));

    [Fact]
    public void Spc_BeatsHandoff()
    {
        AircraftModel ac = Plain();
        ac.BeaconCode = 7700;
        Handoff(ac, "40");

        Assert.Equal(("EMRG", false), Build(ac));
    }

    [Fact]
    public void Handoff_BeatsRecentlyAccepted()
    {
        AircraftModel ac = Plain();
        Handoff(ac, "L25");
        RecentlyAccepted(ac, "ZOA", "44", forced: false);

        Assert.Equal(("HL25", true), Build(ac));
    }

    [Fact]
    public void RecentlyAccepted_BeatsMismatch()
    {
        AircraftModel ac = Plain();
        RecentlyAccepted(ac, "ZOA", "44", forced: false);
        Mismatch(ac);

        Assert.Equal(("O-40", false), Build(ac));
    }

    [Fact]
    public void Mismatch_BeatsFrozen()
    {
        AircraftModel ac = Plain();
        Mismatch(ac);
        ac.EramIsFrozen = true;

        Assert.Equal(("1234", false), Build(ac));
    }

    [Fact]
    public void None_BeatsFrozen()
    {
        AircraftModel ac = Plain();
        NoBeacon(ac);
        ac.EramIsFrozen = true;

        Assert.Equal(("NONE", false), Build(ac));
    }

    [Fact]
    public void Frozen_BeatsCoast()
    {
        AircraftModel ac = Plain();
        ac.EramIsFrozen = true;
        ac.EramIsCoastTrack = true;

        Assert.Equal(("FRZN", false), Build(ac));
    }

    [Fact]
    public void Uncorrelated_ShowsNoMismatch()
    {
        AircraftModel ac = Plain();
        Mismatch(ac);
        ac.EramIsCorrelated = false;

        Assert.Equal(("", false), Build(ac));
    }

    [Fact]
    public void Uncorrelated_ShowsNoNone()
    {
        AircraftModel ac = Plain();
        NoBeacon(ac);
        ac.EramIsCorrelated = false;

        Assert.Equal(("", false), Build(ac));
    }

    [Fact]
    public void Uncorrelated_SpcIsSteady()
    {
        AircraftModel ac = Plain();
        ac.BeaconCode = 7500;
        ac.EramSpcBlinking = true;

        Assert.Equal(("HIJK", false), Build(ac));
    }

    [Fact]
    public void ViewerSector_NullSectorId_FallsBackToTheActingCTcpDigits()
    {
        var viewer = new ScopeViewer(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), null, null);

        Assert.Equal(("44", "ZOA"), EramStatusLine.ViewerSector(viewer));
    }

    [Fact]
    public void ViewerSector_StarsTcp_HasNoSector()
    {
        var viewer = new ScopeViewer(new ActingPosition("NCT_2S", "NCT", "2S"), null, null);

        Assert.Equal((null, "NCT"), EramStatusLine.ViewerSector(viewer));
    }

    [Fact]
    public void ViewerSector_ServerSector_WinsOverTheTcp()
    {
        var viewer = new ScopeViewer(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOB", "45");

        Assert.Equal(("45", "ZOB"), EramStatusLine.ViewerSector(viewer));
    }

    [Theory]
    [InlineData("c44")]
    [InlineData("C4A")]
    public void ViewerSector_NotACAndDigits_HasNoSector(string tcp)
    {
        var viewer = new ScopeViewer(new ActingPosition("OAK_44_CTR", "ZOA", tcp), null, null);

        Assert.Null(EramStatusLine.ViewerSector(viewer).SectorId);
    }

    [Fact]
    public void RecentlyAccepted_NullFacilitiesAndMatchingSectors_ShowsO()
    {
        AircraftModel ac = Plain();
        RecentlyAccepted(ac, "ZOA", "44", forced: false);
        ac.EramRecentHandoffFromFacilityId = null;
        var viewer = new ScopeViewer(new ActingPosition("OAK_44_CTR", null, "C44"), null, null);

        Assert.Equal(("O-40", false), EramStatusLine.Build(ac, viewer));
    }

    [Fact]
    public void RecentlyAccepted_NoViewer_IsHidden()
    {
        AircraftModel ac = Plain();
        RecentlyAccepted(ac, "ZOA", "44", forced: false);

        Assert.Equal(("", false), EramStatusLine.Build(ac, ScopeViewer.None));
    }

    [Fact]
    public void RecentlyAccepted_ViewerFromTheTcpFallback_ShowsO()
    {
        AircraftModel ac = Plain();
        RecentlyAccepted(ac, "ZOA", "44", forced: false);
        var viewer = new ScopeViewer(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), null, null);

        Assert.Equal(("O-40", false), EramStatusLine.Build(ac, viewer));
    }
}
