using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.VStrips.Web;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        var connection = new ServerConnection();
        var vm = new VStripsViewModel(
            connection,
            sendCommand: (_, _, _) => System.Threading.Tasks.Task.CompletedTask,
            preferences: null,
            autoBootstrapFromScenarioLoaded: false
        );

        var stripsView = this.FindControl<UserControl>("StripsView");
        if (stripsView is not null)
        {
            stripsView.DataContext = vm;
        }

        SeedSpikeFixture(vm);
    }

    private static void SeedSpikeFixture(VStripsViewModel vm)
    {
        // ApplyBayConfig posts its work to the dispatcher; subsequent
        // ReconcileItems/ReconcileFullState calls would otherwise race ahead
        // and run before the bays exist (silently dropping the strip
        // ordering). Queue the seed *after* the config so they execute in
        // FIFO order on the same dispatcher.
        vm.ApplyBayConfig(
            new FlightStripsConfigDto(
                FacilityId: "SPIKE",
                FacilityName: "WASM SPIKE",
                Bays: [new StripBayConfigDto("bay-gnd", "GROUND", 2), new StripBayConfigDto("bay-loc", "LOCAL", 2)],
                HasTwoPrinters: false,
                SeparatorsLocked: false
            )
        );

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            vm.SetConnected(true);
            vm.ReconcileItems([
                new StripItemDto(
                    Id: "spike-1",
                    AircraftId: "UAL238",
                    IsDisconnected: false,
                    Type: StripItemType.DepartureStrip,
                    IsOffset: false,
                    FieldValues: ["UAL238", "", "B738/L", "1234", "5512", "", "240", "KSFO", "DCT BERKS DCT FAIRR", ""]
                ),
                new StripItemDto(
                    Id: "spike-2",
                    AircraftId: "AAL2839",
                    IsDisconnected: false,
                    Type: StripItemType.ArrivalStrip,
                    IsOffset: false,
                    FieldValues: ["AAL2839", "", "A320/L", "5678", "6612", "", "180", "KOAK", "ALWYS3.SFO", ""]
                ),
            ]);
            vm.ReconcileFullState(
                new FlightStripsStateDto(
                    PrinterItems: [],
                    BayItems:
                    [
                        new StripBayContentsDto(
                            "bay-gnd",
                            [
                                ["spike-1"],
                                ["spike-2"],
                            ]
                        ),
                    ],
                    NewItemInPrinter: false,
                    NewItemInArrivalPrinter: false,
                    NewItemInBayId: null,
                    ItemMovedOrCreatedBySessionId: null
                )
            );
        });
    }
}
