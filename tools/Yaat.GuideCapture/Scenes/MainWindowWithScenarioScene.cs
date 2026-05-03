namespace Yaat.GuideCapture.Scenes;

// "Main Window" overview shot for USER_GUIDE.md's Interface Overview section.
// Defaults to the Aircraft List tab (0) so the viewer sees the most chrome
// at once: menu bar, tabs, data grid populated with 18 OAK aircraft, terminal
// with the connect/create/load entries.
internal sealed class MainWindowWithScenarioScene : ScenarioSceneBase
{
    public override string Name => "main-window-overview";

    protected override int TabIndex => 0;
}
