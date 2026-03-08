using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class ConnectWindow : Window
{
    public ConnectWindow() { InitializeComponent(); }

    public ConnectWindow(ConnectViewModel vm, UserPreferences preferences)
    {
        InitializeComponent();
        DataContext = vm;
        new WindowGeometryHelper(this, preferences, "Connect", 560, 400).Restore();

        this.FindControl<Button>("CancelButton")!.Click += (_, _) =>
            vm.CancelConnectCommand.Execute(null);
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
    }
}
