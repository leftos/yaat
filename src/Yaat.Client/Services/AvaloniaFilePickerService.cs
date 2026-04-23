using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Yaat.Client.Services;

// Production IFilePickerService that delegates to the Avalonia StorageProvider
// of the given TopLevel (usually a Window). The TopLevel determines which
// window the picker dialog is modal to, so each Window should construct its
// own instance rather than share one from MainWindow.
public sealed class AvaloniaFilePickerService : IFilePickerService
{
    private readonly TopLevel _topLevel;

    public AvaloniaFilePickerService(TopLevel topLevel)
    {
        _topLevel = topLevel;
    }

    public async Task<string?> OpenFileAsync(OpenFileOptions options)
    {
        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = options.Title,
                AllowMultiple = false,
                FileTypeFilter = ToAvalonia(options.Filters),
            }
        );

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<IReadOnlyList<string>> OpenFilesAsync(OpenFileOptions options)
    {
        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = options.Title,
                AllowMultiple = true,
                FileTypeFilter = ToAvalonia(options.Filters),
            }
        );

        var paths = new List<string>(files.Count);
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is not null)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    public async Task<string?> SaveFileAsync(SaveFileOptions options)
    {
        var file = await _topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = options.Title,
                SuggestedFileName = options.SuggestedFileName,
                FileTypeChoices = ToAvalonia(options.Filters),
                DefaultExtension = options.DefaultExtension,
            }
        );

        return file?.TryGetLocalPath();
    }

    public async Task<string?> OpenFolderAsync(OpenFolderOptions options)
    {
        var folders = await _topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = options.Title, AllowMultiple = false });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private static List<FilePickerFileType> ToAvalonia(IReadOnlyList<FilePickerFilter> filters)
    {
        var result = new List<FilePickerFileType>(filters.Count);
        foreach (var filter in filters)
        {
            result.Add(new FilePickerFileType(filter.Name) { Patterns = filter.Patterns.ToArray() });
        }
        return result;
    }
}
