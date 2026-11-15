using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope.Dock;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>The LA/CA/MCI list's rows, in CRC's format, one per conflict pair in broadcast order.</summary>
public class ConflictListRowsTests
{
    [Fact]
    public void APair_IsSortedPaddedTo15_ThenThreeSpacesAndCa()
    {
        IReadOnlyList<string> rows = ConflictListRows.Build([new ConflictAlertDto("CA_AAL22_UAL1", "UAL1", "AAL22")]);

        Assert.Equal(["AAL22*UAL1        CA"], rows);
        Assert.Equal(15 + 3 + 2, rows[0].Length);
    }

    [Fact]
    public void TwoPairs_KeepBroadcastOrder()
    {
        IReadOnlyList<string> rows = ConflictListRows.Build([
            new ConflictAlertDto("CA_SKW5_UAL9", "UAL9", "SKW5"),
            new ConflictAlertDto("CA_AAL1_DAL2", "AAL1", "DAL2"),
        ]);

        Assert.Equal(["SKW5*UAL9         CA", "AAL1*DAL2         CA"], rows);
    }

    [Fact]
    public void NoPairs_GiveNoRows() => Assert.Empty(ConflictListRows.Build([]));

    [Fact]
    public void ALongPair_IsPaddedNeverCut()
    {
        IReadOnlyList<string> rows = ConflictListRows.Build([new ConflictAlertDto("CA_ABCDEFGH_IJKLMNO", "ABCDEFGH", "IJKLMNO")]);

        Assert.Equal(["ABCDEFGH*IJKLMNO   CA"], rows);
    }
}
