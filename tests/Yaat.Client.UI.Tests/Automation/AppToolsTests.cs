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
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Client.Views.Scope.Profiles;
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

    /// <summary>The centre of the facility area <see cref="ScopeReady"/> loads, where the Scope seeds.</summary>
    private const double AreaLat = 37.62;

    private const double AreaLon = -122.38;

    /// <summary>
    /// <see cref="InScenario"/> with the Scope's map source loaded with maps 2 (Oakland), 5 (Center map) and 9 (San Francisco),
    /// every one off, as after a finished load: the primary Scope is seeded at the area's centre and 40 NM.
    /// </summary>
    private static MainViewModel ScopeReady()
    {
        MainViewModel viewModel = InScenario();
        LoadScopeMaps(viewModel);
        return viewModel;
    }

    /// <summary>Runs one load of the Scope's map source to its end, with the maps <see cref="ScopeReady"/> names.</summary>
    private static void LoadScopeMaps(MainViewModel viewModel) =>
        viewModel.ScopeMaps.ApplyVideoMapsDtoForTesting(
            new FacilityVideoMapsDto(
                "ZOA",
                "NCT",
                [new StarsAreaDto("A", "Area A", AreaLat, AreaLon, 60, [])],
                [MapInfo(2, "Oakland", "OAK"), MapInfo(5, "Center map", "CENTER"), MapInfo(9, "San Francisco", "SFO")],
                []
            ),
            null,
            null
        );

    private static VideoMapInfoDto MapInfo(int starsId, string name, string shortName) =>
        new($"map-{starsId}", name, shortName, [], "A", starsId, AlwaysVisible: false, TdmOnly: false);

    /// <summary>The STARS ids of the primary Scope's maps that are on, ascending.</summary>
    private static int[] ScopeMapsOn(MainViewModel viewModel) =>
        [.. viewModel.PrimaryScope.MapToggles.Where(toggle => toggle.IsOn).Select(toggle => toggle.StarsId).Order()];

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
    public async Task SendCommand_NoAircraftMatched_AnswersNotSentWithTheStatusLine()
    {
        JsonElement result = Result(await CallTool(ToolsOf(InScenario()), "send_command", new { text = "FOO123 TAXIAUTO 30" }));

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        using var answer = JsonDocument.Parse(result.GetProperty("message").GetString() ?? "");
        JsonElement root = answer.RootElement;
        Assert.False(root.GetProperty("sent").GetBoolean(), root.GetRawText());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("status").GetString()), root.GetRawText());
    }

    [AvaloniaFact]
    public async Task SendCommand_EmptyText_IsInvalidParam()
    {
        JsonElement error = Error(await CallTool(ToolsOf(InScenario()), "send_command", new { text = "  " }), AutomationErrorCodes.InvalidParam);

        Assert.Equal("text", ErrorParam(error));
    }

    [AvaloniaFact]
    public async Task ArmPauseTriggers_HeartbeatBelowFiveSeconds_IsInvalidParam()
    {
        JsonElement error = Error(
            await CallTool(
                ToolsOf(InScenario()),
                "arm_pause_triggers",
                new
                {
                    pilotCalls = true,
                    warnings = true,
                    newAircraft = true,
                    groundStops = true,
                    heartbeatSeconds = 3,
                }
            ),
            AutomationErrorCodes.InvalidParam
        );

        Assert.Equal("heartbeatSeconds", ErrorParam(error));
    }

    [AvaloniaFact]
    public async Task ListAppTools_ReportsEveryToolWithItsParameters()
    {
        JsonElement list = Result(await ListTools(ToolsOf(InScenario())));

        string[] signatures = [.. list.EnumerateArray().Select(Signature).Order(StringComparer.Ordinal)];
        Assert.Equal(
            [
                "arm_pause_triggers(pilotCalls:bool,warnings:bool,newAircraft:bool,groundStops:bool,heartbeatSeconds:int)",
                "center_ground(target:string,zoom:double)",
                "center_radar(callsign:string,rangeNm:double)",
                "center_radar_at(lat:double,lon:double,rangeNm:double)",
                "center_radar_on_fix(fix:string,rangeNm:double)",
                "clear_rbls()",
                "connect(url:string)",
                "create_room(artccId:string)",
                "disarm_pause_triggers()",
                "drag(window:string,fromX:double,fromY:double,toX:double,toY:double,button:string,steps:int,holdMs:int)",
                "get_aircraft(callsign:string)",
                "get_airport_context(airport:string)",
                "get_framing()",
                "get_pause_events(sinceSequence:int)",
                "get_traffic(airport:string,activeOnly:bool)",
                "hover(window:string,selector:string,x:double,y:double,durationMs:int)",
                "load_recording(path:string)",
                "pause()",
                "place_rbl(from:string,to:string)",
                "play()",
                "prepare_take(path:string,centerLat:double,centerLon:double,rangeNm:double,videoMaps:string,ptlMinutes:double,ptlAll:bool,"
                    + "rbls:string,simRate:int,seekSeconds:double)",
                "read_terminal(sinceSequence:int)",
                "remove_rbl(slot:int)",
                "reset_datablock_offset(callsign:string)",
                "seek(simSeconds:double)",
                "send_command(text:string)",
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

        // connect needs no server: it is how the agent gets one. set_cloaked, hover and drag act on the client's own windows only.
        string[] needNoRoom = ["connect", "set_cloaked", "hover", "drag"];
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
    public async Task CenterRadar_CentresThePrimaryScopeOnTheAircraftAtTheRange()
    {
        MainViewModel viewModel = ScopeReady();
        viewModel.Aircraft.Add(new AircraftModel { Callsign = "UAL123", Position = new LatLon(37.7213, -122.2208) });

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "center_radar", new { callsign = "ual123", rangeNm = 15.5 }));

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        ScopeViewModel scope = viewModel.PrimaryScope;
        Assert.Equal(37.7213, scope.CenterLat, 6);
        Assert.Equal(-122.2208, scope.CenterLon, 6);
        Assert.Equal(15.5, scope.RangeNm, 6);
        Assert.Equal("Primary Scope centred on UAL123 (37.7213, -122.2208) at 15.5 nm.", result.GetProperty("message").GetString());
    }

    // The tools frame the primary Scope whatever Scope last had the focus, so an agent's picture does not depend on it.
    [AvaloniaFact]
    public async Task CenterRadarAt_AnotherScopeActive_StillCentresThePrimary()
    {
        MainViewModel viewModel = ScopeReady();
        using var other = new ScopeViewModel(viewModel, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        viewModel.ActiveScope = other;
        double otherRangeBefore = other.RangeNm;

        Result(
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

        Assert.Equal(30, viewModel.PrimaryScope.RangeNm, 6);
        Assert.Equal(otherRangeBefore, other.RangeNm);
    }

    [AvaloniaFact]
    public async Task CenterRadar_RangeOutOfBounds_InvalidParam()
    {
        MainViewModel viewModel = ScopeReady();
        viewModel.Aircraft.Add(new AircraftModel { Callsign = "UAL123", Position = new LatLon(37.7213, -122.2208) });
        double rangeBefore = viewModel.PrimaryScope.RangeNm;

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, new { tool = "center_radar", arguments = new { callsign = "UAL123", rangeNm = 256.5 } }),
            (ProtocolMethods.CallAppTool, new { tool = "center_radar", arguments = new { callsign = "UAL123", rangeNm = 0.5 } })
        );

        Assert.All(responses, response => Assert.Equal("rangeNm", ErrorParam(Error(response, AutomationErrorCodes.InvalidParam))));
        Assert.Equal(rangeBefore, viewModel.PrimaryScope.RangeNm);
    }

    [AvaloniaFact]
    public async Task CenterRadar_UnknownCallsign_Unavailable()
    {
        JsonElement result = Result(await CallTool(ToolsOf(ScopeReady()), "center_radar", new { callsign = "AAL9", rangeNm = 20 }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Contains("AAL9", result.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    /// <summary>The JSON object a tool that answers in JSON put in its result's message.</summary>
    private static JsonElement Answer(JsonElement result)
    {
        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        using var document = JsonDocument.Parse(result.GetProperty("message").GetString() ?? "");
        return document.RootElement.Clone();
    }

    private static string[] Callsigns(JsonElement traffic) =>
        [.. traffic.GetProperty("aircraft").EnumerateArray().Select(row => row.GetProperty("callsign").GetString() ?? "")];

    private static TerminalEntry Line(string message, bool isHistory) =>
        new()
        {
            Timestamp = DateTime.UtcNow,
            ElapsedSeconds = 12.34,
            Initials = "",
            Kind = TerminalEntryKind.PilotSpeech,
            Callsign = "UAL1",
            Message = message,
            PilotKind = "Proactive",
            IsHistory = isHistory,
        };

    [AvaloniaFact]
    public async Task GetTraffic_FiltersByAirportOmitsEmptyFieldsAndSkipsDelayedAircraft()
    {
        MainViewModel viewModel = InScenario();
        viewModel.Aircraft.Add(
            new AircraftModel
            {
                Callsign = "UAL1",
                GroundAirportId = "OAK",
                Position = new LatLon(37.7213, -122.2208),
            }
        );
        viewModel.Aircraft.Add(
            new AircraftModel
            {
                Callsign = "AAL2",
                Departure = "KSFO",
                ParkingSpot = "A1",
                Position = new LatLon(37.6188, -122.3756),
            }
        );
        viewModel.Aircraft.Add(
            new AircraftModel
            {
                Callsign = "DAL3",
                GroundAirportId = "OAK",
                Status = "Delayed (120s)",
                Position = new LatLon(37.7213, -122.2208),
            }
        );

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, new { tool = "get_traffic", arguments = new { airport = "KOAK", activeOnly = false } }),
            (ProtocolMethods.CallAppTool, new { tool = "get_traffic", arguments = new { airport = "", activeOnly = false } })
        );

        JsonElement oakOnly = Answer(Result(responses[0]));
        Assert.Equal(["UAL1"], Callsigns(oakOnly));
        JsonElement row = oakOnly.GetProperty("aircraft")[0];
        Assert.Equal(37.7213, row.GetProperty("lat").GetDouble(), 6);
        Assert.False(row.TryGetProperty("parking", out _), row.GetRawText());
        Assert.False(row.TryGetProperty("holdShortAhead", out _), row.GetRawText());
        Assert.False(row.TryGetProperty("request", out _), row.GetRawText());
        Assert.Equal(["AAL2", "UAL1"], Callsigns(Answer(Result(responses[1]))));
        Assert.Equal("A1", Answer(Result(responses[1])).GetProperty("aircraft")[0].GetProperty("parking").GetString());
    }

    [AvaloniaFact]
    public async Task GetTraffic_ActiveOnly_LeavesOutQuietParkedAircraft()
    {
        MainViewModel viewModel = InScenario();
        viewModel.Aircraft.Add(
            new AircraftModel
            {
                Callsign = "UAL1",
                IsOnGround = true,
                Position = new LatLon(37.7213, -122.2208),
            }
        );
        viewModel.Aircraft.Add(
            new AircraftModel
            {
                Callsign = "AAL2",
                IsOnGround = true,
                Position = new LatLon(37.7213, -122.2208),
                PendingPilotRequest = new PendingPilotRequestDto("Taxi", "Pending", "ready to taxi", 1.0, 1.0),
            }
        );
        viewModel.Aircraft.Add(
            new AircraftModel
            {
                Callsign = "DAL3",
                IsOnGround = true,
                GroundSpeed = 12,
                Position = new LatLon(37.7213, -122.2208),
            }
        );

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, new { tool = "get_traffic", arguments = new { airport = "", activeOnly = true } }),
            (ProtocolMethods.CallAppTool, new { tool = "get_traffic", arguments = new { airport = "", activeOnly = false } })
        );

        Assert.Equal(["AAL2", "DAL3"], Callsigns(Answer(Result(responses[0]))));
        Assert.Equal(["AAL2", "DAL3", "UAL1"], Callsigns(Answer(Result(responses[1]))));
    }

    [AvaloniaFact]
    public async Task GetAircraft_UnknownCallsign_Unavailable()
    {
        JsonElement result = Result(await CallTool(ToolsOf(InScenario()), "get_aircraft", new { callsign = "AAL9" }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Contains("AAL9", result.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task GetAircraft_ReportsTheSquawkDigits()
    {
        MainViewModel viewModel = InScenario();
        viewModel.Aircraft.Add(
            new AircraftModel
            {
                Callsign = "UAL1",
                BeaconCode = 4601,
                AssignedBeaconCode = 4601,
            }
        );

        JsonElement answer = Answer(Result(await CallTool(ToolsOf(viewModel), "get_aircraft", new { callsign = "UAL1" })));

        Assert.Equal("4601", answer.GetProperty("squawk").GetString());
        Assert.Equal("4601", answer.GetProperty("assignedSquawk").GetString());
    }

    [AvaloniaFact]
    public async Task ReadTerminal_ReturnsEntriesAfterTheCursorAndSkipsHistory()
    {
        MainViewModel viewModel = InScenario();
        TerminalEntry before = Line("before", isHistory: false);
        viewModel.TerminalEntries.Add(before);
        viewModel.TerminalEntries.Add(Line("recorded", isHistory: true));
        TerminalEntry after = Line("ready to taxi", isHistory: false);
        viewModel.TerminalEntries.Add(after);

        JsonElement answer = Answer(Result(await CallTool(ToolsOf(viewModel), "read_terminal", new { sinceSequence = (int)before.Sequence })));

        JsonElement entry = Assert.Single(answer.GetProperty("entries").EnumerateArray());
        Assert.Equal(after.Sequence, entry.GetProperty("seq").GetInt64());
        Assert.Equal("ready to taxi", entry.GetProperty("message").GetString());
        Assert.Equal("Proactive", entry.GetProperty("pilotKind").GetString());
        Assert.Equal("PilotSpeech", entry.GetProperty("kind").GetString());
        Assert.Equal(12.3, entry.GetProperty("t").GetDouble(), 6);
        Assert.False(entry.TryGetProperty("initials", out _), entry.GetRawText());
        Assert.False(answer.TryGetProperty("more", out _), answer.GetRawText());
        Assert.True(answer.GetProperty("cursor").GetInt64() >= after.Sequence);
    }

    [AvaloniaFact]
    public async Task ReadTerminal_OverTwoHundredEntries_SetsMoreAndTheCursor()
    {
        MainViewModel viewModel = InScenario();
        TerminalEntry[] lines = [.. Enumerable.Range(0, 201).Select(index => Line($"line {index}", isHistory: false))];
        foreach (TerminalEntry line in lines)
        {
            viewModel.TerminalEntries.Add(line);
        }

        JsonElement answer = Answer(
            Result(await CallTool(ToolsOf(viewModel), "read_terminal", new { sinceSequence = (int)(lines[0].Sequence - 1) }))
        );

        Assert.Equal(200, answer.GetProperty("entries").GetArrayLength());
        Assert.True(answer.GetProperty("more").GetBoolean());
        Assert.Equal(lines[199].Sequence, answer.GetProperty("cursor").GetInt64());
    }

    [AvaloniaFact]
    public async Task GetAirportContext_NoAirport_InvalidParam()
    {
        JsonElement response = await CallTool(ToolsOf(InScenario()), "get_airport_context", new { airport = " " });

        Assert.Equal("airport", ErrorParam(Error(response, AutomationErrorCodes.InvalidParam)));
    }

    [AvaloniaFact]
    public async Task GetAirportContext_NoPositionOrRunways_LeavesThemOut()
    {
        JsonElement answer = Answer(Result(await CallTool(ToolsOf(InScenario()), "get_airport_context", new { airport = "koak" })));

        Assert.Equal("KOAK", answer.GetProperty("airport").GetString());
        Assert.False(answer.TryGetProperty("position", out _), answer.GetRawText());
        Assert.False(answer.TryGetProperty("runwaysInUse", out _), answer.GetRawText());
        Assert.False(answer.GetProperty("solo").GetProperty("enabled").GetBoolean());
        Assert.True(answer.GetProperty("sim").TryGetProperty("rate", out _), answer.GetRawText());
    }

    [AvaloniaFact]
    public async Task CenterGround_OnACallsign_MovesTheCentreAndZeroKeepsTheZoom()
    {
        MainViewModel viewModel = InScenario();
        viewModel.Aircraft.Add(new AircraftModel { Callsign = "UAL123", Position = new LatLon(37.7213, -122.2208) });
        double zoomBefore = viewModel.Ground.ViewZoom;

        JsonElement answer = Answer(Result(await CallTool(ToolsOf(viewModel), "center_ground", new { target = "ual123", zoom = 0 })));

        Assert.Equal(37.7213, viewModel.Ground.ViewCenterLat, 6);
        Assert.Equal(-122.2208, viewModel.Ground.ViewCenterLon, 6);
        Assert.Equal(zoomBefore, viewModel.Ground.ViewZoom);
        Assert.Equal(37.7213, answer.GetProperty("centerLat").GetDouble(), 6);
        Assert.Equal(zoomBefore, answer.GetProperty("zoom").GetDouble());
    }

    /// <summary>Every tool that frames, measures on or moves blocks of the primary Scope, with valid arguments.</summary>
    private static readonly (string Tool, object Arguments)[] ScopeTools =
    [
        ("center_radar", new { callsign = "UAL123", rangeNm = 15 }),
        (
            "center_radar_at",
            new
            {
                lat = 37.5,
                lon = -122.25,
                rangeNm = 30,
            }
        ),
        ("center_radar_on_fix", new { fix = "OAK", rangeNm = 20 }),
        ("set_ptl", new { lengthMinutes = 2, all = true }),
        ("get_framing", new { }),
        ("place_rbl", new { from = "UAL123", to = "AAL9" }),
        ("remove_rbl", new { slot = 1 }),
        ("clear_rbls", new { }),
        (
            "set_datablock_offset",
            new
            {
                callsign = "UAL123",
                dxPx = 4,
                dyPx = 4,
            }
        ),
        ("reset_datablock_offset", new { callsign = "UAL123" }),
    ];

    // Until the Scope's map source has seeded it, the seed (centre, 40 NM and the facility's saved state) would overwrite
    // a framing set now.
    [AvaloniaFact]
    public async Task ScopeTools_BeforeTheScopesMapsLoad_AreUnavailable()
    {
        MainViewModel viewModel = InScenario();
        viewModel.Aircraft.Add(new AircraftModel { Callsign = "UAL123", Position = new LatLon(37.7213, -122.2208) });
        double rangeBefore = viewModel.PrimaryScope.RangeNm;

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            [.. ScopeTools.Select(call => (ProtocolMethods.CallAppTool, (object)new { tool = call.Tool, arguments = call.Arguments }))]
        );

        Assert.All(
            responses,
            response =>
            {
                Assert.False(Result(response).GetProperty("available").GetBoolean(), response.GetRawText());
                Assert.Equal("The Scope is still loading its maps.", Result(response).GetProperty("reason").GetString());
            }
        );
        Assert.Equal(rangeBefore, viewModel.PrimaryScope.RangeNm);
    }

    // A load that ends with no catalogue leaves nothing to seed over a tool's change, so the tools run.
    [AvaloniaFact]
    public async Task CenterRadar_FacilityWithNoVideoMaps_Available()
    {
        MainViewModel viewModel = InScenario();
        viewModel.Aircraft.Add(new AircraftModel { Callsign = "UAL123", Position = new LatLon(37.7213, -122.2208) });

        viewModel.ScopeMaps.FinishLoadForTesting(viewModel.ScopeMaps.BeginLoadForTesting(), null, null);
        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, new { tool = "center_radar", arguments = new { callsign = "UAL123", rangeNm = 15 } }),
            (ProtocolMethods.CallAppTool, new { tool = "set_video_map", arguments = new { starsId = 5, enabled = true } })
        );

        Assert.True(Result(responses[0]).GetProperty("available").GetBoolean(), responses[0].GetRawText());
        Assert.Equal(15, viewModel.PrimaryScope.RangeNm, 6);
        Assert.Equal("No video map with STARS id 5 in this scenario.", Result(responses[1]).GetProperty("reason").GetString());
    }

    [AvaloniaFact]
    public async Task SetVideoMap_SetsThePrimaryScopesMapToTheStateNotAFlip()
    {
        MainViewModel viewModel = ScopeReady();
        ScopeMapToggle toggle = viewModel.PrimaryScope.MapToggles.Single(candidate => candidate.StarsId == 5);
        var on = new { tool = "set_video_map", arguments = new { starsId = 5, enabled = true } };

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, on),
            (ProtocolMethods.CallAppTool, on),
            (ProtocolMethods.CallAppTool, new { tool = "set_video_map", arguments = new { starsId = 99, enabled = true } })
        );

        Assert.Equal("Video map 5 CENTER is on.", Result(responses[1]).GetProperty("message").GetString());
        Assert.True(toggle.IsOn);
        Assert.Equal([5], ScopeMapsOn(viewModel));
        Assert.False(Result(responses[2]).GetProperty("available").GetBoolean());
        Assert.Contains("99", Result(responses[2]).GetProperty("reason").GetString(), StringComparison.Ordinal);

        Result(await CallTool(ToolsOf(viewModel), "set_video_map", new { starsId = 5, enabled = false }));
        Assert.False(toggle.IsOn);
    }

    [AvaloniaFact]
    public async Task SetVideoMap_BeforeMapsLoad_Unavailable()
    {
        JsonElement result = Result(await CallTool(ToolsOf(InScenario()), "set_video_map", new { starsId = 5, enabled = true }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal("The video maps are still loading.", result.GetProperty("reason").GetString());
    }

    // A second load keeps the toggles of the first until it seeds; a map turned on in that window would be undone by the seed.
    [AvaloniaFact]
    public async Task SetVideoMap_WhileTheNextLoadRuns_Unavailable()
    {
        MainViewModel viewModel = ScopeReady();
        viewModel.ScopeMaps.BeginLoadForTesting();

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "set_video_map", new { starsId = 5, enabled = true }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal("The video maps are still loading.", result.GetProperty("reason").GetString());
        Assert.Empty(ScopeMapsOn(viewModel));
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

        // The view model's session solo mode is seeded from this preference, so the test puts it back however it ends.
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
        await viewModel.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
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

    /// <summary><see cref="ScopeReady"/> with two aircraft, UAL123 and AAL9, to measure between.</summary>
    private static MainViewModel ScopeReadyWithTwoAircraft()
    {
        MainViewModel viewModel = ScopeReady();
        viewModel.Aircraft.Add(new AircraftModel { Callsign = "UAL123", Position = new LatLon(37.7213, -122.2208) });
        viewModel.Aircraft.Add(new AircraftModel { Callsign = "AAL9", Position = new LatLon(37.6188, -122.3750) });
        return viewModel;
    }

    [AvaloniaFact]
    public async Task PlaceRbl_TwoCallsigns_ReturnsTheSlotAndDrawsTheLineOnTheScope()
    {
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "place_rbl", new { from = "UAL123", to = "AAL9" }));

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        Assert.Equal("RBL 1 placed: UAL123 to AAL9.", result.GetProperty("message").GetString());
        RangeBearingLine line = Assert.Single(viewModel.Measure.Lines);
        Assert.Equal(1, line.Slot);
        Assert.Equal("UAL123", line.A.Callsign);
        Assert.Equal("AAL9", line.B.Callsign);
        Assert.Equal(ScopeViewModel.MeasureView, line.View);
    }

    [AvaloniaFact]
    public async Task PlaceRbl_UnknownEndpoint_IsUnavailableWithTheReason()
    {
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();

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
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        for (int i = 0; i < RangeBearingLineStore.MaxLines; i++)
        {
            PlaceScopeLine(viewModel, "UAL123", "AAL9");
        }

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "place_rbl", new { from = "UAL123", to = "AAL9" }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal(RangeBearingViewState.FullStatus, result.GetProperty("reason").GetString());
        Assert.Equal(RangeBearingLineStore.MaxLines, viewModel.Measure.Lines.Count);
    }

    [AvaloniaFact]
    public async Task RemoveRbl_RemovesOnlyThatSlot()
    {
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        PlaceScopeLine(viewModel, "UAL123", "AAL9");
        PlaceScopeLine(viewModel, "AAL9", "UAL123");
        PlaceScopeLine(viewModel, "UAL123", "AAL9");

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "remove_rbl", new { slot = 2 }));

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        Assert.Equal("RBL 2 removed.", result.GetProperty("message").GetString());
        Assert.Equal([1, 3], viewModel.Measure.Lines.Select(line => line.Slot));
    }

    [AvaloniaFact]
    public async Task PlaceRbl_ReusesTheLowestFreeSlot()
    {
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        PlaceScopeLine(viewModel, "UAL123", "AAL9");
        PlaceScopeLine(viewModel, "AAL9", "UAL123");
        PlaceScopeLine(viewModel, "UAL123", "AAL9");
        viewModel.Measure.Remove(2);

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "place_rbl", new { from = "AAL9", to = "UAL123" }));

        Assert.Equal("RBL 2 placed: AAL9 to UAL123.", result.GetProperty("message").GetString());
        Assert.Equal([1, 2, 3], viewModel.Measure.Lines.Select(line => line.Slot));
    }

    [AvaloniaFact]
    public async Task RemoveRbl_EmptySlot_IsUnavailable()
    {
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        PlaceScopeLine(viewModel, "UAL123", "AAL9");

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
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        PlaceScopeLine(viewModel, "UAL123", "AAL9");
        PlaceScopeLine(viewModel, "AAL9", "UAL123");

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "clear_rbls", new { }));

        Assert.Equal("Cleared 2 RBL(s).", result.GetProperty("message").GetString());
        Assert.Empty(viewModel.Measure.Lines);
    }

    [AvaloniaFact]
    public async Task ClearRbls_KeepsTheGroundLines()
    {
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        PlaceScopeLine(viewModel, "UAL123", "AAL9");
        PlaceGroundLine(viewModel);
        PlaceScopeLine(viewModel, "AAL9", "UAL123");
        PlaceTypedGroundLine(viewModel);

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "clear_rbls", new { }));

        Assert.Equal("Cleared 2 RBL(s).", result.GetProperty("message").GetString());
        Assert.Equal(
            [(RblView.Ground, 2), (RblView.Ground, 4)],
            viewModel.Measure.Lines.Select(line => (line.View, line.Slot)).OrderBy(line => line.Slot)
        );
    }

    // The app tools act on the primary Scope: a line an extra Scope window placed is neither listed nor cleared.
    [AvaloniaFact]
    public async Task ClearRblsAndGetFraming_LeaveAnExtraScopesLine()
    {
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        ScopeViewInstance extra = viewModel.OpenExtraScopeView();
        try
        {
            Assert.NotNull(
                viewModel.Measure.PlaceSilently(RblEndpoint.OnAircraft("UAL123"), RblEndpoint.OnAircraft("AAL9"), extra.Vm.MeasureTarget).Slot
            );
            PlaceScopeLine(viewModel, "AAL9", "UAL123");

            JsonElement framing = await FramingOf(viewModel);
            JsonElement result = Result(await CallTool(ToolsOf(viewModel), "clear_rbls", new { }));

            Assert.Equal([2], framing.GetProperty("rbls").EnumerateArray().Select(slot => slot.GetInt32()));
            Assert.Equal("Cleared 1 RBL(s).", result.GetProperty("message").GetString());
            Assert.Same(extra.Vm, Assert.Single(viewModel.Measure.Lines).Surface);
        }
        finally
        {
            viewModel.CloseExtraScopeView(extra);
        }
    }

    /// <summary>Places a line on the Scope view in the shared store, as a measurement drawn on the Scope does.</summary>
    private static void PlaceScopeLine(MainViewModel viewModel, string from, string to) =>
        Assert.NotNull(viewModel.Measure.Place(RblEndpoint.OnAircraft(from), RblEndpoint.OnAircraft(to), viewModel.PrimaryScope.MeasureTarget));

    // The command and place_rbl share PlaceMeasurementFromText; the command still writes its outcome to the status line.
    [AvaloniaFact]
    public async Task MeasureCommand_PlacesTheLineAndReportsItInTheStatusLine()
    {
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        await viewModel.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

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
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        await viewModel.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
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
            ToolsOf(ScopeReadyWithTwoAircraft()),
            (ProtocolMethods.CallAppTool, new { tool = "set_leader_direction", arguments = new { callsign = "UAL123", direction = 0 } }),
            (ProtocolMethods.CallAppTool, new { tool = "set_leader_direction", arguments = new { callsign = "UAL123", direction = 10 } })
        );

        Assert.All(responses, response => Assert.Equal("direction", ErrorParam(Error(response, AutomationErrorCodes.InvalidParam))));
    }

    [AvaloniaFact]
    public async Task SetLeaderDirection_SyncOff_SaysSo()
    {
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        viewModel.Preferences.SetSyncStudentLeaderDirection(false);

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "set_leader_direction", new { callsign = "UAL123", direction = 9 }));

        Assert.EndsWith(
            " The Scope shows it only while the student leader direction sync preference is on; it is off.",
            result.GetProperty("message").GetString(),
            StringComparison.Ordinal
        );
    }

    [AvaloniaFact]
    public async Task SetDatablockOffset_WritesThePrimaryScopesOffsetAndRaisesManualOffsetsChanged()
    {
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        int raised = 0;
        viewModel.PrimaryScope.DataBlockState.ManualOffsetsChanged += () => raised++;

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
        Assert.Equal(new SKPoint(40, -20), viewModel.PrimaryScope.DataBlockState.ManualOffsets["UAL123"]);
        Assert.Equal(1, raised);
        Assert.Equal("No aircraft with callsign DAL1 in the client.", Result(responses[1]).GetProperty("reason").GetString());
    }

    [AvaloniaFact]
    public async Task ResetDatablockOffset_RemovesTheOffset()
    {
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        viewModel.PrimaryScope.SetDataBlockOffset("UAL123", new SKPoint(40, -20));
        var reset = new { tool = "reset_datablock_offset", arguments = new { callsign = "UAL123" } };

        JsonElement[] responses = await SendAll(ToolsOf(viewModel), (ProtocolMethods.CallAppTool, reset), (ProtocolMethods.CallAppTool, reset));

        Assert.Equal("Data block of UAL123 reset.", Result(responses[0]).GetProperty("message").GetString());
        Assert.Equal("Data block of UAL123 was already at its default position.", Result(responses[1]).GetProperty("message").GetString());
        Assert.False(viewModel.PrimaryScope.DataBlockState.ManualOffsets.ContainsKey("UAL123"));
    }

    [AvaloniaFact]
    public async Task CenterRadarAt_SetsCentreAndRange()
    {
        MainViewModel viewModel = ScopeReady();

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
        Assert.Equal("Primary Scope centred on (37.5000, -122.2500) at 30 nm.", result.GetProperty("message").GetString());
        Assert.Equal(37.5, viewModel.PrimaryScope.CenterLat, 6);
        Assert.Equal(-122.25, viewModel.PrimaryScope.CenterLon, 6);
        Assert.Equal(30, viewModel.PrimaryScope.RangeNm, 6);
    }

    [AvaloniaFact]
    public async Task CenterRadarAt_RejectsOutOfRangeLat()
    {
        MainViewModel viewModel = ScopeReady();
        double latBefore = viewModel.PrimaryScope.CenterLat;

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
        Assert.Equal(latBefore, viewModel.PrimaryScope.CenterLat);
    }

    /// <summary><see cref="ScopeReady"/> with the real navigation data installed and the client told it is loaded.</summary>
    private static MainViewModel ScopeReadyWithNavData()
    {
        TestVnasData.EnsureInitialized();
        Assert.NotNull(TestVnasData.NavigationDb);
        MainViewModel viewModel = ScopeReady();
        viewModel.MarkNavDbReady();
        return viewModel;
    }

    [AvaloniaFact]
    public async Task CenterRadarOnFix_UnknownFix_IsUnavailable()
    {
        MainViewModel viewModel = ScopeReadyWithNavData();
        double latBefore = viewModel.PrimaryScope.CenterLat;

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "center_radar_on_fix", new { fix = "ZZZZQ", rangeNm = 20 }));

        Assert.False(result.GetProperty("available").GetBoolean());
        Assert.Equal("No fix or airport named ZZZZQ in the navigation data.", result.GetProperty("reason").GetString());
        Assert.Equal(latBefore, viewModel.PrimaryScope.CenterLat);
    }

    [AvaloniaFact]
    public async Task CenterRadarOnFix_KnownFix_Centres()
    {
        MainViewModel viewModel = ScopeReadyWithNavData();
        (double Lat, double Lon)? oak = NavigationDatabase.Instance.GetFixPosition("OAK");
        Assert.NotNull(oak);

        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "center_radar_on_fix", new { fix = "oak", rangeNm = 20 }));

        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        Assert.Equal(oak.Value.Lat, viewModel.PrimaryScope.CenterLat, 6);
        Assert.Equal(oak.Value.Lon, viewModel.PrimaryScope.CenterLon, 6);
        Assert.Equal(20, viewModel.PrimaryScope.RangeNm, 6);
        Assert.StartsWith("Primary Scope centred on OAK (", result.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task SetPtl_EachVectorLengthAndOwnership_SetsThePrimaryScope()
    {
        MainViewModel viewModel = ScopeReady();
        (double Minutes, bool All)[] cases = [(0, true), (1, false), (2, true), (4, false), (8, true)];

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            [
                .. cases.Select(c =>
                    (ProtocolMethods.CallAppTool, (object)new { tool = "set_ptl", arguments = new { lengthMinutes = c.Minutes, all = c.All } })
                ),
            ]
        );

        Assert.Equal(
            [
                "Vector 0 min on all tracks.",
                "Vector 1 min on own tracks.",
                "Vector 2 min on all tracks.",
                "Vector 4 min on own tracks.",
                "Vector 8 min on all tracks.",
            ],
            responses.Select(response => Result(response).GetProperty("message").GetString())
        );
        ScopeViewModel scope = viewModel.PrimaryScope;
        Assert.Equal(8, scope.Pins.VectorMin);
        Assert.Equal(8, scope.EffectiveBand.VectorMin);
        Assert.Equal(ScopeVectorOwnership.All, scope.VectorOwnership);
    }

    [AvaloniaFact]
    public async Task SetPtl_Own_SetsOwnTracks()
    {
        MainViewModel viewModel = ScopeReady();
        viewModel.PrimaryScope.VectorOwnership = ScopeVectorOwnership.All;

        Result(await CallTool(ToolsOf(viewModel), "set_ptl", new { lengthMinutes = 4, all = false }));

        Assert.Equal(4, viewModel.PrimaryScope.EffectiveBand.VectorMin);
        Assert.Equal(ScopeVectorOwnership.Own, viewModel.PrimaryScope.VectorOwnership);
    }

    [AvaloniaFact]
    public async Task SetPtl_UnsupportedLength_IsInvalidParamNamingTheAllowedValues()
    {
        MainViewModel viewModel = ScopeReady();
        ScopeBandPins pinsBefore = viewModel.PrimaryScope.Pins;
        ScopeVectorOwnership ownershipBefore = viewModel.PrimaryScope.VectorOwnership;

        JsonElement[] responses = await SendAll(
            ToolsOf(viewModel),
            (ProtocolMethods.CallAppTool, new { tool = "set_ptl", arguments = new { lengthMinutes = 3, all = true } }),
            (ProtocolMethods.CallAppTool, new { tool = "set_ptl", arguments = new { lengthMinutes = 0.5, all = true } }),
            (ProtocolMethods.CallAppTool, new { tool = "set_ptl", arguments = new { lengthMinutes = 16, all = true } })
        );

        Assert.All(
            responses,
            response =>
            {
                JsonElement error = Error(response, AutomationErrorCodes.InvalidParam);
                Assert.Equal("lengthMinutes", ErrorParam(error));
                Assert.Contains("0, 1, 2, 4 or 8", Message(error), StringComparison.Ordinal);
            }
        );
        Assert.Equal("'lengthMinutes' must be 0, 1, 2, 4 or 8 minutes, not 3.", Message(Error(responses[0], AutomationErrorCodes.InvalidParam)));
        Assert.Equal(pinsBefore, viewModel.PrimaryScope.Pins);
        Assert.Equal(ownershipBefore, viewModel.PrimaryScope.VectorOwnership);
    }

    /// <summary><see cref="ScopeReadyWithTwoAircraft"/> framed: maps 2 and 5 on, centre, range, a pinned vector and Scope RBLs.</summary>
    private static MainViewModel FramedScope()
    {
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        ScopeViewModel scope = viewModel.PrimaryScope;
        Assert.True(scope.SetMapOn(5, true));
        Assert.True(scope.SetMapOn(2, true));
        scope.CenterLat = 37.7;
        scope.CenterLon = -122.2;
        scope.RangeNm = 25;
        scope.PinVectorMin(2);
        scope.VectorOwnership = ScopeVectorOwnership.All;
        PlaceScopeLine(viewModel, "UAL123", "AAL9");
        PlaceScopeLine(viewModel, "AAL9", "UAL123");
        PlaceScopeLine(viewModel, "UAL123", "AAL9");
        viewModel.Measure.Remove(1);
        PlaceGroundLine(viewModel);
        PlaceTypedGroundLine(viewModel);
        return viewModel;
    }

    private async Task<JsonElement> FramingOf(MainViewModel viewModel)
    {
        JsonElement result = Result(await CallTool(ToolsOf(viewModel), "get_framing", new { }));
        Assert.True(result.GetProperty("available").GetBoolean(), result.GetRawText());
        using var framing = JsonDocument.Parse(result.GetProperty("message").GetString() ?? "");
        return framing.RootElement.Clone();
    }

    [AvaloniaFact]
    public async Task GetFraming_ReportsThePrimaryScopesCentreRangeMapsVectorAndRbls()
    {
        MainViewModel viewModel = FramedScope();
        viewModel.SimRate = 4;
        viewModel.IsPaused = true;
        viewModel.ScenarioElapsedSeconds = 123.5;

        JsonElement root = await FramingOf(viewModel);

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

    // A load reseeds the Scope at the area's centre and 40 NM, keeping on the maps that were on; get_framing reads the Scope
    // as it stands, not a copy taken at the last framing.
    [AvaloniaFact]
    public async Task GetFraming_AfterALoad_ReportsTheReseededScope()
    {
        MainViewModel viewModel = FramedScope();

        LoadScopeMaps(viewModel);
        JsonElement root = await FramingOf(viewModel);

        Assert.Equal(AreaLat, root.GetProperty("centerLat").GetDouble(), 6);
        Assert.Equal(AreaLon, root.GetProperty("centerLon").GetDouble(), 6);
        Assert.Equal(ScopeViewModel.DefaultRangeNm, root.GetProperty("rangeNm").GetDouble(), 6);
        Assert.Equal([2, 5], root.GetProperty("videoMaps").EnumerateArray().Select(map => map.GetProperty("starsId").GetInt32()));
    }

    // A seek replaces the traffic and moves the clock; the Scope's framing stays as it was.
    [AvaloniaFact]
    public async Task GetFraming_AfterASeek_ReportsTheScopesFramingAtTheNewTime()
    {
        MainViewModel viewModel = FramedScope();

        viewModel.ApplyRewindResult(new RewindResultDto(true, null, [], [], null, [], "scenario-1"), 60, "Rewound to 1:00.");
        JsonElement root = await FramingOf(viewModel);

        Assert.Equal(37.7, root.GetProperty("centerLat").GetDouble(), 6);
        Assert.Equal(25, root.GetProperty("rangeNm").GetDouble(), 6);
        Assert.Equal([2, 5], root.GetProperty("videoMaps").EnumerateArray().Select(map => map.GetProperty("starsId").GetInt32()));
        Assert.Equal(2.0, root.GetProperty("ptl").GetProperty("lengthMinutes").GetDouble());
        Assert.Equal(60, root.GetProperty("simSeconds").GetDouble(), 6);
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

    /// <summary><see cref="ScopeReadyWithTwoAircraft"/> with the primary Scope's maps 2 and 9 on and map 5 off.</summary>
    private static MainViewModel FramingReady()
    {
        MainViewModel viewModel = ScopeReadyWithTwoAircraft();
        Assert.True(viewModel.PrimaryScope.SetMapOn(2, true));
        Assert.True(viewModel.PrimaryScope.SetMapOn(9, true));
        return viewModel;
    }

    /// <summary>Places a Ground-view RBL in the shared store, as a measurement drawn on the Ground view does.</summary>
    private static void PlaceGroundLine(MainViewModel viewModel) =>
        Assert.NotNull(
            viewModel.Measure.Place(
                RblEndpoint.OnAircraft("UAL123"),
                RblEndpoint.OnAircraft("AAL9"),
                new MeasureTarget(RblView.Ground, viewModel.Ground.MeasureTrackLookup, GroundViewModel.MeasureUnits, null)
            )
        );

    /// <summary>Places a Ground-view RBL from typed points, as a measurement typed on the Ground surface does.</summary>
    private static void PlaceTypedGroundLine(MainViewModel viewModel) =>
        Assert.NotNull(viewModel.PlaceMeasurementFromText("UAL123", "AAL9", new IgnoredSink(), RblView.Ground, null).Slot);

    /// <summary>A command sink that drops every report.</summary>
    private sealed class IgnoredSink : ICommandTextSink
    {
        public void ReportStatus(string status) { }

        public void ReportError(string message) { }

        public void ClearInput() { }

        public void AddHistory(string callsign, string command) { }

        public void AddWarning(string message) { }
    }

    private static TakeFraming Framing(IReadOnlySet<int> maps, double ptlMinutes, bool ptlAll, IReadOnlyList<(string From, string To)> rbls) =>
        new(CenterLat: 37.5, CenterLon: -122.25, RangeNm: 35, VideoMaps: maps, PtlMinutes: ptlMinutes, PtlAll: ptlAll, Rbls: rbls);

    private static AutomationTools RealTools(MainViewModel viewModel) => new(viewModel, new MainViewModelAutomationState(viewModel));

    [AvaloniaFact]
    public void FrameTake_SetsExactlyTheGivenMapsCentreVectorAndScopeRbls()
    {
        MainViewModel viewModel = FramingReady();
        PlaceScopeLine(viewModel, "AAL9", "UAL123");
        PlaceScopeLine(viewModel, "AAL9", "UAL123");
        PlaceGroundLine(viewModel);

        string? stopped = RealTools(viewModel).FrameTake(Framing(new HashSet<int> { 5 }, 2, true, [("UAL123", "AAL9")]));

        Assert.Null(stopped);
        ScopeViewModel scope = viewModel.PrimaryScope;
        Assert.Equal([5], ScopeMapsOn(viewModel));
        Assert.Equal(37.5, scope.CenterLat, 6);
        Assert.Equal(-122.25, scope.CenterLon, 6);
        Assert.Equal(35, scope.RangeNm, 6);
        Assert.Equal(2, scope.EffectiveBand.VectorMin);
        Assert.Equal(ScopeVectorOwnership.All, scope.VectorOwnership);
        RangeBearingLine ground = Assert.Single(viewModel.Measure.Lines, line => line.View == RblView.Ground);
        Assert.Equal(3, ground.Slot);
        RangeBearingLine placed = Assert.Single(viewModel.Measure.Lines, line => line.View == ScopeViewModel.MeasureView);
        Assert.Equal(("UAL123", "AAL9"), (placed.A.Callsign, placed.B.Callsign));
    }

    [AvaloniaFact]
    public void FrameTake_UnknownStarsId_StopsBeforeAnyToggleChanges()
    {
        MainViewModel viewModel = FramingReady();
        double latBefore = viewModel.PrimaryScope.CenterLat;

        string? stopped = RealTools(viewModel).FrameTake(Framing(new HashSet<int> { 5, 77 }, 1, true, []));

        Assert.Equal("prepare_take stopped at 'set the video maps': no video map with STARS id 77 in this scenario.", stopped);
        Assert.Equal([2, 9], ScopeMapsOn(viewModel));
        Assert.Equal(latBefore, viewModel.PrimaryScope.CenterLat);
    }

    [AvaloniaFact]
    public void FrameTake_VectorZero_DrawsNoVectorOnOwnTracks()
    {
        MainViewModel viewModel = FramingReady();
        viewModel.PrimaryScope.PinVectorMin(4);
        viewModel.PrimaryScope.VectorOwnership = ScopeVectorOwnership.All;

        string? stopped = RealTools(viewModel).FrameTake(Framing(new HashSet<int>(), 0, false, []));

        Assert.Null(stopped);
        Assert.Equal(0, viewModel.PrimaryScope.EffectiveBand.VectorMin);
        Assert.Equal(ScopeVectorOwnership.Own, viewModel.PrimaryScope.VectorOwnership);
        Assert.Empty(ScopeMapsOn(viewModel));
    }

    [AvaloniaFact]
    public void FrameTake_RblFailure_NamesItsStep()
    {
        MainViewModel viewModel = FramingReady();

        string? stopped = RealTools(viewModel).FrameTake(Framing(new HashSet<int> { 2 }, 1, false, [("UAL123", "AAL9"), ("UAL123", "ZZZ9")]));

        Assert.StartsWith("prepare_take stopped at 'place the RBL UAL123,ZZZ9': Unknown", stopped, StringComparison.Ordinal);
        Assert.Single(viewModel.Measure.Lines);
    }

    [AvaloniaFact]
    public void FrameTake_SelectsTheScopeTab()
    {
        MainViewModel viewModel = FramingReady();
        viewModel.SelectedTabIndex = 0;

        Assert.Null(RealTools(viewModel).FrameTake(Framing(new HashSet<int>(), 1, false, [])));

        Assert.Equal(2, viewModel.SelectedTabIndex);
    }

    // A popped-out Scope has no tab to select: the tab strip stays where it was.
    [AvaloniaFact]
    public void FrameTake_ScopePoppedOut_LeavesTheTabAlone()
    {
        MainViewModel viewModel = FramingReady();
        viewModel.IsScopePoppedOut = true;
        viewModel.SelectedTabIndex = 0;

        Assert.Null(RealTools(viewModel).FrameTake(Framing(new HashSet<int>(), 1, false, [])));

        Assert.Equal(0, viewModel.SelectedTabIndex);
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
        await viewModel.NavDataInitialization.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
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

#pragma warning disable CS0067 // This fake never changes its state, so it never raises Changed.
        public event EventHandler? Changed;
#pragma warning restore CS0067

        public IReadOnlyList<AircraftModel> Aircraft => [];

        public double ScenarioElapsedSeconds => 0;

        public bool IsPaused { get; set; }

        public int SimRate => 1;

        public string ClientLogPath => "";

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
