using System.Globalization;
using System.Net.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Yaat.Client.Services.Speech;

namespace Yaat.Client.Tests;

/// <summary>
/// Removing the GPU runtime on the next start: the marker <see cref="SherpaCudaInstaller.ScheduleRemovalOnNextStart"/> writes,
/// and the startup pass that deletes the set only when that marker is there. Each test has a fresh install root.
/// </summary>
public sealed class SherpaCudaPendingRemovalTests : IDisposable
{
    private const string ScheduledMessage = "Scheduled: the GPU runtime is removed the next time YAAT starts.";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-sherpa-cuda-removal-tests", Guid.NewGuid().ToString("N"));

    private string MarkerPath => Path.Combine(_root, "remove-pending");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        string lockPath = _root + ".lock";
        if (File.Exists(lockPath))
        {
            File.Delete(lockPath);
        }
    }

    [Fact]
    public void LockDelete_InAMissingFolder_ThrowsUnlessGuarded()
    {
        string lockPath = Path.Combine(Path.GetTempPath(), "yaat-sherpa-cuda-missing-" + Guid.NewGuid().ToString("N"), "x.lock");

        Assert.Throws<DirectoryNotFoundException>(() => File.Delete(lockPath));
        Assert.Null(
            Record.Exception(() =>
            {
                if (File.Exists(lockPath))
                {
                    File.Delete(lockPath);
                }
            })
        );
    }

    [Fact]
    public void Schedule_Writes_The_Marker_With_The_Utc_Time_And_Sets_The_Status()
    {
        WriteInstalledSet();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 30, 0, TimeSpan.Zero));
        var installer = new SherpaCudaInstaller(new HttpClient(), _root, [], clock);

        installer.ScheduleRemovalOnNextStart();

        Assert.True(installer.IsRemovalScheduled);
        Assert.Equal(ScheduledMessage, installer.StatusMessage);
        var written = DateTimeOffset.Parse(File.ReadAllText(MarkerPath), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(clock.GetUtcNow(), written);
        Assert.True(File.Exists(Path.Combine(_root, ".installed")), "Scheduling must not remove anything yet.");
    }

    [Fact]
    public void Process_With_The_Marker_Removes_The_Set_And_The_Marker()
    {
        WriteInstalledSet();
        File.WriteAllText(MarkerPath, "2026-11-01T12:30:00.0000000+00:00");

        bool processed = SherpaCudaInstaller.ProcessPendingRemoval(_root);

        Assert.True(processed);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Process_Without_The_Marker_Deletes_Nothing()
    {
        WriteInstalledSet();
        string sentinel = Path.Combine(_root, "native", "sentinel.txt");
        File.WriteAllText(sentinel, "keep me");

        bool processed = SherpaCudaInstaller.ProcessPendingRemoval(_root);

        Assert.True(processed);
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(Path.Combine(_root, ".installed")));
        Assert.True(File.Exists(Path.Combine(_root, "native", "x.dll")));
    }

    [Fact]
    public void Process_On_An_Io_Failure_Leaves_The_Marker_For_The_Next_Start()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a file another handle holds.");
        WriteInstalledSet();
        File.WriteAllText(MarkerPath, "2026-11-01T12:30:00.0000000+00:00");

        bool processed;
        using (new FileStream(Path.Combine(_root, "native", "MANIFEST.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            processed = SherpaCudaInstaller.ProcessPendingRemoval(_root);
        }

        Assert.False(processed);
        Assert.True(File.Exists(MarkerPath));
        Assert.False(File.Exists(Path.Combine(_root, ".installed")), "The delete had begun: the sentinel goes first.");
    }

    [Fact]
    public void Process_While_A_Set_Library_Is_Held_Deletes_Nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The in-use check is Windows only.");
        WriteInstalledSet();
        File.WriteAllText(MarkerPath, "2026-11-01T12:30:00.0000000+00:00");

        bool processed;
        using (new FileStream(Path.Combine(_root, "native", "x.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            processed = SherpaCudaInstaller.ProcessPendingRemoval(_root);
        }

        Assert.False(processed);
        AssertNothingDeleted();
    }

    [Fact]
    public void Process_While_Another_Writer_Holds_The_Root_Deletes_Nothing()
    {
        WriteInstalledSet();
        File.WriteAllText(MarkerPath, "2026-11-01T12:30:00.0000000+00:00");

        bool processed;
        using (Assert.IsType<FileStream>(SherpaCudaInstaller.TryLockRoot(_root)))
        {
            processed = SherpaCudaInstaller.ProcessPendingRemoval(_root);
        }

        Assert.False(processed);
        AssertNothingDeleted();
    }

    [Fact]
    public void A_New_Instance_On_A_Marked_Root_Reports_Scheduled()
    {
        WriteInstalledSet();
        File.WriteAllText(MarkerPath, "2026-11-01T12:30:00.0000000+00:00");

        var installer = new SherpaCudaInstaller(new HttpClient(), _root, [], TimeProvider.System);

        Assert.True(installer.IsRemovalScheduled);
        Assert.Equal(ScheduledMessage, installer.StatusMessage);
    }

    [Fact]
    public void A_New_Instance_On_An_Unmarked_Root_Reports_Nothing_Scheduled()
    {
        WriteInstalledSet();

        var installer = new SherpaCudaInstaller(new HttpClient(), _root, [], TimeProvider.System);

        Assert.False(installer.IsRemovalScheduled);
        Assert.Equal("", installer.StatusMessage);
    }

    /// <summary>A set as an install leaves it: the sentinel, one library and the manifest under <c>native/</c>.</summary>
    private void WriteInstalledSet()
    {
        Directory.CreateDirectory(Path.Combine(_root, "native"));
        File.WriteAllText(Path.Combine(_root, ".installed"), "");
        File.WriteAllText(Path.Combine(_root, "native", "x.dll"), "x");
        File.WriteAllText(Path.Combine(_root, "native", "MANIFEST.txt"), "HEAD: abc");
    }

    private void AssertNothingDeleted()
    {
        Assert.True(File.Exists(MarkerPath));
        Assert.True(File.Exists(Path.Combine(_root, ".installed")));
        Assert.True(File.Exists(Path.Combine(_root, "native", "x.dll")));
    }
}
