// Yaat.RecordingConsolidator — find duplicate .zip files in TestData, consolidate to hash-named files,
// and update all .cs references in the codebase.

using System.Security.Cryptography;
using System.Text.RegularExpressions;

if (args.Length < 1)
{
    Console.WriteLine("Usage: dotnet run --project tools/Yaat.RecordingConsolidator -- <test-data-dir> [--dry-run]");
    Console.WriteLine();
    Console.WriteLine("  <test-data-dir>  Path to TestData folder (e.g. tests/Yaat.Sim.Tests/TestData)");
    Console.WriteLine("  --dry-run        Show what would happen without making changes");
    return 1;
}

string testDataDir = Path.GetFullPath(args[0]);
bool dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));

if (!Directory.Exists(testDataDir))
{
    Console.Error.WriteLine($"ERROR: Directory not found: {testDataDir}");
    return 1;
}

// Find the repo root by walking up from testDataDir looking for .git
string repoRoot = FindRepoRoot(testDataDir);
string testDataRelative = Path.GetRelativePath(repoRoot, testDataDir).Replace('\\', '/');

Console.WriteLine($"TestData dir: {testDataDir}");
Console.WriteLine($"Repo root:    {repoRoot}");
Console.WriteLine($"Mode:         {(dryRun ? "DRY RUN" : "LIVE")}");
Console.WriteLine();

// Step 1: Hash all .zip files
var zipFiles = Directory.GetFiles(testDataDir, "*.zip");
if (zipFiles.Length == 0)
{
    Console.WriteLine("No .zip files found.");
    return 0;
}

Console.WriteLine($"Found {zipFiles.Length} .zip files. Hashing...");
var hashGroups = new Dictionary<string, List<string>>();

foreach (string zipPath in zipFiles)
{
    string hash = ComputeSha256(zipPath);
    if (!hashGroups.TryGetValue(hash, out var group))
    {
        group = [];
        hashGroups[hash] = group;
    }

    group.Add(zipPath);
}

// Step 2: Find duplicate groups
var duplicateGroups = hashGroups.Where(g => g.Value.Count > 1).ToList();
if (duplicateGroups.Count == 0)
{
    Console.WriteLine("No duplicate .zip files found.");
    return 0;
}

Console.WriteLine($"Found {duplicateGroups.Count} duplicate group(s):");
Console.WriteLine();

int totalRemoved = 0;
var renames = new Dictionary<string, string>(); // old filename -> new filename (for .cs updates)

foreach (var (hash, files) in duplicateGroups)
{
    string shortHash = hash[..12];
    string newFileName = $"{shortHash}.zip";
    string newFilePath = Path.Combine(testDataDir, newFileName);

    Console.WriteLine($"  Hash: {shortHash}... ({files.Count} files)");
    foreach (string file in files.OrderBy(f => f))
    {
        string name = Path.GetFileName(file);
        Console.WriteLine($"    - {name}");
        renames[$"TestData/{name}"] = $"TestData/{newFileName}";
    }

    Console.WriteLine($"    -> consolidated to: {newFileName}");
    Console.WriteLine();

    if (!dryRun)
    {
        // Keep the first file (alphabetically), rename it to the hash name
        var sorted = files.OrderBy(f => f).ToList();
        string keeper = sorted[0];

        // Delete all except the keeper
        foreach (string file in sorted.Skip(1))
        {
            File.Delete(file);
            totalRemoved++;
        }

        // Rename the keeper to the hash-based name (if not already named that)
        if (Path.GetFileName(keeper) != newFileName)
        {
            if (File.Exists(newFilePath))
            {
                File.Delete(keeper);
            }
            else
            {
                File.Move(keeper, newFilePath);
            }
        }
    }
}

// Step 3: Update .cs file references
Console.WriteLine("Scanning .cs files for references to update...");
var csFiles = Directory
    .GetFiles(repoRoot, "*.cs", SearchOption.AllDirectories)
    .Where(f => !f.Replace('\\', '/').Contains("/bin/"))
    .Where(f => !f.Replace('\\', '/').Contains("/obj/"))
    .ToList();

int filesUpdated = 0;
foreach (string csFile in csFiles)
{
    string content = File.ReadAllText(csFile);
    string updated = content;

    foreach (var (oldRef, newRef) in renames)
    {
        updated = updated.Replace(oldRef, newRef);
    }

    if (updated != content)
    {
        string relPath = Path.GetRelativePath(repoRoot, csFile).Replace('\\', '/');
        Console.WriteLine($"  Updated: {relPath}");

        if (!dryRun)
        {
            File.WriteAllText(csFile, updated);
        }

        filesUpdated++;
    }
}

Console.WriteLine();
Console.WriteLine($"Summary: {duplicateGroups.Count} duplicate group(s), {totalRemoved} file(s) removed, {filesUpdated} .cs file(s) updated.");

if (dryRun)
{
    Console.WriteLine("(Dry run — no changes were made.)");
}

return 0;

static string ComputeSha256(string filePath)
{
    using var stream = File.OpenRead(filePath);
    byte[] hashBytes = SHA256.HashData(stream);
    return Convert.ToHexStringLower(hashBytes);
}

static string FindRepoRoot(string startDir)
{
    string dir = startDir;
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir, ".git")))
        {
            return dir;
        }

        string? parent = Directory.GetParent(dir)?.FullName;
        if (parent == dir)
        {
            break;
        }

        dir = parent!;
    }

    throw new InvalidOperationException($"Could not find .git root from {startDir}");
}
