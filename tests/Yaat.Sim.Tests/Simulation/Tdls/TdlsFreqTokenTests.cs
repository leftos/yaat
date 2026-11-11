using System.Globalization;
using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Tdls;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Tdls;

/// <summary>
/// The <c>$FREQ</c> token on its own: the substitution, and the student position's primary frequency read from the real
/// ZOA configuration in the <c>0.000</c> MHz form, whatever the machine's culture.
/// </summary>
public class TdlsFreqTokenTests
{
    /// <summary>OAK clearance delivery, a real ZOA position on 121.100 (NCT TCP 3O).</summary>
    private static readonly TrackOwner OakDelivery = TrackOwner.CreateStars("OAK_DEL", "NCT", 3, "O");

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public TdlsFreqTokenTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static SimScenarioState Scenario(ArtccConfigRoot? artcc, TrackOwner? student) =>
        new()
        {
            ScenarioId = "tdls-freq",
            ScenarioName = "TDLS frequency token",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            ArtccConfig = artcc,
            StudentPosition = student,
        };

    /// <summary>Every position in the tree, depth first.</summary>
    private static IEnumerable<PositionConfig> AllPositions(FacilityConfig facility) =>
        facility.Positions.Concat(facility.ChildFacilities.SelectMany(AllPositions));

    /// <summary>
    /// A position <paramref name="callsign"/> names that only a facility hint reaches: the first listed under an owning
    /// facility other than the first candidate's, on a different frequency. Null when the callsign has none.
    /// </summary>
    private static (PositionConfig Position, string FacilityId)? HintOnlyPosition(ArtccConfigRoot artcc, string callsign)
    {
        List<PositionConfig> candidates = artcc.FindPositionsByCallsign(callsign);
        if (candidates.Count < 2)
        {
            return null;
        }

        string? firstFacility = artcc.ResolvePosition(candidates[0].Id)?.FacilityId;
        var seenFacilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PositionConfig candidate in candidates)
        {
            string? facility = artcc.ResolvePosition(candidate.Id)?.FacilityId;
            if ((facility is null) || !seenFacilities.Add(facility))
            {
                continue;
            }
            if (!string.Equals(facility, firstFacility, StringComparison.OrdinalIgnoreCase) && (candidate.Frequency != candidates[0].Frequency))
            {
                return (candidate, facility);
            }
        }
        return null;
    }

    [Fact]
    public void Apply_ReplacesTheTokenWithTheFrequency() => Assert.Equal("CTC 121.800 TO PUSH", TdlsFreqToken.Apply("CTC $FREQ TO PUSH", "121.800"));

    [Fact]
    public void Apply_WithNoFrequency_LeavesTheValueUnchanged() => Assert.Equal("CTC $FREQ TO PUSH", TdlsFreqToken.Apply("CTC $FREQ TO PUSH", null));

    [Fact]
    public void StudentPrimaryFrequencyMhz_ForARealZoaPosition_IsThatPositionsFrequency()
    {
        if (_zoa is null)
        {
            return;
        }

        Assert.Equal("121.100", TdlsFreqToken.StudentPrimaryFrequencyMhz(Scenario(_zoa, OakDelivery)));
    }

    [Fact]
    public void StudentPrimaryFrequencyMhz_UnderACommaDecimalCulture_StillUsesAPoint()
    {
        if (_zoa is null)
        {
            return;
        }

        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            Assert.Equal("121.100", TdlsFreqToken.StudentPrimaryFrequencyMhz(Scenario(_zoa, OakDelivery)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void StudentPrimaryFrequencyMhz_WithNoStudentPosition_IsNull()
    {
        if (_zoa is null)
        {
            return;
        }

        Assert.Null(TdlsFreqToken.StudentPrimaryFrequencyMhz(Scenario(_zoa, null)));
    }

    [Fact]
    public void StudentPrimaryFrequencyMhz_ForAPositionNotInTheConfig_IsNull()
    {
        if (_zoa is null)
        {
            return;
        }

        Assert.Null(TdlsFreqToken.StudentPrimaryFrequencyMhz(Scenario(_zoa, TrackOwner.CreateStars("XXX_DEL", "NCT", 3, "O"))));
    }

    [Fact]
    public void StudentPrimaryFrequencyMhz_WithNoArtccConfig_IsNull() =>
        Assert.Null(TdlsFreqToken.StudentPrimaryFrequencyMhz(Scenario(null, OakDelivery)));

    [Fact]
    public void StudentPrimaryFrequencyMhz_ForAPositionWithNoFrequency_IsNull()
    {
        if (_zoa is null)
        {
            return;
        }

        // A private deep copy: the ZOA tree is cached for the whole test process and shared with every other test.
        string json = JsonSerializer.Serialize(_zoa, RecordingJsonOptions.Default);
        ArtccConfigRoot copy = JsonSerializer.Deserialize<ArtccConfigRoot>(json, RecordingJsonOptions.Default)!;
        foreach (PositionConfig position in copy.FindPositionsByCallsign(OakDelivery.Callsign))
        {
            position.Frequency = 0;
        }

        Assert.Null(TdlsFreqToken.StudentPrimaryFrequencyMhz(Scenario(copy, OakDelivery)));
    }

    [Fact]
    public void StudentPrimaryFrequencyMhz_ForACallsignUnderTwoFacilities_IsTheHintedFacilitysFrequency()
    {
        if (_zoa is null)
        {
            return;
        }

        (PositionConfig Position, string FacilityId)? hinted = AllPositions(_zoa.Facility)
            .Select(p => p.Callsign)
            .Distinct(StringComparer.Ordinal)
            .Select(callsign => HintOnlyPosition(_zoa, callsign))
            .FirstOrDefault(c => c is not null);
        Assert.True(hinted is not null, "ZOA should list one callsign under two facilities on different frequencies");
        (PositionConfig position, string facilityId) = hinted.Value;

        string? frequency = TdlsFreqToken.StudentPrimaryFrequencyMhz(Scenario(_zoa, TrackOwner.CreateStars(position.Callsign, facilityId, 1, "A")));

        Assert.Equal((position.Frequency / 1_000_000.0).ToString("0.000", CultureInfo.InvariantCulture), frequency);
    }
}
