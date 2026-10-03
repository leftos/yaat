using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tools;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Automation;

/// <summary>
/// <c>list_app_tools</c> and <c>call_app_tool</c> over a real named pipe, on a real <see cref="MainViewModel"/>: the catalog the
/// client reports, the argument binding and its coded errors, the availability answers, and what each tool does to the client.
/// </summary>
public sealed class AppToolsTests : AutomationHostFixture
{
    private static readonly string[] AllowedTypes = ["string", "int", "double", "bool"];

    private static MainViewModel NewMain() => new(new FakeFilePickerService());

    /// <summary>A view model that believes it is connected, in a room, with a scenario loaded; no server is behind it.</summary>
    private static MainViewModel InScenario()
    {
        MainViewModel viewModel = NewMain();
        viewModel.IsConnected = true;
        viewModel.ActiveRoomId = "room-1";
        viewModel.ActiveScenarioId = "scenario-1";
        return viewModel;
    }

    /// <summary><see cref="InScenario"/> with the primary radar's video maps and saved settings applied, as after a finished load.</summary>
    private static MainViewModel RadarReady()
    {
        MainViewModel viewModel = InScenario();
        viewModel.Radar.SetVideoMapsReadyForTesting();
        return viewModel;
    }

    private static VideoMapToggleItem MapFive() =>
        new()
        {
            MapId = "map-5",
            ShortName = "CENTER",
            Name = "Center map",
            BrightnessCategory = "A",
            StarsId = 5,
        };

    /// <summary>Sends <c>call_app_tool</c> with each raw params JSON in order over one connection, and returns the answers.</summary>
    private async Task<JsonElement[]> CallRaw(Func<AutomationTools?> tools, params string[] paramsJson)
    {
        using AutomationHost host = StartHost(() => Windows, () => null, tools);
        await using AutomationPipeTestClient client = await Connect();
        List<JsonElement> responses = [];
        foreach (string json in paramsJson)
        {
            responses.Add(await SendRawParams(client, ProtocolMethods.CallAppTool, json));
        }

        return [.. responses];
    }

    private static Func<AutomationTools?> ToolsOf(MainViewModel viewModel) =>
        () => new AutomationTools(viewModel, new MainViewModelAutomationState(viewModel));

    /// <summary>Sends each request in order over one connection to a host serving <paramref name="tools"/>, and returns the answers.</summary>
    private async Task<JsonElement[]> SendAll(Func<AutomationTools?> tools, params (string Method, object Parameters)[] requests)
    {
        using AutomationHost host = StartHost(() => Windows, () => null, tools);
        await using AutomationPipeTestClient client = await Connect();
        List<JsonElement> responses = [];
        foreach ((string method, object parameters) in requests)
        {
            responses.Add(await Send(client, method, parameters));
        }

        return [.. responses];
    }

    private async Task<JsonElement> CallTool(Func<AutomationTools?> tools, string tool, object arguments) =>
        (await SendAll(tools, (ProtocolMethods.CallAppTool, new { tool, arguments })))[0];

    private async Task<JsonElement> ListTools(Func<AutomationTools?> tools) => (await SendAll(tools, (ProtocolMethods.ListAppTools, new { })))[0];

    private static JsonElement ToolNamed(JsonElement list, string name) =>
        list.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == name);

    private static string Signature(JsonElement tool) =>
        $"{tool.GetProperty("name").GetString()}("
        + string.Join(
            ",",
            tool.GetProperty("parameters").EnumerateArray().Select(p => $"{p.GetProperty("name").GetString()}:{p.GetProperty("type").GetString()}")
        )
        + ")";

    private static string Message(JsonElement error) => error.GetProperty("message").GetString() ?? "";

    private static string ErrorParam(JsonElement error) => error.GetProperty("details").GetProperty("param").GetString() ?? "";

    [AvaloniaFact]
    public async Task ListAppTools_ReportsEveryToolWithItsParameters()
    {
        JsonElement list = Result(await ListTools(ToolsOf(InScenario())));

        string[] signatures = [.. list.EnumerateArray().Select(Signature).Order(StringComparer.Ordinal)];
        Assert.Equal(
            [
                "center_radar(callsign:string,rangeNm:double)",
                "clear_rbls()",
                "load_recording(path:string)",
                "place_rbl(from:string,to:string)",
                "remove_rbl(slot:int)",
                "reset_datablock_offset(callsign:string)",
                "set_datablock_offset(callsign:string,dxPx:int,dyPx:int)",
                "set_leader_direction(callsign:string,direction:int)",
                "set_sim_rate(rate:int)",
                "set_solo(enabled:bool)",
                "set_video_map(starsId:int,enabled:bool)",
            ],
            signatures
        );
        foreach (JsonElement tool in list.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()), tool.GetRawText());
            Assert.All(
                tool.GetProperty("parameters").EnumerateArray(),
                parameter => Assert.False(string.IsNullOrWhiteSpace(parameter.GetProperty("description").GetString()), tool.GetRawText())
            );
        }

        Assert.True(ToolNamed(list, "set_sim_rate").GetProperty("available").GetBoolean());
        Assert.False(ToolNamed(list, "set_sim_rate").TryGetProperty("reason", out _));
    }

    [AvaloniaFact]
    public async Task ListAppTools_ReportsUnavailableWithReason_WhenNoRoom()
    {
        JsonElement list = Result(await ListTools(ToolsOf(NewMain())));

        foreach (JsonElement tool in list.EnumerateArray())
        {
            Assert.False(tool.GetProperty("available").GetBoolean(), tool.GetRawText());
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("reason").GetString()), tool.GetRawText());
        }

        Assert.Contains("room", ToolNamed(list, "set_sim_rate").GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connected", ToolNamed(list, "load_recording").GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task CallAppTool_UnknownTool_InvalidParam()
    {
        JsonElement error = Error(await CallTool(ToolsOf(InScenario()), "no_such_tool", new { }), AutomationErrorCodes.InvalidParam);

        Assert.Contains("no_such_tool", Message(error), StringComparison.Ordinal);
        Assert.Contains(ProtocolMethods.ListAppTools, Message(error), StringComparison.Ordinal);
        Assert.Equal("tool", ErrorParam(error));
    }

    [AvaloniaFact]
    public async Task CallAppTool_MissingArgument_InvalidParam()
    {
        JsonElement error = Error(await CallTool(ToolsOf(InScenario()), "set_video_map", new { starsId = 5 }), AutomationErrorCodes.InvalidParam);

        Assert.Contains("'enabled'", Message(error), StringComparison.Ordinal);
        Assert.Equal("enabled", ErrorParam(error));
    }

    [AvaloniaFact]
    public async Task CallAppTool_UnknownArgument_InvalidParam()
    {
        JsonElement error = Error(
            await CallTool(ToolsOf(InScenario()), "set_solo", new { enabled = true, extra = 1 }),
            AutomationErrorCodes.InvalidParam
        );

        Assert.Contains("'extra'", Message(error), StringComparison.Ordinal);
        Assert.Equal("extra", ErrorParam(error));
    }

    [AvaloniaFact]
    public async Task CallAppTool_WrongJsonKind_InvalidParam()
    {
        JsonElement[] responses = await SendAll(
            ToolsOf(InScenario()),
            (ProtocolMethods.CallAppTool, new { tool = "set_sim_rate", arguments = new { rate = 1.5 } }),
            (ProtocolMethods.CallAppTool, new { tool = "center_radar", arguments = new { callsign = 3, rangeNm = 20 } }),
            (ProtocolMethods.CallAppTool, new { tool = "set_solo", arguments = new { enabled = "true" } }),
            (ProtocolMethods.CallAppTool, new { tool = "center_radar", arguments = new { callsign = "UAL1", rangeNm = "20" } })
        );

        string[] culprits = [.. responses.Select(response => ErrorParam(Error(response, AutomationErrorCodes.InvalidParam)))];
        Assert.Equal(["rate", "callsign", "enabled", "rangeNm"], culprits);
        Assert.Contains("int", Message(Error(responses[0], AutomationErrorCodes.InvalidParam)), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CallAppTool_Unavailable_AnswersAvailableFalseWithReason()
    {
        JsonElement noRoom = Result(await CallTool(ToolsOf(NewMain()), "set_sim_rate", new { rate = 2 }));
        JsonElement noWindow = Result(await CallTool(() => null, "set_sim_rate", new { rate = 2 }));

        Assert.False(noRoom.GetProperty("available").GetBoolean());
        Assert.Contains("room", noRoom.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(noRoom.TryGetProperty("message", out _));
        Assert.False(noWindow.GetProperty("available").GetBoolean());
        Assert.Equal(AutomationTools.NoMainWindowReason, noWindow.GetProperty("reason").GetString());
    }

    [Fact]
    public void AppTools_EveryParameterIsAllowedTypeAndDescribed()
    {
        MethodInfo[] tools =
        [
            .. typeof(AutomationTools).GetMethods().Where(method => method.GetCustomAttribute<AutomationToolAttribute>() is not null),
        ];

        Assert.NotEmpty(tools);
        foreach (MethodInfo tool in tools)
        {
            Assert.Equal(typeof(Task<AppToolOutcome>), tool.ReturnType);
            foreach (ParameterInfo parameter in tool.GetParameters())
            {
                Assert.Contains(AutomationTools.TypeName(parameter.ParameterType) ?? "", AllowedTypes);
                string? description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
                Assert.False(string.IsNullOrWhiteSpace(description), $"{tool.Name}({parameter.Name}) has no [Description]");
            }
        }
    }

    [AvaloniaFact]
    public async Task SetSimRate_SendsSimrate()
    {
        MainViewModel viewModel = InScenario();
        var state = new RateRecordingState(null);

        JsonElement result = Result(await CallTool(() => new AutomationTools(viewModel, state), "set_sim_rate", new { rate = 4 }));

        Assert.Equal([4], state.Rates);
        Assert.True(result.GetProperty("available").GetBoolean());
        Assert.Contains("4", result.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task SetSimRate_RoomRefuses_ReportsTheRefusal()
    {
        MainViewModel viewModel = InScenario();
        var state = new RateRecordingState("Invalid sim rate");

        JsonElement result = Result(await CallTool(() => new AutomationTools(viewModel, state), "set_sim_rate", new { rate = 3 }));

        Assert.True(result.GetProperty("available").GetBoolean());
        Assert.Equal("The room refused SIMRATE 3: Invalid sim rate", result.GetProperty("message").GetString());
    }

    // A whole number written with a fraction or an exponent is still an int; 1.5 is not (CallAppTool_WrongJsonKind_InvalidParam).
    [AvaloniaFact]
    public async Task CallAppTool_IntGivenTwoPointZero_Binds()
    {
        MainViewModel viewModel = InScenario();
        var state = new RateRecordingState(null);

        JsonElement[] responses = await CallRaw(
            () => new AutomationTools(viewModel, state),
            """{"tool":"set_sim_rate","arguments":{"rate":2.0}}""",
            """{"tool":"set_sim_rate","arguments":{"rate":2e0}}"""
        );

        Assert.All(responses, response => Assert.True(Result(response).GetProperty("available").GetBoolean()));
        Assert.Equal([2, 2], state.Rates);
    }

    [AvaloniaFact]
    public async Task CallAppTool_IntOverflow_InvalidParamNamesTheRange()
    {
        JsonElement[] responses = await CallRaw(ToolsOf(InScenario()), """{"tool":"set_sim_rate","arguments":{"rate":3000000000}}""");

        JsonElement error = Error(responses[0], AutomationErrorCodes.InvalidParam);
        Assert.Equal("rate", ErrorParam(error));
        Assert.Contains("must be a whole number from -2147483648 to 2147483647, not 3000000000", Message(error), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CenterRadar_CentresOnTheAircraftAtTheRange()
    {
        MainViewModel viewModel = RadarReady();
        viewModel.Aircraft.Add(new AircraftModel { Callsign = "UAL123", Position = new LatLon(37.7213, -122.2208) });

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "center_radar", new { callsign = "ual123", rangeNm = 15.5 }));

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        Assert.Equal(37.7213, viewModel.Radar.CenterLat, 6);
        Assert.Equal(-122.2208, viewModel.Radar.CenterLon, 6);
        Assert.Equal(15.5, viewModel.Radar.RangeNm, 6);
        Assert.Contains("UAL123", result.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CenterRadar_RangeOutOfBounds_InvalidParam()
    {
        MainViewModel viewModel = RadarReady();
        viewModel.Aircraft.Add(new AircraftModel { Callsign = "UAL123", Position = new LatLon(37.7213, -122.2208) });
        double rangeBefore = viewModel.Radar.RangeNm;

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, new { tool = "center_radar", arguments = new { callsign = "UAL123", rangeNm = 256.5 } }),
            (ProtocolMethods.CallAppTool, new { tool = "center_radar", arguments = new { callsign = "UAL123", rangeNm = 0.5 } })
        );

        Assert.All(responses, response => Assert.Equal("rangeNm", ErrorParam(Error(response, AutomationErrorCodes.InvalidParam))));
        Assert.Equal(rangeBefore, viewModel.Radar.RangeNm);
    }

    [AvaloniaFact]
    public async Task CenterRadar_UnknownCallsign_Unavailable()
    {
        JsonElement result = Result(await CallTool(ToolsOf(RadarReady()), "center_radar", new { callsign = "AAL9", rangeNm = 20 }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Contains("AAL9", result.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    // Until the load restores the scenario's saved radar settings, a centre set now would be overwritten by them.
    [AvaloniaFact]
    public async Task CenterRadar_BeforeTheRadarIsReady_Unavailable()
    {
        MainViewModel viewModel = InScenario();
        viewModel.Radar.MapToggles.Add(MapFive());
        viewModel.Aircraft.Add(new AircraftModel { Callsign = "UAL123", Position = new LatLon(37.7213, -122.2208) });
        double latBefore = viewModel.Radar.CenterLat;

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "center_radar", new { callsign = "UAL123", rangeNm = 15 }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal("The radar is still loading its maps and settings.", result.GetProperty("reason").GetString());
        Assert.Equal(latBefore, viewModel.Radar.CenterLat);
    }

    // A load attempt that ends with no maps leaves nothing to restore over a tool's change. No client test fakes the server, so
    // the real load runs against the view model's never-connected connection: its facility fetch fails, and the load ends
    // through the same exit as the no-video-maps answer.
    [AvaloniaFact]
    public async Task CenterRadar_ArtccWithNoVideoMaps_Available()
    {
        MainViewModel viewModel = InScenario();
        viewModel.Aircraft.Add(new AircraftModel { Callsign = "UAL123", Position = new LatLon(37.7213, -122.2208) });

        await viewModel.Radar.LoadVideoMapsForArtccAsync("ZOA", "OAK", "scenario-1");
        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, new { tool = "center_radar", arguments = new { callsign = "UAL123", rangeNm = 15 } }),
            (ProtocolMethods.CallAppTool, new { tool = "set_video_map", arguments = new { starsId = 5, enabled = true } })
        );

        Assert.True(viewModel.Radar.VideoMapsReady);
        Assert.True(Result(responses[0]).GetProperty("available").GetBoolean(), responses[0].GetRawText());
        Assert.Equal(15, viewModel.Radar.RangeNm, 6);
        Assert.Equal("No video map with STARS id 5 in this scenario.", Result(responses[1]).GetProperty("reason").GetString());
    }

    [AvaloniaFact]
    public async Task SetVideoMap_SetsToTheStateNotAFlip()
    {
        MainViewModel viewModel = RadarReady();
        VideoMapToggleItem toggle = MapFive();
        viewModel.Radar.MapToggles.Add(toggle);
        var on = new { tool = "set_video_map", arguments = new { starsId = 5, enabled = true } };

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, on),
            (ProtocolMethods.CallAppTool, on),
            (ProtocolMethods.CallAppTool, new { tool = "set_video_map", arguments = new { starsId = 99, enabled = true } })
        );

        Assert.True(Result(responses[1]).GetProperty("available").GetBoolean());
        Assert.True(toggle.IsEnabled);
        Assert.False(Result(responses[2]).GetProperty("available").GetBoolean());
        Assert.Contains("99", Result(responses[2]).GetProperty("reason").GetString(), StringComparison.Ordinal);

        Result(await CallTool(ToolsOf(viewModel), "set_video_map", new { starsId = 5, enabled = false }));
        Assert.False(toggle.IsEnabled);
    }

    [AvaloniaFact]
    public async Task SetVideoMap_BeforeMapsLoad_Unavailable()
    {
        JsonElement result = Result(await CallTool(ToolsOf(InScenario()), "set_video_map", new { starsId = 5, enabled = true }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal("The video maps are still loading.", result.GetProperty("reason").GetString());
    }

    // The toggles exist from the moment the facility's map list arrives, before the maps download and the saved settings are
    // restored over them; a map turned on in that window would be undone by the restore.
    [AvaloniaFact]
    public async Task SetVideoMap_BeforeTheDownloadFinishes_Unavailable()
    {
        MainViewModel viewModel = InScenario();
        VideoMapToggleItem toggle = MapFive();
        viewModel.Radar.MapToggles.Add(toggle);

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "set_video_map", new { starsId = 5, enabled = true }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal("The video maps are still loading.", result.GetProperty("reason").GetString());
        Assert.False(toggle.IsEnabled);
    }

    // The tool sends the change without waiting for the room, so the value it reads back is the request; the preference plays no part.
    [AvaloniaFact]
    public async Task SetSolo_ReportsTheRequestNotTheRoom()
    {
        MainViewModel viewModel = InScenario();
        viewModel.Preferences.SetSoloTrainingMode(false);

        JsonElement on = Result(await CallTool(ToolsOf(viewModel), "set_solo", new { enabled = true }));

        Assert.True(viewModel.SessionSoloTrainingMode);
        Assert.Equal(
            "Solo training mode requested on; the room's next settings broadcast confirms or overrides it.",
            on.GetProperty("message").GetString()
        );

        viewModel.Preferences.SetSoloTrainingMode(true);
        JsonElement off = Result(await CallTool(ToolsOf(viewModel), "set_solo", new { enabled = false }));

        Assert.False(viewModel.SessionSoloTrainingMode);
        Assert.Equal(
            "Solo training mode requested off; the room's next settings broadcast confirms or overrides it.",
            off.GetProperty("message").GetString()
        );
    }

    [AvaloniaFact]
    public async Task LoadRecording_MissingFile_InvalidParam()
    {
        MainViewModel viewModel = NewMain();
        viewModel.IsConnected = true;
        string missing = Path.Combine(DiscoveryDirectory, "missing.yaat-recording.zip");

        JsonElement error = Error(await CallTool(ToolsOf(viewModel), "load_recording", new { path = missing }), AutomationErrorCodes.InvalidParam);

        Assert.Equal("path", ErrorParam(error));
        Assert.Contains(missing, Message(error), StringComparison.Ordinal);
    }

    // No client test fakes the server, so both paths end in the same refusal of the connection that never connected: the
    // picker command and the tool give the same status text, from the one load method they share.
    [AvaloniaFact]
    public async Task LoadRecording_NotConnected_SameStatusAsThePicker()
    {
        var picker = new FakeFilePickerService();
        var viewModel = new MainViewModel(picker) { IsConnected = true };
        Directory.CreateDirectory(DiscoveryDirectory);
        string path = Path.Combine(DiscoveryDirectory, "take.yaat-recording.zip");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        picker.QueueOpenFile(path);

        await viewModel.LoadRecordingCommand.ExecuteAsync(null);
        string pickerStatus = viewModel.StatusText;
        viewModel.StatusText = "";
        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "load_recording", new { path }));

        Assert.StartsWith("Load recording error:", pickerStatus, StringComparison.Ordinal);
        Assert.True(result.GetProperty("available").GetBoolean());
        Assert.Equal(pickerStatus, result.GetProperty("message").GetString());
        Assert.Equal(pickerStatus, viewModel.StatusText);
    }

    /// <summary><see cref="RadarReady"/> with two aircraft, UAL123 and AAL9, to measure between.</summary>
    private static MainViewModel RadarReadyWithTwoAircraft()
    {
        MainViewModel viewModel = RadarReady();
        viewModel.Aircraft.Add(new AircraftModel { Callsign = "UAL123", Position = new LatLon(37.7213, -122.2208) });
        viewModel.Aircraft.Add(new AircraftModel { Callsign = "AAL9", Position = new LatLon(37.6188, -122.3750) });
        return viewModel;
    }

    [AvaloniaFact]
    public async Task PlaceRbl_TwoCallsigns_ReturnsTheSlotAndDrawsTheLine()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "place_rbl", new { from = "UAL123", to = "AAL9" }));

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        Assert.Equal("RBL 1 placed: UAL123 to AAL9.", result.GetProperty("message").GetString());
        RangeBearingLine line = Assert.Single(viewModel.Measure.Lines);
        Assert.Equal(1, line.Slot);
        Assert.Equal("UAL123", line.A.Callsign);
        Assert.Equal("AAL9", line.B.Callsign);
        Assert.Equal(RadarViewModel.MeasureView, line.View);
    }

    [AvaloniaFact]
    public async Task PlaceRbl_UnknownEndpoint_IsUnavailableWithTheReason()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "place_rbl", new { from = "UAL123", to = "ZZZ9" }));

        Assert.False(result.GetProperty("available").GetBoolean());
        string reason = result.GetProperty("reason").GetString() ?? "";
        Assert.StartsWith("Unknown", reason, StringComparison.Ordinal);
        Assert.Contains("ZZZ9", reason, StringComparison.Ordinal);
        Assert.Empty(viewModel.Measure.Lines);
    }

    [AvaloniaFact]
    public async Task PlaceRbl_WhenAllSlotsAreUsed_IsUnavailable()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();
        for (int i = 0; i < RangeBearingLineStore.MaxLines; i++)
        {
            Assert.NotNull(viewModel.PlaceMeasurementFromText("UAL123", "AAL9").Slot);
        }

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "place_rbl", new { from = "UAL123", to = "AAL9" }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal(RangeBearingViewState.FullStatus, result.GetProperty("reason").GetString());
        Assert.Equal(RangeBearingLineStore.MaxLines, viewModel.Measure.Lines.Count);
    }

    [AvaloniaFact]
    public async Task RemoveRbl_RemovesOnlyThatSlot()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();
        viewModel.PlaceMeasurementFromText("UAL123", "AAL9");
        viewModel.PlaceMeasurementFromText("AAL9", "UAL123");
        viewModel.PlaceMeasurementFromText("UAL123", "AAL9");

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "remove_rbl", new { slot = 2 }));

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        Assert.Equal("RBL 2 removed.", result.GetProperty("message").GetString());
        Assert.Equal([1, 3], viewModel.Measure.Lines.Select(line => line.Slot));
    }

    [AvaloniaFact]
    public async Task PlaceRbl_ReusesTheLowestFreeSlot()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();
        viewModel.PlaceMeasurementFromText("UAL123", "AAL9");
        viewModel.PlaceMeasurementFromText("AAL9", "UAL123");
        viewModel.PlaceMeasurementFromText("UAL123", "AAL9");
        viewModel.Measure.Remove(2);

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "place_rbl", new { from = "AAL9", to = "UAL123" }));

        Assert.Equal("RBL 2 placed: AAL9 to UAL123.", result.GetProperty("message").GetString());
        Assert.Equal([1, 2, 3], viewModel.Measure.Lines.Select(line => line.Slot));
    }

    [AvaloniaFact]
    public async Task RemoveRbl_EmptySlot_IsUnavailable()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();
        viewModel.PlaceMeasurementFromText("UAL123", "AAL9");

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, new { tool = "remove_rbl", arguments = new { slot = 3 } }),
            (ProtocolMethods.CallAppTool, new { tool = "remove_rbl", arguments = new { slot = 99 } })
        );

        Assert.Equal("No RBL in slot 3.", Result(responses[0]).GetProperty("reason").GetString());
        Assert.Equal("No RBL in slot 99.", Result(responses[1]).GetProperty("reason").GetString());
        Assert.Single(viewModel.Measure.Lines);
    }

    [AvaloniaFact]
    public async Task ClearRbls_RemovesEveryLine()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();
        viewModel.PlaceMeasurementFromText("UAL123", "AAL9");
        viewModel.PlaceMeasurementFromText("AAL9", "UAL123");

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "clear_rbls", new { }));

        Assert.Equal("Cleared 2 RBL(s).", result.GetProperty("message").GetString());
        Assert.Empty(viewModel.Measure.Lines);
    }

    // The command and place_rbl share PlaceMeasurementFromText; the command still writes its outcome to the status line.
    [AvaloniaFact]
    public async Task MeasureCommand_PlacesTheLineAndReportsItInTheStatusLine()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();

        viewModel.CommandText = ".rbl UAL123 AAL9";
        await viewModel.SendCommandCommand.ExecuteAsync(null);
        string placedStatus = viewModel.StatusText;
        viewModel.CommandText = ".rbl UAL123 ZZZ9";
        await viewModel.SendCommandCommand.ExecuteAsync(null);
        string unknownStatus = viewModel.StatusText;

        Assert.Equal(1, Assert.Single(viewModel.Measure.Lines).Slot);
        Assert.Matches(@"^Measurement \d{3}/[\d.]+-1$", placedStatus);
        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "place_rbl", new { from = "UAL123", to = "ZZZ9" }));
        Assert.Equal(unknownStatus, result.GetProperty("reason").GetString());
    }

    // No client test fakes the server, so no test observes the LDR on the wire: this pins the text the tool sends, and
    // SetLeaderDirection_NeverConnected_ReportsNotSent proves the tool reaches the send.
    [Fact]
    public void LeaderDirectionCommand_IsLdrAndTheDigit()
    {
        Assert.Equal("LDR 7", AutomationTools.LeaderDirectionCommand(7));
        Assert.Equal("LDR 5", AutomationTools.LeaderDirectionCommand(5));
    }

    [AvaloniaFact]
    public async Task SetLeaderDirection_NeverConnected_ReportsNotSent()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();
        viewModel.Preferences.SetSyncStudentLeaderDirection(true);

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "set_leader_direction", new { callsign = "ual123", direction = 3 }));

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        string message = result.GetProperty("message").GetString() ?? "";
        Assert.StartsWith("LDR 3 for UAL123 was not sent: Command error:", message, StringComparison.Ordinal);
        Assert.DoesNotContain("preference", message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task SetLeaderDirection_OutOfRange_IsInvalidParam()
    {
        JsonElement[] responses = await SendAll(
            ToolsOf(RadarReadyWithTwoAircraft()),
            (ProtocolMethods.CallAppTool, new { tool = "set_leader_direction", arguments = new { callsign = "UAL123", direction = 0 } }),
            (ProtocolMethods.CallAppTool, new { tool = "set_leader_direction", arguments = new { callsign = "UAL123", direction = 10 } })
        );

        Assert.All(responses, response => Assert.Equal("direction", ErrorParam(Error(response, AutomationErrorCodes.InvalidParam))));
    }

    [AvaloniaFact]
    public async Task SetLeaderDirection_SyncOff_SaysSo()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();
        viewModel.Preferences.SetSyncStudentLeaderDirection(false);

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "set_leader_direction", new { callsign = "UAL123", direction = 9 }));

        Assert.EndsWith(
            " The radar shows it only while the student leader direction sync preference is on; it is off.",
            result.GetProperty("message").GetString(),
            StringComparison.Ordinal
        );
    }

    [AvaloniaFact]
    public void RadarViewModel_SetDataBlockOffset_RaisesTheRepaintEvent()
    {
        MainViewModel viewModel = RadarReady();
        int raised = 0;
        viewModel.Radar.DataBlockState.ManualOffsetsChanged += () => raised++;

        viewModel.Radar.SetDataBlockOffset("UAL123", new SKPoint(12, -8));
        bool removed = viewModel.Radar.ResetDataBlockOffset("UAL123");
        bool removedAgain = viewModel.Radar.ResetDataBlockOffset("UAL123");

        Assert.True(removed);
        Assert.False(removedAgain);
        Assert.Equal(2, raised);
    }

    [AvaloniaFact]
    public async Task SetDatablockOffset_WritesTheOffsetAndRaisesManualOffsetsChanged()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();
        int raised = 0;
        viewModel.Radar.DataBlockState.ManualOffsetsChanged += () => raised++;

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (
                ProtocolMethods.CallAppTool,
                new
                {
                    tool = "set_datablock_offset",
                    arguments = new
                    {
                        callsign = "ual123",
                        dxPx = 40,
                        dyPx = -20,
                    },
                }
            ),
            (
                ProtocolMethods.CallAppTool,
                new
                {
                    tool = "set_datablock_offset",
                    arguments = new
                    {
                        callsign = "DAL1",
                        dxPx = 1,
                        dyPx = 1,
                    },
                }
            )
        );

        Assert.Equal("Data block of UAL123 offset to (40, -20) px.", Result(responses[0]).GetProperty("message").GetString());
        Assert.Equal(new SKPoint(40, -20), viewModel.Radar.DataBlockState.ManualOffsets["UAL123"]);
        Assert.Equal(1, raised);
        Assert.Equal("No aircraft with callsign DAL1 in the client.", Result(responses[1]).GetProperty("reason").GetString());
    }

    [AvaloniaFact]
    public async Task ResetDatablockOffset_RemovesTheOffset()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();
        viewModel.Radar.SetDataBlockOffset("UAL123", new SKPoint(40, -20));
        var reset = new { tool = "reset_datablock_offset", arguments = new { callsign = "UAL123" } };

        JsonElement[] responses = await SendAll(ToolsOf(viewModel), (ProtocolMethods.CallAppTool, reset), (ProtocolMethods.CallAppTool, reset));

        Assert.Equal("Data block of UAL123 reset.", Result(responses[0]).GetProperty("message").GetString());
        Assert.Equal("Data block of UAL123 was already at its default position.", Result(responses[1]).GetProperty("message").GetString());
        Assert.False(viewModel.Radar.DataBlockState.ManualOffsets.ContainsKey("UAL123"));
    }

    /// <summary>
    /// An <see cref="IAutomationState"/> that records each sim rate it is asked to send, and accepts it, or refuses it with
    /// <paramref name="refusal"/> when one is given.
    /// </summary>
    private sealed class RateRecordingState(string? refusal) : IAutomationState
    {
        public List<int> Rates { get; } = [];

        public IReadOnlyList<AircraftModel> Aircraft => [];

        public double ScenarioElapsedSeconds => 0;

        public bool IsPaused => false;

        public int SimRate => 1;

        public long TerminalCursor => 0;

        public IReadOnlyList<TerminalEntry> TerminalEntriesSince(long cursor) => [];

        public Task<AutomationActionOutcome> PauseAsync() => Task.FromResult(new AutomationActionOutcome(true, null));

        public Task<AutomationActionOutcome> SetRateAsync(int rate)
        {
            Rates.Add(rate);
            return Task.FromResult(new AutomationActionOutcome(refusal is null, refusal));
        }
    }
}
