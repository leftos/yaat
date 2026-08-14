using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

/// <summary>
/// Modal prompt shown when importing a favorites file while the user already has favorites. The user
/// chooses whether the imported set should be appended to their existing favorites or replace them
/// entirely. Result is returned via <c>ShowDialog&lt;FavoriteImportMode?&gt;</c> (null on cancel).
/// </summary>
public partial class FavoriteImportWindow : Window
{
    // Parameterless ctor required for the Avalonia designer / XamlLoader. Not used at runtime.
    public FavoriteImportWindow()
    {
        InitializeComponent();
    }

    public FavoriteImportWindow(int importCount)
        : this()
    {
        var suffix = importCount == 1 ? "" : "s";
        MessageText.Text = $"{importCount} favorite{suffix} in file. Append to your existing favorites, or replace all of them?";
    }

    /// <summary>
    /// Variant for all-sets bundle files: Append becomes "Merge" (same-named sets gain the imported
    /// favorites, new names become new sets) and Replace discards every existing named set. The base
    /// favorites pool is not touched either way.
    /// </summary>
    public static FavoriteImportWindow CreateForBundle(int setCount, int favoriteCount)
    {
        var window = new FavoriteImportWindow { Title = "Import Favorite Sets" };
        var setSuffix = setCount == 1 ? "" : "s";
        var favSuffix = favoriteCount == 1 ? "" : "s";
        window.MessageText.Text =
            $"{setCount} favorite set{setSuffix} ({favoriteCount} favorite{favSuffix}) in file. Merge into your existing sets, or replace all of your sets?";
        window.AppendButton.Content = "Merge";
        window.ReplaceButton.Content = "Replace All Sets";
        window.ReplaceButton.Width = 130;
        return window;
    }

    private void OnAppendClick(object? sender, RoutedEventArgs e) => Close(FavoriteImportMode.Append);

    private void OnReplaceClick(object? sender, RoutedEventArgs e) => Close(FavoriteImportMode.Replace);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
