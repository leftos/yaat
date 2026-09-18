using Avalonia.Controls;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        TextBlock? versionText = this.FindControl<TextBlock>("VersionText");
        if (versionText is not null)
        {
            versionText.Text = BuildInfo.Version;
        }

        TextBlock? buildKindText = this.FindControl<TextBlock>("BuildKindText");
        if (buildKindText is not null)
        {
            buildKindText.Text = BuildInfo.IsInstalledRelease ? "release (installed via Velopack)" : "dev build (not installed via Velopack)";
        }

        TextBlock? runtimeText = this.FindControl<TextBlock>("RuntimeText");
        if (runtimeText is not null)
        {
            runtimeText.Text = $".NET {Environment.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
        }

        TextBlock? logPathText = this.FindControl<TextBlock>("LogPathText");
        if (logPathText is not null)
        {
            logPathText.Text = string.IsNullOrEmpty(AppLog.LogPath) ? "(not initialized)" : AppLog.LogPath;
        }

        Button? openRepoBtn = this.FindControl<Button>("OpenRepoButton");
        if (openRepoBtn is not null)
        {
            openRepoBtn.Click += (_, _) => UrlLauncher.OpenInBrowser(DocLinks.Repo);
        }

        Button? supportBtn = this.FindControl<Button>("SupportButton");
        if (supportBtn is not null)
        {
            supportBtn.Click += (_, _) => UrlLauncher.OpenInBrowser(DocLinks.Donate);
        }

        Button? closeBtn = this.FindControl<Button>("CloseButton");
        if (closeBtn is not null)
        {
            closeBtn.Click += (_, _) => Close();
        }
    }
}
