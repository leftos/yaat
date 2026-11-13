using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Tdls.Services;
using Yaat.Client.Tdls.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// The vTDLS view's Temp. Dep Freqs and Override Dep Freq, at the view-model: how a typed list is accepted, and what a
/// commit that drops the override takes off the open editor — only the departure frequency the override itself set.
/// </summary>
public class VTdlsTempDepFreqsTests
{
    private static TdlsConfigDto Config() =>
        new(
            FacilityId: "OAK",
            FacilityName: "Oakland ATCT",
            MandatorySid: false,
            MandatoryClimbout: false,
            MandatoryClimbvia: false,
            MandatoryInitialAlt: false,
            MandatoryDepFreq: false,
            MandatoryExpect: false,
            MandatoryContactInfo: false,
            MandatoryLocalInfo: false,
            Sids:
            [
                new TdlsSidDto(
                    "OAKLAND4",
                    "OAKLAND4",
                    [new TdlsSidTransitionDto("ALTAM", "ALTAMONT", "ALTAM", null, null, null, null, DefaultDepFreq: "120.9", null, null)]
                ),
            ],
            Climbouts: [],
            Climbvias: [],
            InitialAlts: [],
            DepFreqs: [new TdlsClearanceValueDto("120.9", "120.9")],
            Expects: [],
            ContactInfos: [],
            LocalInfos: [],
            DefaultSidId: "OAKLAND4",
            DefaultTransitionId: "ALTAM"
        );

    /// <summary>A view-model on OAK's page with one pending item, ready to be selected.</summary>
    private static VTdlsViewModel PageWithOneItem()
    {
        var transport = new FakeTdlsTransport();
        var vm = new VTdlsViewModel(
            transport,
            (_, _, _) => Task.FromResult<CommandResultDto?>(new CommandResultDto(true, null)),
            getUserInitials: null
        );
        TdlsConfigDto config = Config();
        vm.ApplyFacilityView(config.FacilityId, new TdlsFacilityViewDto(config.FacilityId, config.FacilityName, [config], StudentFrequencyMhz: null));
        var item = new TdlsItemDto(
            "id1",
            "UAL111",
            Cid: null,
            FacilityId: "OAK",
            TdlsStatus.Pending,
            Sequence: 0,
            CreatedUtc: default,
            SentUtc: null,
            WilcoUtc: null,
            ExpiresUtc: default,
            SentPayload: null,
            FlightPlan: null
        );
        transport.Push(new TdlsStateDto([item], []));
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    [AvaloniaFact]
    public void Commit_DroppingTheOverride_ClearsItAndTheDepFreqItSet()
    {
        VTdlsViewModel vm = PageWithOneItem();
        vm.CommitTempDepFreqs("125.35");
        vm.OverrideDepFreq = "125.35";
        vm.SelectedItem = vm.DclItems.Single();
        TdlsFlightPlanEditorViewModel editor = vm.Editor!;
        Assert.Equal("125.35", editor.DepFreq);

        vm.CommitTempDepFreqs("119.1");

        Assert.Null(vm.OverrideDepFreq);
        Assert.Null(editor.DepFreq);
    }

    [AvaloniaFact]
    public void Commit_DroppingTheOverride_KeepsADepFreqTheControllerPicked()
    {
        VTdlsViewModel vm = PageWithOneItem();
        vm.CommitTempDepFreqs("125.35");
        vm.SelectedItem = vm.DclItems.Single();
        TdlsFlightPlanEditorViewModel editor = vm.Editor!;
        editor.DepFreq = "125.35";
        vm.OverrideDepFreq = "125.35";

        vm.CommitTempDepFreqs("119.1");

        Assert.Null(vm.OverrideDepFreq);
        Assert.Equal("125.35", editor.DepFreq);
    }

    [AvaloniaFact]
    public void Commit_DroppingTheOverride_StopsItReturningOnTheNextSidChange()
    {
        VTdlsViewModel vm = PageWithOneItem();
        vm.CommitTempDepFreqs("125.35");
        vm.OverrideDepFreq = "125.35";
        vm.SelectedItem = vm.DclItems.Single();
        TdlsFlightPlanEditorViewModel editor = vm.Editor!;

        vm.CommitTempDepFreqs("119.1");
        TdlsSidDto sid = editor.SelectedSid!;
        editor.SelectedSid = null;
        editor.SelectedSid = sid;

        Assert.Equal("120.9", editor.DepFreq);
    }

    [AvaloniaFact]
    public void Commit_DroppingTheOverride_KeepsADepFreqPickedOverIt()
    {
        VTdlsViewModel vm = PageWithOneItem();
        vm.CommitTempDepFreqs("125.35");
        vm.OverrideDepFreq = "125.35";
        vm.SelectedItem = vm.DclItems.Single();
        TdlsFlightPlanEditorViewModel editor = vm.Editor!;
        editor.DepFreq = "120.9";

        vm.CommitTempDepFreqs("119.1");

        Assert.Equal("120.9", editor.DepFreq);
    }

    [AvaloniaFact]
    public void Commit_UpperCasesAndCollapsesDuplicatesToTheFirst()
    {
        VTdlsViewModel vm = PageWithOneItem();

        vm.CommitTempDepFreqs("125.35 120.9x,125.35, 119.1");

        Assert.Equal(["125.35", "120.9X", "119.1"], vm.TempDepFreqs);
        Assert.False(vm.IsTempDepFreqsInvalid);
    }

    [AvaloniaFact]
    public void Commit_WithOneFrequencyTooLong_EmptiesTheList()
    {
        VTdlsViewModel vm = PageWithOneItem();
        vm.CommitTempDepFreqs("125.35");

        vm.CommitTempDepFreqs("125.35, 1234.567");

        Assert.Empty(vm.TempDepFreqs);
        Assert.True(vm.IsTempDepFreqsInvalid);
    }
}
