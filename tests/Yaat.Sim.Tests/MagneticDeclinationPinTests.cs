using System.Globalization;
using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// A test process resolves <see cref="MagneticDeclination.EvaluationDateUtc"/> once (the first read fixes the day), so a
/// pin must land before that read. A pin afterwards throws instead of being silently ignored.
/// </summary>
public class MagneticDeclinationPinTests
{
    [Fact]
    public void PinEvaluationDate_AfterTheDateIsResolved_Throws()
    {
        // Read first, so the test holds whether or not an earlier class in this process already resolved the day.
        DateTime resolved = MagneticDeclination.EvaluationDateUtc;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            MagneticDeclination.PinEvaluationDate(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc))
        );

        Assert.Contains(IsoDay(resolved), error.Message, StringComparison.Ordinal);
        Assert.Equal(resolved, MagneticDeclination.EvaluationDateUtc);
    }

    [Fact]
    public void PinEvaluationDate_NonUtcKind_Throws()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            MagneticDeclination.PinEvaluationDate(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Unspecified))
        );

        Assert.Contains(nameof(DateTimeKind.Unspecified), error.Message, StringComparison.Ordinal);
    }

    private static string IsoDay(DateTime dateUtc) => dateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
