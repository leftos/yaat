using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests.Find;

// Locks the "all visible text" search contract for both view-models that back the
// shared Find (Ctrl+F): a query must be able to hit any field the row shows, not
// just the callsign.
public class GetFindTextTests
{
    [Fact]
    public void StripItem_IncludesEveryField_AndFlattensRouteRemarksNewline()
    {
        var dto = new StripItemDto(
            "id1",
            "UAL123",
            IsDisconnected: false,
            StripItemType.DepartureStrip,
            IsOffset: false,
            FieldValues: ["UAL123", "", "B738/L", "0123", "4567", "1200", "350", "KOAK KSFO", "", "KOAK..SFO\nRMK FREETEXT", "H1"]
        );
        var vm = new StripItemViewModel(dto);

        var text = vm.GetFindText();

        Assert.Contains("UAL123", text);
        Assert.Contains("KSFO", text);
        Assert.Contains("RMK", text); // remarks tail after the packed newline
        Assert.Contains("H1", text); // annotation box
        Assert.DoesNotContain("\n", text); // newline flattened so route/remarks tokens split
    }

    [Fact]
    public void TdlsItem_IncludesCallsign_FlightPlan_Beacon_AndClearance()
    {
        var flightPlan = new TdlsFlightPlanInfoDto(
            AssignedBeaconCode: 1234,
            Departure: "KSFO",
            Destination: "KLAX",
            Route: "SSTIK2",
            AircraftType: "B738",
            EquipmentSuffix: "L",
            Remarks: "",
            Cid: "123",
            CruiseAltitude: 35000
        );
        var clearance = new ClearanceDto(
            Expect: null,
            Sid: "SSTIK2",
            Transition: null,
            Climbout: null,
            Climbvia: null,
            InitialAlt: null,
            ContactInfo: null,
            LocalInfo: null,
            DepFreq: null
        );
        var dto = new TdlsItemDto(
            "id1",
            "UAL123",
            Cid: "123",
            FacilityId: "SFO",
            TdlsStatus.Sent,
            Sequence: 1,
            CreatedUtc: default,
            SentUtc: null,
            WilcoUtc: null,
            ExpiresUtc: default,
            SentPayload: clearance,
            FlightPlan: flightPlan
        );
        var vm = new TdlsItemViewModel(dto);

        var text = vm.GetFindText();

        Assert.Contains("UAL123", text);
        Assert.Contains("KLAX", text);
        Assert.Contains("SSTIK2", text);
        Assert.Contains("1234", text); // beacon formatted as D4
    }
}
