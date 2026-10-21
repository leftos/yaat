using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Client.Tests;

/// <summary>
/// The solo advisory's live check (<see cref="SoloAdvisoryModel.Check"/>), the sim's own matcher run over the client's aircraft:
/// each form resolves with a mark per part, a partial call names the parts it needs, a call nothing fits shows the sim's reply,
/// a call can resolve to other traffic than the pointed row, and Tab offers the pointed aircraft's parts as the pre-fill
/// setting allows.
/// </summary>
public class SoloAdvisoryLiveCheckTests
{
    private const string Me = "N52TW";
    private static readonly LatLon Here = new(37.72, -122.22);
    private static readonly TrafficAdvisoryCandidate Recipient = new(Me, Here, 300, 300, 2000, "PA28", "PA28", false, null, null);
    private static readonly TrafficAdvisoryCandidate N22Rx = new Target("N22RX", "SR22")
    {
        Clock = 2,
        Nm = 4,
        Track = 270,
        AltitudeFt = 3500,
    }.Candidate;

    [Fact]
    public void Empty_HintsToTypeTheCall() => Assert.Equal("type the call as you would say it", CheckOf("", N22Rx).Text);

    [Fact]
    public void Filter_SaysWhatTheListIsFilteredBy() => Assert.Equal("filtering the list by N22", CheckOf("n22", N22Rx).Text);

    [Theory]
    [InlineData("2 4", "needs ‹direction› ‹type›")]
    [InlineData("nr 2", "needs ‹type›")]
    [InlineData("base l", "needs ‹miles› ‹runway› ‹type›")]
    [InlineData("over", "needs ‹reporting point› ‹type›")]
    public void Partial_OfEachForm_NamesWhatItNeeds(string typed, string line)
    {
        AdvisoryCheck check = CheckOf(typed, N22Rx);

        Assert.Equal(AdvisoryCheckState.Incomplete, check.State);
        Assert.Equal(line, check.Text);
        Assert.Null(check.ResolvedCallsign);
    }

    [Fact]
    public void Radar_TrueCall_ScoresClean()
    {
        AdvisoryCheck check = CheckOf("2 4 w sr22 035", N22Rx);

        Assert.Equal("→ N22RX clock ✓ miles ✓ direction ✓ altitude ✓ type ✓ · scores clean", check.Text);
        Assert.Equal("N22RX", check.ResolvedCallsign);
    }

    [Fact]
    public void Radar_OffParts_GradeEachAndReadImprecise()
    {
        AdvisoryCheck check = CheckOf("3 5 w c172 040", N22Rx);

        Assert.Equal("→ N22RX clock ✓ ±1 h miles ✓ ±1 NM direction ✓ altitude ≈ 500 ft off type ≈ is SR22 · imprecise", check.Text);
    }

    [Fact]
    public void Radar_DirectionOff_GradesOctants() =>
        Assert.Equal("→ N22RX clock ✓ miles ✓ direction ≈ 1 octant off type ✓ · imprecise", CheckOf("2 4 nw sr22", N22Rx).Text);

    [Fact]
    public void NoFit_ShowsTheSimsReply()
    {
        AdvisoryCheck check = CheckOf("8 4 w sr22", N22Rx);

        Assert.Equal(AdvisoryCheckState.Refused, check.State);
        Assert.StartsWith("✗ no traffic fits the sim would answer “Unable, no aircraft matches traffic 8 o'clock, 4 mile(s)", check.Text);
        Assert.Null(check.ResolvedCallsign);
    }

    [Fact]
    public void Call_CanResolveToOtherTrafficThanThePointedRow()
    {
        TrafficAdvisoryCandidate pointed = new Target("N811CP", "C182")
        {
            Clock = 4,
            Nm = 7,
            Track = 180,
            AltitudeFt = 2500,
        }.Candidate;
        TrafficAdvisoryCandidate other = new Target("N4417Q", "C182")
        {
            Clock = 6,
            Nm = 5,
            Track = 315,
            AltitudeFt = 2000,
        }.Candidate;

        AdvisoryCheck check = CheckOf("5 6 nw c182 020", pointed, other);

        Assert.Equal("N4417Q", check.ResolvedCallsign);
        Assert.NotEqual(pointed.Callsign, check.ResolvedCallsign);
    }

    [Fact]
    public void Nose_Resolves() =>
        Assert.Equal(
            "→ N738SP position ✓ miles ✓ type ✓ · scores clean",
            CheckOf(
                "nr 2 c172",
                new Target("N738SP", "C172")
                {
                    Clock = 1.5,
                    Nm = 2,
                    Track = 90,
                    AltitudeFt = 1000,
                }.Candidate
            ).Text
        );

    [Fact]
    public void Pattern_Resolves()
    {
        TrafficAdvisoryCandidate onBase = new Target("N9152M", "C152")
        {
            Clock = 11,
            Nm = 4,
            Track = 0,
            AltitudeFt = 900,
        }.Candidate with
        {
            AssignedRunwayId = "28L",
            PatternPosition = new PatternPosition(PatternEntryLeg.Base, PatternDirection.Left, 2.2),
        };

        Assert.Equal("→ N9152M leg ✓ miles ✓ type ✓ · scores clean", CheckOf("base l 2 28l c152", onBase).Text);
    }

    [Fact]
    public void Over_ResolvesNearThePoint_AndAnUnknownPointIsRefused()
    {
        LatLon vpcol = GeoMath.ProjectPoint(N22Rx.Position, new TrueHeading(90), 0.5);
        LatLon? fixes(string name) => (name == "VPCOL") ? vpcol : null;

        Assert.Equal(
            "→ N22RX point ✓ type ✓ · scores clean",
            SoloAdvisoryModel.Check(SoloAdvisoryText.Read("over vpcol sr22"), Me, [Recipient, N22Rx], fixes).Text
        );
        Assert.Equal(
            "✗ no traffic fits the sim would answer “Unable, unknown landmark NOFIX”",
            SoloAdvisoryModel.Check(SoloAdvisoryText.Read("over nofix sr22"), Me, [Recipient, N22Rx], fixes).Text
        );
    }

    [Fact]
    public void CompleteButUnparsed_ShowsTheParsersReason()
    {
        AdvisoryCheck check = CheckOf("2 4 w sr22 035 9", N22Rx);

        Assert.Equal(AdvisoryCheckState.Invalid, check.State);
        Assert.StartsWith("✗ RTIS descriptive form requires", check.Text);
    }

    [Fact]
    public void TrueValues_TabOffersEveryPart()
    {
        IReadOnlyDictionary<AdvisoryPart, string>? truth = SoloAdvisoryModel.TruthOf(AdvisoryForm.Radar, Recipient, N22Rx, []);

        Assert.Equal(new AdvisoryGhost("Tab 2 clock", "2", null), GhostOf("", truth, SoloAdvisoryPrefill.TrueValues));
        Assert.Equal(new AdvisoryGhost("Tab W direction", "W", null), GhostOf("2 4 ", truth, SoloAdvisoryPrefill.TrueValues));
        Assert.Equal(new AdvisoryGhost("Tab 035 altitude (optional)", "035", null), GhostOf("2 4 w sr22", truth, SoloAdvisoryPrefill.TrueValues));
        Assert.Equal(
            new AdvisoryGhost(SoloAdvisoryModel.CompleteGhost, null, null),
            GhostOf("2 4 w sr22 035", truth, SoloAdvisoryPrefill.TrueValues)
        );
    }

    [Fact]
    public void LeftForTheStudent_TabWithholdsClockMilesAndDirection()
    {
        IReadOnlyDictionary<AdvisoryPart, string>? truth = SoloAdvisoryModel.TruthOf(AdvisoryForm.Radar, Recipient, N22Rx, []);

        Assert.Equal(new AdvisoryGhost("next: ‹clock›", null, null), GhostOf("", truth, SoloAdvisoryPrefill.LeftForStudent));
        Assert.Equal(new AdvisoryGhost("next: ‹direction›", null, null), GhostOf("2 4", truth, SoloAdvisoryPrefill.LeftForStudent));
        Assert.Equal(new AdvisoryGhost("Tab SR22 type", "SR22", null), GhostOf("2 4 w", truth, SoloAdvisoryPrefill.LeftForStudent));
    }

    [Fact]
    public void Ghost_WithoutAPointedRow_AndForAFilter()
    {
        List<MenuAirborneTrafficRow> rows = [new(new MenuTrafficRow("N22RX", "SR22", 4, 2, 1500), "westbound", null)];

        Assert.Equal(
            "next: ‹clock› · point at a row for help",
            SoloAdvisoryModel.Ghost(SoloAdvisoryText.Read(""), AdvisoryForm.Radar, null, false, SoloAdvisoryPrefill.TrueValues).Text
        );
        Assert.Equal(new AdvisoryGhost("Tab picks N22RX", null, "N22RX"), SoloAdvisoryModel.PickGhost("N22", rows));
        Assert.Equal(SoloAdvisoryModel.NoPickGhost, SoloAdvisoryModel.PickGhost("ZZ", rows).Text);
    }

    [Fact]
    public void UnavailableForms_GiveTheirReasons()
    {
        Assert.Null(SoloAdvisoryModel.TruthOf(AdvisoryForm.Pattern, Recipient, N22Rx, []));
        Assert.Equal("N22RX is in no pattern leg", SoloAdvisoryModel.UnavailableReason(AdvisoryForm.Pattern, "N22RX", null));
        Assert.Equal(
            "N22RX is not within 2 NM of a reporting point",
            SoloAdvisoryModel.UnavailableReason(AdvisoryForm.Over, "N22RX", SoloAdvisoryModel.TruthOf(AdvisoryForm.Over, Recipient, N22Rx, []))
        );
        Assert.Null(
            SoloAdvisoryModel.UnavailableReason(AdvisoryForm.Radar, "N22RX", SoloAdvisoryModel.TruthOf(AdvisoryForm.Radar, Recipient, N22Rx, []))
        );
    }

    [Fact]
    public void ReportingPoints_WithinTwoNmNearestFirst_AtMostSix() =>
        Assert.Equal(
            ["A", "B", "C", "D", "E", "F"],
            SoloAdvisoryModel.ReportingPoints([("A", 0.1), ("B", 0.2), ("C", 0.3), ("D", 0.4), ("E", 0.5), ("F", 0.6), ("G", 0.7), ("H", 2.5)])
        );

    [Fact]
    public void Spoken_ReadsACompleteCallOnly()
    {
        Assert.StartsWith("Traffic, 2 o'clock, 4 miles, westbound", SoloAdvisoryModel.Spoken(SoloAdvisoryText.Read("2 4 w sr22 035")));
        Assert.StartsWith("Traffic, off your nose and to the right, 2 miles", SoloAdvisoryModel.Spoken(SoloAdvisoryText.Read("nr 2 c172")));
        Assert.Null(SoloAdvisoryModel.Spoken(SoloAdvisoryText.Read("2 4")));
    }

    private static AdvisoryCheck CheckOf(string typed, params TrafficAdvisoryCandidate[] traffic) =>
        SoloAdvisoryModel.Check(SoloAdvisoryText.Read(typed), Me, [Recipient, .. traffic], _ => null);

    private static AdvisoryGhost GhostOf(string typed, IReadOnlyDictionary<AdvisoryPart, string>? truth, SoloAdvisoryPrefill prefill)
    {
        AdvisoryReading reading = SoloAdvisoryText.Read(typed);
        return SoloAdvisoryModel.Ghost(reading, reading.Form ?? AdvisoryForm.Radar, truth, true, prefill);
    }

    /// <summary>Another aircraft placed by clock and miles off N52TW (heading 300), flying <see cref="Track"/>.</summary>
    private sealed record Target(string Callsign, string Type)
    {
        public required double Clock { get; init; }

        public required double Nm { get; init; }

        public required double Track { get; init; }

        public required double AltitudeFt { get; init; }

        public TrafficAdvisoryCandidate Candidate =>
            new(
                Callsign,
                GeoMath.ProjectPoint(Here, new TrueHeading((300 + (Clock * 30)) % 360), Nm),
                0,
                Track,
                AltitudeFt,
                Type,
                Type,
                false,
                null,
                null
            );
    }
}
