using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for <see cref="RadarCanvas.FilterAircraft"/>, which decides what the radar draws.
/// Issue #169: a ground aircraft is normally hidden on the radar, but when it carries an active
/// speech bubble and no ground view is showing its airport, the radar must surface it so the
/// SAY / pilot / WARN prompt isn't missed.
/// </summary>
public class RadarCanvasFilterTests
{
    private static readonly DateTime Now = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    private static AircraftModel Ground(string callsign, string? airportId, AircraftSpeechBubble? bubble = null, string status = "Active")
    {
        return new AircraftModel
        {
            Callsign = callsign,
            AircraftType = "C172",
            IsOnGround = true,
            GroundAirportId = airportId,
            SpeechBubble = bubble,
            Status = status,
        };
    }

    private static AircraftSpeechBubble ActiveBubble() => new("Ready to taxi", Now + TimeSpan.FromSeconds(5), SpeechBubbleSeverity.Speech);

    private static AircraftSpeechBubble ExpiredBubble() => new("Ready to taxi", Now - TimeSpan.FromSeconds(1), SpeechBubbleSeverity.Speech);

    private static List<string> Filter(IReadOnlyList<AircraftModel> aircraft, bool showTopDown, bool showSpeechBubbles, string? groundShownAirportId)
    {
        var result = RadarCanvas.FilterAircraft(aircraft, showTopDown, showSpeechBubbles, groundShownAirportId, Now);
        return result.Select(a => a.Callsign).ToList();
    }

    [Fact]
    public void GroundAircraft_NoBubble_HiddenOnRadar()
    {
        var list = Filter([Ground("N1", "OAK")], showTopDown: false, showSpeechBubbles: true, groundShownAirportId: null);
        Assert.Empty(list);
    }

    [Fact]
    public void GroundAircraft_ActiveBubble_GroundViewNotShowingAirport_Surfaced()
    {
        // Ground view shows SFO (or nothing); the talking aircraft is at OAK → surface on radar.
        var list = Filter([Ground("N1", "OAK", ActiveBubble())], showTopDown: false, showSpeechBubbles: true, groundShownAirportId: "SFO");
        Assert.Equal(["N1"], list);
    }

    [Fact]
    public void GroundAircraft_ActiveBubble_NoGroundViewOpen_Surfaced()
    {
        var list = Filter([Ground("N1", "OAK", ActiveBubble())], showTopDown: false, showSpeechBubbles: true, groundShownAirportId: null);
        Assert.Equal(["N1"], list);
    }

    [Fact]
    public void GroundAircraft_ActiveBubble_GroundViewShowingItsAirport_Hidden()
    {
        // Ground view already shows OAK → don't duplicate the bubble onto the radar.
        var list = Filter([Ground("N1", "OAK", ActiveBubble())], showTopDown: false, showSpeechBubbles: true, groundShownAirportId: "OAK");
        Assert.Empty(list);
    }

    [Fact]
    public void GroundAircraft_ActiveBubble_AirportMatchIsCaseInsensitive_Hidden()
    {
        var list = Filter([Ground("N1", "oak", ActiveBubble())], showTopDown: false, showSpeechBubbles: true, groundShownAirportId: "OAK");
        Assert.Empty(list);
    }

    [Fact]
    public void GroundAircraft_ExpiredBubble_Hidden()
    {
        var list = Filter([Ground("N1", "OAK", ExpiredBubble())], showTopDown: false, showSpeechBubbles: true, groundShownAirportId: "SFO");
        Assert.Empty(list);
    }

    [Fact]
    public void GroundAircraft_ActiveBubble_MasterToggleOff_Hidden()
    {
        var list = Filter([Ground("N1", "OAK", ActiveBubble())], showTopDown: false, showSpeechBubbles: false, groundShownAirportId: "SFO");
        Assert.Empty(list);
    }

    [Fact]
    public void GroundAircraft_UnknownAirport_ActiveBubble_Surfaced()
    {
        // Can't match an unknown airport to any ground view → surface rather than risk missing it.
        var list = Filter([Ground("N1", null, ActiveBubble())], showTopDown: false, showSpeechBubbles: true, groundShownAirportId: null);
        Assert.Equal(["N1"], list);
    }

    [Fact]
    public void GroundAircraft_TopDown_AlwaysShown()
    {
        var list = Filter([Ground("N1", "OAK")], showTopDown: true, showSpeechBubbles: false, groundShownAirportId: null);
        Assert.Equal(["N1"], list);
    }

    [Fact]
    public void GroundAircraft_ActiveBubble_ButDelayed_Hidden()
    {
        var list = Filter(
            [Ground("N1", "OAK", ActiveBubble(), status: "Delayed")],
            showTopDown: false,
            showSpeechBubbles: true,
            groundShownAirportId: "SFO"
        );
        Assert.Empty(list);
    }

    [Fact]
    public void AirborneAircraft_AlwaysShown()
    {
        var airborne = new AircraftModel
        {
            Callsign = "N2",
            AircraftType = "C172",
            IsOnGround = false,
            Status = "Active",
        };
        var list = Filter([airborne], showTopDown: false, showSpeechBubbles: true, groundShownAirportId: null);
        Assert.Equal(["N2"], list);
    }
}
