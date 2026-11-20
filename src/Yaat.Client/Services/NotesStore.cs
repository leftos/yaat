using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yaat.Sim;

namespace Yaat.Client.Services;

/// <summary>
/// Keeps the Notes window's text in its own UTF-8 file, so it survives a restart and a scenario change until the user clears
/// it. The text is the user's own and belongs to this machine, so it stays out of the preferences and the settings bundle.
/// </summary>
/// <param name="path">The file the text is kept in.</param>
/// <param name="log">Takes the warning about a file that cannot be read or written.</param>
public sealed class NotesStore(string path, ILogger log)
{
    private readonly string _path = path;
    private readonly ILogger _log = log;
    private readonly bool _discards;

    private NotesStore()
        : this("", NullLogger.Instance) => _discards = true;

    /// <summary>
    /// A store with no file: it loads empty and drops every save, so a designer or screenshot window never shows or overwrites
    /// the user's notes.
    /// </summary>
    /// <returns>The store.</returns>
    public static NotesStore Discarding() => new();

    /// <summary>The file the app keeps the notes in.</summary>
    public static string DefaultPath => YaatPaths.Combine("notes.txt");

    /// <summary>Reads the kept text.</summary>
    /// <returns>The text, or empty when there is no file or it cannot be read (the latter with a warning).</returns>
    public string Load()
    {
        if (_discards)
        {
            return "";
        }
        try
        {
            return File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.LogWarning(ex, "The notes file {Path} could not be read; starting with no notes", _path);
            return "";
        }
    }

    /// <summary>Replaces the kept text. A write that fails is logged and dropped; it never throws.</summary>
    /// <param name="text">The text to keep.</param>
    public void Save(string text)
    {
        if (_discards)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? throw new IOException($"{_path} has no folder"));
            AtomicFile.WriteAllText(_path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.LogWarning(ex, "The notes file {Path} could not be written", _path);
        }
    }
}
