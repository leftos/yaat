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
        versionText?.Text = BuildInfo.Version;

        TextBlock? buildKindText = this.FindControl<TextBlock>("BuildKindText");
        buildKindText?.Text = BuildInfo.IsInstalledRelease ? "release (installed via Velopack)" : "dev build (not installed via Velopack)";

        TextBlock? runtimeText = this.FindControl<TextBlock>("RuntimeText");
        runtimeText?.Text = $".NET {Environment.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";

        TextBlock? logPathText = this.FindControl<TextBlock>("LogPathText");
        logPathText?.Text = string.IsNullOrEmpty(AppLog.LogPath) ? "(not initialized)" : AppLog.LogPath;

        Button? openRepoBtn = this.FindControl<Button>("OpenRepoButton");
        openRepoBtn?.Click += (_, _) => UrlLauncher.OpenInBrowser(DocLinks.Repo);

        Button? supportBtn = this.FindControl<Button>("SupportButton");
        supportBtn?.Click += (_, _) => UrlLauncher.OpenInBrowser(DocLinks.Donate);

        Button? closeBtn = this.FindControl<Button>("CloseButton");
        closeBtn?.Click += (_, _) => Close();
    }
}
