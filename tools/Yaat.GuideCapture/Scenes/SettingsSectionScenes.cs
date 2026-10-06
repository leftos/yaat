using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Customization > Settings, one section each. The window at
// its natural size with the section selected in the sidebar, as a user who
// clicks it sees it: a section longer than the pane shows its top.
internal abstract class SettingsSectionSceneBase : StandaloneWindowSceneBase
{
    protected abstract SettingsSectionId Section { get; }

    public override Window CreateWindow(CaptureContext ctx) => new SettingsWindow();

    public override Task AfterShowAsync(Window window, CaptureContext ctx)
    {
        var settings = (SettingsWindow)window;
        settings.SelectSection(Section);
        Dispatcher.UIThread.RunJobs();
        settings.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return Task.CompletedTask;
    }
}

internal sealed class SettingsRadarScene : SettingsSectionSceneBase
{
    public override string Name => "settings-radar";

    protected override SettingsSectionId Section => SettingsSectionId.Radar;
}

internal sealed class SettingsGroundScene : SettingsSectionSceneBase
{
    public override string Name => "settings-ground";

    protected override SettingsSectionId Section => SettingsSectionId.Ground;
}

internal sealed class SettingsQuickCommandsScene : SettingsSectionSceneBase
{
    public override string Name => "settings-quick-commands";

    protected override SettingsSectionId Section => SettingsSectionId.QuickCommands;
}

internal sealed class SettingsKeysScene : SettingsSectionSceneBase
{
    public override string Name => "settings-keys";

    protected override SettingsSectionId Section => SettingsSectionId.Keys;
}
