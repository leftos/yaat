using Xunit;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The retired <c>HSE</c> verb (literal half-strip field replacement by id) folded
/// into <c>HSA</c>'s id form. Recorded action logs that still carry <c>HSE</c> are
/// rewritten in place by <see cref="RecordingSchemaUpgrader"/> through this rewriter.
/// </summary>
public class HalfStripEditCanonicalRewriterTests
{
    [Theory]
    [InlineData(@"HSE HSTRIP_abc123 a\b", @"HSA HSTRIP_abc123 a\b")]
    [InlineData(@"HALFSTRIPEDIT HSTRIP_abc123 a\b", @"HSA HSTRIP_abc123 a\b")]
    [InlineData(@"hse HSTRIP_abc123 n436ms\\\\\", @"HSA HSTRIP_abc123 n436ms\\\\\")]
    [InlineData("HSE HSTRIP_abc123", "HSA HSTRIP_abc123")]
    // A bare verb (never emitted, HSE always required an id) still maps; HSA rejects it at dispatch.
    [InlineData("HSE", "HSA")]
    // Incidental padding around a single unit survives, same as inside a compound.
    [InlineData(@"  HSE HSTRIP_x a\b  ", @"  HSA HSTRIP_x a\b  ")]
    // Compound units are rewritten independently; separators and padding survive.
    [InlineData(@"HSE HSTRIP_x a; AN 3 RV", @"HSA HSTRIP_x a; AN 3 RV")]
    [InlineData(@"AN 3 RV, HSE HSTRIP_x a\b", @"AN 3 RV, HSA HSTRIP_x a\b")]
    public void Rewrite_RetiredHseVerb_BecomesHsaIdForm(string recorded, string expected)
    {
        Assert.Equal(expected, HalfStripEditCanonicalRewriter.Rewrite(recorded));
    }

    [Theory]
    [InlineData(@"HSA HSTRIP_abc123 a\b")]
    [InlineData(@"HSC OAK/Local 1/1 NORDO\28L")]
    [InlineData("HSD HSTRIP_abc123")]
    [InlineData("HSEX")]
    [InlineData("H 270")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rewrite_OtherCanonicals_Unchanged(string recorded)
    {
        Assert.Same(recorded, HalfStripEditCanonicalRewriter.Rewrite(recorded));
    }

    [Fact]
    public void Rewrite_IsIdempotent()
    {
        var once = HalfStripEditCanonicalRewriter.Rewrite(@"HSE HSTRIP_x a\b; HSE HSTRIP_y c");
        Assert.Same(once, HalfStripEditCanonicalRewriter.Rewrite(once));
    }
}
