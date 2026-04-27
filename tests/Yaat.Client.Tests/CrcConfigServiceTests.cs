using System;
using System.IO;
using System.Linq;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class CrcConfigServiceTests
{
    [Fact]
    public void FindFirstConfigDir_returns_dir_when_marker_present()
    {
        var temp = Directory.CreateTempSubdirectory("yaat-crc-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(temp, "GeneralSettings.json"), "{}");

            var result = CrcConfigService.FindFirstConfigDir([temp]);

            Assert.Equal(temp, result);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void FindFirstConfigDir_returns_null_when_marker_missing()
    {
        var temp = Directory.CreateTempSubdirectory("yaat-crc-test-").FullName;
        try
        {
            var result = CrcConfigService.FindFirstConfigDir([temp]);

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void FindFirstConfigDir_returns_null_when_directory_does_not_exist()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), $"yaat-crc-nonexistent-{Guid.NewGuid():N}");

        var result = CrcConfigService.FindFirstConfigDir([nonExistent]);

        Assert.Null(result);
    }

    [Fact]
    public void FindFirstConfigDir_skips_to_next_candidate_when_first_lacks_marker()
    {
        var first = Directory.CreateTempSubdirectory("yaat-crc-test-first-").FullName;
        var second = Directory.CreateTempSubdirectory("yaat-crc-test-second-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(second, "GeneralSettings.json"), "{}");

            var result = CrcConfigService.FindFirstConfigDir([first, second]);

            Assert.Equal(second, result);
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void FindFirstConfigDir_prefers_earlier_candidate_when_both_have_marker()
    {
        var first = Directory.CreateTempSubdirectory("yaat-crc-test-first-").FullName;
        var second = Directory.CreateTempSubdirectory("yaat-crc-test-second-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(first, "GeneralSettings.json"), "{}");
            File.WriteAllText(Path.Combine(second, "GeneralSettings.json"), "{}");

            var result = CrcConfigService.FindFirstConfigDir([first, second]);

            Assert.Equal(first, result);
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void EnumerateCandidates_includes_platform_default()
    {
        var candidates = CrcConfigService.EnumerateCandidates().ToArray();

        Assert.NotEmpty(candidates);

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.Contains(Path.Combine(localAppData, "CRC"), candidates);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Contains(Path.Combine(home, "Library", "Application Support", "CRC"), candidates);
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Contains(Path.Combine(home, ".config", "CRC"), candidates);
        }
    }
}
