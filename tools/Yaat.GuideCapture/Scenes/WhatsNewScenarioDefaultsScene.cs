using Yaat.Client.ViewModels;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > Scenario defaults. Settings at the Scenario defaults
// section: solo training mode, the go-around probability and the solo pacing
// defaults (parking call-up interval, arrival generator rate) new rooms start from.
internal sealed class WhatsNewScenarioDefaultsScene : SettingsSectionSceneBase
{
    public override string Name => "whats-new-scenario-defaults";

    protected override SettingsSectionId Section => SettingsSectionId.ScenarioDefaults;
}
