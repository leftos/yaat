using Avalonia.Controls;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var versionText = this.FindControl<TextBlock>("VersionText");
        if (versionText is not null)
        {
            versionText.Text = BuildInfo.Version;
        }

        var buildKindText = this.FindControl<TextBlock>("BuildKindText");
        if (buildKindText is not null)
        {
            buildKindText.Text = BuildInfo.IsInstalledRelease ? "release (installed via Velopack)" : "dev build (not installed via Velopack)";
        }

        var runtimeText = this.FindControl<TextBlock>("RuntimeText");
        if (runtimeText is not null)
        {
            runtimeText.Text = $".NET {Environment.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
        }

        var logPathText = this.FindControl<TextBlock>("LogPathText");
        if (logPathText is not null)
        {
            logPathText.Text = string.IsNullOrEmpty(AppLog.LogPath) ? "(not initialized)" : AppLog.LogPath;
        }

        var openRepoBtn = this.FindControl<Button>("OpenRepoButton");
        if (openRepoBtn is not null)
        {
            openRepoBtn.Click += (_, _) => UrlLauncher.OpenInBrowser(DocLinks.Repo);
        }

        var closeBtn = this.FindControl<Button>("CloseButton");
        if (closeBtn is not null)
        {
            closeBtn.Click += (_, _) => Close();
        }
    }
}
