namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Views > Radar View. Same scenario; tab 2 shows the radar
// canvas. With aircraft at parking they'll be ground tracks rather than
// airborne tags, but the canvas still exercises range-rings, sector
// geometry, and aircraft tag rendering through MapDrawOperation.
internal sealed class RadarViewScene : ScenarioSceneBase
{
    public override string Name => "radar-view";

    protected override int TabIndex => 2;
}
