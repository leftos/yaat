using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Sim;
using Yaat.Sim.Phases;
using Yaat.Sim.Situation;

namespace Yaat.Client.Tests;

/// <summary>
/// The server-classified situation on <see cref="AircraftDto"/>: it crosses the hub's JSON as a number and lands on
/// <see cref="AircraftModel.Situation"/> on both the create and the update path.
/// </summary>
public class AircraftDtoSituationTests
{
    private static readonly JsonSerializerOptions WireOptions = HubPayloadOptions();

    /// <summary>The payload options the client's hub connection deserializes with: SignalR's defaults plus the hub contexts.</summary>
    private static JsonSerializerOptions HubPayloadOptions()
    {
        JsonSerializerOptions options = new JsonHubProtocolOptions().PayloadSerializerOptions;
        ServerConnection.UseHubJsonContexts(options);
        return options;
    }

    [Fact]
    public void Json_RoundTrip_CarriesSituationAsNumber()
    {
        string json = JsonSerializer.Serialize(Dto(AircraftSituation.Final), WireOptions);

        using var doc = JsonDocument.Parse(json);
        JsonElement situation = doc.RootElement.GetProperty("situation");
        Assert.Equal(JsonValueKind.Number, situation.ValueKind);
        Assert.Equal((int)AircraftSituation.Final, situation.GetInt32());

        AircraftDto? back = JsonSerializer.Deserialize<AircraftDto>(json, WireOptions);
        Assert.NotNull(back);
        Assert.Equal(AircraftSituation.Final, back.Situation);
    }

    [Fact]
    public void Json_MissingSituation_DeserializesAsUnknown()
    {
        string json = JsonSerializer.Serialize(Dto(AircraftSituation.Taxiing), WireOptions).Replace("\"situation\":4,", "", StringComparison.Ordinal);
        Assert.DoesNotContain("\"situation\"", json, StringComparison.Ordinal);

        AircraftDto? back = JsonSerializer.Deserialize<AircraftDto>(json, WireOptions);

        Assert.NotNull(back);
        Assert.Equal(AircraftSituation.Unknown, back.Situation);
    }

    [Fact]
    public void Model_CopiesSituation_OnCreateAndUpdate()
    {
        var model = AircraftModel.FromDto(Dto(AircraftSituation.HoldingShort));
        Assert.Equal(AircraftSituation.HoldingShort, model.Situation);

        model.UpdateFromDto(Dto(AircraftSituation.LinedUp));
        Assert.Equal(AircraftSituation.LinedUp, model.Situation);
    }

    [Fact]
    public void Model_CopiesEramDataBlockInputs_OnCreateAndUpdate()
    {
        AircraftDto set = Dto(AircraftSituation.Final) with
        {
            EramInterimAltitude = 100,
            EramLocalInterimAltitude = 110,
            EramProcedureAltitude = 120,
            EramControllerEnteredAltitude = 130,
            EramReachedAssignedAltitude = true,
            EramPlanAltitudeFeet = 25000,
            TransponderHasReportedModeC = true,
            EramIsFrozen = true,
            EramIsCoastTrack = true,
            EramIsCoverageCoasting = true,
        };

        var model = AircraftModel.FromDto(set);
        AssertEramInputs(model, set);

        model.UpdateFromDto(Dto(AircraftSituation.Final));
        AssertEramInputs(model, Dto(AircraftSituation.Final));

        model.UpdateFromDto(set);
        AssertEramInputs(model, set);
    }

    [Fact]
    public void Json_MissingEramDataBlockInputs_DeserializeAsUnset()
    {
        string json = JsonSerializer.Serialize(Dto(AircraftSituation.Final), WireOptions);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("eramInterimAltitude").ValueKind);
        Assert.False(doc.RootElement.GetProperty("transponderHasReportedModeC").GetBoolean());

        AircraftDto back = JsonSerializer.Deserialize<AircraftDto>("{\"callsign\":\"UAL1\"}", WireOptions)!;

        Assert.Null(back.EramPlanAltitudeFeet);
        Assert.False(back.EramReachedAssignedAltitude);
        Assert.False(back.EramIsCoverageCoasting);
    }

    private static void AssertEramInputs(AircraftModel model, AircraftDto dto)
    {
        Assert.Equal(dto.EramInterimAltitude, model.EramInterimAltitude);
        Assert.Equal(dto.EramLocalInterimAltitude, model.EramLocalInterimAltitude);
        Assert.Equal(dto.EramProcedureAltitude, model.EramProcedureAltitude);
        Assert.Equal(dto.EramControllerEnteredAltitude, model.EramControllerEnteredAltitude);
        Assert.Equal(dto.EramReachedAssignedAltitude, model.EramReachedAssignedAltitude);
        Assert.Equal(dto.EramPlanAltitudeFeet, model.EramPlanAltitudeFeet);
        Assert.Equal(dto.TransponderHasReportedModeC, model.TransponderHasReportedModeC);
        Assert.Equal(dto.EramIsFrozen, model.EramIsFrozen);
        Assert.Equal(dto.EramIsCoastTrack, model.EramIsCoastTrack);
        Assert.Equal(dto.EramIsCoverageCoasting, model.EramIsCoverageCoasting);
    }

    [Fact]
    public void Model_CopiesEramLine34Inputs_OnCreateAndUpdate()
    {
        AircraftDto set = Dto(AircraftSituation.Final) with
        {
            EramAssignedHeading = "270",
            EramAssignedSpeed = "250",
            EramFreeText = "WX",
            EramOnFrequencySectorIds = ["44", "40"],
            EramIsCorrelated = true,
            EramSpcBlinking = true,
            EramHandoffPeerFieldE = "L25",
            EramRecentHandoffFromSectorId = "40",
            EramRecentHandoffFromFacilityId = "ZOA",
            EramRecentHandoffWasForced = true,
            EramRecentHandoffPeerFieldE = "44",
        };

        var model = AircraftModel.FromDto(set);
        AssertEramLine34Inputs(model, set);

        model.UpdateFromDto(Dto(AircraftSituation.Final));
        AssertEramLine34Inputs(model, Dto(AircraftSituation.Final));

        model.UpdateFromDto(set);
        AssertEramLine34Inputs(model, set);
    }

    [Fact]
    public void Json_MissingEramLine34Inputs_DeserializeAsUnset()
    {
        string json = JsonSerializer.Serialize(Dto(AircraftSituation.Final), WireOptions);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("eramHandoffPeerFieldE").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("eramOnFrequencySectorIds").ValueKind);
        Assert.False(doc.RootElement.GetProperty("eramIsCorrelated").GetBoolean());

        AircraftDto back = JsonSerializer.Deserialize<AircraftDto>("{\"callsign\":\"UAL1\"}", WireOptions)!;

        Assert.Null(back.EramAssignedHeading);
        Assert.Null(back.EramAssignedSpeed);
        Assert.Null(back.EramFreeText);
        Assert.Null(back.EramOnFrequencySectorIds);
        Assert.False(back.EramIsCorrelated);
        Assert.False(back.EramSpcBlinking);
        Assert.Null(back.EramHandoffPeerFieldE);
        Assert.Null(back.EramRecentHandoffFromSectorId);
        Assert.Null(back.EramRecentHandoffFromFacilityId);
        Assert.False(back.EramRecentHandoffWasForced);
        Assert.Null(back.EramRecentHandoffPeerFieldE);
    }

    [Fact]
    public void ApplyDto_SameOnFrequencySectors_RaisesNoPropertyChanged()
    {
        AircraftDto set = Dto(AircraftSituation.Final) with { EramOnFrequencySectorIds = ["44", "40"] };
        var model = AircraftModel.FromDto(set);
        IReadOnlyList<string> before = model.EramOnFrequencySectorIds;
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.UpdateFromDto(set with { EramOnFrequencySectorIds = ["44", "40"] });

        Assert.DoesNotContain(nameof(AircraftModel.EramOnFrequencySectorIds), changed);
        Assert.Same(before, model.EramOnFrequencySectorIds);
    }

    [Fact]
    public void ApplyDto_ChangedOnFrequencySectors_Updates()
    {
        var model = AircraftModel.FromDto(Dto(AircraftSituation.Final) with { EramOnFrequencySectorIds = ["44"] });
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.UpdateFromDto(Dto(AircraftSituation.Final) with { EramOnFrequencySectorIds = ["40", "44"] });
        Assert.Equal(["40", "44"], model.EramOnFrequencySectorIds);
        Assert.Contains(nameof(AircraftModel.EramOnFrequencySectorIds), changed);

        changed.Clear();
        model.UpdateFromDto(Dto(AircraftSituation.Final));
        Assert.Empty(model.EramOnFrequencySectorIds);
        Assert.Contains(nameof(AircraftModel.EramOnFrequencySectorIds), changed);
    }

    [Fact]
    public void Model_CopiesDwellLockedSectors_OnCreate_AndDefaultsToEmpty()
    {
        AircraftDto set = Dto(AircraftSituation.Final) with { EramDwellLockedSectors = [new("ZLA", "25"), new("ZOA", "44")] };

        Assert.Equal([new EramSectorKey("ZLA", "25"), new EramSectorKey("ZOA", "44")], AircraftModel.FromDto(set).EramDwellLockedSectors);
        Assert.Empty(AircraftModel.FromDto(Dto(AircraftSituation.Final)).EramDwellLockedSectors);
    }

    [Fact]
    public void Json_DwellLockedSectors_RoundTrip_AndMissingDeserializesAsNull()
    {
        AircraftDto set = Dto(AircraftSituation.Final) with { EramDwellLockedSectors = [new("ZOA", "44")] };
        string json = JsonSerializer.Serialize(set, WireOptions);

        AircraftDto back = JsonSerializer.Deserialize<AircraftDto>(json, WireOptions)!;

        Assert.Equal([new EramSectorKey("ZOA", "44")], back.EramDwellLockedSectors);
        Assert.Null(JsonSerializer.Deserialize<AircraftDto>("{\"callsign\":\"UAL1\"}", WireOptions)!.EramDwellLockedSectors);
    }

    [Fact]
    public void Json_DwellLockedSectors_ReadTheServersWireNames()
    {
        const string json = "{\"callsign\":\"UAL1\",\"eramDwellLockedSectors\":[{\"facility\":\"ZOA\",\"sector\":\"44\"}]}";

        AircraftDto back = JsonSerializer.Deserialize<AircraftDto>(json, WireOptions)!;

        Assert.Equal([new EramSectorKey("ZOA", "44")], back.EramDwellLockedSectors);
    }

    [Fact]
    public void ApplyDto_SameDwellLockedSectors_RaisesNoPropertyChanged()
    {
        AircraftDto set = Dto(AircraftSituation.Final) with { EramDwellLockedSectors = [new("ZOA", "44"), new("ZOA", "40")] };
        var model = AircraftModel.FromDto(set);
        IReadOnlyList<EramSectorKey> before = model.EramDwellLockedSectors;
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.UpdateFromDto(set with { EramDwellLockedSectors = [new("ZOA", "44"), new("ZOA", "40")] });

        Assert.DoesNotContain(nameof(AircraftModel.EramDwellLockedSectors), changed);
        Assert.Same(before, model.EramDwellLockedSectors);
    }

    [Fact]
    public void ApplyDto_ChangedDwellLockedSectors_Updates()
    {
        var model = AircraftModel.FromDto(Dto(AircraftSituation.Final) with { EramDwellLockedSectors = [new("ZOA", "44")] });
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.UpdateFromDto(Dto(AircraftSituation.Final) with { EramDwellLockedSectors = [new("ZOA", "40"), new("ZOA", "44")] });
        Assert.Equal([new EramSectorKey("ZOA", "40"), new EramSectorKey("ZOA", "44")], model.EramDwellLockedSectors);
        Assert.Contains(nameof(AircraftModel.EramDwellLockedSectors), changed);

        changed.Clear();
        model.UpdateFromDto(Dto(AircraftSituation.Final));
        Assert.Empty(model.EramDwellLockedSectors);
        Assert.Contains(nameof(AircraftModel.EramDwellLockedSectors), changed);
    }

    [Fact]
    public void Model_CopiesFourthLineSuppressedSectors_OnCreate_AndDefaultsToEmpty()
    {
        AircraftDto set = Dto(AircraftSituation.Final) with { EramFourthLineSuppressedSectors = [new("ZLA", "25"), new("ZOA", "44")] };

        Assert.Equal([new EramSectorKey("ZLA", "25"), new EramSectorKey("ZOA", "44")], AircraftModel.FromDto(set).EramFourthLineSuppressedSectors);
        Assert.Empty(AircraftModel.FromDto(Dto(AircraftSituation.Final)).EramFourthLineSuppressedSectors);
    }

    [Fact]
    public void Json_FourthLineSuppressedSectors_ReadTheServersWireNames_AndMissingDeserializesAsNull()
    {
        const string json = "{\"callsign\":\"UAL1\",\"eramFourthLineSuppressedSectors\":[{\"facility\":\"ZOA\",\"sector\":\"44\"}]}";

        AircraftDto back = JsonSerializer.Deserialize<AircraftDto>(json, WireOptions)!;

        Assert.Equal([new EramSectorKey("ZOA", "44")], back.EramFourthLineSuppressedSectors);
        Assert.Null(JsonSerializer.Deserialize<AircraftDto>("{\"callsign\":\"UAL1\"}", WireOptions)!.EramFourthLineSuppressedSectors);
    }

    [Fact]
    public void ApplyDto_ChangedFourthLineSuppressedSectors_Updates_AndTheSameListRaisesNothing()
    {
        AircraftDto set = Dto(AircraftSituation.Final) with { EramFourthLineSuppressedSectors = [new("ZOA", "44")] };
        var model = AircraftModel.FromDto(Dto(AircraftSituation.Final));
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.UpdateFromDto(set);
        Assert.Equal([new EramSectorKey("ZOA", "44")], model.EramFourthLineSuppressedSectors);
        Assert.Contains(nameof(AircraftModel.EramFourthLineSuppressedSectors), changed);

        changed.Clear();
        model.UpdateFromDto(set with { EramFourthLineSuppressedSectors = [new("ZOA", "44")] });
        Assert.DoesNotContain(nameof(AircraftModel.EramFourthLineSuppressedSectors), changed);

        model.UpdateFromDto(Dto(AircraftSituation.Final));
        Assert.Empty(model.EramFourthLineSuppressedSectors);
        Assert.Contains(nameof(AircraftModel.EramFourthLineSuppressedSectors), changed);
    }

    [Fact]
    public void Json_EramCrrGroup_ReadsTheServersWireName_AndMissingDeserializesAsNull()
    {
        AircraftDto back = JsonSerializer.Deserialize<AircraftDto>("{\"callsign\":\"UAL1\",\"eramCrrGroup\":\"BOSOX\"}", WireOptions)!;

        Assert.Equal("BOSOX", back.EramCrrGroup);
        Assert.Null(JsonSerializer.Deserialize<AircraftDto>("{\"callsign\":\"UAL1\"}", WireOptions)!.EramCrrGroup);
    }

    [Fact]
    public void Model_TakesTheEramCrrGroup_OnCreate_AndOnUpdate()
    {
        var model = AircraftModel.FromDto(Dto(AircraftSituation.Final) with { EramCrrGroup = "BOSOX" });
        Assert.Equal("BOSOX", model.EramCrrGroup);

        model.UpdateFromDto(Dto(AircraftSituation.Final) with { EramCrrGroup = "REDSOX" });
        Assert.Equal("REDSOX", model.EramCrrGroup);

        model.UpdateFromDto(Dto(AircraftSituation.Final));
        Assert.Null(model.EramCrrGroup);
    }

    private static void AssertEramLine34Inputs(AircraftModel model, AircraftDto dto)
    {
        Assert.Equal(dto.EramAssignedHeading, model.EramAssignedHeading);
        Assert.Equal(dto.EramAssignedSpeed, model.EramAssignedSpeed);
        Assert.Equal(dto.EramFreeText, model.EramFreeText);
        Assert.Equal(dto.EramOnFrequencySectorIds ?? [], model.EramOnFrequencySectorIds);
        Assert.Equal(dto.EramIsCorrelated, model.EramIsCorrelated);
        Assert.Equal(dto.EramSpcBlinking, model.EramSpcBlinking);
        Assert.Equal(dto.EramHandoffPeerFieldE, model.EramHandoffPeerFieldE);
        Assert.Equal(dto.EramRecentHandoffFromSectorId, model.EramRecentHandoffFromSectorId);
        Assert.Equal(dto.EramRecentHandoffFromFacilityId, model.EramRecentHandoffFromFacilityId);
        Assert.Equal(dto.EramRecentHandoffWasForced, model.EramRecentHandoffWasForced);
        Assert.Equal(dto.EramRecentHandoffPeerFieldE, model.EramRecentHandoffPeerFieldE);
    }

    [Fact]
    public void Json_SituationFlagsNumber_DeserializesToTheFlags()
    {
        string json = JsonSerializer.Serialize(Dto(AircraftSituation.Final), WireOptions);
        using var doc = JsonDocument.Parse(json);
        JsonElement flags = doc.RootElement.GetProperty("situationFlags");
        Assert.Equal(JsonValueKind.Number, flags.ValueKind);
        Assert.Equal(0, flags.GetInt32());

        // The server sends the flags as a number: 20 is InsideFinalApproachFix (4) | HasReportedFieldInSight (16).
        string hubJson = json.Replace("\"situationFlags\":0", "\"situationFlags\":20", StringComparison.Ordinal);
        AircraftDto? back = JsonSerializer.Deserialize<AircraftDto>(hubJson, WireOptions);

        Assert.NotNull(back);
        Assert.Equal(SituationFlags.InsideFinalApproachFix | SituationFlags.HasReportedFieldInSight, back.SituationFlags);
    }

    [Fact]
    public void Model_CopiesSituationFlags_OnCreateAndUpdate()
    {
        var model = AircraftModel.FromDto(Dto(AircraftSituation.HoldingShort) with { SituationFlags = SituationFlags.HoldShortIsDepartureRunway });
        Assert.Equal(SituationFlags.HoldShortIsDepartureRunway, model.SituationFlags);

        model.UpdateFromDto(Dto(AircraftSituation.Final) with { SituationFlags = SituationFlags.InsideFinalApproachFix });
        Assert.Equal(SituationFlags.InsideFinalApproachFix, model.SituationFlags);

        model.UpdateFromDto(Dto(AircraftSituation.Final));
        Assert.Equal(SituationFlags.None, model.SituationFlags);
    }

    [Fact]
    public void Json_NextCrossingRunway_ReachesTheModel_OnCreateAndUpdate()
    {
        string json = JsonSerializer.Serialize(Dto(AircraftSituation.Taxiing) with { NextCrossingRunway = "28R" }, WireOptions);
        Assert.Contains("\"nextCrossingRunway\":\"28R\"", json, StringComparison.Ordinal);
        AircraftDto back = JsonSerializer.Deserialize<AircraftDto>(json, WireOptions)!;

        var model = AircraftModel.FromDto(back);
        Assert.Equal("28R", model.NextCrossingRunway);

        model.UpdateFromDto(Dto(AircraftSituation.Taxiing));
        Assert.Null(model.NextCrossingRunway);

        model.UpdateFromDto(Dto(AircraftSituation.RolloutExit) with { NextCrossingRunway = "10L" });
        Assert.Equal("10L", model.NextCrossingRunway);
    }

    [Fact]
    public void Json_ExitsAhead_ReachesTheModel_OnCreateAndUpdate()
    {
        ExitAheadDto[] rows = [new ExitAheadDto("W2", ExitSide.Right, 1800, true, 45), new ExitAheadDto("W3", ExitSide.Left, 3600, false, 128)];
        string json = JsonSerializer.Serialize(Dto(AircraftSituation.RolloutExit) with { ExitsAhead = rows }, WireOptions);
        Assert.Contains("\"exitsAhead\":[", json, StringComparison.Ordinal);
        Assert.Contains("\"turnOffAngleDeg\":128", json, StringComparison.Ordinal);
        AircraftDto back = JsonSerializer.Deserialize<AircraftDto>(json, WireOptions)!;

        var model = AircraftModel.FromDto(back);
        Assert.Equal(rows, model.ExitsAhead!);

        model.UpdateFromDto(Dto(AircraftSituation.RolloutExit) with { ExitsAhead = [] });
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ExitAheadDto>>(model.ExitsAhead));

        model.UpdateFromDto(Dto(AircraftSituation.Taxiing));
        Assert.Null(model.ExitsAhead);
    }

    [Fact]
    public void Json_NoExitsAhead_DeserializesAsNull()
    {
        string json = JsonSerializer.Serialize(Dto(AircraftSituation.Final), WireOptions);

        AircraftDto back = JsonSerializer.Deserialize<AircraftDto>(json, WireOptions)!;

        Assert.Null(back.ExitsAhead);
        Assert.Null(AircraftModel.FromDto(back).ExitsAhead);
    }

    [Fact]
    public void Json_MeetPoints_ReachTheModel_OnCreateAndUpdate()
    {
        GroundMeetPointDto[] rows = [new GroundMeetPointDto("N52417", 352, new LatLon(37.7306, -122.2161))];
        string json = JsonSerializer.Serialize(Dto(AircraftSituation.Taxiing) with { MeetPoints = rows }, WireOptions);
        string expected = "\"meetPoints\":[{\"callsign\":\"N52417\",\"nodeId\":352,\"position\":{\"lat\":37.7306,\"lon\":-122.2161}}]";
        Assert.Contains(expected, json, StringComparison.Ordinal);
        AircraftDto back = JsonSerializer.Deserialize<AircraftDto>(json, WireOptions)!;

        var model = AircraftModel.FromDto(back);
        Assert.Equal(rows, model.MeetPoints!);

        GroundMeetPointDto[] moved = [new GroundMeetPointDto("N52417", 376, new LatLon(37.7307, -122.2160))];
        model.UpdateFromDto(Dto(AircraftSituation.Taxiing) with { MeetPoints = moved });
        Assert.Equal(moved, model.MeetPoints!);

        model.UpdateFromDto(Dto(AircraftSituation.Taxiing));
        Assert.Null(model.MeetPoints);
    }

    [Fact]
    public void Json_ArmedReports_DeserializeFromTheServersShape()
    {
        string json = JsonSerializer.Serialize(Dto(AircraftSituation.Final), WireOptions);
        Assert.Contains("\"armedReports\":null", json, StringComparison.Ordinal);

        // The server writes the legs as a number, as it does the situation flags: 9 is Crosswind (1) | Base (8).
        string hubJson = json.Replace(
            "\"armedReports\":null",
            "\"armedReports\":{\"legs\":9,\"finalMiles\":5,\"fix\":\"SUNOL\"}",
            StringComparison.Ordinal
        );
        AircraftDto back = JsonSerializer.Deserialize<AircraftDto>(hubJson, WireOptions)!;

        Assert.Equal(new ArmedReportsDto(ArmedReportLegs.Crosswind | ArmedReportLegs.Base, 5, "SUNOL"), back.ArmedReports);
    }

    [Fact]
    public void Json_NoArmedReports_DeserializesAsNull()
    {
        string json = JsonSerializer
            .Serialize(Dto(AircraftSituation.Final), WireOptions)
            .Replace(",\"armedReports\":null", "", StringComparison.Ordinal);
        Assert.DoesNotContain("\"armedReports\"", json, StringComparison.Ordinal);

        AircraftDto back = JsonSerializer.Deserialize<AircraftDto>(json, WireOptions)!;

        Assert.Null(back.ArmedReports);
    }

    [Fact]
    public void Model_CopiesArmedReports_OnCreateAndUpdate()
    {
        var baseArmed = new ArmedReportsDto(ArmedReportLegs.Base, null, null);
        var model = AircraftModel.FromDto(Dto(AircraftSituation.Final) with { ArmedReports = baseArmed });
        Assert.Equal(baseArmed, model.ArmedReports);

        var fixArmed = new ArmedReportsDto(ArmedReportLegs.None, 5, "SUNOL");
        model.UpdateFromDto(Dto(AircraftSituation.Final) with { ArmedReports = fixArmed });
        Assert.Equal(fixArmed, model.ArmedReports);

        model.UpdateFromDto(Dto(AircraftSituation.Final));
        Assert.Null(model.ArmedReports);
    }

    [Fact]
    public void OwnerDisplay_AppendsTheStudentOwnerSymbol()
    {
        var model = AircraftModel.FromDto(Dto(AircraftSituation.Final) with { OwnerSectorCode = "3Y", StudentOwnerSymbol = "1" });

        Assert.Equal("3Y(1)", model.OwnerDisplay);
    }

    [Theory]
    [InlineData("3Y", "1", "3Y(1)")]
    [InlineData("3Y", "Y", "3Y")]
    [InlineData("3Y", "3", "3Y")]
    [InlineData("C44", "C", "C44")]
    [InlineData("C44", "4", "C44")]
    [InlineData("C25", "L", "C25(L)")]
    [InlineData("1N", "C", "1N(C)")]
    [InlineData("c44", "C", "c44")]
    public void OwnerDisplay_SuffixShowsOnlyWhenItDiffersFromTheOwnersEnds(string owner, string symbol, string expected)
    {
        var model = AircraftModel.FromDto(Dto(AircraftSituation.Final) with { OwnerSectorCode = owner, StudentOwnerSymbol = symbol });

        Assert.Equal(expected, model.OwnerDisplay);
    }

    [Fact]
    public void OwnerDisplay_NoSymbol_IsTheBareOwner()
    {
        var model = AircraftModel.FromDto(Dto(AircraftSituation.Final) with { OwnerSectorCode = "3Y" });

        Assert.Equal("3Y", model.OwnerDisplay);
    }

    [Fact]
    public void ApplyDto_StudentOwnerSymbolChange_RaisesOwnerDisplayChanged()
    {
        var model = AircraftModel.FromDto(Dto(AircraftSituation.Final) with { OwnerSectorCode = "3Y" });
        List<string?> changed = [];
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.UpdateFromDto(Dto(AircraftSituation.Final) with { OwnerSectorCode = "3Y", StudentOwnerSymbol = "1" });

        Assert.Contains(nameof(AircraftModel.OwnerDisplay), changed);
        Assert.Equal("3Y(1)", model.OwnerDisplay);
    }

    private static AircraftDto Dto(AircraftSituation situation) =>
        new()
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Latitude = 37.62,
            Longitude = -122.38,
            Heading = 90,
            Altitude = 0,
            GroundSpeed = 0,
            BeaconCode = 1200,
            TransponderMode = "C",
            IsIdenting = false,
            VerticalSpeed = 0,
            AssignedHeading = null,
            AssignedAltitude = null,
            AssignedSpeed = null,
            Departure = "KSFO",
            Destination = "KLAX",
            Route = "",
            FlightRules = "IFR",
            Status = "",
            Situation = situation,
        };
}
