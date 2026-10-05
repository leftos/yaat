using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;

namespace Yaat.GuideCapture.Scenes;

// GETTING_STARTED.md > Step 3. The Connect dialog as a first-launch user sees
// it from File > Connect (the default server list), after clicking Add: the
// new entry is selected and its URL field reads http://localhost:5000. The
// actions are no-ops: the dialog is never submitted.
internal sealed class ConnectDialogScene : StandaloneWindowSceneBase
{
    public override string Name => "connect-dialog";

    public override Window CreateWindow(CaptureContext ctx)
    {
        var preferences = new UserPreferences();
        var vm = new ConnectViewModel(
            UserPreferences.DefaultServers,
            preferences.LastUsedServerUrl,
            preferences.UserInitials,
            connectAction: (_, _) => Task.FromResult<string?>(null),
            saveAction: (_, _) => { },
            identitySaveAction: _ => { },
            closeAction: () => { }
        );
        vm.AddServerCommand.Execute(null);
        return new ConnectWindow(vm, preferences);
    }
}
