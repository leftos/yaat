using Avalonia.Controls;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// USER_GUIDE.md > Commands. Captures the dedicated command reference window.
internal sealed class CommandCheatsheetScene : StandaloneWindowSceneBase
{
    public override string Name => "command-cheatsheet";

    public override Window CreateWindow(CaptureContext ctx) => new CommandCheatsheetWindow();
}
