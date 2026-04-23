namespace Yaat.Client.Services;

// File-picker abstraction so ViewModels and Windows don't reach into Avalonia's
// TopLevel.StorageProvider directly. Returns absolute filesystem paths (strings)
// rather than IStorageFile — every desktop caller already resolves to a local
// path via TryGetLocalPath() anyway, and strings strip the Avalonia dependency
// from call sites and tests.
public interface IFilePickerService
{
    Task<string?> OpenFileAsync(OpenFileOptions options);

    Task<IReadOnlyList<string>> OpenFilesAsync(OpenFileOptions options);

    Task<string?> SaveFileAsync(SaveFileOptions options);

    Task<string?> OpenFolderAsync(OpenFolderOptions options);
}

public sealed record FilePickerFilter(string Name, IReadOnlyList<string> Patterns);

public sealed record OpenFileOptions(string Title, IReadOnlyList<FilePickerFilter> Filters);

public sealed record SaveFileOptions(string Title, string SuggestedFileName, IReadOnlyList<FilePickerFilter> Filters, string DefaultExtension);

public sealed record OpenFolderOptions(string Title);
