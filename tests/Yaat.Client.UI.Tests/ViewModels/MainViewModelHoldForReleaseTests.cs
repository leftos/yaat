using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The hold-for-release rundown groups each held departure under the armed airport it matches, tolerating the FAA/ICAO
/// "K" prefix the same way the server does: "HFR OAK" arms "OAK" while the held departure carries its flight plan's "KOAK".
/// </summary>
public class MainViewModelHoldForReleaseTests
{
    private static MainViewModel NewVm() => new(new FakeFilePickerService());

    private static HeldDepartureDto Held(string callsign, string airport) => new(callsign, airport, "B738", "KLAX", true, "Held");

    [AvaloniaFact]
    public void ArmedByFaaId_GroupsHeldDeparturesFiledWithTheIcaoId()
    {
        MainViewModel vm = NewVm();

        vm.ApplyRundown(new RundownDto(["OAK"], [Held("SWA1", "KOAK"), Held("SWA2", "KOAK")]));

        RundownAirportGroup group = Assert.Single(vm.Rundown);
        Assert.Equal("OAK", group.Airport);
        Assert.Equal(["SWA1", "SWA2"], group.Departures.Select(d => d.Callsign));
    }

    [AvaloniaFact]
    public void ArmedByIcaoId_GroupsHeldDeparturesFiledWithTheFaaId()
    {
        MainViewModel vm = NewVm();

        vm.ApplyRundown(new RundownDto(["KSJC"], [Held("UAL1", "SJC")]));

        RundownAirportGroup group = Assert.Single(vm.Rundown);
        Assert.Equal("KSJC", group.Airport);
        Assert.Equal(["UAL1"], group.Departures.Select(d => d.Callsign));
    }

    [AvaloniaFact]
    public void ArmedAirportWithNothingHeld_ShowsAnEmptyGroup()
    {
        MainViewModel vm = NewVm();

        vm.ApplyRundown(new RundownDto(["OAK", "SJC"], [Held("SWA1", "KOAK")]));

        Assert.Equal(["OAK", "SJC"], vm.Rundown.Select(g => g.Airport));
        Assert.Single(vm.Rundown[0].Departures);
        Assert.Empty(vm.Rundown[1].Departures);
        Assert.False(vm.Rundown[1].HasDepartures);
    }

    [AvaloniaFact]
    public void HeldDepartureAtAnUnarmedAirport_IsNotShown()
    {
        MainViewModel vm = NewVm();

        vm.ApplyRundown(new RundownDto(["OAK"], [Held("SWA1", "KOAK"), Held("ASA9", "KSFO")]));

        RundownAirportGroup group = Assert.Single(vm.Rundown);
        Assert.Equal(["SWA1"], group.Departures.Select(d => d.Callsign));
    }
}
