using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views;

namespace Yaat.Client.Tests;

/// <summary>
/// Bug repro: a user cleared the DEP field in the CRC Flight Plan Editor for N342T
/// and clicked Amend three times — each time the server kept KOAK. Root cause: the
/// editor sent Departure=null and the server's <c>SimulationEngine.AmendFlightPlan</c>
/// uses null as the "leave this field alone" sentinel (load-bearing for partial-update
/// call sites elsewhere in RoomEngine). The fix routes cleared user-editable fields
/// through <see cref="FlightPlanEditorAmendmentBuilder.Build"/>, which maps blanks to
/// empty strings and zero — distinct from null — so the server treats them as explicit
/// clears, matching CRC's <c>FlightPlanEditorViewModel.BuildFlightPlan</c>.
/// </summary>
public class FlightPlanEditorAmendmentBuilderTests
{
    [Fact]
    public void DepartureBlank_DestinationKept_SendsEmptyDeparture()
    {
        FlightPlanAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "A",
            icaoEqText: "",
            depText: "",
            destText: "KOAK",
            spdText: "250",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: "+/V/",
            originalRemarks: "OLD REMARKS"
        );

        Assert.Equal("", amendment.Departure);
        Assert.NotNull(amendment.Departure);
        Assert.Equal("KOAK", amendment.Destination);
    }

    [Fact]
    public void DepartureNullText_SendsEmptyDeparture()
    {
        // Avalonia TextBox.Text can be null when the box is empty — emulates the path
        // that produced "Departure":null on the wire in the original bug bundle.
        FlightPlanAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "A",
            icaoEqText: "",
            depText: null,
            destText: "KOAK",
            spdText: "250",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: "",
            originalRemarks: "OLD REMARKS"
        );

        Assert.Equal("", amendment.Departure);
        Assert.NotNull(amendment.Departure);
    }

    [Fact]
    public void AllTextFieldsBlank_AllStringsAreEmptyNotNull()
    {
        FlightPlanAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "",
            eqText: "",
            icaoEqText: "",
            depText: "",
            destText: "",
            spdText: "",
            altText: "",
            rteText: "",
            rmkText: "",
            strippedRemarksPrefix: "",
            originalRemarks: "OLD REMARKS"
        );

        Assert.Equal("", amendment.AircraftType);
        // The equipment suffix is the one field a blank box leaves unedited rather than clears.
        Assert.Null(amendment.EquipmentSuffix);
        Assert.Equal("", amendment.Departure);
        Assert.Equal("", amendment.Destination);
        Assert.Equal("", amendment.Route);
        Assert.Equal("", amendment.Remarks);
        Assert.NotNull(amendment.Departure);
        Assert.NotNull(amendment.Destination);
        Assert.NotNull(amendment.Route);
    }

    [Fact]
    public void SpeedBlank_SendsZero()
    {
        // int? null would mean "don't touch" server-side. 0 means "explicit clear".
        FlightPlanAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "A",
            icaoEqText: "",
            depText: "KOAK",
            destText: "KOAK",
            spdText: "",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: "",
            originalRemarks: "OLD REMARKS"
        );

        Assert.Equal(0, amendment.CruiseSpeed);
        Assert.NotNull(amendment.CruiseSpeed);
    }

    [Fact]
    public void SpeedUnparseable_SendsZero()
    {
        FlightPlanAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "A",
            icaoEqText: "",
            depText: "KOAK",
            destText: "KOAK",
            spdText: "abc",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: "",
            originalRemarks: "OLD REMARKS"
        );

        Assert.Equal(0, amendment.CruiseSpeed);
    }

    [Theory]
    [InlineData(78, false)]
    [InlineData(null, true)]
    public void UntouchedSpeedBox_OverAMachOrClassifiedPlan_SendsZero_WhichKeepsTheFiledSpeed(int? cruiseMach, bool isSpeedClassified)
    {
        // The server's SetTrueAirspeed(0) leaves a filed Mach or classified speed as it is; any other knots value replaces it.
        var model = new AircraftModel
        {
            CruiseAltitude = 35000,
            CruiseMach = cruiseMach,
            IsSpeedClassified = isSpeedClassified,
        };

        FlightPlanAmendment amendment = BuildWithSpeed(model.EditorSpeedText);

        Assert.Equal(0, amendment.CruiseSpeed);
    }

    [Fact]
    public void KnotsTypedOverAMachPlan_SendsTheKnots()
    {
        var model = new AircraftModel { CruiseAltitude = 35000, CruiseMach = 78 };
        Assert.Equal("", model.EditorSpeedText);

        FlightPlanAmendment amendment = BuildWithSpeed("450");

        Assert.Equal(450, amendment.CruiseSpeed);
    }

    private static FlightPlanAmendment BuildWithSpeed(string spdText) =>
        FlightPlanEditorAmendmentBuilder.Build(
            typText: "B738",
            eqText: "L",
            icaoEqText: "",
            depText: "KSFO",
            destText: "KJFK",
            spdText: spdText,
            altText: "350",
            rteText: "DCT",
            rmkText: "",
            strippedRemarksPrefix: "",
            originalRemarks: ""
        );

    [Fact]
    public void RouteBlank_SendsEmptyRoute()
    {
        FlightPlanAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "A",
            icaoEqText: "",
            depText: "KOAK",
            destText: "KOAK",
            spdText: "250",
            altText: "VFR/010",
            rteText: "",
            rmkText: "",
            strippedRemarksPrefix: "",
            originalRemarks: "OLD REMARKS"
        );

        Assert.Equal("", amendment.Route);
        Assert.NotNull(amendment.Route);
    }

    [Fact]
    public void TypeSetWithoutEquipment_LeavesEquipmentUnedited()
    {
        // The equipment suffix is a separate field: a blank box means "not edited", so amending
        // the type never resets it. The Sim defaults it to /A only when the amend files a new plan.
        FlightPlanAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "",
            icaoEqText: "",
            depText: "KOAK",
            destText: "KOAK",
            spdText: "250",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: "",
            originalRemarks: "OLD REMARKS"
        );

        Assert.Null(amendment.EquipmentSuffix);
    }

    [Fact]
    public void EquipmentWhitespace_LeavesEquipmentUnedited()
    {
        FlightPlanAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "   ",
            icaoEqText: "",
            depText: "KOAK",
            destText: "KOAK",
            spdText: "250",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: "",
            originalRemarks: "OLD REMARKS"
        );

        Assert.Null(amendment.EquipmentSuffix);
    }

    [Fact]
    public void TypeBlankAndEquipmentBlank_LeavesEquipmentUnedited()
    {
        FlightPlanAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "",
            eqText: "",
            icaoEqText: "",
            depText: "KOAK",
            destText: "KOAK",
            spdText: "250",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: "",
            originalRemarks: "OLD REMARKS"
        );

        Assert.Null(amendment.EquipmentSuffix);
    }

    [Fact]
    public void RemarksPrefixPreservedWhenRemarksBlank()
    {
        // Protocol prefix (+/V/, /T/, etc.) is hidden from the user but must round-trip
        // intact even when the user blanks the visible remark portion.
        FlightPlanAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "A",
            icaoEqText: "",
            depText: "KOAK",
            destText: "KOAK",
            spdText: "250",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: "+/V/",
            originalRemarks: "OLD REMARKS"
        );

        Assert.Equal("+/V/RMK/", amendment.Remarks);
    }

    [Fact]
    public void TextIsTrimmedAndUpperCased()
    {
        FlightPlanAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: " da42 ",
            eqText: " a ",
            icaoEqText: "",
            depText: " koak ",
            destText: " ksfo ",
            spdText: " 250 ",
            altText: " VFR/010 ",
            rteText: " pattern ",
            rmkText: " notes ",
            strippedRemarksPrefix: "",
            originalRemarks: "OLD REMARKS"
        );

        Assert.Equal("DA42", amendment.AircraftType);
        Assert.Equal("A", amendment.EquipmentSuffix);
        Assert.Equal("KOAK", amendment.Departure);
        Assert.Equal("KSFO", amendment.Destination);
        Assert.Equal("PATTERN", amendment.Route);
        Assert.Equal(250, amendment.CruiseSpeed);
        Assert.Equal("notes", amendment.Remarks);
    }

    [Fact]
    public void IcaoEquipment_TrimmedUpperCased_BlankIsExplicitClear()
    {
        FlightPlanAmendment amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "B77W",
            eqText: "L",
            icaoEqText: " sde2e3fghij5m1rwxy ",
            depText: "KSFO",
            destText: "PHNL",
            spdText: "480",
            altText: "350",
            rteText: "",
            rmkText: "",
            strippedRemarksPrefix: "",
            originalRemarks: "OLD REMARKS"
        );

        Assert.Equal("SDE2E3FGHIJ5M1RWXY", amendment.IcaoEquipmentCodes);

        FlightPlanAmendment cleared = FlightPlanEditorAmendmentBuilder.Build(
            typText: "B77W",
            eqText: "L",
            icaoEqText: "",
            depText: "KSFO",
            destText: "PHNL",
            spdText: "480",
            altText: "350",
            rteText: "",
            rmkText: "",
            strippedRemarksPrefix: "",
            originalRemarks: "OLD REMARKS"
        );

        Assert.Equal("", cleared.IcaoEquipmentCodes);
        Assert.NotNull(cleared.IcaoEquipmentCodes);
    }
}
