using System;
using System.IO;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Exercises the real MiniDumpWriteDump self-capture that <see cref="UiThreadWatchdog"/> uses on a
/// hard UI freeze (GitHub #347) — the dump carries the native stacks the managed capture cannot
/// walk. Runs against the test process itself; a stub would not catch a broken P/Invoke signature
/// or flag combination.
/// </summary>
public class FreezeDumpWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"yaat-freeze-dump-test-{Guid.NewGuid():N}");

    public FreezeDumpWriterTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void WritesReadableDumpFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "MiniDumpWriteDump is Windows-only.");

        string? path = FreezeDumpWriter.TryWrite(_dir);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        // MDMP magic — proves dbghelp actually wrote a minidump, not an empty placeholder.
        using var stream = File.OpenRead(path);
        var magic = new byte[4];
        stream.ReadExactly(magic);
        Assert.Equal("MDMP"u8.ToArray(), magic);
    }

    [Fact]
    public void PrunesOldestDumpsKeepingThree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "MiniDumpWriteDump is Windows-only.");

        // Timestamped names sort lexicographically; these are all older than any real capture.
        File.WriteAllText(Path.Combine(_dir, "yaat-freeze-20200101-000001.dmp"), "old");
        File.WriteAllText(Path.Combine(_dir, "yaat-freeze-20200101-000002.dmp"), "old");
        File.WriteAllText(Path.Combine(_dir, "yaat-freeze-20200101-000003.dmp"), "old");

        string? path = FreezeDumpWriter.TryWrite(_dir);

        Assert.NotNull(path);
        var remaining = Directory.GetFiles(_dir, "yaat-freeze-*.dmp");
        Assert.Equal(3, remaining.Length);
        Assert.DoesNotContain(remaining, f => f.EndsWith("000001.dmp", StringComparison.Ordinal));
    }
}
