using Avalonia.Controls;
using Yaat.Client.Automation;

namespace Yaat.Client.Services;

/// <summary>
/// Builds the <see cref="IFilePickerService"/> a window uses: the queue-filled injected picker in automation mode, where
/// no dialog may open, and the Avalonia storage provider otherwise. This is the only place in the client that constructs
/// <see cref="AvaloniaFilePickerService"/>.
/// </summary>
public static class FilePickerFactory
{
    public static IFilePickerService Create(TopLevel topLevel) =>
        AutomationMode.IsEnabled ? new InjectedFilePickerService() : new AvaloniaFilePickerService(topLevel);
}
