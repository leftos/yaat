using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

/// <summary>Outcome of a <see cref="CrcAliasStore.Load" />, for the status line shown to the user.</summary>
/// <param name="Directory">Directory the files were read from, or null when no CRC install was found.</param>
/// <param name="FilesLoaded">File names actually read, in load order.</param>
/// <param name="AliasCount">Aliases available after duplicates collapsed.</param>
/// <param name="ShadowedByBuiltins">Alias names that a YAAT built-in dot command takes precedence over.</param>
public sealed record CrcAliasLoadResult(
    string? Directory,
    IReadOnlyList<string> FilesLoaded,
    int AliasCount,
    IReadOnlyList<string> ShadowedByBuiltins
);

/// <summary>
/// Holds the CRC aliases read from the user's CRC installation and expands terminal input against them.
/// </summary>
/// <remarks>
/// Deliberately mirrors CRC's <c>AliasParser</c> so an alias means the same thing in both clients:
/// only <c>{ArtccId}.txt</c> and <c>MyAliases.txt</c> are read (never a directory glob), the personal
/// file loads second so it overrides the ARTCC file, names match case-insensitively, and expansion
/// recurses at token level so an alias body may reference other aliases.
/// </remarks>
public sealed class CrcAliasStore
{
    /// <summary>Matches CRC's cap on total substitution work, which guards against a self-referential alias.</summary>
    private const int MaxSubstitutions = 1000;

    private const string PersonalAliasesFileName = "MyAliases.txt";
    private const string AliasesDirectoryName = "Aliases";

    private static readonly ILogger Log = AppLog.CreateLogger("CrcAliasStore");

    private readonly Dictionary<string, CrcAlias> _aliases = new(StringComparer.InvariantCultureIgnoreCase);

    public int Count => _aliases.Count;

    public CrcAliasLoadResult LastLoad { get; private set; } = new(null, [], 0, []);

    public bool Contains(string name) => _aliases.ContainsKey(name);

    public bool TryGet(string name, out CrcAlias alias) => _aliases.TryGetValue(name, out alias!);

    /// <summary>
    /// Resolves the alias directory: <paramref name="overrideDirectory" /> when set, otherwise the
    /// <c>Aliases</c> folder inside the detected CRC config directory. Null when CRC isn't installed.
    /// </summary>
    public static string? ResolveDirectory(string? overrideDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return overrideDirectory;
        }

        var configDir = CrcConfigService.GetCrcConfigDir();
        return configDir is null ? null : Path.Combine(configDir, AliasesDirectoryName);
    }

    /// <summary>
    /// Replaces the alias table from disk. A missing directory or missing file is a normal no-op — plenty
    /// of users have no CRC installed, or an ARTCC whose alias file was never downloaded.
    /// </summary>
    /// <param name="overrideDirectory">User-configured alias directory, or null to auto-detect CRC.</param>
    /// <param name="artccId">ARTCC whose <c>{id}.txt</c> to load, or null to load only the personal file.</param>
    /// <param name="reservedNames">YAAT built-in dot commands, which take precedence over an alias of the same name.</param>
    public CrcAliasLoadResult Load(string? overrideDirectory, string? artccId, IReadOnlySet<string> reservedNames)
    {
        _aliases.Clear();

        var directory = ResolveDirectory(overrideDirectory);
        if (directory is null || !Directory.Exists(directory))
        {
            Log.LogInformation("No CRC alias directory found (resolved {Directory}); CRC aliases unavailable", directory ?? "<none>");
            LastLoad = new CrcAliasLoadResult(directory, [], 0, []);
            return LastLoad;
        }

        var filesLoaded = new List<string>();

        // Order matters and mirrors CRC: the ARTCC file first, the personal file second so it wins.
        if (!string.IsNullOrWhiteSpace(artccId))
        {
            LoadFile(Path.Combine(directory, $"{artccId}.txt"), filesLoaded);
        }

        LoadFile(Path.Combine(directory, PersonalAliasesFileName), filesLoaded);

        var shadowed = _aliases.Keys.Where(reservedNames.Contains).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (shadowed.Count > 0)
        {
            Log.LogWarning("{Count} CRC alias(es) are shadowed by YAAT built-in commands: {Names}", shadowed.Count, string.Join(", ", shadowed));
        }

        Log.LogInformation("Loaded {Count} CRC aliases from {Files}", _aliases.Count, string.Join(", ", filesLoaded));
        LastLoad = new CrcAliasLoadResult(directory, filesLoaded, _aliases.Count, shadowed);
        return LastLoad;
    }

    private void LoadFile(string path, List<string> filesLoaded)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var alias in CrcAliasFileParser.Parse(File.ReadAllLines(path), Path.GetFileName(path)))
            {
                _aliases[alias.Name] = alias;
            }

            filesLoaded.Add(Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Failed to read CRC alias file {Path}", path);
        }
    }

    /// <summary>
    /// Expands every alias reference in <paramref name="input" /> and returns the resulting command text.
    /// </summary>
    /// <remarks>
    /// Follows CRC exactly: scan right-to-left, and after each substitution restart the scan from the new
    /// end of the token list, so aliases referenced inside a replacement body get expanded too. Arguments
    /// are the tokens immediately following the alias name — missing ones substitute as empty string, and
    /// any the body doesn't declare are left in place, ending up appended after the expansion.
    /// </remarks>
    public bool TryExpand(string input, out string expanded, out string? error)
    {
        error = null;
        var tokens = CrcAliasFileParser.Tokenize(input).ToList();
        var budget = MaxSubstitutions;

        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (--budget <= 0)
            {
                expanded = "";
                error = "Infinite recursion detected. Possible self-referential alias.";
                return false;
            }

            if (!_aliases.TryGetValue(tokens[i], out var alias))
            {
                continue;
            }

            var arguments = new List<string>(alias.ArgumentCount);
            var consumed = 0;
            for (var a = 0; a < alias.ArgumentCount; a++)
            {
                var index = i + 1 + a;
                if (index < tokens.Count)
                {
                    arguments.Add(tokens[index]);
                    consumed++;
                }
                else
                {
                    arguments.Add("");
                }
            }

            var replacement = arguments.Count == 0 ? alias.ReplacementTokens : SubstituteArguments(alias.ReplacementTokens, arguments);
            tokens.RemoveRange(i, consumed + 1);
            tokens.InsertRange(i, replacement);
            i = tokens.Count;
        }

        expanded = string.Join(" ", tokens);
        return true;
    }

    /// <summary>
    /// Replaces <c>$1</c>..<c>$n</c> in each replacement token. Plain ordered substring replacement, as in
    /// CRC — which is why a body can write <c>$1Z</c> to mean "argument one followed by a literal Z".
    /// </summary>
    private static List<string> SubstituteArguments(IReadOnlyList<string> tokens, List<string> arguments)
    {
        var result = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            var substituted = token;
            for (var a = 0; a < arguments.Count; a++)
            {
                substituted = substituted.Replace($"${a + 1}", arguments[a], StringComparison.Ordinal);
            }

            result.Add(substituted);
        }

        return result;
    }
}
