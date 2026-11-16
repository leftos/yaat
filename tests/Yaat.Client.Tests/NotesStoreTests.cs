using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>The notes file: what it gives back after a restart, and the two ways it can have nothing to give.</summary>
public sealed class NotesStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("notes-store-").FullName;
    private readonly WarningLog _log = new();

    private string FilePath => Path.Combine(_dir, "notes.txt");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_Missing_File_Loads_As_Empty_With_No_Warning()
    {
        Assert.Equal("", new NotesStore(FilePath, _log).Load());
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void An_Existing_File_Loads_Its_Text()
    {
        File.WriteAllText(FilePath, "first line\nsecond line é");

        Assert.Equal("first line\nsecond line é", new NotesStore(FilePath, _log).Load());
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void A_Saved_Text_Loads_Back()
    {
        var store = new NotesStore(FilePath, _log);

        store.Save("kept until cleared");

        Assert.Equal("kept until cleared", new NotesStore(FilePath, _log).Load());
    }

    [Fact]
    public void An_Unreadable_Path_Loads_As_Empty_With_A_Warning_Naming_It()
    {
        // A folder where the file should be: reading it fails, which must not reach the window.
        string folder = Path.Combine(_dir, "a-folder");
        Directory.CreateDirectory(folder);

        string text = new NotesStore(folder, _log).Load();

        Assert.Equal("", text);
        Assert.Contains(folder, Assert.Single(_log.Warnings), StringComparison.Ordinal);
    }

    private sealed class WarningLog : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
