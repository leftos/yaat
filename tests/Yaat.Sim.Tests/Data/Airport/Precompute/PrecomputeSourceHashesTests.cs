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
    public void PushSetContains_GroundOutline_GroundOutlineSweep_AircraftLength_AircraftCategory_DesignGroupJson()
    {
        string[] sharedWithLayout =
        [
            "GroundOutline.cs",
            "GroundOutlineSweep.cs",
            "Data/Faa/AircraftLength.cs",
            "AircraftCategory.cs",
            "TrueHeading.cs",
            "Data/AirportSidecarCatalog.cs",
            "Data/AirportSidecarDefinition.cs",
            "Data/AirportSidecarLoader.cs",
            "Data/Airport/TugMovePlanner.cs",
        ];
        string[] pushOnly =
        [
            "Data/PrecomputeCache/design-group-envelopes.json",
            "Data/Airport/Precompute/PushTargetPlanner.cs",
            "Data/Airport/Precompute/DesignGroupEnvelopes.cs",
            "Data/Airport/Precompute/PushMoveEntry.cs",
            "Data/Airport/Precompute/AirportSidecarHash.cs",
        ];

        Assert.All(sharedWithLayout.Concat(pushOnly), path => Assert.Contains(path, PrecomputeSourceHashes.PushTargetFiles));
        Assert.All(pushOnly, path => Assert.DoesNotContain(path, PrecomputeSourceHashes.LayoutFiles));
        Assert.DoesNotContain("Data/Airport/Precompute/PrecomputeStore.cs", PrecomputeSourceHashes.PushTargetFiles);
        Assert.DoesNotContain("Data/Airport/Precompute/PrecomputeKey.cs", PrecomputeSourceHashes.PushTargetFiles);
    }

    /// <summary>
    /// Every type a push-only file declares (a file in the push set and not the layout set) and no layout file declares is
    /// absent from every layout file's text, so a layout source never reaches code whose edit stales only the push half.
    /// </summary>
    [Fact]
    public void NoLayoutFileReferencesAPushOnlyType()
    {
        string simRoot = SimRoot();
        string[] layout = PrecomputeSourceHashes.LayoutFiles;
        string[] pushOnly =
        [
            .. PrecomputeSourceHashes.PushTargetFiles.Except(layout).Where(relative => relative.EndsWith(".cs", StringComparison.Ordinal)),
        ];
        string[] layoutTypes =
        [
            .. layout.SelectMany(relative => DeclaredTypesWithModifier(Path.Combine(simRoot, relative))).Distinct(StringComparer.Ordinal),
        ];
        string[] pushOnlyTypes =
        [
            .. pushOnly
                .SelectMany(relative => DeclaredTypesWithModifier(Path.Combine(simRoot, relative)))
                .Where(type => !layoutTypes.Contains(type, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];

        Assert.Contains("PushTargetPlanner", pushOnlyTypes);
        foreach (string relative in layout)
        {
            string text = File.ReadAllText(Path.Combine(simRoot, relative));
            foreach (string type in pushOnlyTypes)
            {
                Assert.False(Regex.IsMatch(text, $@"\b{Regex.Escape(type)}\b"), $"{relative} names {type}, which only a push-only file declares.");
            }
        }
    }

    /// <summary>
    /// Every Yaat.Sim type <c>PushTargetPlanner.cs</c> names is declared in a push-set file, so the payload depends on
    /// nothing outside the hashed set. <c>SimLog</c> is the one exception: logging writes nothing into the payload.
    /// </summary>
    [Fact]
    public void EveryTypePushTargetPlannerNames_IsDeclaredInAPushSetFile()
    {
        string simRoot = SimRoot();
        string planner = string.Join(
            '\n',
            File.ReadAllLines(Path.Combine(simRoot, "Data", "Airport", "Precompute", "PushTargetPlanner.cs"))
                .Where(line => !line.StartsWith("using ", StringComparison.Ordinal) && !line.StartsWith("namespace ", StringComparison.Ordinal))
        );
        string[] simTypes =
        [
            .. typeof(PushTargetPlanner)
                .Assembly.GetTypes()
                .Select(type => type.Name.Split('`')[0])
                .Where(name => Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$") && (name != "SimLog"))
                .Distinct(StringComparer.Ordinal),
        ];
        HashSet<string> pushDeclared =
        [
            .. PrecomputeSourceHashes
                .PushTargetFiles.Where(relative => relative.EndsWith(".cs", StringComparison.Ordinal))
                .SelectMany(relative => DeclaredTypesWithModifier(Path.Combine(simRoot, relative))),
        ];
        // Log-template placeholders such as {Airport} name no type.
        string code = Regex.Replace(planner, @"\{[A-Za-z]+(?::[^}]*)?\}", "");
        string[] named = [.. simTypes.Where(type => Regex.IsMatch(code, $@"\b{Regex.Escape(type)}\b"))];

        Assert.Contains("TugMovePlanner", named);
        Assert.Contains("AirportSidecarCatalog", named);
        Assert.All(named, type => Assert.True(pushDeclared.Contains(type), $"PushTargetPlanner.cs names {type}, which no push-set file declares"));
    }

    /// <summary>
    /// The layout set's exclusions are checked; the push set holds every source under <c>Data/Airport</c> outside
    /// <c>Precompute/</c>, so it excludes none there.
    /// </summary>
    [Fact]
    public void NoHashedFileReferencesAnExcludedType()
    {
        string simRoot = SimRoot();
        string[] hashed = PrecomputeSourceHashes.LayoutFiles;
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

    /// <summary>
    /// Type declarations with an access modifier, so a doc-comment phrase such as "a record in" is not taken for a type
    /// named <c>in</c>.
    /// </summary>
    private static IEnumerable<string> DeclaredTypesWithModifier(string filePath) =>
        Regex
            .Matches(
                File.ReadAllText(filePath),
                @"\b(?:public|internal|private|protected|file)\s+(?:(?:static|sealed|abstract|readonly|partial|ref)\s+)*"
                    + @"(?:class|record|struct|interface|enum)(?:\s+(?:class|struct))?\s+([A-Za-z_][A-Za-z0-9_]*)"
            )
            .Select(match => match.Groups[1].Value);

    private static void AssertSha256(string hash)
    {
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(char.IsAsciiHexDigitLower(c), $"Not lowercase hex: {c}"));
    }
}
