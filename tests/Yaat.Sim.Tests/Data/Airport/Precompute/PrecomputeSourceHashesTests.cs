using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// The build-time source hashes that key the precompute cache: every path they name must exist, each hash must be a
/// lowercase SHA-256 hex string, the constants must match the files on disk, and no hashed file may name a type that
/// only an excluded file declares.
/// </summary>
public class PrecomputeSourceHashesTests
{
    [Fact]
    public void EveryHashedFileExists()
    {
        string simRoot = SimRoot();

        foreach (string relativePath in PrecomputeSourceHashes.PushTargetFiles)
        {
            Assert.True(File.Exists(Path.Combine(simRoot, relativePath)), $"Hashed source file is missing: {relativePath}");
        }

        Assert.Empty(PrecomputeSourceHashes.LayoutFiles.Except(PrecomputeSourceHashes.PushTargetFiles));
    }

    [Fact]
    public void HashesAreLowercaseHexSha256()
    {
        AssertSha256(PrecomputeSourceHashes.Layout);
        AssertSha256(PrecomputeSourceHashes.PushTargets);
        Assert.NotEqual(PrecomputeSourceHashes.Layout, PrecomputeSourceHashes.PushTargets);
    }

    /// <summary>
    /// The same algorithm the build uses: SHA-256 per file over its bytes, uppercase hex lowered, concatenated in
    /// forward-slash path ordinal order, then SHA-256 of that ASCII string.
    /// </summary>
    [Fact]
    public void ConstantsMatchTheFilesOnDisk()
    {
        string simRoot = SimRoot();

        Assert.Equal(PrecomputeSourceHashes.Layout, SetHash(simRoot, PrecomputeSourceHashes.LayoutFiles));
        Assert.Equal(PrecomputeSourceHashes.PushTargets, SetHash(simRoot, PrecomputeSourceHashes.PushTargetFiles));
    }

    [Fact]
    public void NoHashedFileReferencesAnExcludedType()
    {
        string simRoot = SimRoot();
        string[] hashed = PrecomputeSourceHashes.PushTargetFiles;
        string airportRoot = Path.Combine(simRoot, "Data", "Airport");

        string[] onDisk =
        [
            .. Directory
                .EnumerateFiles(airportRoot, "*.cs", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(simRoot, file).Replace('\\', '/'))
                .Where(relative => !relative.Contains("/Precompute/", StringComparison.Ordinal)),
        ];
        string[] excluded = [.. onDisk.Except(hashed)];
        string[] hashedTypes = [.. hashed.SelectMany(relative => DeclaredTypes(Path.Combine(simRoot, relative))).Distinct(StringComparer.Ordinal)];
        string[] excludedOnlyTypes =
        [
            .. excluded
                .SelectMany(relative => DeclaredTypes(Path.Combine(simRoot, relative)))
                .Where(type => !hashedTypes.Contains(type, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];

        Assert.NotEmpty(excludedOnlyTypes);

        foreach (string relative in hashed)
        {
            string text = File.ReadAllText(Path.Combine(simRoot, relative));
            foreach (string type in excludedOnlyTypes)
            {
                Assert.False(Regex.IsMatch(text, $@"\b{Regex.Escape(type)}\b"), $"{relative} names {type}, which only an excluded file declares.");
            }
        }
    }

    private static string SimRoot() => Path.Combine(TickRecorder.FindRepoRoot(), "src", "Yaat.Sim");

    private static string SetHash(string simRoot, IEnumerable<string> relativePaths)
    {
        string[] ordered = [.. relativePaths.Select(relative => relative.Replace('\\', '/')).OrderBy(relative => relative, StringComparer.Ordinal)];
        var concatenated = new StringBuilder();
        foreach (string relative in ordered)
        {
            concatenated.Append(Sha256Hex(File.ReadAllBytes(Path.Combine(simRoot, relative))));
        }

        return Sha256Hex(Encoding.ASCII.GetBytes(concatenated.ToString()));
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static IEnumerable<string> DeclaredTypes(string filePath) =>
        Regex
            .Matches(File.ReadAllText(filePath), @"(?:class|record|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)")
            .Select(match => match.Groups[1].Value);

    private static void AssertSha256(string hash)
    {
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(char.IsAsciiHexDigitLower(c), $"Not lowercase hex: {c}"));
    }
}
