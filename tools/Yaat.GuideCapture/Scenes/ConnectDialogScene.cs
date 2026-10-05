using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// GETTING_STARTED.md > Step 3. The Connect dialog as a first-launch user sees
// it from File > Connect: the server list holds the default servers, with the
// public server YAAT1 selected and its URL (https://yaat1.leftos.dev) in the
// URL field. The actions are no-ops: the dialog is never submitted.
internal sealed class ConnectDialogScene : StandaloneWindowSceneBase
{
    public override string Name => "connect-dialog";

    public override Window CreateWindow(CaptureContext ctx)
    {
        var preferences = new UserPreferences();
        var vm = new ConnectViewModel(
            UserPreferences.DefaultServers,
            UserPreferences.OfficialServerUrl,
            preferences.UserInitials,
            connectAction: (_, _) => Task.FromResult<string?>(null),
            saveAction: (_, _) => { },
            identitySaveAction: _ => { },
            closeAction: () => { }
        );
        return new ConnectWindow(vm, preferences);
    }
}
