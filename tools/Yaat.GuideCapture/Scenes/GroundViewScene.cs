namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Views > Ground View. OAK ramp diagram with the 18
// scenario aircraft tagged at their parking spots. Equivalent to the
// Phase C smoke test that proved MapDrawOperation renders under real-Skia
// headless.
internal sealed class GroundViewScene : ScenarioSceneBase
{
    public override string Name => "ground-view";

    protected override int TabIndex => 1;
}
