using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Yaat.VStrips.Web;

public partial class App : Application
{
    /// <summary>
    /// <c>window.location.search</c> captured at WASM bootstrap, threaded
    /// in from <see cref="Program.Main"/>. Drives identity, room, and
    /// server-URL bootstrap in <see cref="MainView"/>. Empty when the page
    /// was loaded with no query string — in that case the view falls back
    /// to the spike fixture (visible bays + two stub strips) so the
    /// page-not-blank invariant holds even before a server connection.
    /// </summary>
    public static string LocationSearch { get; set; } = "";

    /// <summary>
    /// <c>window.location.origin</c> at WASM bootstrap. Default for the
    /// SignalR hub URL when the query string doesn't override <c>?server=</c>.
    /// </summary>
    public static string LocationOrigin { get; set; } = "";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
