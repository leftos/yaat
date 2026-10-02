using Yaat.Client.Services;

namespace Yaat.Client.Automation;

/// <summary>
/// Automation mode's <see cref="IFilePickerService"/>: it holds no storage provider and opens no dialog. Every call takes
/// one answer from <see cref="FilePickQueue"/> — that path, or null (an empty list from <see cref="OpenFilesAsync"/>) for
/// a cancel. A call with nothing queued fails at once.
/// </summary>
public sealed class InjectedFilePickerService : IFilePickerService
{
    private const string EmptyQueueMessage = "No file pick queued: call queue_file_pick before opening a file dialog in automation mode.";

    public Task<string?> OpenFileAsync(OpenFileOptions options) => Task.FromResult(PathOrNull(Dequeue()));

    public Task<IReadOnlyList<string>> OpenFilesAsync(OpenFileOptions options)
    {
        FilePickAnswer answer = Dequeue();
        IReadOnlyList<string> paths = answer.IsCancel ? [] : [answer.Path!];
        return Task.FromResult(paths);
    }

    public Task<string?> SaveFileAsync(SaveFileOptions options) => Task.FromResult(PathOrNull(Dequeue()));

    public Task<string?> OpenFolderAsync(OpenFolderOptions options) => Task.FromResult(PathOrNull(Dequeue()));

    private static string? PathOrNull(FilePickAnswer answer) => answer.IsCancel ? null : answer.Path;

    private static FilePickAnswer Dequeue()
    {
        if (!FilePickQueue.TryDequeue(out FilePickAnswer? answer))
        {
            throw new InvalidOperationException(EmptyQueueMessage);
        }

        return answer;
    }
}
