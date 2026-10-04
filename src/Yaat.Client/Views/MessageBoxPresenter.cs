using Avalonia.Controls;
using MsBox.Avalonia.Base;
using MsBox.Avalonia.Controls;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.ViewModels;
using MsBox.Avalonia.Windows;

namespace Yaat.Client.Views;

/// <summary>
/// Opens every MessageBox.Avalonia box. The package's own <c>ShowWindowDialogAsync</c> builds its
/// window and calls <see cref="Window.ShowDialog(Window)"/> on it, which activates the box and
/// re-activates the owner on close. This builds the same view, view model and window the package
/// does and opens the window through <see cref="DialogPresenter"/>, so in automation mode the box
/// shows never-activated and non-modal with its owner disabled, and outside it the box is modal as before.
/// </summary>
public static class MessageBoxPresenter
{
    /// <summary>
    /// Shows a standard box (title, message, a <see cref="ButtonEnum"/> button set) centred on the
    /// screen, as <c>MessageBoxManager.GetMessageBoxStandard(title, text, buttons)</c> does, and
    /// completes with the button the user chose.
    /// </summary>
    public static Task<ButtonResult> ShowStandardAsync(Window owner, string title, string text, ButtonEnum buttons)
    {
        MessageBoxStandardParams parameters = new()
        {
            ContentTitle = title,
            ContentMessage = text,
            ButtonDefinitions = buttons,
            Icon = Icon.None,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        MsBoxStandardViewModel viewModel = new(parameters);
        MsBoxStandardView view = new() { DataContext = viewModel };
        return ShowAsync<MsBoxStandardView, MsBoxStandardViewModel, ButtonResult>(owner, view, viewModel);
    }

    /// <summary>Shows a box with custom buttons and completes with the name of the button the user chose.</summary>
    public static Task<string> ShowCustomAsync(Window owner, MessageBoxCustomParams parameters)
    {
        MsBoxCustomViewModel viewModel = new(parameters);
        MsBoxCustomView view = new() { DataContext = viewModel };
        return ShowAsync<MsBoxCustomView, MsBoxCustomViewModel, string>(owner, view, viewModel);
    }

    // The package's ShowWindowDialogAsync with DialogPresenter in place of ShowDialog. The view's
    // close action (a button, or the view's own CloseWindow when the window is closed from its title
    // bar) records the result before the window closes, so the result is known once the dialog ends.
    private static async Task<T> ShowAsync<TView, TViewModel, T>(Window owner, TView view, TViewModel viewModel)
        where TView : UserControl, IFullApi<T>, ISetCloseAction
        where TViewModel : ISetFullApi<T>
    {
        viewModel.SetFullApi(view);
        MsBoxWindow window = new() { Content = view, DataContext = viewModel };
        window.Closed += (_, e) => view.CloseWindow(window, e);
        TaskCompletionSource<T> chosen = new();
        view.SetCloseAction(() =>
        {
            chosen.TrySetResult(view.GetButtonResult());
            window.Close();
        });
        await DialogPresenter.ShowModalAsync(window, owner);
        return await chosen.Task;
    }
}
