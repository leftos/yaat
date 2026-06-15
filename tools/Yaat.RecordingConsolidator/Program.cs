// Yaat.RecordingConsolidator — find duplicate .zip files in TestData, consolidate to hash-named files,
// and update all .cs and .md references in the codebase.

using System.Diagnostics;
using System.Security.Cryptography;

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

// Guard: each consolidated keeper takes the hash-based name `<hash>.zip`. The source
// duplicates are LFS-tracked recording bundles, so the keeper's new name must also be
// covered by an LFS rule in .gitattributes. A name outside the LFS patterns would
// silently demote the kept copy to a plain Git blob — bloating history and tripping the
// large-file pre-commit hook. Verify every target name before touching anything on disk.
foreach (var (hash, _) in duplicateGroups)
{
    string candidateRel = $"{testDataRelative}/{hash[..12]}.zip";
    if (!IsLfsTracked(repoRoot, candidateRel))
    {
        Console.Error.WriteLine($"ERROR: '{candidateRel}' would not be tracked by Git LFS.");
        Console.Error.WriteLine("       Consolidated recordings must stay in LFS. Add a rule to .gitattributes:");
        Console.Error.WriteLine($"         {testDataRelative}/*.zip filter=lfs diff=lfs merge=lfs -text");
        return 1;
    }
}

int totalRemoved = 0;

// Prefixed mapping (e.g. `TestData/<old>` → `TestData/<new>`) used for source code,
// where the rename should only fire when the name appears as a path inside a string
// literal. Bare-name mapping (`<old>` → `<new>`) used for markdown, where the docs
// table lists bundle filenames without the `TestData/` prefix in backticks.
var renamesPrefixed = new Dictionary<string, string>();
var renamesBare = new Dictionary<string, string>();

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
        if (name == newFileName)
        {
            continue;
        }
        renamesPrefixed[$"TestData/{name}"] = $"TestData/{newFileName}";
        renamesBare[name] = newFileName;
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

// Step 3: Update references in source (.cs) and documentation (.md) files.
// Docs like docs/e2e-tdd-issue-debugging.md list bundle filenames in the Recordings
// table; missing markdown coverage leaves the table pointing at deleted files.
Console.WriteLine("Scanning .cs and .md files for references to update...");
var scannableFiles = new[] { "*.cs", "*.md" }
    .SelectMany(pattern => Directory.GetFiles(repoRoot, pattern, SearchOption.AllDirectories))
    .Where(f => !f.Replace('\\', '/').Contains("/bin/"))
    .Where(f => !f.Replace('\\', '/').Contains("/obj/"))
    .Where(f => !f.Replace('\\', '/').Contains("/.git/"))
    .ToList();

int filesUpdated = 0;
foreach (string file in scannableFiles)
{
    string content = File.ReadAllText(file);
    string updated = content;

    bool isMarkdown = file.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    var renames = isMarkdown ? renamesBare : renamesPrefixed;

    foreach (var (oldRef, newRef) in renames)
    {
        updated = updated.Replace(oldRef, newRef);
    }

    if (updated != content)
    {
        string relPath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
        Console.WriteLine($"  Updated: {relPath}");

        if (!dryRun)
        {
            File.WriteAllText(file, updated);
        }

        filesUpdated++;
    }
}

Console.WriteLine();
Console.WriteLine($"Summary: {duplicateGroups.Count} duplicate group(s), {totalRemoved} file(s) removed, {filesUpdated} source/doc file(s) updated.");

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

// Returns true when .gitattributes assigns the `lfs` filter to the given repo-relative
// path. Works for paths that do not exist yet — `git check-attr` matches the path string
// against the attribute patterns, so we can validate a consolidated name before creating it.
static bool IsLfsTracked(string repoRoot, string relPath)
{
    var psi = new ProcessStartInfo("git")
    {
        WorkingDirectory = repoRoot,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add("check-attr");
    psi.ArgumentList.Add("filter");
    psi.ArgumentList.Add("--");
    psi.ArgumentList.Add(relPath);

    using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start 'git' — is Git on PATH?");
    string output = proc.StandardOutput.ReadToEnd();
    string error = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    if (proc.ExitCode != 0)
    {
        throw new InvalidOperationException($"'git check-attr' failed (exit {proc.ExitCode}): {error.Trim()}");
    }

    // `git check-attr filter -- <path>` prints e.g. "<path>: filter: lfs".
    return output.Contains("filter: lfs", StringComparison.Ordinal);
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
