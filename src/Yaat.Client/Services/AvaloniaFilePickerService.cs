using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;

namespace Yaat.Client.Services;

// Production IFilePickerService that delegates to the Avalonia StorageProvider
// of the given TopLevel (usually a Window). The TopLevel determines which
// window the picker dialog is modal to, so each Window should construct its
// own instance rather than share one from MainWindow.
public sealed class AvaloniaFilePickerService(TopLevel topLevel) : IFilePickerService
{
    private static readonly ILogger Log = AppLog.CreateLogger<AvaloniaFilePickerService>();

    private readonly TopLevel _topLevel = topLevel;

    public async Task<string?> OpenFileAsync(OpenFileOptions options)
    {
        IReadOnlyList<IStorageFile> files = await _topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = options.Title,
                AllowMultiple = false,
                FileTypeFilter = ToAvalonia(options.Filters),
                SuggestedStartLocation = await StartLocationAsync(options.StartFolder),
            }
        );

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<IReadOnlyList<string>> OpenFilesAsync(OpenFileOptions options)
    {
        IReadOnlyList<IStorageFile> files = await _topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = options.Title,
                AllowMultiple = true,
                FileTypeFilter = ToAvalonia(options.Filters),
                SuggestedStartLocation = await StartLocationAsync(options.StartFolder),
            }
        );

        var paths = new List<string>(files.Count);
        foreach (IStorageFile file in files)
        {
            string? path = file.TryGetLocalPath();
            if (path is not null)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    public async Task<string?> SaveFileAsync(SaveFileOptions options)
    {
        IStorageFile? file = await _topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = options.Title,
                SuggestedFileName = options.SuggestedFileName,
                FileTypeChoices = ToAvalonia(options.Filters),
                DefaultExtension = options.DefaultExtension,
                SuggestedStartLocation = await StartLocationAsync(options.StartFolder),
            }
        );

        return file?.TryGetLocalPath();
    }

    public async Task<string?> OpenFolderAsync(OpenFolderOptions options)
    {
        IReadOnlyList<IStorageFolder> folders = await _topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = options.Title, AllowMultiple = false }
        );

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    // The folder a picker opens in: null when none is asked for, or the folder asked for no longer exists.
    private async Task<IStorageFolder?> StartLocationAsync(string? folder)
    {
        if (folder is null)
        {
            return null;
        }

        if (!Directory.Exists(folder))
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("File picker: start folder {Folder} no longer exists; opening without one", folder);
            }

            return null;
        }

        IStorageFolder? start = await _topLevel.StorageProvider.TryGetFolderFromPathAsync(folder);
        if ((start is null) && (Log.IsEnabled(LogLevel.Debug)))
        {
            Log.LogDebug("File picker: the platform gave no folder for {Folder}; opening without a start folder", folder);
        }

        return start;
    }

    private static List<FilePickerFileType> ToAvalonia(IReadOnlyList<FilePickerFilter> filters)
    {
        var result = new List<FilePickerFileType>(filters.Count);
        foreach (FilePickerFilter filter in filters)
        {
            result.Add(new FilePickerFileType(filter.Name) { Patterns = [.. filter.Patterns] });
        }
        return result;
    }
}
