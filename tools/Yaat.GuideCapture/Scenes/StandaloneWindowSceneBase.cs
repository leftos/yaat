using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// Base for scenes that capture a standalone window directly (Settings, Load
// Scenario, About, etc.) without booting MainWindow or talking to the server.
// Subclasses just declare a Name and a CreateWindow factory; size 0/0 means
// "use the window's XAML-declared default" so the dialog renders at its
// natural dimensions.
internal abstract class StandaloneWindowSceneBase : Scene
{
    public override int Width => 0;

    public override int Height => 0;
}
