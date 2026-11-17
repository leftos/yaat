using Xunit;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.FieldMenus;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The ERAM scope-data entries a field click produces (<see cref="ScopeDataEntries"/>), each the exact text the server's ERAM
/// parsers take: altitudes as three zero-padded digits in hundreds of feet, the flight ID last.
/// </summary>
public class ScopeDataEntriesTests
{
    public static TheoryData<string, string> Entries =>
        new()
        {
            { ScopeDataEntries.FpAltitude(350, "UAL238"), "QZ 350 UAL238" },
            { ScopeDataEntries.Interim(110, "UAL238"), "QQ 110 UAL238" },
            { ScopeDataEntries.Local(80, "UAL238"), "QQ L080 UAL238" },
            { ScopeDataEntries.Procedure(120, "UAL238"), "QQ P120 UAL238" },
            { ScopeDataEntries.InterimWithCera(90, "UAL238"), "QQ R090 UAL238" },
            { ScopeDataEntries.CeraOnly(70, "UAL238"), "QR 070 UAL238" },
            { ScopeDataEntries.ClearInterimOrProcedure("UAL238"), "QQ UAL238" },
            { ScopeDataEntries.ClearLocal("UAL238"), "QQ L UAL238" },
            { ScopeDataEntries.HsfHeading("H270", "UAL238"), "QS H270 UAL238" },
            { ScopeDataEntries.HsfSpeed("S250", "UAL238"), "QS /S250 UAL238" },
            { ScopeDataEntries.HsfClearHeading("UAL238"), "QS */ UAL238" },
            { ScopeDataEntries.HsfClearSpeed("UAL238"), "QS /* UAL238" },
            { ScopeDataEntries.HsfClearAll("UAL238"), "QS * UAL238" },
            { ScopeDataEntries.HsfFreeText("HOLD", "UAL238"), "QS `HOLD UAL238" },
            { ScopeDataEntries.ToggleHsf("UAL238"), "QS UAL238" },
            { ScopeDataEntries.ToggleVci("UAL238"), "QN // UAL238" },
        };

    [Theory]
    [MemberData(nameof(Entries))]
    public void Entry_IsTheExactEramText(string actual, string expected) => Assert.Equal(expected, actual);

    /// <summary>The field-action toggles pick their entry by action.</summary>
    [Theory]
    [InlineData(ScopeFieldAction.ToggleHsf, "QS UAL238")]
    [InlineData(ScopeFieldAction.ToggleVci, "QN // UAL238")]
    public void Toggle_PicksTheEntryByAction(ScopeFieldAction action, string expected) =>
        Assert.Equal(expected, ScopeDataEntries.Toggle(action, "UAL238"));

    [Fact]
    public void Toggle_AnyOtherAction_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScopeDataEntries.Toggle(ScopeFieldAction.OpenAltitudeMenu, "UAL238"));

    /// <summary>Altitudes are 001-999 hundreds of feet: 000 is refused by QQ, and four digits fit no field.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(-10)]
    public void Altitude_OutsideOneTo999_Throws(int hundreds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScopeDataEntries.Interim(hundreds, "UAL238"));

    [Fact]
    public void Altitude_AtTheBounds_IsThreeDigits()
    {
        Assert.Equal("QQ 001 UAL238", ScopeDataEntries.Interim(1, "UAL238"));
        Assert.Equal("QR 999 UAL238", ScopeDataEntries.CeraOnly(999, "UAL238"));
    }
}
