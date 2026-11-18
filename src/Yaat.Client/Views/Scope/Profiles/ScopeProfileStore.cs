using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope.Profiles;

/// <summary>
/// The Scope profile files: one <c>&lt;name&gt;.json</c> per profile under the root. The shipped profiles are embedded in
/// the app and written to the root when their file is absent, never over an existing file, so an edit survives an
/// update. A field a file lacks takes the shipped value, a field this build does not know is kept, a field holding a value
/// the scope does not take keeps the shipped value with a warning, and a file that is not a profile is renamed
/// <c>.bak</c> and the shipped profile used.
/// </summary>
/// <param name="rootDir">The folder holding the profile files, created on write.</param>
/// <param name="log">The logger unreadable, corrupt and out-of-range files are reported to.</param>
public sealed class ScopeProfileStore(string rootDir, ILogger log)
{
    // Serializes file IO across instances (parallel tests share processes).
    private static readonly Lock FileLock = new();

    private static readonly ConcurrentDictionary<ScopeProfileKind, string> ShippedJson = new();

    private bool _shippedWritten;

    /// <summary>The production root: <c>%LOCALAPPDATA%/yaat/scope/profiles</c>.</summary>
    public static string DefaultRootDir => YaatPaths.Combine("scope", "profiles");

    /// <summary>The shipped profile of <paramref name="kind"/>, as the app embeds it.</summary>
    public static ScopeProfile Shipped(ScopeProfileKind kind) =>
        JsonSerializer.Deserialize<ScopeProfile>(ShippedText(kind), ScopeProfile.JsonOptions)
        ?? throw new InvalidOperationException($"The shipped scope profile '{kind}' is empty.");

    /// <summary>
    /// The profile of <paramref name="kind"/> from its file, after the shipped files absent from the root are written; the
    /// shipped profile when the file cannot be read or is not a profile.
    /// </summary>
    public ScopeProfile Get(ScopeProfileKind kind)
    {
        lock (FileLock)
        {
            WriteAbsentShippedFiles();
            return Load(kind);
        }
    }

    private static string ShippedText(ScopeProfileKind kind) =>
        ShippedJson.GetOrAdd(
            kind,
            k =>
            {
                string resource = $"Yaat.Client.ScopeProfiles.{ScopeProfilePicker.NameOf(k)}.json";
                using Stream stream =
                    typeof(ScopeProfileStore).Assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException($"The shipped scope profile resource '{resource}' is missing.");
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        );

    private string PathOf(ScopeProfileKind kind) => Path.Combine(rootDir, $"{ScopeProfilePicker.NameOf(kind)}.json");

    /// <summary>Writes each shipped profile whose file is absent, once per store; a folder that refuses the write is reported.</summary>
    private void WriteAbsentShippedFiles()
    {
        if (_shippedWritten)
        {
            return;
        }

        _shippedWritten = true;
        foreach (ScopeProfileKind kind in Enum.GetValues<ScopeProfileKind>())
        {
            if (!File.Exists(PathOf(kind)))
            {
                WriteShipped(kind);
            }
        }
    }

    private void WriteShipped(ScopeProfileKind kind)
    {
        string path = PathOf(kind);
        try
        {
            Directory.CreateDirectory(rootDir);
            AtomicFile.WriteAllText(path, ShippedText(kind));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not write the shipped scope profile {Path}", path);
        }
    }

    private ScopeProfile Load(ScopeProfileKind kind)
    {
        ScopeProfile shipped = Shipped(kind);
        string path = PathOf(kind);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not read scope profile {Path}; using the shipped profile", path);
            return shipped;
        }

        if (Parse(text, kind, path, out string? reason) is not { } profile)
        {
            SetCorruptAside(path, kind, reason);
            return shipped;
        }

        return Checked(profile, shipped, path);
    }

    /// <summary>
    /// The profile <paramref name="text"/> holds, each field it lacks taken from the shipped profile, and a band list that
    /// does not bind or a null template replaced by the shipped one; null with the reason when it holds no profile: text
    /// that is not a JSON object, a property name it repeats, or a field of the wrong JSON type.
    /// </summary>
    private ScopeProfile? Parse(string text, ScopeProfileKind kind, string path, out string? reason)
    {
        JsonObject file;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject parsed)
            {
                reason = "not a JSON object";
                return null;
            }

            file = parsed;

            // The object builds its property map lazily, and a duplicate name throws only then, so it is built here.
            _ = file.Count;
        }
        catch (ArgumentException ex)
        {
            reason = ex.Message;
            return null;
        }
        catch (JsonException ex)
        {
            reason = ex.Message;
            return null;
        }

        try
        {
            JsonObject shipped = JsonNode.Parse(ShippedText(kind))!.AsObject();
            foreach ((string name, JsonNode? value) in shipped)
            {
                if (!file.ContainsKey(name))
                {
                    file[name] = value?.DeepClone();
                }
            }

            KeepShippedBandsUnlessTheyBind(file, shipped, path);
            KeepShippedTemplateWhenNull(file, shipped, path);
            reason = null;
            return file.Deserialize<ScopeProfile>(ScopeProfile.JsonOptions);
        }
        catch (JsonException ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Puts the shipped bands in <paramref name="file"/> when its <c>bands</c> is null or an array that does not bind to
    /// bands (a band missing a field or holding a wrong type), with a warning. Any other JSON type is left to fail the file.
    /// </summary>
    private void KeepShippedBandsUnlessTheyBind(JsonObject file, JsonObject shipped, string path)
    {
        JsonNode? bands = file["bands"];
        if (bands is JsonArray array)
        {
            try
            {
                _ = array.Deserialize<List<ScopeZoomBand>>(ScopeProfile.JsonOptions);
                return;
            }
            catch (JsonException ex)
            {
                log.LogWarning("Scope profile {Path} holds bands that do not read ({Reason}); using the shipped bands", path, ex.Message);
            }
        }
        else if (bands is null)
        {
            log.LogWarning("Scope profile {Path} holds no bands; using the shipped bands", path);
        }
        else
        {
            return;
        }

        file["bands"] = shipped["bands"]?.DeepClone();
    }

    /// <summary>Puts the shipped template in <paramref name="file"/> when its <c>template</c> is null, with a warning.</summary>
    private void KeepShippedTemplateWhenNull(JsonObject file, JsonObject shipped, string path)
    {
        if (file["template"] is not null)
        {
            return;
        }

        log.LogWarning("Scope profile {Path} names no template; using '{Shipped}'", path, (string?)shipped["template"]);
        file["template"] = shipped["template"]?.DeepClone();
    }

    /// <summary>
    /// Renames a corrupt file to <c>.bak</c> and writes the shipped profile in its place; a file that cannot be moved is
    /// left alone.
    /// </summary>
    private void SetCorruptAside(string path, ScopeProfileKind kind, string? reason)
    {
        log.LogWarning("Scope profile {Path} is not a profile ({Reason}); using the shipped profile", path, reason);
        try
        {
            File.Move(path, path + ".bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Could not set the corrupt scope profile {Path} aside; leaving it in place", path);
            return;
        }

        log.LogWarning("Kept the corrupt scope profile {Path} at {Backup}", path, path + ".bak");
        WriteShipped(kind);
    }

    /// <summary>
    /// <paramref name="profile"/> with each field holding a value the scope does not take replaced by the shipped one,
    /// warning for each.
    /// </summary>
    private ScopeProfile Checked(ScopeProfile profile, ScopeProfile shipped, string path)
    {
        ScopeProfile result = profile;
        if (!ScopeProfilePicker.TryParseKind(profile.Kind, out _))
        {
            log.LogWarning("Scope profile {Path} names unknown kind '{Kind}'; using '{Shipped}'", path, profile.Kind, shipped.Kind);
            result = result with { Kind = shipped.Kind };
        }

        if (!ScopeTemplates.IsSelectable(profile.Template))
        {
            log.LogWarning("Scope profile {Path} names unknown template '{Template}'; using '{Shipped}'", path, profile.Template, shipped.Template);
            result = result with { Template = shipped.Template };
        }

        if ((profile.Bands.Count == 0) || !profile.Bands.All(IsBand))
        {
            log.LogWarning("Scope profile {Path} holds no bands or a band the scope does not take; using the shipped bands", path);
            result = result with { Bands = shipped.Bands };
        }

        return result;
    }

    private static bool IsBand(ScopeZoomBand? band) =>
        (band is not null)
        && (band.OthersLevel is ScopeProfile.OthersFull or ScopeProfile.OthersPartial)
        && (band.Rings is ScopeProfile.RingsJRing or ScopeProfile.RingsDri)
        && (band.VectorMin >= 0)
        && (band.History >= 0)
        && (band.MaxNm is null or > 0);
}
