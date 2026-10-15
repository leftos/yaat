using System.IO.Compression;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Sim.Logging;
using Yaat.Sim.Simulation;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// A bug report bundle is posted publicly, so the recording it carries is exported pseudonymised and its bookmarks name
/// their creators by pseudonym; a plain Save Recording keeps the real initials. When the server sends no participant
/// list, the client's own list must still hold a CRC client with no position, whose display name is its bare real name.
/// </summary>
public class MainViewModelBugBundleTests
{
    [AvaloniaFact]
    public async Task SaveRecording_PassesAnonymizeFalse_BugBundle_PassesTrue()
    {
        var vm = new MainViewModel(new FakeFilePickerService()) { IsPaused = true };
        List<bool> flags = [];
        vm.RecordingExporter = anonymize =>
        {
            flags.Add(anonymize);
            return Task.FromResult<byte[]?>(null);
        };

        await vm.SaveRecordingCommand.ExecuteAsync(null);
        await vm.SaveBugReportBundleCommand.ExecuteAsync(null);

        Assert.Equal([false, true], flags);
    }

    [AvaloniaFact]
    public async Task BugBundle_BookmarksJson_HasPseudonymisedCreators()
    {
        string path = Path.Combine(Path.GetTempPath(), $"yaat-bundle-test-{Guid.NewGuid():N}.yaat-bug-report-bundle.zip");
        var picker = new FakeFilePickerService();
        picker.QueueSaveFile(path);
        var vm = new MainViewModel(picker) { IsPaused = true, RecordingExporter = _ => Task.FromResult<byte[]?>(MinimalRecording()) };
        // No server log to give a list: the client's own list makes JD A0.
        vm.RoomMembers.Add(new RoomMemberDto("1826127", "JD", "ZOA", Yaat.Sim.ClientKind.Main, DateTime.UtcNow, "conn-1"));
        vm.ApplyBookmarks([new TimelineBookmarkDto("bm-1", 10, "JD saw the conflict", "JD"), new TimelineBookmarkDto("bm-2", 20, null, "XY")]);

        try
        {
            await vm.SaveBugReportBundleCommand.ExecuteAsync(null);

            Assert.Equal("Bug report bundle saved", vm.StatusText);
            Assert.Equal(
                [new TimelineBookmark("bm-1", 10, "JD saw the conflict", "A0"), new TimelineBookmark("bm-2", 20, null, "??")],
                ReadBookmarks(path)
            );
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The owner's ruling: with no list from the server, everything is scrubbed with the client's own list and the bundle
    // says its pseudonyms may not line up across files.
    [AvaloniaFact]
    public async Task BugBundle_WhenTheServerListFetchThrows_UsesTheLocalListAndAddsTheNote()
    {
        string path = Path.Combine(Path.GetTempPath(), $"yaat-bundle-test-{Guid.NewGuid():N}.yaat-bug-report-bundle.zip");
        var picker = new FakeFilePickerService();
        picker.QueueSaveFile(path);
        var vm = new MainViewModel(picker)
        {
            IsPaused = true,
            ActiveRoomId = "room-1",
            RecordingExporter = _ => Task.FromResult<byte[]?>(MinimalRecording()),
            SessionServerLogFetcher = () => Task.FromException<SessionServerLogDto>(new InvalidOperationException("server unreachable")),
        };
        vm.RoomMembers.Add(new RoomMemberDto("1826127", "JD", "ZOA", Yaat.Sim.ClientKind.Main, DateTime.UtcNow, "conn-1"));
        vm.ApplyBookmarks([new TimelineBookmarkDto("bm-1", 10, "JD saw the conflict", "JD")]);

        try
        {
            await vm.SaveBugReportBundleCommand.ExecuteAsync(null);

            Assert.Equal("Bug report bundle saved", vm.StatusText);
            Assert.Equal([new TimelineBookmark("bm-1", 10, "JD saw the conflict", "A0")], ReadBookmarks(path));
            Assert.Equal(
                "Pseudonyms in this report may not match across files: the server's participant list was unavailable when the report was built.",
                ReadText(path, "ANONYMIZATION-NOTE.txt")
            );
            Assert.Null(ReadText(path, "yaat-server.log"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A room whose scenario failed to load has no recording, but its server log and participant list are still fetched,
    // and the server log gets the client's backstop pass for senders outside the list.
    [AvaloniaFact]
    public async Task BugBundle_InARoomWithNoRecording_StillUsesTheServerList()
    {
        string path = Path.Combine(Path.GetTempPath(), $"yaat-bundle-test-{Guid.NewGuid():N}.zip");
        var vm = new MainViewModel(new FakeFilePickerService())
        {
            ActiveRoomId = "room-1",
            SessionServerLogFetcher = () =>
                Task.FromResult(
                    new SessionServerLogDto(
                        "[SendCommand] UAL238: 'FH 270' (from A0)\n[SendCommand] UAL238: 'FH 090' (from JX)",
                        [new LogParticipant("1826127", "Jane Doe", "JD")]
                    )
                ),
        };

        try
        {
            await vm.WriteBugReportBundleAsync(path, recordingBytes: null);

            Assert.Equal("[SendCommand] UAL238: 'FH 270' (from A0)\n[SendCommand] UAL238: 'FH 090' (from ??)", ReadText(path, "yaat-server.log"));
            Assert.Null(ReadText(path, "ANONYMIZATION-NOTE.txt"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void LocalFallbackList_IncludesABareCrcRealName()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.CrcRoomMembers.Add(new CrcRoomMemberDto("crc-1", "1999999", "Zed Outsider", null, true));
        vm.CrcRoomMembers.Add(new CrcRoomMemberDto("crc-2", "1888888", "OAK_TWR (Pat Smith)", "OAK_TWR", true));
        vm.CrcRoomMembers.Add(new CrcRoomMemberDto("crc-3", "1777777", "OAK_GND", null, true));
        vm.CrcLobbyClients.Add(new CrcLobbyClientDto("crc-4", "1666666", "Carl Lee", "ZOA", null, false));
        vm.CrcLobbyClients.Add(new CrcLobbyClientDto("crc-5", "1555555", "unknown", "ZOA", null, false));

        IReadOnlyList<LogParticipant> participants = vm.BuildLocalLogParticipants();

        Assert.Contains(new LogParticipant("1999999", "Zed Outsider", null), participants);
        Assert.Contains(new LogParticipant("1888888", "Pat Smith", null), participants);
        Assert.Contains(new LogParticipant("1666666", "Carl Lee", null), participants);
        // A bare position callsign and the "unknown" placeholder name nobody.
        Assert.Contains(new LogParticipant("1777777", null, null), participants);
        Assert.Contains(new LogParticipant("1555555", null, null), participants);
    }

    private static byte[] MinimalRecording()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream entry = zip.CreateEntry("manifest.json").Open();
            entry.Write("{}"u8);
        }

        return stream.ToArray();
    }

    private static string? ReadText(string bundlePath, string entryName)
    {
        using ZipArchive bundle = ZipFile.OpenRead(bundlePath);
        ZipArchiveEntry? entry = bundle.GetEntry(entryName);
        if (entry is null)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<TimelineBookmark> ReadBookmarks(string bundlePath)
    {
        using ZipArchive bundle = ZipFile.OpenRead(bundlePath);
        ZipArchiveEntry entry = bundle.GetEntry("bookmarks.json") ?? throw new InvalidOperationException("The bundle has no bookmarks.json");
        using Stream stream = entry.Open();
        RecordingBookmarks? parsed = JsonSerializer.Deserialize<RecordingBookmarks>(stream, RecordingJsonOptions.Default);
        return parsed?.Bookmarks ?? [];
    }
}
