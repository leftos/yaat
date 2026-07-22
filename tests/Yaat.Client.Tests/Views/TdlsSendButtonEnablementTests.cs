using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests.Views;

/// <summary>
/// Pins the vTDLS Send button's enablement to the same binding pair the real view uses
/// (<c>Command</c> + <c>IsEnabled</c>, see <c>VTdlsView.axaml</c>). Avalonia's
/// <see cref="Button"/> ANDs a cached <c>_commandCanExecute</c> into <c>IsEnabledCore</c>
/// and only refreshes it from <see cref="System.Windows.Input.ICommand.CanExecuteChanged"/>,
/// so a view-model that updates <c>IsSendEnabled</c> without raising that event leaves the
/// button greyed out forever — GitHub issue #306. Binding a bare Button (rather than the
/// whole VTdlsView, which pulls app-level font resources the headless app doesn't register)
/// exercises that mechanism directly.
/// </summary>
public class TdlsSendButtonEnablementTests
{
    /// <summary>Facility with a mandatory departure frequency the transition defines no default for, so the editor opens incomplete.</summary>
    private static TdlsConfigDto BuildConfigMissingMandatoryDepFreq() =>
        new(
            FacilityId: "IAD",
            FacilityName: "Washington Dulles ATCT",
            MandatorySid: true,
            MandatoryClimbout: false,
            MandatoryClimbvia: false,
            MandatoryInitialAlt: false,
            MandatoryDepFreq: true,
            MandatoryExpect: true,
            MandatoryContactInfo: false,
            MandatoryLocalInfo: false,
            Sids:
            [
                new TdlsSidDto(
                    "RNLDI4",
                    "RNLDI4",
                    [
                        new TdlsSidTransitionDto(
                            "OTTTO",
                            "OTTTO",
                            FirstRoutePoint: "OTTTO",
                            DefaultExpect: "10 MIN AFT DP",
                            DefaultClimbout: null,
                            DefaultClimbvia: null,
                            DefaultInitialAlt: null,
                            DefaultDepFreq: null,
                            DefaultContactInfo: null,
                            DefaultLocalInfo: null
                        ),
                    ]
                ),
            ],
            Climbouts: [],
            Climbvias: [],
            InitialAlts: [],
            DepFreqs: [new TdlsClearanceValueDto("125050", "125.050")],
            Expects: [new TdlsClearanceValueDto("10MIN", "10 MIN AFT DP")],
            ContactInfos: [],
            LocalInfos: [],
            DefaultSidId: "RNLDI4",
            DefaultTransitionId: "OTTTO"
        );

    [AvaloniaFact]
    public void SendButton_ReEnables_AfterLastMandatoryFieldIsFilled()
    {
        var editor = new TdlsFlightPlanEditorViewModel(
            "UAL1742",
            BuildConfigMissingMandatoryDepFreq(),
            seed: null,
            flightPlan: null,
            isReadOnly: false
        );
        Assert.False(editor.IsSendEnabled);

        var button = new Button { DataContext = editor };
        button.Bind(Button.CommandProperty, new Binding(nameof(TdlsFlightPlanEditorViewModel.SendCommand)));
        button.Bind(Button.IsEnabledProperty, new Binding(nameof(TdlsFlightPlanEditorViewModel.IsSendEnabled)));

        // Button.CanExecuteChanged short-circuits while detached from the logical tree,
        // so the button has to be shown before the assertions mean anything.
        var window = new Window { Content = button };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(button.IsEffectivelyEnabled);

        editor.SelectedDepFreq = editor.DepFreqs[0];
        Dispatcher.UIThread.RunJobs();

        Assert.True(editor.IsSendEnabled);
        Assert.True(button.IsEffectivelyEnabled, "Send stayed greyed out after the last mandatory field was filled.");
    }
}
