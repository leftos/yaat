using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// The file picks <c>queue_file_pick</c> queues and the injected picker that answers the client's dialogs with them in
/// automation mode: a path or a cancel, in order, and a failure at once when nothing is queued. The queue is process-wide,
/// so every test clears it and restores the mode on the way out.
/// </summary>
public sealed class FilePickTests : AutomationHostFixture
{
    protected override void Dispose(bool disposing)
    {
        FilePickQueue.Clear();
        AutomationMode.IsEnabled = false;
        base.Dispose(disposing);
    }

    [AvaloniaFact]
    public async Task QueueFilePick_Path_OpenFileReturnsQueuedPath()
    {
        AutomationMode.IsEnabled = true;
        using AutomationHost host = StartHost(() => []);
        await using AutomationPipeTestClient client = await Connect();
        var window = new Window();

        JsonElement result = Result(await Send(client, ProtocolMethods.QueueFilePick, new { path = @"C:\temp\scenario.json" }));

        Assert.Equal(1, result.GetProperty("queued").GetInt32());
        IFilePickerService picker = FilePickerFactory.Create(window);
        string? path = await picker.OpenFileAsync(new OpenFileOptions("Open scenario", []));
        Assert.Equal(@"C:\temp\scenario.json", path);
        Assert.Equal(0, FilePickQueue.Count);
    }

    [AvaloniaFact]
    public async Task QueueFilePick_Cancel_ReturnsNullAndEmptyList()
    {
        AutomationMode.IsEnabled = true;
        using AutomationHost host = StartHost(() => []);
        await using AutomationPipeTestClient client = await Connect();
        var window = new Window();

        Result(await Send(client, ProtocolMethods.QueueFilePick, new { cancel = true }));
        Result(await Send(client, ProtocolMethods.QueueFilePick, new { cancel = true }));

        IFilePickerService picker = FilePickerFactory.Create(window);
        Assert.Null(await picker.SaveFileAsync(new SaveFileOptions("Save", "sample.zip", [], "zip")));
        Assert.Empty(await picker.OpenFilesAsync(new OpenFileOptions("Open", [])));
        Assert.Equal(0, FilePickQueue.Count);
    }

    [AvaloniaFact]
    public async Task QueueFilePick_Fifo_AnswersInOrder()
    {
        AutomationMode.IsEnabled = true;
        using AutomationHost host = StartHost(() => []);
        await using AutomationPipeTestClient client = await Connect();
        var window = new Window();

        JsonElement first = Result(await Send(client, ProtocolMethods.QueueFilePick, new { path = @"C:\first" }));
        JsonElement second = Result(await Send(client, ProtocolMethods.QueueFilePick, new { path = @"C:\second" }));

        Assert.Equal(1, first.GetProperty("queued").GetInt32());
        Assert.Equal(2, second.GetProperty("queued").GetInt32());
        IFilePickerService picker = FilePickerFactory.Create(window);
        Assert.Equal(@"C:\first", await picker.OpenFolderAsync(new OpenFolderOptions("Pick a folder")));
        Assert.Equal(@"C:\second", await picker.SaveFileAsync(new SaveFileOptions("Save", "second", [], "zip")));
    }

    [AvaloniaFact]
    public async Task InjectedPicker_EmptyQueue_ThrowsAtOnce()
    {
        AutomationMode.IsEnabled = true;
        var picker = new InjectedFilePickerService();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            picker.OpenFileAsync(new OpenFileOptions("Open", []))
        );

        Assert.Equal("No file pick queued: call queue_file_pick before opening a file dialog in automation mode.", ex.Message);
    }

    [AvaloniaFact]
    public async Task QueueFilePick_InvalidParams_ReturnsInvalidParam()
    {
        AutomationMode.IsEnabled = true;
        using AutomationHost host = StartHost(() => []);
        await using AutomationPipeTestClient client = await Connect();

        string[] invalid =
        [
            "{}",
            """{"path":""}""",
            """{"cancel":false}""",
            """{"path":"a","cancel":true}""",
            """{"cancel":"yes"}""",
            """{"path":5}""",
        ];

        foreach (string paramsJson in invalid)
        {
            Error(await SendRawParams(client, ProtocolMethods.QueueFilePick, paramsJson), AutomationErrorCodes.InvalidParam);
            Assert.Equal(0, FilePickQueue.Count);
        }
    }

    [AvaloniaFact]
    public void FilePickerFactory_ModeOff_ReturnsAvaloniaPicker()
    {
        AutomationMode.IsEnabled = false;

        Assert.IsType<AvaloniaFilePickerService>(FilePickerFactory.Create(new Window()));
    }

    [AvaloniaFact]
    public void FilePickerFactory_ModeOn_ReturnsInjectedPicker()
    {
        AutomationMode.IsEnabled = true;

        Assert.IsType<InjectedFilePickerService>(FilePickerFactory.Create(new Window()));
    }
}
