using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Pilot;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Friendly waypoint names (e.g. <c>VPCBT</c> → "Lake Chabot") rendered as "Name (ID)" in
/// operator-facing terminal text — command responses, pilot readbacks, and AT-condition lead-ins.
/// Phonetic-only pronunciation hints (e.g. <c>SYRAH</c> → "see rah") must never leak into the
/// display; they stay bare identifiers.
/// </summary>
public class FixDisplayNameTests
{
    public FixDisplayNameTests()
    {
        // The friendly-name lookups walk NavigationDatabase.Instance, which loads the bundled
        // ZOA FixPronunciations (incl. displayName) and CustomFixes. Pin the singleton up-front
        // so the class doesn't race another class mid-populating it.
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft() =>
        new()
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(90),
            Altitude = 8000,
            Position = new LatLon(37.7, -122.2),
        };

    // --- Data lookup: displayName separated from phonetic pronunciation ---

    [Fact]
    public void GetFixDisplayName_VisualPoint_ReturnsAuthoredName()
    {
        Assert.Equal("Lake Chabot", NavigationDatabase.Instance.GetFixDisplayName("VPCBT"));
        Assert.Equal("Oakland Coliseum", NavigationDatabase.Instance.GetFixDisplayName("VPCOL"));
    }

    [Fact]
    public void GetFixDisplayName_CustomFix_ReturnsFriendlyName()
    {
        Assert.Equal("Oakland Runway 30 Numbers", NavigationDatabase.Instance.GetFixDisplayName("OAK30NUM"));
    }

    [Fact]
    public void GetFixDisplayName_PhoneticOnlyHint_ReturnsNull()
    {
        // SYRAH is in ambiguous.json as a pure phonetic spelling ("see rah") with no displayName.
        Assert.Null(NavigationDatabase.Instance.GetFixDisplayName("SYRAH"));
    }

    [Fact]
    public void GetFixDisplayName_UnknownFix_ReturnsNull()
    {
        Assert.Null(NavigationDatabase.Instance.GetFixDisplayName("SUNOL"));
    }

    // --- Presentation helpers ---

    [Fact]
    public void FixDisplayText_NamedFix_RendersNameAndId()
    {
        Assert.Equal("Lake Chabot (VPCBT)", PhraseologyVerbalizer.FixDisplayText("VPCBT"));
        Assert.Equal("LAKE CHABOT (VPCBT)", PhraseologyVerbalizer.FixDisplayTextUpper("VPCBT"));
    }

    [Fact]
    public void FixDisplayText_PhoneticOnlyFix_RendersBareId()
    {
        Assert.Equal("SYRAH", PhraseologyVerbalizer.FixDisplayText("SYRAH"));
        Assert.Equal("SYRAH", PhraseologyVerbalizer.FixDisplayTextUpper("SYRAH"));
    }

    [Fact]
    public void FixDisplayText_LowercaseInput_NormalizesId()
    {
        Assert.Equal("Lake Chabot (VPCBT)", PhraseologyVerbalizer.FixDisplayText("vpcbt"));
    }

    // --- Command-response messages (RPO terminal) ---

    [Fact]
    public void CrossFixResponse_NamedFix_ShowsFriendlyName()
    {
        var aircraft = MakeAircraft();
        var cmd = new CrossFixCommand("VPCBT", 37.7197, -122.1064, 3000, CrossFixAltitudeType.At, null);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Contains("LAKE CHABOT (VPCBT)", result.Message);
    }

    [Fact]
    public void CrossFixResponse_PhoneticOnlyFix_StaysBareId_NoPhoneticLeak()
    {
        var aircraft = MakeAircraft();
        var cmd = new CrossFixCommand("SYRAH", 37.8, -121.9, 5000, CrossFixAltitudeType.At, null);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Contains("SYRAH", result.Message);
        Assert.DoesNotContain("see rah", result.Message);
        Assert.DoesNotContain("(", result.Message);
    }

    [Fact]
    public void DirectToResponse_NamedFix_ShowsFriendlyName()
    {
        var aircraft = MakeAircraft();
        var cmd = new DirectToCommand([new ResolvedFix("VPCBT", 37.7197, -122.1064)], []);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Contains("LAKE CHABOT (VPCBT)", result.Message);
    }

    // --- Pilot readback (terminal channel) ---

    [Fact]
    public void DirectToReadback_NamedFix_ShowsFriendlyNameNaturalCase()
    {
        var cmd = new DirectToCommand([new ResolvedFix("VPCBT", 37.7197, -122.1064)], []);

        var terminal = PhraseologyVerbalizer.VerbalizeTerminal(cmd);

        Assert.NotNull(terminal);
        Assert.Contains("Lake Chabot (VPCBT)", terminal);
    }

    // --- AT <fix> condition lead-in ---

    [Fact]
    public void AtFixCondition_Terminal_ShowsFriendlyName()
    {
        var lead = PilotResponder.FormatConditionTerminal(new AtFixCondition("VPCBT", 37.7197, -122.1064));

        Assert.Equal("at Lake Chabot (VPCBT),", lead);
    }

    [Fact]
    public void AtFixCondition_Spoken_SpeaksFriendlyName()
    {
        var lead = PilotResponder.FormatCondition(new AtFixCondition("VPCBT", 37.7197, -122.1064));

        Assert.Equal("at Lake Chabot,", lead);
    }
}
