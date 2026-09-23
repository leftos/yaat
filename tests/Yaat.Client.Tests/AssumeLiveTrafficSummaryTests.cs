using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>The one terminal line a bulk live-traffic assume writes.</summary>
public class AssumeLiveTrafficSummaryTests
{
    [Fact]
    public void AllZero_HasNoParentheses() => Assert.Equal("Assumed 0 of 0 live aircraft", Format(0, 0, 0, 0, 0));

    [Fact]
    public void EverythingAssumed_HasNoParentheses() => Assert.Equal("Assumed 4 of 4 live aircraft", Format(4, 4, 0, 0, 0));

    [Fact]
    public void OnGroundOnly() => Assert.Equal("Assumed 2 of 3 live aircraft (1 on the ground)", Format(3, 2, 1, 0, 0));

    [Fact]
    public void StaleOnly() => Assert.Equal("Assumed 2 of 4 live aircraft (2 stale)", Format(4, 2, 0, 2, 0));

    [Fact]
    public void RefusedOnly() => Assert.Equal("Assumed 1 of 2 live aircraft (1 refused)", Format(2, 1, 0, 0, 1));

    [Fact]
    public void AllParts_InOrder() => Assert.Equal("Assumed 3 of 9 live aircraft (2 on the ground, 1 stale, 3 refused)", Format(9, 3, 2, 1, 3));

    [Fact]
    public void Error_IsTheWholeLine()
    {
        AssumeLiveTrafficResultDto result = Result(0, 0, 0, 0, 0) with { Error = "Unknown fix or airport: QQXZY" };

        Assert.Equal("Unknown fix or airport: QQXZY", MainViewModel.FormatAssumeLiveTrafficSummary(result));
    }

    private static string Format(int considered, int assumed, int onGround, int stale, int refused) =>
        MainViewModel.FormatAssumeLiveTrafficSummary(Result(considered, assumed, onGround, stale, refused));

    private static AssumeLiveTrafficResultDto Result(int considered, int assumed, int onGround, int stale, int refused) =>
        new()
        {
            Considered = considered,
            Assumed = assumed,
            SkippedOnGround = onGround,
            SkippedStale = stale,
            Refused = [.. Enumerable.Range(1, refused).Select(i => $"UAL{i}: refused")],
            Error = null,
        };
}
