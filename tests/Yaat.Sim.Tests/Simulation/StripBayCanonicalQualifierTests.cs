using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Coverage for the recorded-canonical migration that adds the now-required
/// <c>FACILITY/</c> segment to bay tokens. Runs against the committed ZOA
/// snapshot so the bay names are the real ones ("Local 1", "NCT"), including the
/// case where a facility id and a bay name collide. Skips silently without the
/// snapshot (mirrors TestVnasData).
/// </summary>
public class StripBayCanonicalQualifierTests
{
    private static IReadOnlyList<AccessibleBay>? OakBays() => TestArtccConfig.LoadZoa()?.GetAllAccessibleStripBays("OAK_TWR");

    [Theory]
    // Full strips, with and without the id form; multi-word bay; rack/index tails.
    [InlineData("STRIP Local 1/1/1", "STRIP OAK/Local 1/1/1")]
    [InlineData("STRIP STRIP_SWA1089 Local 1/2/3", "STRIP STRIP_SWA1089 OAK/Local 1/2/3")]
    [InlineData("STRIP ARRIVAL_UAL1 Ground 1", "STRIP ARRIVAL_UAL1 OAK/Ground 1")]
    // Half-strips, separators, blanks.
    [InlineData(@"HSC Local 1/1 NORDO\28L", @"HSC OAK/Local 1/1 NORDO\28L")]
    [InlineData("HSM HSTRIP_abc123 Local 2/1/1", "HSM HSTRIP_abc123 OAK/Local 2/1/1")]
    [InlineData("SEP H Local 1/1 HOLD", "SEP H OAK/Local 1/1 HOLD")]
    [InlineData("SEPM SEP_abc123 Local 1/2/1", "SEPM SEP_abc123 OAK/Local 1/2/1")]
    [InlineData("BLANK Ground 2/1/1", "BLANK OAK/Ground 2/1/1")]
    // A bay OAK links from another facility keeps that facility's id.
    [InlineData("SCAN NCT/1/1", "SCAN NCT/NCT/1/1")]
    public void Qualify_AddsTheOwningFacility(string recorded, string expected)
    {
        if (OakBays() is not { } bays)
        {
            return;
        }

        Assert.Equal(expected, StripBayCanonicalQualifier.Qualify(recorded, bays));
    }

    [Theory]
    // Verbs with no bay token at all.
    [InlineData("AN 3 RV")]
    [InlineData("AN STRIP_N346G 8a")]
    [InlineData("STRIPD STRIP_N346G")]
    [InlineData("STRIPO STRIP_N346G")]
    [InlineData(@"HSE HSTRIP_abc123 a\b")]
    // Id forms address the strip directly, so the trailing token is a label, not a bay.
    [InlineData("SEPE SEP_abc123 Local 1")]
    [InlineData("SEPD SEP_abc123")]
    [InlineData("BLANKD BLANK_2")]
    [InlineData("HSD HSTRIP_abc123")]
    // Aircraft-scoped shorthand carries no bay scope.
    [InlineData("HSD")]
    [InlineData("HSA")]
    public void Qualify_LeavesBaylessCanonicalsAlone(string recorded)
    {
        if (OakBays() is not { } bays)
        {
            return;
        }

        Assert.Equal(recorded, StripBayCanonicalQualifier.Qualify(recorded, bays));
    }

    [Fact]
    public void Qualify_IsIdempotent_EvenWhenAFacilityIdEqualsABayName()
    {
        if (OakBays() is not { } bays)
        {
            return;
        }

        // NCT owns a bay literally called NCT, so "does the first segment name a
        // bay?" is not enough to tell qualified from unqualified.
        Assert.Equal("STRIP NCT/NCT/1/1", StripBayCanonicalQualifier.Qualify("STRIP NCT/NCT/1/1", bays));
        Assert.Equal("STRIP OAK/Local 1/1/1", StripBayCanonicalQualifier.Qualify("STRIP OAK/Local 1/1/1", bays));
    }

    [Fact]
    public void Qualify_LeavesUnknownBaysUntouched()
    {
        if (OakBays() is not { } bays)
        {
            return;
        }

        // Nothing to guess at — the caller reports these rather than inventing a facility.
        Assert.Equal("STRIP NOSUCHBAY/1/1", StripBayCanonicalQualifier.Qualify("STRIP NOSUCHBAY/1/1", bays));
    }

    [Fact]
    public void QualifyCompound_RewritesEveryUnit_PreservingSeparators()
    {
        if (OakBays() is not { } bays)
        {
            return;
        }

        Assert.Equal(
            "STRIP OAK/Local 1/1/1; AN 3 RV, HSC OAK/Ground 1/1 NOTE",
            StripBayCanonicalQualifier.QualifyCompound("STRIP Local 1/1/1; AN 3 RV, HSC Ground 1/1 NOTE", bays)
        );
    }
}
