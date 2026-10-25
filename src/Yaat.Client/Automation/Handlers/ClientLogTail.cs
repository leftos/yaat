using System.Text;

namespace Yaat.Client.Automation.Handlers;

/// <summary>
/// The lines <c>client_log_matches</c> reads from the client's log file: every complete line the file gained after a byte offset.
/// A read opens the file afresh (the client's writer holds it with <see cref="FileShare.ReadWrite"/> and
/// <see cref="FileShare.Delete"/>), seeks to the offset and reads to the end, so it never waits on the writer and never changes the
/// file. A trailing partial line's bytes wait for the newline that completes it, and the tail of a line already partly written at
/// the offset is not a line the wait began after. Reads run off the UI thread, and only one read runs at a time, from that wait's
/// poll loop.
/// </summary>
public sealed class ClientLogTail(string path, long offset)
{
    private static volatile Action? s_beforeRead;
    private readonly List<byte> _pending = [];
    private long _offset = offset;
    private bool _started;
    private bool _discardUntilNewline;

    /// <summary>Runs on the reading thread just before the file is opened, before every read; a test's probe for which thread reads.</summary>
    public static Action? BeforeRead
    {
        get => s_beforeRead;
        set => s_beforeRead = value;
    }

    /// <summary>The log file this tail reads.</summary>
    public string FilePath => path;

    /// <summary>The complete lines the file gained since the previous read, oldest first, without their newlines.</summary>
    /// <exception cref="IOException">The file could not be opened or read; the offset does not move.</exception>
    public IReadOnlyList<string> Read()
    {
        BeforeRead?.Invoke();

        byte[] chunk;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (!_started)
            {
                // The wait began mid-line when the byte before the offset is not a newline: what follows up to the next newline
                // is the end of a line written before it.
                _discardUntilNewline = (_offset > 0) && PreviousByteIsNotNewline(stream, _offset);
                _started = true;
            }

            stream.Seek(_offset, SeekOrigin.Begin);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            chunk = buffer.ToArray();
        }

        _offset += chunk.Length;
        if (_discardUntilNewline)
        {
            int firstNewline = Array.IndexOf(chunk, (byte)'\n');
            if (firstNewline < 0)
            {
                return [];
            }

            _discardUntilNewline = false;
            chunk = chunk[(firstNewline + 1)..];
        }

        int lastNewline = Array.LastIndexOf(chunk, (byte)'\n');
        if (lastNewline < 0)
        {
            _pending.AddRange(chunk);
            return [];
        }

        // Everything up to the last newline is whole lines: decoding it on its own never splits a UTF-8 sequence.
        byte[] complete = [.. _pending, .. chunk[..(lastNewline + 1)]];
        _pending.Clear();
        _pending.AddRange(chunk[(lastNewline + 1)..]);
        string[] lines = Encoding.UTF8.GetString(complete).Split('\n');
        // The text ends with that newline, so the split's last piece is the empty remnant of it rather than a line.
        return [.. lines[..^1].Select(line => line.TrimEnd('\r'))];
    }

    /// <summary>Whether the byte just before <paramref name="offset"/> is not a newline, read from the stream already open.</summary>
    private static bool PreviousByteIsNotNewline(FileStream stream, long offset)
    {
        stream.Seek(offset - 1, SeekOrigin.Begin);
        return stream.ReadByte() != '\n';
    }
}
