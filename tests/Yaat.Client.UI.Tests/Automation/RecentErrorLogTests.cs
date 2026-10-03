using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Client.Logging;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// The ring of recent client errors the automation pipe attaches to its answers: what it keeps, the sequence it numbers
/// entries with, what <see cref="RecentErrorLog.Since"/> returns, and how an exception is rendered.
/// </summary>
public sealed class RecentErrorLogTests
{
    private const string Category = "RecentErrorLogTests";

    [Fact]
    public void Log_KeepsErrorAndCritical_ButNotWarning()
    {
        var ring = new RecentErrorLog();
        ILogger logger = ring.CreateLogger(Category);

        logger.LogWarning("a warning");
        logger.LogError("an error");
        logger.LogCritical("a critical");

        IReadOnlyList<RecentErrorEntry> entries = ring.Since(0, 10);
        Assert.Equal(["an error", "a critical"], entries.Select(e => e.Message));
        Assert.Equal([LogLevel.Error, LogLevel.Critical], entries.Select(e => e.Level));
        Assert.All(entries, e => Assert.Equal(Category, e.Category));
    }

    [Fact]
    public void LastSequence_IncreasesWithEachKeptEntry()
    {
        var ring = new RecentErrorLog();
        ILogger logger = ring.CreateLogger(Category);
        long empty = ring.LastSequence;

        logger.LogError("first");
        long first = ring.LastSequence;
        logger.LogWarning("not kept");
        long afterWarning = ring.LastSequence;
        logger.LogError("second");
        long second = ring.LastSequence;

        Assert.True(first > empty, $"{first} is not above {empty}");
        Assert.Equal(first, afterWarning);
        Assert.True(second > first, $"{second} is not above {first}");
        Assert.Equal([first, second], ring.Since(empty, 10).Select(e => e.Sequence));
    }

    [Fact]
    public void Since_ReturnsOnlyNewerEntries_OldestFirst_CappedAtMax()
    {
        var ring = new RecentErrorLog();
        ILogger logger = ring.CreateLogger(Category);
        logger.LogError("one");
        logger.LogError("two");
        long afterTwo = ring.LastSequence;
        logger.LogError("three");
        logger.LogError("four");
        logger.LogError("five");

        Assert.Equal(["three", "four"], ring.Since(afterTwo, 2).Select(e => e.Message));
        Assert.Equal(["three", "four", "five"], ring.Since(afterTwo, 10).Select(e => e.Message));
        Assert.Empty(ring.Since(ring.LastSequence, 10));
    }

    [Fact]
    public void Ring_DropsTheOldestPastItsCapacity()
    {
        var ring = new RecentErrorLog();
        ILogger logger = ring.CreateLogger(Category);
        for (int i = 0; i < RecentErrorLog.Capacity + 5; i++)
        {
            logger.LogError("error {Index}", i);
        }

        IReadOnlyList<RecentErrorEntry> entries = ring.Since(0, 1000);

        Assert.Equal(200, RecentErrorLog.Capacity);
        Assert.Equal(RecentErrorLog.Capacity, entries.Count);
        Assert.Equal("error 5", entries[0].Message);
        Assert.Equal($"error {RecentErrorLog.Capacity + 4}", entries[^1].Message);
    }

    [Fact]
    public void Exception_RendersAsTypeAndMessageOnOneLine()
    {
        var ring = new RecentErrorLog();
        ILogger logger = ring.CreateLogger(Category);
        Exception thrown = Thrown();

        logger.LogError(thrown, "load failed");
        logger.LogError("no exception");

        IReadOnlyList<RecentErrorEntry> entries = ring.Since(0, 10);
        Assert.Equal("System.InvalidOperationException: first line second line", entries[0].Exception);
        Assert.Equal("load failed", entries[0].Message);
        Assert.Null(entries[1].Exception);
    }

    [Fact]
    public void Message_WithNewlines_IsStoredOnOneLine()
    {
        var ring = new RecentErrorLog();
        ILogger logger = ring.CreateLogger(Category);

        logger.LogError("first line\nsecond line");

        RecentErrorEntry entry = Assert.Single(ring.Since(0, 10));
        Assert.Equal("first line second line", entry.Message);
    }

    private static Exception Thrown()
    {
        try
        {
            throw new InvalidOperationException("first line\nsecond line");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }
}
