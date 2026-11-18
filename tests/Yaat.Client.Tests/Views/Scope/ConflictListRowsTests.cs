using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope.Dock;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>The LA/CA/MCI list's rows, in CRC's format, one per conflict pair in broadcast order.</summary>
public class ConflictListRowsTests
{
    /// <summary>The texts of the rows of <paramref name="pairs"/>, with no member on the scope.</summary>
    private static IReadOnlyList<string> TextsOf(params ConflictAlertDto[] pairs) => [.. ConflictListRows.Rows(pairs, _ => null).Select(r => r.Text)];

    [Fact]
    public void APair_IsSortedPaddedTo15_ThenThreeSpacesAndCa()
    {
        IReadOnlyList<string> rows = TextsOf(new ConflictAlertDto("CA_AAL22_UAL1", "UAL1", "AAL22"));

        Assert.Equal(["AAL22*UAL1        CA"], rows);
        Assert.Equal(15 + 3 + 2, rows[0].Length);
    }

    [Fact]
    public void TwoPairs_KeepBroadcastOrder()
    {
        IReadOnlyList<string> rows = TextsOf(
            new ConflictAlertDto("CA_SKW5_UAL9", "UAL9", "SKW5"),
            new ConflictAlertDto("CA_AAL1_DAL2", "AAL1", "DAL2")
        );

        Assert.Equal(["SKW5*UAL9         CA", "AAL1*DAL2         CA"], rows);
    }

    [Fact]
    public void NoPairs_GiveNoRows() => Assert.Empty(TextsOf());

    private static AircraftModel Tracked(string callsign) => new() { Callsign = callsign, Owner = "OAK_44_CTR" };

    /// <summary>An untracked target with no flight plan squawking 1200: a Mode C intruder.</summary>
    private static AircraftModel Intruder(string callsign) => new() { Callsign = callsign, BeaconCode = 1200 };

    private static Func<string, AircraftModel?> Find(params AircraftModel[] aircraft) => cs => aircraft.FirstOrDefault(a => a.Callsign == cs);

    [Fact]
    public void AStarsPairWithAModeCIntruder_IsAnMciRow_WithTheIntrudersBeacon()
    {
        IReadOnlyList<ConflictListRow> rows = ConflictListRows.Rows(
            [new ConflictAlertDto("CA_N123_SKW5", "SKW5", "N123")],
            Find(Tracked("SKW5"), Intruder("N123"))
        );

        Assert.Equal([new ConflictListRow("1200*SKW5         MCI", false)], rows);
    }

    [Fact]
    public void AStarsPairOfTrackedTargets_IsACaRow()
    {
        IReadOnlyList<ConflictListRow> rows = ConflictListRows.Rows(
            [new ConflictAlertDto("CA_AAL1_DAL2", "AAL1", "DAL2")],
            Find(Tracked("AAL1"), Tracked("DAL2"))
        );

        Assert.Equal([new ConflictListRow("AAL1*DAL2         CA", false)], rows);
    }

    [Fact]
    public void AnEramPair_IsAnEramRow()
    {
        IReadOnlyList<ConflictListRow> rows = ConflictListRows.Rows(
            [new ConflictAlertDto("ESTCA_AAL1_DAL2", "AAL1", "DAL2", ConflictAlertKind.Eram)],
            Find(Tracked("AAL1"), Tracked("DAL2"))
        );

        Assert.Equal([new ConflictListRow("AAL1*DAL2         ERAM", false)], rows);
    }

    [Fact]
    public void AnEramPairWithAnIntruder_IsAnEramMciRow_WithTheIntrudersBeacon()
    {
        IReadOnlyList<ConflictListRow> rows = ConflictListRows.Rows(
            [new ConflictAlertDto("ESTCA_N123_SKW5410", "SKW5410", "N123", ConflictAlertKind.Eram, IntruderCallsign: "N123")],
            Find(Tracked("SKW5410"), Intruder("N123"))
        );

        Assert.Equal([new ConflictListRow("1200*SKW5410      ERAM MCI", false)], rows);
    }

    [Fact]
    public void ASuppressedStarsPair_ListsDimmed()
    {
        IReadOnlyList<ConflictListRow> rows = ConflictListRows.Rows(
            [new ConflictAlertDto("CA_AAL1_DAL2", "AAL1", "DAL2", Suppressed: true, Acknowledged: true)],
            Find(Tracked("AAL1"), Tracked("DAL2"))
        );

        ConflictListRow row = Assert.Single(rows);
        Assert.Equal("AAL1*DAL2         CA", row.Text);
        Assert.True(row.Dimmed);
        Assert.Equal(ConflictListRow.DimmedOpacity, row.Opacity);
    }

    [Fact]
    public void ALongPair_IsPaddedNeverCut()
    {
        IReadOnlyList<string> rows = TextsOf(new ConflictAlertDto("CA_ABCDEFGH_IJKLMNO", "ABCDEFGH", "IJKLMNO"));

        Assert.Equal(["ABCDEFGH*IJKLMNO   CA"], rows);
    }
}
