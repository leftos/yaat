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
        string temp = Directory.CreateTempSubdirectory("yaat-crc-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(temp, "GeneralSettings.json"), "{}");

            string? result = CrcConfigService.FindFirstConfigDir([temp]);

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
        string temp = Directory.CreateTempSubdirectory("yaat-crc-test-").FullName;
        try
        {
            string? result = CrcConfigService.FindFirstConfigDir([temp]);

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
        string nonExistent = Path.Combine(Path.GetTempPath(), $"yaat-crc-nonexistent-{Guid.NewGuid():N}");

        string? result = CrcConfigService.FindFirstConfigDir([nonExistent]);

        Assert.Null(result);
    }

    [Fact]
    public void FindFirstConfigDir_skips_to_next_candidate_when_first_lacks_marker()
    {
        string first = Directory.CreateTempSubdirectory("yaat-crc-test-first-").FullName;
        string second = Directory.CreateTempSubdirectory("yaat-crc-test-second-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(second, "GeneralSettings.json"), "{}");

            string? result = CrcConfigService.FindFirstConfigDir([first, second]);

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
        string first = Directory.CreateTempSubdirectory("yaat-crc-test-first-").FullName;
        string second = Directory.CreateTempSubdirectory("yaat-crc-test-second-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(first, "GeneralSettings.json"), "{}");
            File.WriteAllText(Path.Combine(second, "GeneralSettings.json"), "{}");

            string? result = CrcConfigService.FindFirstConfigDir([first, second]);

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
        string[] candidates = [.. CrcConfigService.EnumerateCandidates()];

        Assert.NotEmpty(candidates);

        if (OperatingSystem.IsWindows())
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.Contains(Path.Combine(localAppData, "CRC"), candidates);
        }
        else if (OperatingSystem.IsMacOS())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Contains(Path.Combine(home, "Library", "Application Support", "CRC"), candidates);
        }
        else if (OperatingSystem.IsLinux())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Contains(Path.Combine(home, ".config", "CRC"), candidates);
        }
    }

    /// <summary>
    /// Pins the embedded crc-environments.json content. The same file is consumed by the
    /// standalone yaat-crc-config Rust tool and Setup-CrcEnvironment.ps1 — a typo in the JSON
    /// (or a missing EmbeddedResource entry) should fail loudly here.
    /// </summary>
    [Fact]
    public void YaatEntries_loaded_from_embedded_resource_match_canonical_list()
    {
        CrcConfigService.CrcEnvironmentEntry[] entries = CrcConfigService.YaatEntries;

        CrcConfigService.CrcEnvironmentEntry prod = Assert.Single(entries);
        Assert.Equal("YAAT1", prod.Name);
        Assert.Equal("https://yaat1.leftos.dev/hubs/client", prod.ClientHubUrl);
        Assert.Equal("https://yaat1.leftos.dev", prod.ApiBaseUrl);
        Assert.False(prod.IsDisabled);
        Assert.False(prod.IsSweatbox);
    }
}
