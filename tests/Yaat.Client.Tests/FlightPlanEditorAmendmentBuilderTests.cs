using Xunit;
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
        var amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "A",
            depText: "",
            destText: "KOAK",
            spdText: "250",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: "+/V/"
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
        var amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "A",
            depText: null,
            destText: "KOAK",
            spdText: "250",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: ""
        );

        Assert.Equal("", amendment.Departure);
        Assert.NotNull(amendment.Departure);
    }

    [Fact]
    public void AllTextFieldsBlank_AllStringsAreEmptyNotNull()
    {
        var amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "",
            eqText: "",
            depText: "",
            destText: "",
            spdText: "",
            altText: "",
            rteText: "",
            rmkText: "",
            strippedRemarksPrefix: ""
        );

        Assert.Equal("", amendment.AircraftType);
        Assert.Equal("", amendment.EquipmentSuffix);
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
        var amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "A",
            depText: "KOAK",
            destText: "KOAK",
            spdText: "",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: ""
        );

        Assert.Equal(0, amendment.CruiseSpeed);
        Assert.NotNull(amendment.CruiseSpeed);
    }

    [Fact]
    public void SpeedUnparseable_SendsZero()
    {
        var amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "A",
            depText: "KOAK",
            destText: "KOAK",
            spdText: "abc",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: ""
        );

        Assert.Equal(0, amendment.CruiseSpeed);
    }

    [Fact]
    public void RouteBlank_SendsEmptyRoute()
    {
        var amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "A",
            depText: "KOAK",
            destText: "KOAK",
            spdText: "250",
            altText: "VFR/010",
            rteText: "",
            rmkText: "",
            strippedRemarksPrefix: ""
        );

        Assert.Equal("", amendment.Route);
        Assert.NotNull(amendment.Route);
    }

    [Fact]
    public void TypeSetWithoutEquipment_DefaultsToA()
    {
        // CRC compat: FlightPlanEditorViewModel sets EquipmentSuffix="A" when TypeCode is
        // populated but EquipmentSuffix is blank (line 669-672 of the decompiled VM).
        var amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "",
            depText: "KOAK",
            destText: "KOAK",
            spdText: "250",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: ""
        );

        Assert.Equal("A", amendment.EquipmentSuffix);
    }

    [Fact]
    public void TypeBlankAndEquipmentBlank_LeavesEquipmentBlank()
    {
        var amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "",
            eqText: "",
            depText: "KOAK",
            destText: "KOAK",
            spdText: "250",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: ""
        );

        Assert.Equal("", amendment.EquipmentSuffix);
    }

    [Fact]
    public void RemarksPrefixPreservedWhenRemarksBlank()
    {
        // Protocol prefix (+/V/, /T/, etc.) is hidden from the user but must round-trip
        // intact even when the user blanks the visible remark portion.
        var amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: "DA42",
            eqText: "A",
            depText: "KOAK",
            destText: "KOAK",
            spdText: "250",
            altText: "VFR/010",
            rteText: "PATTERN",
            rmkText: "",
            strippedRemarksPrefix: "+/V/"
        );

        Assert.Equal("+/V/RMK/", amendment.Remarks);
    }

    [Fact]
    public void TextIsTrimmedAndUpperCased()
    {
        var amendment = FlightPlanEditorAmendmentBuilder.Build(
            typText: " da42 ",
            eqText: " a ",
            depText: " koak ",
            destText: " ksfo ",
            spdText: " 250 ",
            altText: " VFR/010 ",
            rteText: " pattern ",
            rmkText: " notes ",
            strippedRemarksPrefix: ""
        );

        Assert.Equal("DA42", amendment.AircraftType);
        Assert.Equal("A", amendment.EquipmentSuffix);
        Assert.Equal("KOAK", amendment.Departure);
        Assert.Equal("KSFO", amendment.Destination);
        Assert.Equal("PATTERN", amendment.Route);
        Assert.Equal(250, amendment.CruiseSpeed);
        Assert.Equal("notes", amendment.Remarks);
    }
}
