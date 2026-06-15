using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests;

/// <summary>
/// Navaids are spoken with their actual facility type from the CIFP class field (ARINC 424
/// field 5.35) — "Mendocino VORTAC", "Woodside VOR" — rather than defaulting every navaid to
/// "VOR". Tests skip when a navaid isn't present in the bundled CIFP.
/// </summary>
public class NavaidTypePhraseologyTests
{
    public NavaidTypePhraseologyTests() => TestVnasData.EnsureInitialized();

    private static bool Known(string code) => NavigationDatabase.Instance.GetNavaidName(code) is not null;

    [Theory]
    [InlineData("ENI", "VORTAC")] // class VT.. — VOR + TACAN
    [InlineData("RBL", "VORTAC")]
    [InlineData("SNS", "VORTAC")]
    [InlineData("OSI", "VOR")] // class VD.. — VOR/DME, spoken "VOR"
    [InlineData("OAK", "VOR")]
    [InlineData("SFO", "VOR")]
    public void GetNavaidType_ClassifiesFromCifpClassField(string code, string expected)
    {
        if (!Known(code))
        {
            return;
        }

        Assert.Equal(expected, NavigationDatabase.Instance.GetNavaidType(code));
    }

    [Theory]
    [InlineData("ENI", "Mendocino VORTAC")]
    [InlineData("RBL", "Red Bluff VORTAC")]
    public void SpellFix_Vortac_SaysVortacNotVor(string code, string expected)
    {
        if (!Known(code))
        {
            return;
        }

        Assert.Equal(expected, PhraseologyVerbalizer.SpellFix(code));
    }

    [Theory]
    [InlineData("OSI", "Woodside VOR")] // VOR/DME stays "VOR"
    [InlineData("OAK", "Oakland VOR")]
    public void SpellFix_VorDme_SaysVor(string code, string expected)
    {
        if (!Known(code))
        {
            return;
        }

        Assert.Equal(expected, PhraseologyVerbalizer.SpellFix(code));
    }
}
