using Yaat.Client.Services;

namespace Yaat.Client.UI.Tests.Fakes;

// Test double: each call pops the next queued response, so tests can script a
// sequence of pick/save/cancel outcomes. Every call is logged so assertions can
// verify the options passed by production code.
internal sealed class FakeFilePickerService : IFilePickerService
{
    private readonly Queue<object?> _responses = new();

    public List<RecordedCall> Calls { get; } = [];

    public void QueueOpenFile(string? path) => _responses.Enqueue(path);

    public void QueueOpenFiles(IReadOnlyList<string> paths) => _responses.Enqueue(paths);

    public void QueueSaveFile(string? path) => _responses.Enqueue(path);

    public void QueueOpenFolder(string? path) => _responses.Enqueue(path);

    public Task<string?> OpenFileAsync(OpenFileOptions options)
    {
        Calls.Add(new RecordedCall(PickerKind.OpenFile, options.Title, options));
        return Task.FromResult(DequeueOrDefault<string?>());
    }

    public Task<IReadOnlyList<string>> OpenFilesAsync(OpenFileOptions options)
    {
        Calls.Add(new RecordedCall(PickerKind.OpenFiles, options.Title, options));
        return Task.FromResult(DequeueOrDefault<IReadOnlyList<string>?>() ?? []);
    }

    public Task<string?> SaveFileAsync(SaveFileOptions options)
    {
        Calls.Add(new RecordedCall(PickerKind.SaveFile, options.Title, options));
        return Task.FromResult(DequeueOrDefault<string?>());
    }

    public Task<string?> OpenFolderAsync(OpenFolderOptions options)
    {
        Calls.Add(new RecordedCall(PickerKind.OpenFolder, options.Title, options));
        return Task.FromResult(DequeueOrDefault<string?>());
    }

    private T? DequeueOrDefault<T>()
    {
        if (_responses.Count == 0)
        {
            return default;
        }

        var next = _responses.Dequeue();
        return next is T typed ? typed : default;
    }
}

internal enum PickerKind
{
    OpenFile,
    OpenFiles,
    SaveFile,
    OpenFolder,
}

internal sealed record RecordedCall(PickerKind Kind, string Title, object Options);
