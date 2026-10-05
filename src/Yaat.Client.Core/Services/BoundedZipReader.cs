using System.IO.Compression;

namespace Yaat.Client.Services;

/// <summary>
/// Reads a settings file's zip entries into memory with a ceiling, so a crafted file cannot exhaust memory: one entry
/// may unpack to at most <see cref="SettingsBundleFile.MaxEntryBytes"/>, checked against its header and again while
/// copying (a header can lie), and the entries of one file to at most <see cref="SettingsBundleFile.MaxBundleBytes"/>.
/// </summary>
/// <param name="fileName">The file the entries belong to, named in every error.</param>
internal sealed class BoundedZipReader(string fileName)
{
    private const int MegaByte = 1024 * 1024;

    private long _total;

    /// <summary>Reads a whole single-item file, failing past <see cref="SettingsBundleFile.MaxEntryBytes"/>.</summary>
    public static byte[] ReadFile(Stream input, string fileName) =>
        ReadCapped(input) ?? throw new InvalidDataException($"'{fileName}' is larger than {SettingsBundleFile.MaxEntryBytes / MegaByte} MB.");

    /// <summary>Reads one entry, failing when it or the file's entries together pass their ceiling, or when it is damaged.</summary>
    public byte[] Read(ZipArchiveEntry entry)
    {
        if (entry.Length > SettingsBundleFile.MaxEntryBytes)
        {
            throw EntryTooLarge(entry);
        }

        byte[]? content;
        try
        {
            using Stream stream = entry.Open();
            content = ReadCapped(stream);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"'{fileName}' has a damaged entry '{entry.FullName}': {ex.Message}", ex);
        }

        if (content is null)
        {
            throw EntryTooLarge(entry);
        }

        _total += content.Length;
        if (_total > SettingsBundleFile.MaxBundleBytes)
        {
            throw new InvalidDataException($"'{fileName}' unpacks to more than {SettingsBundleFile.MaxBundleBytes / MegaByte} MB.");
        }

        return content;
    }

    private InvalidDataException EntryTooLarge(ZipArchiveEntry entry) =>
        new($"'{fileName}' has an entry '{entry.FullName}' larger than {SettingsBundleFile.MaxEntryBytes / MegaByte} MB.");

    // Returns null as soon as the copy passes the ceiling, whatever the stream claimed its length was.
    private static byte[]? ReadCapped(Stream stream)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > SettingsBundleFile.MaxEntryBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }
}
