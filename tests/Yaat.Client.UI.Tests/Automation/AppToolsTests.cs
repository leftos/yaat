using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Automation;
using Yaat.Client.Automation.Protocol;
using Yaat.Client.Automation.Tools;
using Yaat.Client.Models;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

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

    /// <summary><see cref="InScenario"/> with the room mid-load, as another member's scenario load leaves it.</summary>
    private static MainViewModel RoomLoading()
    {
        MainViewModel viewModel = InScenario();
        viewModel.OnRoomLoadingChanged("AB");
        Dispatcher.UIThread.RunJobs();
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
                "center_radar_at(lat:double,lon:double,rangeNm:double)",
                "center_radar_on_fix(fix:string,rangeNm:double)",
                "clear_rbls()",
                "connect(url:string)",
                "create_room(artccId:string)",
                "get_framing()",
                "load_recording(path:string)",
                "pause()",
                "place_rbl(from:string,to:string)",
                "play()",
                "prepare_take(path:string,centerLat:double,centerLon:double,rangeNm:double,videoMaps:string,ptlMinutes:double,ptlAll:bool,"
                    + "rbls:string,simRate:int,seekSeconds:double)",
                "remove_rbl(slot:int)",
                "reset_datablock_offset(callsign:string)",
                "seek(simSeconds:double)",
                "set_cloaked(cloaked:bool)",
                "set_datablock_offset(callsign:string,dxPx:int,dyPx:int)",
                "set_leader_direction(callsign:string,direction:int)",
                "set_ptl(lengthMinutes:double,all:bool)",
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

        // connect needs no server: it is how the agent gets one. set_cloaked acts on the client's own windows only.
        string[] needNoRoom = ["connect", "set_cloaked"];
        foreach (JsonElement tool in list.EnumerateArray().Where(tool => !needNoRoom.Contains(tool.GetProperty("name").GetString())))
        {
            Assert.False(tool.GetProperty("available").GetBoolean(), tool.GetRawText());
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("reason").GetString()), tool.GetRawText());
        }

        Assert.True(ToolNamed(list, "connect").GetProperty("available").GetBoolean());
        Assert.Contains("room", ToolNamed(list, "set_sim_rate").GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connected", ToolNamed(list, "create_room").GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
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
    public async Task SetCloaked_CloaksEveryOpenWindow_ThenUncloaks()
    {
        Window main = new() { Title = "YAAT" };
        Window settings = new() { Title = "Settings" };
        List<(WindowBase Window, bool Cloaked)> calls = [];

        await WithWindowsAndCloaker(
            [main, settings],
            (_, _) => null,
            calls,
            async () =>
            {
                // No server and no room: set_cloaked acts on the client's own windows only.
                JsonElement cloak = Result(await CallTool(ToolsOf(NewMain()), "set_cloaked", new { cloaked = true }));
                Assert.True(AutomationGate.CloakWindows);
                Assert.Equal([(main, true), (settings, true)], calls);
                Assert.True(cloak.GetProperty("available").GetBoolean());
                Assert.Equal("Cloaked 2 windows; windows opened from now on open cloaked.", cloak.GetProperty("message").GetString());

                calls.Clear();
                JsonElement uncloak = Result(await CallTool(ToolsOf(NewMain()), "set_cloaked", new { cloaked = false }));
                Assert.False(AutomationGate.CloakWindows);
                Assert.Equal([(main, false), (settings, false)], calls);
                Assert.Equal("Uncloaked 2 windows; windows opened from now on open uncloaked.", uncloak.GetProperty("message").GetString());
            }
        );
    }

    [AvaloniaFact]
    public async Task SetCloaked_OneWindowFails_ReportsThePartialResult()
    {
        Window main = new() { Title = "YAAT" };
        Window settings = new() { Title = "Settings" };
        List<(WindowBase Window, bool Cloaked)> calls = [];

        await WithWindowsAndCloaker(
            [main, settings],
            (window, _) => ReferenceEquals(window, settings) ? "DwmSetWindowAttribute(DWMWA_CLOAK, 1) failed with HRESULT 0x80070005" : null,
            calls,
            async () =>
            {
                JsonElement result = Result(await CallTool(ToolsOf(NewMain()), "set_cloaked", new { cloaked = true }));

                Assert.Equal([(main, true), (settings, true)], calls);
                Assert.True(AutomationGate.CloakWindows);
                Assert.True(result.GetProperty("available").GetBoolean());
                Assert.Equal(
                    "Cloaked 1 of 2 windows; could not cloak 'Settings': DwmSetWindowAttribute(DWMWA_CLOAK, 1) failed with HRESULT 0x80070005. "
                        + "Windows opened from now on open cloaked.",
                    result.GetProperty("message").GetString()
                );
            }
        );
    }

    [AvaloniaFact]
    public async Task SetCloaked_EveryWindowFails_KeepsThePreviousState()
    {
        Window main = new() { Title = "YAAT" };
        Window settings = new() { Title = "Settings" };
        List<(WindowBase Window, bool Cloaked)> calls = [];

        await WithWindowsAndCloaker(
            [main, settings],
            (_, _) => "DwmSetWindowAttribute(DWMWA_CLOAK, 1) failed with HRESULT 0x80070005",
            calls,
            async () =>
            {
                AutomationGate.CloakWindows = false;

                JsonElement result = Result(await CallTool(ToolsOf(NewMain()), "set_cloaked", new { cloaked = true }));

                Assert.Equal([(main, true), (settings, true)], calls);
                Assert.False(AutomationGate.CloakWindows);
                Assert.True(result.GetProperty("available").GetBoolean());
                Assert.Equal(
                    "Could not cloak any of the 2 windows: 'YAAT': DwmSetWindowAttribute(DWMWA_CLOAK, 1) failed with HRESULT 0x80070005; "
                        + "'Settings': DwmSetWindowAttribute(DWMWA_CLOAK, 1) failed with HRESULT 0x80070005. "
                        + "Nothing changed: windows opened from now on still open uncloaked.",
                    result.GetProperty("message").GetString()
                );
            }
        );
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

        // The preference is saved to the run's shared preferences file, and every later MainViewModel seeds its session solo
        // mode from it, so the test puts it back however it ends.
        viewModel.Preferences.SetSoloTrainingMode(true);
        try
        {
            JsonElement off = Result(await CallTool(ToolsOf(viewModel), "set_solo", new { enabled = false }));

            Assert.False(viewModel.SessionSoloTrainingMode);
            Assert.Equal(
                "Solo training mode requested off; the room's next settings broadcast confirms or overrides it.",
                off.GetProperty("message").GetString()
            );
        }
        finally
        {
            viewModel.Preferences.SetSoloTrainingMode(false);
        }
    }

    [AvaloniaFact]
    public async Task LoadRecording_MissingFile_InvalidParam()
    {
        MainViewModel viewModel = NewMain();
        viewModel.IsConnected = true;
        viewModel.ActiveRoomId = "room-1";
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
        var viewModel = new MainViewModel(picker) { IsConnected = true, ActiveRoomId = "room-1" };
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

    // Out of a room the server answers a recording load with "Not in a room", so the tool says so before it sends anything.
    [AvaloniaFact]
    public async Task LoadRecording_OutOfRoom_IsUnavailable_NotInRoom()
    {
        var viewModel = new MainViewModel(new FakeFilePickerService()) { IsConnected = true };
        Directory.CreateDirectory(DiscoveryDirectory);
        string path = Path.Combine(DiscoveryDirectory, "take.yaat-recording.zip");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "load_recording", new { path }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal("Not in a room: connect, then create or join one.", result.GetProperty("reason").GetString());
    }

    // The server refuses a load while a scenario load is in flight, so the tool says so before it sends anything.
    [AvaloniaFact]
    public async Task LoadRecording_WhileRoomLoading_IsUnavailable()
    {
        MainViewModel viewModel = RoomLoading();

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "load_recording", new { path = "take.yaat-recording.zip" }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal("A scenario is loading in the room.", result.GetProperty("reason").GetString());
    }

    [AvaloniaFact]
    public async Task CreateRoom_WhenInRoom_IsUnavailable()
    {
        JsonElement result = Result(await CallTool(ToolsOf(InScenario()), "create_room", new { artccId = "ZOA" }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal("Already in a room; leave it first.", result.GetProperty("reason").GetString());
    }

    [AvaloniaFact]
    public async Task CreateRoom_RejectsBadArtccId()
    {
        MainViewModel viewModel = NewMain();
        viewModel.IsConnected = true;

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, new { tool = "create_room", arguments = new { artccId = "ZO1" } }),
            (ProtocolMethods.CallAppTool, new { tool = "create_room", arguments = new { artccId = "ZOAK" } }),
            (ProtocolMethods.CallAppTool, new { tool = "create_room", arguments = new { artccId = "" } })
        );

        Assert.All(responses, response => Assert.Equal("artccId", ErrorParam(Error(response, AutomationErrorCodes.InvalidParam))));
        Assert.False(viewModel.IsInRoom);
    }

    [AvaloniaFact]
    public async Task Connect_WhenConnected_IsUnavailable()
    {
        MainViewModel viewModel = NewMain();
        viewModel.IsConnected = true;

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "connect", new { url = "http://localhost:5130" }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.EndsWith("; disconnect first.", result.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Already connected to ", result.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Connect_RejectsNonHttpUrl()
    {
        MainViewModel viewModel = NewMain();

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, new { tool = "connect", arguments = new { url = "ftp://localhost:5130" } }),
            (ProtocolMethods.CallAppTool, new { tool = "connect", arguments = new { url = "localhost:5130" } }),
            (ProtocolMethods.CallAppTool, new { tool = "connect", arguments = new { url = "not a url" } })
        );

        Assert.All(responses, response => Assert.Equal("url", ErrorParam(Error(response, AutomationErrorCodes.InvalidParam))));
        Assert.False(viewModel.IsConnected);
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

    [AvaloniaFact]
    public async Task ClearRbls_KeepsTheGroundViewsLines()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();
        viewModel.PlaceMeasurementFromText("UAL123", "AAL9");
        PlaceGroundLine(viewModel);
        viewModel.PlaceMeasurementFromText("AAL9", "UAL123");

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "clear_rbls", new { }));

        Assert.Equal("Cleared 2 RBL(s).", result.GetProperty("message").GetString());
        RangeBearingLine ground = Assert.Single(viewModel.Measure.Lines);
        Assert.Equal(RblView.Ground, ground.View);
        Assert.Equal(2, ground.Slot);
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

    [AvaloniaFact]
    public async Task CenterRadarAt_SetsCentreAndRange()
    {
        MainViewModel viewModel = RadarReady();

        JsonElement result = Result(
            await CallTool(
                ToolsOf(viewModel),
                "center_radar_at",
                new
                {
                    lat = 37.5,
                    lon = -122.25,
                    rangeNm = 30,
                }
            )
        );

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        Assert.Equal("Primary radar centred on (37.5000, -122.2500) at 30 nm.", result.GetProperty("message").GetString());
        Assert.Equal(37.5, viewModel.Radar.CenterLat, 6);
        Assert.Equal(-122.25, viewModel.Radar.CenterLon, 6);
        Assert.Equal(30, viewModel.Radar.RangeNm, 6);
    }

    [AvaloniaFact]
    public async Task CenterRadarAt_RejectsOutOfRangeLat()
    {
        MainViewModel viewModel = RadarReady();
        double latBefore = viewModel.Radar.CenterLat;

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (
                ProtocolMethods.CallAppTool,
                new
                {
                    tool = "center_radar_at",
                    arguments = new
                    {
                        lat = 90.5,
                        lon = -122.25,
                        rangeNm = 30,
                    },
                }
            ),
            (
                ProtocolMethods.CallAppTool,
                new
                {
                    tool = "center_radar_at",
                    arguments = new
                    {
                        lat = -91,
                        lon = -122.25,
                        rangeNm = 30,
                    },
                }
            ),
            (
                ProtocolMethods.CallAppTool,
                new
                {
                    tool = "center_radar_at",
                    arguments = new
                    {
                        lat = 37.5,
                        lon = 180.5,
                        rangeNm = 30,
                    },
                }
            ),
            (
                ProtocolMethods.CallAppTool,
                new
                {
                    tool = "center_radar_at",
                    arguments = new
                    {
                        lat = 37.5,
                        lon = -122.25,
                        rangeNm = 300,
                    },
                }
            )
        );

        string[] culprits = [.. responses.Select(response => ErrorParam(Error(response, AutomationErrorCodes.InvalidParam)))];
        Assert.Equal(["lat", "lat", "lon", "rangeNm"], culprits);
        Assert.Equal(latBefore, viewModel.Radar.CenterLat);
    }

    /// <summary><see cref="RadarReady"/> with the real navigation data installed and the client told it is loaded.</summary>
    private static MainViewModel RadarReadyWithNavData()
    {
        TestVnasData.EnsureInitialized();
        Assert.NotNull(TestVnasData.NavigationDb);
        MainViewModel viewModel = RadarReady();
        viewModel.MarkNavDbReady();
        return viewModel;
    }

    [AvaloniaFact]
    public async Task CenterRadarOnFix_UnknownFix_IsUnavailable()
    {
        MainViewModel viewModel = RadarReadyWithNavData();
        double latBefore = viewModel.Radar.CenterLat;

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "center_radar_on_fix", new { fix = "ZZZZQ", rangeNm = 20 }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal("No fix or airport named ZZZZQ in the navigation data.", result.GetProperty("reason").GetString());
        Assert.Equal(latBefore, viewModel.Radar.CenterLat);
    }

    [AvaloniaFact]
    public async Task CenterRadarOnFix_KnownFix_Centres()
    {
        MainViewModel viewModel = RadarReadyWithNavData();
        (double Lat, double Lon)? oak = NavigationDatabase.Instance.GetFixPosition("OAK");
        Assert.NotNull(oak);

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "center_radar_on_fix", new { fix = "oak", rangeNm = 20 }));

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        Assert.Equal(oak.Value.Lat, viewModel.Radar.CenterLat, 6);
        Assert.Equal(oak.Value.Lon, viewModel.Radar.CenterLon, 6);
        Assert.Equal(20, viewModel.Radar.RangeNm, 6);
        Assert.StartsWith("Primary radar centred on OAK (", result.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task SetPtl_SetsLengthAndAll()
    {
        MainViewModel viewModel = RadarReady();
        viewModel.Radar.PtlOwn = true;

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, new { tool = "set_ptl", arguments = new { lengthMinutes = 1.5, all = true } }),
            (ProtocolMethods.CallAppTool, new { tool = "set_ptl", arguments = new { lengthMinutes = 3.0, all = false } })
        );

        Assert.Equal("PTL 1.5 min, all tracks on.", Result(responses[0]).GetProperty("message").GetString());
        Assert.Equal("PTL 3.0 min, all tracks off.", Result(responses[1]).GetProperty("message").GetString());
        Assert.Equal(3.0, viewModel.Radar.PtlLengthMinutes);
        Assert.False(viewModel.Radar.PtlAll);
        Assert.True(viewModel.Radar.PtlOwn);
    }

    [AvaloniaFact]
    public async Task SetPtl_RejectsOffStepLength()
    {
        MainViewModel viewModel = RadarReady();
        double lengthBefore = viewModel.Radar.PtlLengthMinutes;

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, new { tool = "set_ptl", arguments = new { lengthMinutes = 0.75, all = true } }),
            (ProtocolMethods.CallAppTool, new { tool = "set_ptl", arguments = new { lengthMinutes = 3.5, all = true } }),
            (ProtocolMethods.CallAppTool, new { tool = "set_ptl", arguments = new { lengthMinutes = 0, all = true } })
        );

        Assert.All(responses, response => Assert.Equal("lengthMinutes", ErrorParam(Error(response, AutomationErrorCodes.InvalidParam))));
        Assert.Equal(lengthBefore, viewModel.Radar.PtlLengthMinutes);
        Assert.False(viewModel.Radar.PtlAll);
    }

    [AvaloniaFact]
    public async Task GetFraming_ReportsCentreRangeMapsPtlAndRbls()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();
        RadarViewModel radar = viewModel.Radar;
        VideoMapToggleItem five = MapFive();
        five.IsEnabled = true;
        radar.MapToggles.Add(five);
        radar.MapToggles.Add(
            new VideoMapToggleItem
            {
                MapId = "map-2",
                ShortName = "OAK",
                Name = "Oakland",
                BrightnessCategory = "A",
                StarsId = 2,
                IsEnabled = true,
            }
        );
        radar.MapToggles.Add(
            new VideoMapToggleItem
            {
                MapId = "map-9",
                ShortName = "SFO",
                Name = "San Francisco",
                BrightnessCategory = "B",
                StarsId = 9,
            }
        );
        radar.CenterLat = 37.7;
        radar.CenterLon = -122.2;
        radar.RangeNm = 25;
        radar.PtlLengthMinutes = 2.0;
        radar.PtlAll = true;
        radar.PtlOwn = false;
        viewModel.PlaceMeasurementFromText("UAL123", "AAL9");
        viewModel.PlaceMeasurementFromText("AAL9", "UAL123");
        viewModel.PlaceMeasurementFromText("UAL123", "AAL9");
        viewModel.Measure.Remove(1);
        PlaceGroundLine(viewModel);
        viewModel.SimRate = 4;
        viewModel.IsPaused = true;
        viewModel.ScenarioElapsedSeconds = 123.5;

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "get_framing", new { }));

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        using var framing = JsonDocument.Parse(result.GetProperty("message").GetString() ?? "");
        JsonElement root = framing.RootElement;
        Assert.Equal(
            ["centerLat", "centerLon", "rangeNm", "videoMaps", "ptl", "rbls", "simRate", "paused", "simSeconds"],
            root.EnumerateObject().Select(property => property.Name)
        );
        Assert.Equal(37.7, root.GetProperty("centerLat").GetDouble(), 6);
        Assert.Equal(-122.2, root.GetProperty("centerLon").GetDouble(), 6);
        Assert.Equal(25, root.GetProperty("rangeNm").GetDouble(), 6);
        JsonElement[] maps = [.. root.GetProperty("videoMaps").EnumerateArray()];
        Assert.Equal([2, 5], maps.Select(map => map.GetProperty("starsId").GetInt32()));
        Assert.Equal(["Oakland", "Center map"], maps.Select(map => map.GetProperty("name").GetString()));
        Assert.Equal(["starsId", "name"], maps[0].EnumerateObject().Select(property => property.Name));
        JsonElement ptl = root.GetProperty("ptl");
        Assert.Equal(["lengthMinutes", "all", "own"], ptl.EnumerateObject().Select(property => property.Name));
        Assert.Equal(2.0, ptl.GetProperty("lengthMinutes").GetDouble());
        Assert.True(ptl.GetProperty("all").GetBoolean());
        Assert.False(ptl.GetProperty("own").GetBoolean());
        Assert.Equal([2, 3], root.GetProperty("rbls").EnumerateArray().Select(slot => slot.GetInt32()));
        Assert.Equal(4, root.GetProperty("simRate").GetInt32());
        Assert.True(root.GetProperty("paused").GetBoolean());
        Assert.Equal(123.5, root.GetProperty("simSeconds").GetDouble(), 6);
    }

    [AvaloniaFact]
    public async Task Seek_RejectsNegative()
    {
        JsonElement error = Error(await CallTool(ToolsOf(InScenario()), "seek", new { simSeconds = -1 }), AutomationErrorCodes.InvalidParam);

        Assert.Equal("simSeconds", ErrorParam(error));
    }

    // The server refuses a rewind while a scenario load is in flight, so the tool says so before it sends anything.
    [AvaloniaFact]
    public async Task Seek_WhileRoomLoading_IsUnavailable()
    {
        MainViewModel viewModel = RoomLoading();

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "seek", new { simSeconds = 60 }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal("A scenario is loading in the room.", result.GetProperty("reason").GetString());
    }

    /// <summary>A recording file's path in the test's directory; prepare_take checks only that it exists before its other arguments.</summary>
    private async Task<string> TakeFile()
    {
        Directory.CreateDirectory(DiscoveryDirectory);
        string path = Path.Combine(DiscoveryDirectory, "take.yaat-recording.zip");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        return path;
    }

    /// <summary>A prepare_take call whose arguments are all valid except the ones <paramref name="change"/> overrides.</summary>
    private static object TakeCall(string path, Func<Dictionary<string, object>, Dictionary<string, object>> change) =>
        new
        {
            tool = "prepare_take",
            arguments = change(
                new Dictionary<string, object>
                {
                    ["path"] = path,
                    ["centerLat"] = 37.7,
                    ["centerLon"] = -122.2,
                    ["rangeNm"] = 30,
                    ["videoMaps"] = "5, 12",
                    ["ptlMinutes"] = 1.0,
                    ["ptlAll"] = true,
                    ["rbls"] = "UAL123,AAL9; OAK,SFO",
                    ["simRate"] = 2,
                    ["seekSeconds"] = 60,
                }
            ),
        };

    private static Func<Dictionary<string, object>, Dictionary<string, object>> With(string name, object value) =>
        arguments =>
        {
            arguments[name] = value;
            return arguments;
        };

    [AvaloniaFact]
    public async Task PrepareTake_RejectsBadVideoMapList()
    {
        MainViewModel viewModel = InScenario();
        string path = await TakeFile();

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, TakeCall(path, With("videoMaps", "5,abc"))),
            (ProtocolMethods.CallAppTool, TakeCall(path, With("videoMaps", "5;12"))),
            (ProtocolMethods.CallAppTool, TakeCall(path, With("videoMaps", "-5")))
        );

        Assert.All(responses, response => Assert.Equal("videoMaps", ErrorParam(Error(response, AutomationErrorCodes.InvalidParam))));
        // Every argument is checked before the load, so no load was attempted.
        Assert.DoesNotContain("recording", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task PrepareTake_RejectsBadRblPair()
    {
        MainViewModel viewModel = InScenario();
        string path = await TakeFile();

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, TakeCall(path, With("rbls", "UAL123"))),
            (ProtocolMethods.CallAppTool, TakeCall(path, With("rbls", "UAL123,AAL9,OAK"))),
            (ProtocolMethods.CallAppTool, TakeCall(path, With("rbls", "UAL123,")))
        );

        Assert.All(responses, response => Assert.Equal("rbls", ErrorParam(Error(response, AutomationErrorCodes.InvalidParam))));
        // Every argument is checked before the load, so no load was attempted.
        Assert.DoesNotContain("recording", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task PrepareTake_MissingFile_IsArgumentError()
    {
        string missing = Path.Combine(DiscoveryDirectory, "missing.yaat-recording.zip");

        JsonElement error = Error(
            (await SendAll(ToolsOf(InScenario()), (ProtocolMethods.CallAppTool, TakeCall(missing, arguments => arguments))))[0],
            AutomationErrorCodes.InvalidParam
        );

        Assert.Equal("path", ErrorParam(error));
        Assert.Contains(missing, Message(error), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task PrepareTake_RejectsEachBadArgument()
    {
        MainViewModel viewModel = InScenario();
        string path = await TakeFile();
        string sixteenRbls = string.Join(";", Enumerable.Repeat("UAL123,AAL9", RangeBearingLineStore.MaxLines + 1));

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, TakeCall(path, With("simRate", 0))),
            (ProtocolMethods.CallAppTool, TakeCall(path, With("seekSeconds", -1))),
            (ProtocolMethods.CallAppTool, TakeCall(path, With("ptlMinutes", 0.75))),
            (ProtocolMethods.CallAppTool, TakeCall(path, With("rbls", sixteenRbls))),
            (ProtocolMethods.CallAppTool, TakeCall(path, With("centerLat", 91))),
            (ProtocolMethods.CallAppTool, TakeCall(path, With("centerLon", -181))),
            (ProtocolMethods.CallAppTool, TakeCall(path, With("videoMaps", "0")))
        );

        string[] culprits = [.. responses.Select(response => ErrorParam(Error(response, AutomationErrorCodes.InvalidParam)))];
        Assert.Equal(["simRate", "seekSeconds", "ptlMinutes", "rbls", "centerLat", "centerLon", "videoMaps"], culprits);
        Assert.DoesNotContain("recording", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    // With no server behind the view model the load is refused, so an accepted call ends at the load step: a Done naming it.
    [AvaloniaFact]
    public async Task PrepareTake_PtlZeroAccepted_StopsAtTheLoadWithItsStatus()
    {
        string path = await TakeFile();

        JsonElement result = Result((await SendAll(ToolsOf(InScenario()), (ProtocolMethods.CallAppTool, TakeCall(path, With("ptlMinutes", 0)))))[0]);

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        Assert.StartsWith(
            "prepare_take stopped at 'load the recording': Load recording error:",
            result.GetProperty("message").GetString(),
            StringComparison.Ordinal
        );
    }

    // A non-mentor may not load a scenario, so the room would refuse the load; the tool says so before it sends anything.
    [AvaloniaFact]
    public async Task PrepareTake_NonMentor_IsUnavailable()
    {
        MainViewModel viewModel = InScenario();
        viewModel.IsNonMentor = true;
        string path = await TakeFile();

        JsonElement result = Result((await SendAll(ToolsOf(viewModel), (ProtocolMethods.CallAppTool, TakeCall(path, arguments => arguments))))[0]);

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal(
            "Signed in without mentor or instructor rights: only a mentor or instructor can load a recording.",
            result.GetProperty("reason").GetString()
        );
    }

    /// <summary><see cref="RadarReadyWithTwoAircraft"/> with maps 2 and 9 on and map 5 off.</summary>
    private static MainViewModel FramingReady()
    {
        MainViewModel viewModel = RadarReadyWithTwoAircraft();
        RadarViewModel radar = viewModel.Radar;
        radar.MapToggles.Add(MapFive());
        foreach (int starsId in new[] { 2, 9 })
        {
            radar.MapToggles.Add(
                new VideoMapToggleItem
                {
                    MapId = $"map-{starsId}",
                    ShortName = $"M{starsId}",
                    Name = $"Map {starsId}",
                    BrightnessCategory = "A",
                    StarsId = starsId,
                    IsEnabled = true,
                }
            );
        }

        return viewModel;
    }

    /// <summary>Places a Ground-view RBL in the shared store, as a measurement drawn on the Ground view does.</summary>
    private static void PlaceGroundLine(MainViewModel viewModel) =>
        Assert.NotNull(
            viewModel.Measure.Place(
                RblEndpoint.OnAircraft("UAL123"),
                RblEndpoint.OnAircraft("AAL9"),
                RblView.Ground,
                viewModel.Radar.MeasureTrackLookup,
                RadarViewModel.MeasureUnits
            )
        );

    private static TakeFraming Framing(IReadOnlySet<int> maps, double ptlMinutes, bool ptlAll, IReadOnlyList<(string From, string To)> rbls) =>
        new(CenterLat: 37.5, CenterLon: -122.25, RangeNm: 35, VideoMaps: maps, PtlMinutes: ptlMinutes, PtlAll: ptlAll, Rbls: rbls);

    private static AutomationTools RealTools(MainViewModel viewModel) => new(viewModel, new MainViewModelAutomationState(viewModel));

    [AvaloniaFact]
    public void FrameTake_SetsExactlyTheGivenMapsCentrePtlAndPrimaryRadarRbls()
    {
        MainViewModel viewModel = FramingReady();
        viewModel.PlaceMeasurementFromText("AAL9", "UAL123");
        viewModel.PlaceMeasurementFromText("AAL9", "UAL123");
        PlaceGroundLine(viewModel);

        string? stopped = RealTools(viewModel).FrameTake(Framing(new HashSet<int> { 5 }, 1.5, true, [("UAL123", "AAL9")]));

        Assert.Null(stopped);
        RadarViewModel radar = viewModel.Radar;
        Assert.Equal([5], radar.MapToggles.Where(toggle => toggle.IsEnabled).Select(toggle => toggle.StarsId));
        Assert.Equal(37.5, radar.CenterLat, 6);
        Assert.Equal(-122.25, radar.CenterLon, 6);
        Assert.Equal(35, radar.RangeNm, 6);
        Assert.Equal(1.5, radar.PtlLengthMinutes);
        Assert.True(radar.PtlAll);
        RangeBearingLine ground = Assert.Single(viewModel.Measure.Lines, line => line.View == RblView.Ground);
        Assert.Equal(3, ground.Slot);
        RangeBearingLine placed = Assert.Single(viewModel.Measure.Lines, line => line.View == RadarViewModel.MeasureView);
        Assert.Equal(("UAL123", "AAL9"), (placed.A.Callsign, placed.B.Callsign));
    }

    [AvaloniaFact]
    public void FrameTake_UnknownStarsId_StopsBeforeAnyToggleChanges()
    {
        MainViewModel viewModel = FramingReady();
        double latBefore = viewModel.Radar.CenterLat;

        string? stopped = RealTools(viewModel).FrameTake(Framing(new HashSet<int> { 5, 77 }, 1.0, true, []));

        Assert.Equal("prepare_take stopped at 'set the video maps': no video map with STARS id 77 in this scenario.", stopped);
        Assert.Equal([2, 9], viewModel.Radar.MapToggles.Where(toggle => toggle.IsEnabled).Select(toggle => toggle.StarsId).Order());
        Assert.Equal(latBefore, viewModel.Radar.CenterLat);
    }

    [AvaloniaFact]
    public void FrameTake_PtlZero_TurnsAllTracksOffAndKeepsTheLength()
    {
        MainViewModel viewModel = FramingReady();
        viewModel.Radar.PtlLengthMinutes = 2.0;
        viewModel.Radar.PtlAll = true;

        string? stopped = RealTools(viewModel).FrameTake(Framing(new HashSet<int>(), 0, true, []));

        Assert.Null(stopped);
        Assert.False(viewModel.Radar.PtlAll);
        Assert.Equal(2.0, viewModel.Radar.PtlLengthMinutes);
        Assert.DoesNotContain(viewModel.Radar.MapToggles, toggle => toggle.IsEnabled);
    }

    [AvaloniaFact]
    public void FrameTake_RblFailure_NamesItsStep()
    {
        MainViewModel viewModel = FramingReady();

        string? stopped = RealTools(viewModel).FrameTake(Framing(new HashSet<int> { 2 }, 1.0, false, [("UAL123", "AAL9"), ("UAL123", "ZZZ9")]));

        Assert.StartsWith("prepare_take stopped at 'place the RBL UAL123,ZZZ9': Unknown", stopped, StringComparison.Ordinal);
        Assert.Single(viewModel.Measure.Lines);
    }

    [AvaloniaFact]
    public async Task PlayAndPause_SendUnpauseAndPause()
    {
        MainViewModel viewModel = InScenario();
        var state = new RateRecordingState(null);

        JsonElement[] responses = await SendAll(
            () => new AutomationTools(viewModel, state),
            (ProtocolMethods.CallAppTool, new { tool = "play", arguments = new { } }),
            (ProtocolMethods.CallAppTool, new { tool = "pause", arguments = new { } })
        );

        Assert.Equal(["unpause", "pause"], state.Calls);
        Assert.Equal("Sim resumed.", Result(responses[0]).GetProperty("message").GetString());
        Assert.Equal("Sim paused.", Result(responses[1]).GetProperty("message").GetString());
    }

    [AvaloniaFact]
    public async Task Connect_WhileConnecting_IsUnavailable()
    {
        MainViewModel viewModel = NewMain();
        viewModel.IsConnecting = true;

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "connect", new { url = "http://localhost:5130" }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal("A connection attempt is already in progress.", result.GetProperty("reason").GetString());
    }

    [AvaloniaFact]
    public async Task CreateRoom_NonMentor_IsUnavailable()
    {
        MainViewModel viewModel = NewMain();
        viewModel.IsConnected = true;
        viewModel.IsNonMentor = true;

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "create_room", new { artccId = "ZOA" }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.StartsWith("Signed in without mentor or instructor rights", result.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    // No server is behind the view model, so the create itself is refused; the answer shows the lower-case id was accepted.
    [AvaloniaFact]
    public async Task CreateRoom_LowerCaseArtcc_IsAccepted()
    {
        MainViewModel viewModel = NewMain();
        viewModel.IsConnected = true;
        viewModel.PermittedArtccs.Add("ZOA");

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "create_room", new { artccId = "zoa" }));

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        Assert.StartsWith("Could not create a ZOA room: Create room error:", result.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("ZOA", viewModel.SelectedCreateArtccId);
    }

    [AvaloniaFact]
    public async Task CreateRoom_ArtccNotPermitted_IsInvalidParam()
    {
        MainViewModel viewModel = NewMain();
        viewModel.IsConnected = true;
        viewModel.PermittedArtccs.Add("ZOA");
        viewModel.PermittedArtccs.Add("ZLA");
        viewModel.SelectedCreateArtccId = "ZOA";

        JsonElement error = Error(await CallTool(ToolsOf(viewModel), "create_room", new { artccId = "ZNY" }), AutomationErrorCodes.InvalidParam);

        Assert.Equal("artccId", ErrorParam(error));
        Assert.Contains("ZOA, ZLA", Message(error), StringComparison.Ordinal);
        Assert.Equal("ZOA", viewModel.SelectedCreateArtccId);
    }

    /// <summary>Runs the <c>prepare_take</c> steps after the load on <paramref name="viewModel"/> and its stub state.</summary>
    private static Task<string?> RunTakeSteps(MainViewModel viewModel, IAutomationState state, double seekSeconds, TimeSpan videoMapWait) =>
        new AutomationTools(viewModel, state).RunTakeStepsAsync(Framing(new HashSet<int> { 2 }, 1.0, false, []), 2, seekSeconds, videoMapWait);

    [AvaloniaFact]
    public async Task TakeSteps_AlreadyPaused_SkipsThePause()
    {
        MainViewModel viewModel = FramingReady();
        var state = new RateRecordingState(null) { IsPaused = true };

        string? stopped = await RunTakeSteps(viewModel, state, 0, TimeSpan.FromSeconds(30));

        Assert.Null(stopped);
        Assert.Empty(state.Calls);
        Assert.Equal([2], state.Rates);
    }

    // Standing in for a load whose maps never arrive, with a wait short enough for a test.
    [AvaloniaFact]
    public async Task TakeSteps_VideoMapsNotReady_StopsAtTheWait()
    {
        var state = new RateRecordingState(null);

        string? stopped = await RunTakeSteps(InScenario(), state, 0, TimeSpan.FromMilliseconds(50));

        Assert.Equal("prepare_take stopped at 'wait for the video maps': still loading after 0.05 s.", stopped);
    }

    [AvaloniaFact]
    public async Task TakeSteps_RewindFails_StopsAtSeek()
    {
        MainViewModel viewModel = FramingReady();
        var state = new RateRecordingState(null);

        string? stopped = await RunTakeSteps(viewModel, state, 60, TimeSpan.FromSeconds(30));

        Assert.Equal("prepare_take stopped at 'seek': Rewind error: Not connected.", stopped);
        Assert.Empty(state.Calls);
    }

    [AvaloniaFact]
    public async Task TakeSteps_PauseRefused_StopsAtPause()
    {
        MainViewModel viewModel = FramingReady();
        var state = new RateRecordingState(null) { PauseRefusal = "The room did not accept the pause." };

        string? stopped = await RunTakeSteps(viewModel, state, 0, TimeSpan.FromSeconds(30));

        Assert.Equal("prepare_take stopped at 'pause': The room did not accept the pause.", stopped);
        Assert.Equal(["pause"], state.Calls);
        Assert.Empty(state.Rates);
    }

    [AvaloniaFact]
    public async Task TakeSteps_RateRefused_StopsAtTheRate()
    {
        MainViewModel viewModel = FramingReady();
        var state = new RateRecordingState("Invalid sim rate");

        string? stopped = await RunTakeSteps(viewModel, state, 0, TimeSpan.FromSeconds(30));

        Assert.Equal("prepare_take stopped at 'set the sim rate': Invalid sim rate", stopped);
        Assert.Equal(["pause"], state.Calls);
        Assert.Equal([2], state.Rates);
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

        public bool IsPaused { get; set; }

        public int SimRate => 1;

        public long TerminalCursor => 0;

        public IReadOnlyList<TerminalEntry> TerminalEntriesSince(long cursor) => [];

        /// <summary>Each pause and unpause asked for, in order: "pause" or "unpause".</summary>
        public List<string> Calls { get; } = [];

        /// <summary>The reason <see cref="PauseAsync"/> refuses with, or null to accept the pause.</summary>
        public string? PauseRefusal { get; init; }

        public Task<AutomationActionOutcome> PauseAsync()
        {
            Calls.Add("pause");
            return Task.FromResult(new AutomationActionOutcome(PauseRefusal is null, PauseRefusal));
        }

        public Task<AutomationActionOutcome> UnpauseAsync()
        {
            Calls.Add("unpause");
            return Task.FromResult(new AutomationActionOutcome(true, null));
        }

        public Task<AutomationActionOutcome> SetRateAsync(int rate)
        {
            Rates.Add(rate);
            return Task.FromResult(new AutomationActionOutcome(refusal is null, refusal));
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> with <c>set_cloaked</c>'s window source answering <paramref name="windows"/> (the headless session
    /// has no desktop lifetime, and Avalonia's lifetime interfaces cannot be implemented outside it) and the cloaker seam recording each
    /// call into <paramref name="calls"/> and answering <paramref name="cloaker"/>; restores both seams and the cloak flag after.
    /// </summary>
    private static async Task WithWindowsAndCloaker(
        IReadOnlyList<Window> windows,
        Func<WindowBase, bool, string?> cloaker,
        List<(WindowBase Window, bool Cloaked)> calls,
        Func<Task> body
    )
    {
        Func<IReadOnlyList<Window>> previousWindows = AutomationTools.OpenWindows;
        Func<WindowBase, bool, string?> previousCloaker = AutomationGate.Cloaker;
        bool previousCloak = AutomationGate.CloakWindows;
        AutomationTools.OpenWindows = () => windows;
        AutomationGate.Cloaker = (window, cloaked) =>
        {
            calls.Add((window, cloaked));
            return cloaker(window, cloaked);
        };
        try
        {
            await body();
        }
        finally
        {
            AutomationTools.OpenWindows = previousWindows;
            AutomationGate.Cloaker = previousCloaker;
            AutomationGate.CloakWindows = previousCloak;
            foreach (Window window in windows)
            {
                window.Close();
            }
        }
    }
}
