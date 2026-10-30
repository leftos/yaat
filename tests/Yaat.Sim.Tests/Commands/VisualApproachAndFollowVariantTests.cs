using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// A varied solo pilot reads back a visual approach clearance and an airborne follow by the frequency's load band: terse on a
/// busy frequency, a courtesy on a quiet one. Only the spoken form varies; the terminal texts stay canonical in every band. A
/// busy pilot never drops "visual", "landing runway", the pattern side or the traffic to follow (7110.65 7-4-3), and keeps
/// "the traffic" and the callsign on a follow (7110.65 7-2-1, AIM 5-5-12).
/// </summary>
public class VisualApproachAndFollowVariantTests
{
    private const string Cs = "november one two three";

    private static readonly FrequencyLoadBand[] Bands = [FrequencyLoadBand.Quiet, FrequencyLoadBand.Normal, FrequencyLoadBand.Busy];

    public VisualApproachAndFollowVariantTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // Five miles east of KOAK 28R, heading for it, with the field and the traffic to follow in sight.
    private static AircraftState MakeKoakArrival()
    {
        var aircraft = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(295),
            Altitude = 3000,
            IndicatedAirspeed = 180,
            Position = new LatLon(37.735, -122.12),
            FlightPlan = new AircraftFlightPlan { Destination = "OAK" },
        };
        aircraft.Approach.HasReportedFieldInSight = true;
        aircraft.Approach.HasReportedTrafficInSight = true;
        aircraft.Approach.LastReportedTrafficCallsign = "N456";
        return aircraft;
    }

    // Eight miles south-east of Petaluma (O69), an untowered field in ZOA.
    private static AircraftState MakePetalumaArrival() =>
        new()
        {
            Callsign = "N123",
            AircraftType = "C172",
            TrueHeading = new TrueHeading(200),
            TrueTrack = new TrueHeading(200),
            Altitude = 3000,
            IndicatedAirspeed = 120,
            Position = new LatLon(38.17, -122.50),
            FlightPlan = new AircraftFlightPlan { Destination = "O69" },
            Approach = { HasReportedFieldInSight = true },
        };

    // A VFR aircraft south-bound at 3,000 ft.
    private static AircraftState MakeAirborne()
    {
        var aircraft = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = "KOAK" },
            Position = new LatLon(37.75, -122.221),
            TrueHeading = new TrueHeading(180),
            TrueTrack = new TrueHeading(180),
            Altitude = 3000,
            IndicatedAirspeed = 90,
            Phases = new PhaseList(),
        };
        aircraft.Targets.TargetTrueHeading = new TrueHeading(180);
        return aircraft;
    }

    private static ToweredAirports Zoa() =>
        ToweredAirports.From([TestArtccConfig.LoadZoa() ?? throw new InvalidOperationException("ZOA snapshot missing")]);

    private static WeatherProfile Wind(double directionMagnetic, double speedKt) =>
        new() { WindLayers = [new WindLayer { Direction = directionMagnetic, Speed = speedKt }] };

    private static PilotSpeechContext Varied(FrequencyLoadBand band, int seed, int second) =>
        new(PilotPersonality.Varied, band, seed, second)
        {
            ShortenCallsign = false,
            GroupContactFrequency = false,
            CurrentIndicatedAirspeed = null,
        };

    private static CompoundCommand ParseCompound(string text) => CommandParser.ParseCompound(text).Value!;

    /// <summary>The readbacks a varied pilot speaks over seeds 0-4 and seconds 0-199 for a compound dispatched once.</summary>
    private static List<PilotSpeechText> DispatchedReadbacks(string text, FrequencyLoadBand band, AircraftState ac, DispatchContext context)
    {
        CompoundCommand compound = ParseCompound(text);
        CommandResult result = CommandDispatcher.Dispatch(compound.Blocks[0].Commands[0], ac, context);
        Assert.True(result.Success, result.Message);
        return Sweep(ctx => PilotResponder.BuildReadbackAsApplied(compound, result, ac, ctx, ActiveRunways.Empty), band);
    }

    private static List<PilotSpeechText> KoakReadbacks(string text, FrequencyLoadBand band) =>
        DispatchedReadbacks(text, band, MakeKoakArrival(), TestDispatch.Context(new Random(1), toweredAirports: Zoa()));

    private static List<PilotSpeechText> FollowReadbacks(FrequencyLoadBand band)
    {
        CompoundCommand compound = ParseCompound("FOLLOW N456");
        AircraftState ac = MakeAirborne();
        return Sweep(ctx => PilotResponder.BuildReadback(compound, ac, ctx, ActiveRunways.Empty), band);
    }

    private static List<PilotSpeechText> Sweep(Func<PilotSpeechContext, PilotSpeechText?> build, FrequencyLoadBand band) =>
        [
            .. Enumerable
                .Range(0, 5)
                .SelectMany(seed =>
                    Enumerable.Range(0, 200).Select(second => build(Varied(band, seed, second)) ?? throw new InvalidOperationException("no readback"))
                ),
        ];

    private static void AssertSpoken(IEnumerable<string> spoken, params string[] expected)
    {
        HashSet<string> actual = [.. spoken];
        string nl = Environment.NewLine;
        string message = $"Expected exactly:{nl}{string.Join(nl, expected)}{nl}Spoken:{nl}{string.Join(nl, actual)}";
        Assert.True(actual.SetEquals(expected), message);
    }

    private static int Occurrences(string text, string term)
    {
        int count = 0;
        for (int at = text.IndexOf(term, StringComparison.Ordinal); at >= 0; at = text.IndexOf(term, at + term.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void RunwayForm_Busy_SpeaksOnlyTheTerseRowsWithTheTail()
    {
        const string Runway = "cleared visual runway two eight right, left traffic, follow the traffic";
        const string Bare = "cleared visual two eight right, left traffic, follow the traffic";

        List<string> spoken = [.. KoakReadbacks("CVA 28R LEFT FOLLOW N456", FrequencyLoadBand.Busy).Select(r => r.Tts)];

        Assert.All(
            spoken,
            tts => Assert.True(tts.StartsWith(Runway, StringComparison.Ordinal) || tts.StartsWith(Bare, StringComparison.Ordinal), tts)
        );
        Assert.Contains(spoken, tts => tts.StartsWith(Runway, StringComparison.Ordinal));
        Assert.Contains(spoken, tts => tts.StartsWith(Bare, StringComparison.Ordinal));
    }

    [Fact]
    public void RunwayForm_Quiet_OffersTheThanksRowAndNoFlavourOnTop()
    {
        List<string> spoken = [.. KoakReadbacks("CVA 28R", FrequencyLoadBand.Quiet).Select(r => r.Tts)];

        Assert.Contains($"cleared visual approach runway two eight right, {Cs}.", spoken);
        Assert.Contains($"cleared visual approach runway two eight right, {Cs}, thanks.", spoken);
        Assert.DoesNotContain(spoken, tts => Occurrences(tts, "thanks") > 1);
        Assert.DoesNotContain(spoken, tts => tts.StartsWith("alright", StringComparison.Ordinal));
    }

    [Fact]
    public void RunwayForm_EveryBand_KeepsTerminalAndRpoTerminalCanonical()
    {
        foreach (FrequencyLoadBand band in Bands)
        {
            List<PilotSpeechText> readbacks = KoakReadbacks("CVA 28R LEFT FOLLOW N456", band);

            Assert.All(readbacks, r => Assert.Equal("cleared visual approach runway 28R, left traffic, follow the traffic", r.Terminal));
            Assert.All(readbacks, r => Assert.Equal("cleared visual approach runway 28R, left traffic, follow N456", r.TerminalForRpo));
        }
    }

    [Fact]
    public void AirportLevel_Busy_SpeaksVisualToTheFieldKeepingTheLandingRunway()
    {
        List<string> spoken =
        [
            .. DispatchedReadbacks(
                    "CVA O69",
                    FrequencyLoadBand.Busy,
                    MakePetalumaArrival(),
                    TestDispatch.Context(new Random(1), weather: Wind(290, 12), toweredAirports: Zoa())
                )
                .Select(r => r.Tts),
        ];

        Assert.Contains($"cleared visual to Petaluma, landing runway two nine, {Cs}.", spoken);
        Assert.All(spoken, tts => Assert.Contains("landing runway", tts, StringComparison.Ordinal));
    }

    [Fact]
    public void AirborneFollow_Busy_SaysFollowingTheTrafficWithTheCallsign() =>
        AssertSpoken(FollowReadbacks(FrequencyLoadBand.Busy).Select(r => r.Tts), $"following the traffic, {Cs}.");

    [Fact]
    public void BareCapp_ClearingAnExpectedVisual_SpeaksTheVisualRows()
    {
        foreach (FrequencyLoadBand band in Bands)
        {
            AircraftState ac = MakeKoakArrival();
            DispatchContext context = TestDispatch.Context(new Random(1), toweredAirports: Zoa());
            CommandResult expect = CommandDispatcher.Dispatch(ParseCompound("EAPP VIS28R").Blocks[0].Commands[0], ac, context);
            Assert.True(expect.Success, expect.Message);

            List<PilotSpeechText> readbacks = DispatchedReadbacks("CAPP", band, ac, context);

            Assert.All(readbacks, r => Assert.Equal("cleared visual approach runway 28R", r.Terminal));
            if (band == FrequencyLoadBand.Busy)
            {
                AssertSpoken(readbacks.Select(r => r.Tts), $"cleared visual runway two eight right, {Cs}.", $"cleared visual two eight right, {Cs}.");
            }
        }
    }

    [Fact]
    public void AirborneFollow_Quiet_OffersWellFollow() =>
        AssertSpoken(FollowReadbacks(FrequencyLoadBand.Quiet).Select(r => r.Tts), $"follow the traffic, {Cs}.", $"we'll follow the traffic, {Cs}.");

    [Fact]
    public void AirborneFollow_EveryBand_KeepsTerminalAndRpoTerminalCanonical()
    {
        foreach (FrequencyLoadBand band in Bands)
        {
            List<PilotSpeechText> readbacks = FollowReadbacks(band);

            Assert.All(readbacks, r => Assert.Equal("follow the traffic", r.Terminal));
            Assert.All(readbacks, r => Assert.Equal("follow N456", r.TerminalForRpo));
        }
    }
}
