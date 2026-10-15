using Xunit;
using Yaat.Client.Logging;
using Yaat.Sim.Logging;

namespace Yaat.Client.Tests;

/// <summary>
/// A bug report bundle's client logs are posted publicly, so <see cref="ClientLogScrubber"/> must hide the people
/// (with the server log's pseudonyms), the machine and the user's own servers, while keeping the log readable.
/// </summary>
public class ClientLogScrubberTests
{
    private const string Prefix = "2026-10-14 12:00:00.123 -07:00 [info] Yaat.Client.ViewModels.MainViewModel: ";

    private static readonly IReadOnlyList<string> SavedServerUrls =
    [
        "https://my.private.box:5000",
        "ws://lab.example.net:8080/hubs",
        "https://yaat1.leftos.dev",
        "http://localhost:5130",
        "http://127.0.0.1:5130",
        "http://[::1]:5130",
        "http://box:5000",
        "http://yaat:5130",
    ];

    // A0 = Jane Doe; A1 = Pat Smith, whose initials CM are also the climb-and-maintain verb.
    private static readonly ClientLogScrubContext Context = new(
        [new LogParticipant("1826127", "Jane Doe", "JD"), new LogParticipant("7001234", "Pat Smith", "CM")],
        @"C:\Users\danmi",
        "danmi",
        "DANMI-PC",
        ClientLogScrubber.ServerHostsOf(SavedServerUrls)
    );

    private static string Scrub(string text) => ClientLogScrubber.Scrub(text, Context);

    // A client log line as FileLoggerProvider writes it. A terminal line is MainViewModel.AddTerminalEntry's
    // "[Terminal] [{Kind}] {Initials} {Callsign}: {Message}" (MainViewModel.Aircraft.cs:16), so a server room line with
    // no initials and no callsign reads "[Terminal] [System]  : {Message}".
    private const string At = "2026-10-14 18:02:05.121 -07:00 ";
    private const string Vm = At + "[info] Yaat.Client.ViewModels.MainViewModel: ";
    private const string VmWarn = At + "[warn] Yaat.Client.ViewModels.MainViewModel: ";
    private const string Services = At + "[info] Yaat.Client.Services.";
    private const string ServicesWarn = At + "[warn] Yaat.Client.Services.";
    private const string Sys = Vm + "[Terminal] [System]  : ";

    // Every template that can carry a person or a private host, taken from the call site that writes it (yaat-server paths
    // under src/Yaat.Server, yaat paths under src). CM is a listed participant (A1) whose initials are a command verb;
    // XY, Zed Outsider and CID 1999999 are a departed person outside the list.
    [Theory]
    // TrainingHub.BroadcastRoomTerminal (Hubs/TrainingHub.cs:3119): "{who} {action}", who = the member's initials.
    // Hubs/TrainingHub.cs:419
    [InlineData(Sys + "CM joined the room", Sys + "A1 joined the room")]
    // Hubs/TrainingHub.cs:419
    [InlineData(Sys + "XY joined the room (Flight Strips)", Sys + "?? joined the room (Flight Strips)")]
    // Hubs/TrainingHub.cs:486
    [InlineData(Sys + "CM left the room (vTDLS)", Sys + "A1 left the room (vTDLS)")]
    // Hubs/TrainingHub.cs:341
    [InlineData(Sys + "XY created the room", Sys + "?? created the room")]
    // Hubs/TrainingHub.cs:961
    [InlineData(Sys + "CM loaded scenario 'KOAK VFR'", Sys + "A1 loaded scenario 'KOAK VFR'")]
    // Hubs/TrainingHub.cs:1085
    [InlineData(Sys + "XY unloaded the scenario", Sys + "?? unloaded the scenario")]
    // Hubs/TrainingHub.cs:1263
    [InlineData(Sys + "CM set command run delay to 2s", Sys + "A1 set command run delay to 2s")]
    // Hubs/TrainingHub.cs:1525
    [InlineData(Sys + "XY set live traffic filter: KOAK 20nm", Sys + "?? set live traffic filter: KOAK 20nm")]
    // Hubs/TrainingHub.cs:1358
    [InlineData(Sys + "CM enabled auto cross-runway", Sys + "A1 enabled auto cross-runway")]
    // Hubs/TrainingHub.cs:1746
    [InlineData(Sys + "XY disabled pilot-speech rendering for RPO mode", Sys + "?? disabled pilot-speech rendering for RPO mode")]
    // Hubs/TrainingHub.cs:1540
    [InlineData(Sys + "CM loaded weather 'KOAK IFR'", Sys + "A1 loaded weather 'KOAK IFR'")]
    // Hubs/TrainingHub.cs:1559
    [InlineData(Sys + "XY cleared weather", Sys + "?? cleared weather")]
    // Hubs/TrainingHub.cs:1574
    [InlineData(Sys + "CM updated arrival generators (3)", Sys + "A1 updated arrival generators (3)")]
    // Hubs/TrainingHub.cs:1788
    [InlineData(Sys + "XY updated solo pacing rates", Sys + "?? updated solo pacing rates")]
    // Hubs/TrainingHub.cs:1802
    [InlineData(Sys + "CM amended flight plan for UAL238", Sys + "A1 amended flight plan for UAL238")]
    // Hubs/TrainingHub.cs:2008
    [InlineData(Sys + "XY went live", Sys + "?? went live")]
    // Hubs/TrainingHub.cs:3117, no member
    [InlineData(Sys + "Unknown joined the room", Sys + "Unknown joined the room")]
    // The kick line's who is the member's initials, else the CRC display name, else the CID (Hubs/TrainingHub.cs:2699).
    // Hubs/TrainingHub.cs:2740
    [InlineData(Sys + "CM was kicked from the room", Sys + "A1 was kicked from the room")]
    // Hubs/TrainingHub.cs:2740
    [InlineData(Sys + "XY was kicked from the room", Sys + "?? was kicked from the room")]
    // Hubs/TrainingHub.cs:2740
    [InlineData(Sys + "1999999 was kicked from the room", Sys + "?? was kicked from the room")]
    // Hubs/TrainingHub.cs:2740
    [InlineData(Sys + "7001234 was kicked from the room", Sys + "A1 was kicked from the room")]
    // Hubs/TrainingHub.cs:2740
    [InlineData(Sys + "OAK_TWR (Zed Outsider) was kicked from the room", Sys + "OAK_TWR (??) was kicked from the room")]
    // Hubs/TrainingHub.cs:2740
    [InlineData(Sys + "OAK_TWR was kicked from the room", Sys + "OAK_TWR was kicked from the room")]
    // RPO pull: "pulled {name, else CID} into the room as an RPO".
    // Hubs/TrainingHub.cs:3186
    [InlineData(Sys + "CM pulled Zed Outsider into the room as an RPO", Sys + "A1 pulled ?? into the room as an RPO")]
    // Hubs/TrainingHub.cs:3186
    [InlineData(Sys + "JD pulled 1999999 into the room as an RPO", Sys + "A0 pulled ?? into the room as an RPO")]
    // Hubs/TrainingHub.cs:3186
    [InlineData(Sys + "XY pulled Pat Smith into the room as an RPO", Sys + "?? pulled A1 into the room as an RPO")]
    // The loading line carries the initials in the slot and in the message.
    // Hubs/TrainingHub.cs:654-658
    [InlineData(Vm + "[Terminal] [System] CM : CM is loading 'KOAK VFR'…", Vm + "[Terminal] [System] A1 : A1 is loading 'KOAK VFR'…")]
    // Hubs/TrainingHub.cs:654-658
    [InlineData(Vm + "[Terminal] [System] XY : XY is loading 'KOAK VFR'…", Vm + "[Terminal] [System] ?? : ?? is loading 'KOAK VFR'…")]
    // Tower Cab membership (TrainingBroadcastService.BroadcastMembershipChange, Simulation/TrainingBroadcastService.cs:312):
    // the who is the same-CID member's initials, else the CID (Hubs/TowerCabRooms.cs:325).
    // Hubs/TowerCabRooms.cs:333
    [InlineData(Sys + "CM joined the room (Tower Cab 3D)", Sys + "A1 joined the room (Tower Cab 3D)")]
    // Hubs/TowerCabRooms.cs:333
    [InlineData(Sys + "1999999 joined the room (Tower Cab 3D)", Sys + "?? joined the room (Tower Cab 3D)")]
    // Hubs/TowerCabRooms.cs:358
    [InlineData(Sys + "1999999 left the room (Tower Cab 3D)", Sys + "?? left the room (Tower Cab 3D)")]
    // Hubs/TowerCabRooms.cs:358
    [InlineData(Sys + "7001234 left the room (Tower Cab 3D)", Sys + "A1 left the room (Tower Cab 3D)")]
    // RoomEngine assignment lines: "{actor} assigned {list} to {target}", "{actor} unassigned {list} (available to all)".
    // Simulation/RoomEngine.cs:1324
    [InlineData(Sys + "CM assigned UAL238, SKW12 to XY", Sys + "A1 assigned UAL238, SKW12 to ??")]
    // Simulation/RoomEngine.cs:1324
    [InlineData(Sys + "XY assigned UAL238 to CM", Sys + "?? assigned UAL238 to A1")]
    // Simulation/RoomEngine.cs:1346
    [InlineData(Sys + "CM unassigned UAL238 (available to all)", Sys + "A1 unassigned UAL238 (available to all)")]
    // The sync line's parentheses hold a sector code, not a name.
    // Simulation/RoomEngine.cs:1977
    [InlineData(Sys + "CRC position synced: now acting as OAK_TWR (TW)", Sys + "CRC position synced: now acting as OAK_TWR (TW)")]
    // CRC lines: "[CRC] {CrcDisplayName.Build(callsign, realName)} {event}", the display name "{callsign} ({realName})".
    // Hubs/CrcClientState.Session.cs:100
    [InlineData(Sys + "[CRC] OAK_TWR (Zed Outsider) connected", Sys + "[CRC] OAK_TWR (??) connected")]
    // Hubs/CrcClientState.Session.cs:353
    [InlineData(Sys + "[CRC] OAK_TWR (Pat Smith) activated", Sys + "[CRC] OAK_TWR (A1) activated")]
    // Hubs/CrcClientState.Session.cs:372
    [InlineData(Sys + "[CRC] SFO_GND (XY) deactivated", Sys + "[CRC] SFO_GND (??) deactivated")]
    // Hubs/CrcClientState.cs:511
    [InlineData(Sys + "[CRC] OAK_TWR (BOB) disconnected", Sys + "[CRC] OAK_TWR (??) disconnected")]
    // Hubs/CrcClientState.Session.cs:781
    [InlineData(Sys + "[CRC] OAK_TWR (Jane Doe) ended session", Sys + "[CRC] OAK_TWR (A0) ended session")]
    // Hubs/CrcClientState.FlightPlan.cs:215
    [InlineData(Sys + "[CRC] OAK_TWR (Zed Outsider) FP: (UAL238 B738/L KSFO KOAK)", Sys + "[CRC] OAK_TWR (??) FP: (UAL238 B738/L KSFO KOAK)")]
    // Hubs/CrcClientState.FlightPlan.cs:215, a CRC client with no position: the bare real name
    [InlineData(Sys + "[CRC] Zed Outsider FP: (UAL238 B738/L KSFO KOAK)", Sys + "[CRC] ?? FP: (UAL238 B738/L KSFO KOAK)")]
    // Hubs/CrcClientState.FlightPlan.cs:279, a CRC client with no position
    [InlineData(Sys + "[CRC] Zed Outsider FP: (N172SP C172/G KOAK KSFO)", Sys + "[CRC] ?? FP: (N172SP C172/G KOAK KSFO)")]
    // Hubs/CrcClientState.FlightPlan.cs:279, a listed CRC client with no position
    [InlineData(Sys + "[CRC] Pat Smith FP: (N172SP C172/G KOAK KSFO)", Sys + "[CRC] A1 FP: (N172SP C172/G KOAK KSFO)")]
    // Hubs/CrcClientState.FlightPlan.cs:279, a position with no real name
    [InlineData(Sys + "[CRC] OAK_TWR FP: (N172SP C172/G KOAK KSFO)", Sys + "[CRC] OAK_TWR FP: (N172SP C172/G KOAK KSFO)")]
    // Hubs/CrcClientState.Session.cs:100, no real name
    [InlineData(Sys + "[CRC] OAK_TWR connected", Sys + "[CRC] OAK_TWR connected")]
    // A CRC client with no position: CrcDisplayName.Build (Yaat.Sim/Logging/CrcDisplayName.cs:28) gives the bare real name.
    // Hubs/CrcClientState.Session.cs:100
    [InlineData(Sys + "[CRC] Zed Outsider connected", Sys + "[CRC] ?? connected")]
    // Hubs/CrcClientState.Session.cs:353
    [InlineData(Sys + "[CRC] Pat Smith activated", Sys + "[CRC] A1 activated")]
    // Hubs/CrcClientState.Session.cs:353
    [InlineData(Sys + "[CRC] BOB activated", Sys + "[CRC] ?? activated")]
    // Hubs/CrcClientState.Session.cs:372
    [InlineData(Sys + "[CRC] Zed Outsider deactivated", Sys + "[CRC] ?? deactivated")]
    // Hubs/CrcClientState.cs:511
    [InlineData(Sys + "[CRC] Zed Outsider disconnected", Sys + "[CRC] ?? disconnected")]
    // Hubs/CrcClientState.Session.cs:781
    [InlineData(Sys + "[CRC] Zed Outsider ended session", Sys + "[CRC] ?? ended session")]
    // Hubs/CrcClientState.Session.cs:781, no callsign and no name
    [InlineData(Sys + "[CRC] unknown ended session", Sys + "[CRC] unknown ended session")]
    // Hubs/TrainingHub.cs:2530: $"[CRC] Pulled {name} into room", name = the display name, else the client id.
    [InlineData(Sys + "[CRC] Pulled Zed Outsider into room", Sys + "[CRC] Pulled ?? into room")]
    // Hubs/TrainingHub.cs:2530
    [InlineData(Sys + "[CRC] Pulled Jane Doe into room", Sys + "[CRC] Pulled A0 into room")]
    // Hubs/TrainingHub.cs:2622: $"[CRC] Kicked {name} from room"
    [InlineData(Sys + "[CRC] Kicked Zed Outsider from room", Sys + "[CRC] Kicked ?? from room")]
    // Hubs/TrainingHub.cs:2743 (who = the kicked CID's CRC display name, a bare real name with no position) and :2784
    [InlineData(Sys + "Zed Outsider was kicked from the room", Sys + "?? was kicked from the room")]
    // Hubs/TrainingHub.cs:2784, a listed CRC name
    [InlineData(Sys + "Jane Doe was kicked from the room", Sys + "A0 was kicked from the room")]
    // Hubs/CrcClientState.Messaging.cs:22: sender = GetPositionCallsign(), the display name when the client has no position
    [InlineData(Sys + "[CRC] Zed Outsider: UAL238 cleared to land", Sys + "[CRC] ??: UAL238 cleared to land")]
    // Hubs/CrcClientState.Messaging.cs:22, a listed CRC name
    [InlineData(Sys + "[CRC] Jane Doe: UAL238 cleared to land", Sys + "[CRC] A0: UAL238 cleared to land")]
    // Hubs/CrcClientState.Messaging.cs:80
    [InlineData(Sys + "[CRC PM → OAK_TWR] Zed Outsider: are you there", Sys + "[CRC PM → OAK_TWR] ??: are you there")]
    // Hubs/CrcClientState.Messaging.cs:106
    [InlineData(Sys + "[CRC Wallop] Zed Outsider: need a hand", Sys + "[CRC Wallop] ??: need a hand")]
    // Hubs/CrcClientState.Messaging.cs:106
    [InlineData(Sys + "[CRC ATC] Zed Outsider: release SKW12", Sys + "[CRC ATC] ??: release SKW12")]
    // Hubs/CrcClientState.Info.cs:102
    [InlineData(Sys + "[CRC] Zed Outsider killed OAK_TWR: Disconnected by supervisor", Sys + "[CRC] ?? killed OAK_TWR: Disconnected by supervisor")]
    // Hubs/CrcClientState.Info.cs:102, a position
    [InlineData(Sys + "[CRC] OAK_TWR killed SFO_GND: Disconnected by supervisor", Sys + "[CRC] OAK_TWR killed SFO_GND: Disconnected by supervisor")]
    // Hubs/CrcClientState.Messaging.cs:71: a private message to an aircraft records the sender in the initials slot
    [InlineData(Vm + "[Terminal] [Chat] Zed Outsider UAL238: say altitude", Vm + "[Terminal] [Chat] ?? UAL238: say altitude")]
    // Hubs/CrcClientState.Messaging.cs:22
    [InlineData(Sys + "[CRC] OAK_TWR: UAL238 cleared to land", Sys + "[CRC] OAK_TWR: UAL238 cleared to land")]
    // Messaging.cs:80
    [InlineData(Sys + "[CRC PM → SFO_GND] OAK_TWR: release UAL238", Sys + "[CRC PM → SFO_GND] OAK_TWR: release UAL238")]
    // Hubs/CrcClientState.Messaging.cs:106
    [InlineData(Sys + "[CRC ATC] OAK_TWR: runway 28R closed", Sys + "[CRC ATC] OAK_TWR: runway 28R closed")]
    // Hubs/CrcClientState.Info.cs:102
    [InlineData(Sys + "[CRC] OAK_SUP killed OAK_TWR: Disconnected by supervisor", Sys + "[CRC] OAK_SUP killed OAK_TWR: Disconnected by supervisor")]
    // Hubs/CrcClientState.Messaging.cs:22: a message's text that ends like an event line is the message, not a name
    [InlineData(Sys + "[CRC] OAK_TWR: ILS back up, frequency connected", Sys + "[CRC] OAK_TWR: ILS back up, frequency connected")]
    // Hubs/CrcClientState.Stars.cs:1785, a CRC client with no position
    [InlineData(Sys + "[CRC] Zed Outsider consolidated 2W → 2E (full)", Sys + "[CRC] ?? consolidated 2W → 2E (full)")]
    // Hubs/CrcClientState.Stars.cs:1812
    [InlineData(Sys + "[CRC] Zed Outsider deconsolidated 2W", Sys + "[CRC] ?? deconsolidated 2W")]
    // Hubs/CrcClientState.Stars.cs:1785, a position
    [InlineData(Sys + "[CRC] OAK_TWR consolidated 2W → 2E (basic)", Sys + "[CRC] OAK_TWR consolidated 2W → 2E (basic)")]
    // Hubs/TrainingHub.cs:2530
    [InlineData(Sys + "[CRC] Pulled OAK_TWR (BOB) into room", Sys + "[CRC] Pulled OAK_TWR (??) into room")]
    // Hubs/TrainingHub.cs:2622
    [InlineData(Sys + "[CRC] Kicked SFO_GND (Zed Outsider) from room", Sys + "[CRC] Kicked SFO_GND (??) from room")]
    // The initials slot (Simulation/TrainingBroadcastService.cs:35): a person's initials, or a system sender.
    // Simulation/RoomEngine.cs:1193
    [InlineData(Vm + "[Terminal] [Command] CM UAL238: CM 050", Vm + "[Terminal] [Command] A1 UAL238: CM 050")]
    // Simulation/RoomEngine.cs:1193
    [InlineData(Vm + "[Terminal] [Strip] XY UAL238: FH 270", Vm + "[Terminal] [Strip] ?? UAL238: FH 270")]
    // Simulation/RoomEngine.cs:1178: an override command's slot is "{initials} (override)"
    [InlineData(Vm + "[Terminal] [Command] JD (override) UAL238: FH 270", Vm + "[Terminal] [Command] A0 (override) UAL238: FH 270")]
    // Simulation/RoomEngine.cs:1178, a listed person whose initials are a command verb
    [InlineData(Vm + "[Terminal] [Command] CM (override) UAL238: CM 050", Vm + "[Terminal] [Command] A1 (override) UAL238: CM 050")]
    // Simulation/RoomEngine.cs:1178, someone outside the list
    [InlineData(Vm + "[Terminal] [Command] XY (override) UAL238: FH 270", Vm + "[Terminal] [Command] ?? (override) UAL238: FH 270")]
    // Yaat.Sim SimulationEngine.Commands.cs:39: the AI's own commands, echoed by LiveRoomHost.cs:123
    [InlineData(Vm + "[Terminal] [Command] AI UAL238: FH 270", Vm + "[Terminal] [Command] AI UAL238: FH 270")]
    // Simulation/RoomEngine.cs:1145, in the server log: the AI is no person
    [InlineData(
        At + "[dbug] Yaat.Server.Simulation.RoomEngine: [SendCommand] UAL238: 'FH 270' (from AI)",
        At + "[dbug] Yaat.Server.Simulation.RoomEngine: [SendCommand] UAL238: 'FH 270' (from AI)"
    )]
    // Simulation/RoomEngine.cs:1498
    [InlineData(Vm + "[Terminal] [Chat] CM : break for lunch", Vm + "[Terminal] [Chat] A1 : break for lunch")]
    // Simulation/RoomEngine.cs:1498
    [InlineData(Vm + "[Terminal] [Chat] XY : back in 5", Vm + "[Terminal] [Chat] ?? : back in 5")]
    // Simulation/RoomEngine.cs:147-148
    [InlineData(Vm + "[Terminal] [Command] [CRC] 2W UAL238: FP B738/L KSFO KOAK", Vm + "[Terminal] [Command] [CRC] 2W UAL238: FP B738/L KSFO KOAK")]
    // Messaging.cs:71
    [InlineData(Vm + "[Terminal] [Chat] OAK_TWR UAL238: say altitude", Vm + "[Terminal] [Chat] OAK_TWR UAL238: say altitude")]
    // Hubs/CrcClientState.Eram.Messages.cs:151
    [InlineData(
        Vm + "[Terminal] [Chat] SECTOR 12 : UAL238 frequency change approved",
        Vm + "[Terminal] [Chat] SECTOR 12 : UAL238 frequency change approved"
    )]
    // Eram.Messages.cs:185
    [InlineData(Vm + "[Terminal] [Chat] SIGWX : KOAK TS >> MOV E 10KT", Vm + "[Terminal] [Chat] SIGWX : KOAK TS >> MOV E 10KT")]
    // A command result naming the assignee: $"Assigned to {ownerInitials}. Prefix with ** to override."
    // Simulation/RoomEngine.cs:1170
    [InlineData(
        Vm + "[Terminal] [Error]  UAL238: Assigned to XY. Prefix with ** to override.",
        Vm + "[Terminal] [Error]  UAL238: Assigned to ??. Prefix with ** to override."
    )]
    // Simulation/RoomEngine.cs:1170
    [InlineData(
        Vm + "[Terminal] [Error]  UAL238: Assigned to CM. Prefix with ** to override.",
        Vm + "[Terminal] [Error]  UAL238: Assigned to A1. Prefix with ** to override."
    )]
    // Simulation/RoomEngine.cs:1170
    [InlineData(
        Vm + "[Terminal] [Error]  UAL238: Assigned to JD. Prefix with ** to override.",
        Vm + "[Terminal] [Error]  UAL238: Assigned to A0. Prefix with ** to override."
    )]
    // Kept: echoed alias text, aviation text with no who.
    [InlineData(Sys + "TWR frequency is 118.3", Sys + "TWR frequency is 118.3")]
    // Simulation/TickProcessor.cs:101
    [InlineData(Vm + "[Terminal] [System]  UAL238: Expecting ILS 28R approach", Vm + "[Terminal] [System]  UAL238: Expecting ILS 28R approach")]
    // Simulation/TickProcessor.cs:313
    [InlineData(
        Vm + "[Terminal] [PilotSpeech]  UAL238: climb and maintain 5000, United 238",
        Vm + "[Terminal] [PilotSpeech]  UAL238: climb and maintain 5000, United 238"
    )]
    // yaat: the CRC lobby lists "{ClientId}({DisplayName ?? "no-name"})" (Yaat.Client/ViewModels/MainViewModel.Rooms.cs:585).
    // MainViewModel.Rooms.cs:583
    [InlineData(
        Vm + "[CrcLobby] Pull returned: 3 clients (abc(OAK_TWR (Zed Outsider)), def(SFO_GND (BOB)), ghi(Pat Smith))",
        Vm + "[CrcLobby] Pull returned: 3 clients (abc(OAK_TWR (??)), def(SFO_GND (??)), ghi(A1))"
    )]
    // MainViewModel.Rooms.cs:1081
    [InlineData(
        Vm + "[CrcLobby] Push received: 2 clients (jkl(no-name), mno(unknown))",
        Vm + "[CrcLobby] Push received: 2 clients (jkl(no-name), mno(unknown))"
    )]
    // MainViewModel.Rooms.cs:1081, CRC clients with no position: a one-word upper-case real name and a name with parentheses
    [InlineData(
        Vm + "[CrcLobby] Push received: 3 clients (jkl(BOB), mno(Zed (Jr)), pqr(SFO_TWR))",
        Vm + "[CrcLobby] Push received: 3 clients (jkl(??), mno(??), pqr(SFO_TWR))"
    )]
    // yaat: web client URLs carry the user's own initials (MainViewModel.Rooms.cs:262/:305).
    // MainViewModel.Rooms.cs:281
    [InlineData(
        VmWarn + "OpenStripsInBrowser failed for http://box:5000/vstrips/?initials=CM&artcc=ZOA&room=r1",
        VmWarn + "OpenStripsInBrowser failed for http://{server}:5000/vstrips/?initials=A1&artcc=ZOA&room=r1"
    )]
    // MainViewModel.Rooms.cs:324
    [InlineData(
        VmWarn + "OpenTdlsInBrowser failed for https://my.private.box/vtdls/?initials=XY&artcc=ZOA",
        VmWarn + "OpenTdlsInBrowser failed for https://{server}/vtdls/?initials=??&artcc=ZOA"
    )]
    // MainViewModel.Rooms.cs:324
    [InlineData(
        VmWarn + "OpenTdlsInBrowser failed for https://my.private.box/vtdls/?initials=JD&artcc=ZOA",
        VmWarn + "OpenTdlsInBrowser failed for https://{server}/vtdls/?initials=A0&artcc=ZOA"
    )]
    // No URL writes cid= today (the doc comments at MainViewModel.Rooms.cs:242/:288 still name it); hidden all the same.
    [InlineData(
        VmWarn + "OpenTdlsInBrowser failed for https://my.private.box/vtdls/?cid=1999999&initials=CM",
        VmWarn + "OpenTdlsInBrowser failed for https://{server}/vtdls/?cid=??&initials=A1"
    )]
    [InlineData(
        VmWarn + "OpenStripsInBrowser failed for http://box:5000/vstrips/?cid=1826127&initials=JD",
        VmWarn + "OpenStripsInBrowser failed for http://{server}:5000/vstrips/?cid=A0&initials=A0"
    )]
    // MainViewModel.Rooms.cs:370
    [InlineData(
        VmWarn + "CopyTrafficFeedUrl failed for https://yaat1.leftos.dev/feed/r1/v3/vatsim-data.json",
        VmWarn + "CopyTrafficFeedUrl failed for https://yaat1.leftos.dev/feed/r1/v3/vatsim-data.json"
    )]
    // yaat: server URLs.
    // Yaat.Client.Core/Services/ServerConnection.cs:150
    [InlineData(
        Services + "ServerConnection: Connecting to https://my.private.box:5000/hubs/training",
        Services + "ServerConnection: Connecting to https://{server}:5000/hubs/training"
    )]
    // Yaat.Client.Core/Services/ServerConnection.cs:150
    [InlineData(
        Services + "ServerConnection: Connecting to https://yaat1.leftos.dev/hubs/training",
        Services + "ServerConnection: Connecting to https://yaat1.leftos.dev/hubs/training"
    )]
    // Yaat.Client.Core/Services/VatsimAuthClient.cs:212
    [InlineData(
        Services + "VatsimAuthClient: Opening VATSIM login for http://box:5000",
        Services + "VatsimAuthClient: Opening VATSIM login for http://{server}:5000"
    )]
    // Yaat.Client.Core/Services/VatsimAuthClient.cs:197
    [InlineData(
        ServicesWarn + "VatsimAuthClient: auth/required probe failed for http://yaat; assuming auth is required",
        ServicesWarn + "VatsimAuthClient: auth/required probe failed for http://{server}; assuming auth is required"
    )]
    // Yaat.Client/Services/UrlLauncher.cs:29
    [InlineData(
        ServicesWarn + "UrlLauncher: Failed to open URL https://github.com/leftos/yaat/releases in default browser",
        ServicesWarn + "UrlLauncher: Failed to open URL https://github.com/leftos/yaat/releases in default browser"
    )]
    public void RealTemplates_HidePeopleAndPrivateHosts(string line, string expected) => Assert.Equal(expected, Scrub(line));

    [Fact]
    public void ServerHostsOf_DropsTheOfficialServerAndLoopback() =>
        Assert.Equal(["my.private.box", "lab.example.net", "box", "yaat"], ClientLogScrubber.ServerHostsOf(SavedServerUrls));

    [Fact]
    public void OneWordServerHosts_AreHiddenOnlyInHostPosition()
    {
        Assert.Equal("Connecting to http://{server}:5000/hubs", Scrub("Connecting to http://box:5000/hubs"));
        Assert.Equal("SocketException: refused ({server}:5130)", Scrub("SocketException: refused (yaat:5130)"));
        Assert.Equal($"{Prefix}[SendCommand] N123AB: 'BOX OAK'", Scrub($"{Prefix}[SendCommand] N123AB: 'BOX OAK'"));
        Assert.Equal(@"cache at %LOCALAPPDATA%\yaat\cache", Scrub(@"cache at %LOCALAPPDATA%\yaat\cache"));
    }

    [Theory]
    [InlineData("joined the room")]
    [InlineData("left the room")]
    [InlineData("created the room")]
    [InlineData("was kicked from the room")]
    [InlineData("loaded scenario 'OAK 28R'")]
    [InlineData("unloaded the scenario")]
    [InlineData("set auto-accept delay to 5s")]
    [InlineData("set command run delay to 2s")]
    [InlineData("set auto-delete to scenario default")]
    [InlineData("set departure auto-delete to off")]
    [InlineData("enabled auto cleared-to-land")]
    [InlineData("disabled live traffic")]
    [InlineData("set live traffic ceiling to automatic")]
    [InlineData("set live traffic filter: KOAK 20nm")]
    [InlineData("loaded weather 'KOAK VFR'")]
    [InlineData("cleared weather")]
    [InlineData("updated arrival generators (3)")]
    [InlineData("enabled DCT fix validation")]
    [InlineData("updated solo pacing rates")]
    [InlineData("amended flight plan for UAL238")]
    [InlineData("went live")]
    public void RoomActionLines_ReplaceWho(string action)
    {
        Assert.Equal($"{Prefix}[Terminal] [System]  : A1 {action}", Scrub($"{Prefix}[Terminal] [System]  : CM {action}"));
        Assert.Equal($"{Prefix}[Terminal] [System]  : ?? {action}", Scrub($"{Prefix}[Terminal] [System]  : XY {action}"));
    }

    [Fact]
    public void RpoPullLine_HidesThePulledName()
    {
        Assert.Equal(
            $"{Prefix}[Terminal] [System]  : A0 pulled ?? into the room as an RPO",
            Scrub($"{Prefix}[Terminal] [System]  : JD pulled Carl Lee into the room as an RPO")
        );
        Assert.Equal(
            $"{Prefix}[Terminal] [System]  : A0 pulled A1 into the room as an RPO",
            Scrub($"{Prefix}[Terminal] [System]  : JD pulled Pat Smith into the room as an RPO")
        );
    }

    [Fact]
    public void OneWordServerHosts_WithoutAPortOrInAnotherCase_AreHidden()
    {
        Assert.Equal(
            "auth/required probe failed for http://{server}; assuming auth is required",
            Scrub("auth/required probe failed for http://box; assuming auth is required")
        );
        Assert.Equal("Failed to open URL http://{server} in default browser", Scrub("Failed to open URL http://box in default browser"));
        Assert.Equal("Connected to http://{server}:5000", Scrub("Connected to http://Box:5000"));
    }

    [Fact]
    public void EchoedAliasText_IsKept()
    {
        Assert.Equal($"{Prefix}[Terminal] [System]  : TWR frequency is 118.3", Scrub($"{Prefix}[Terminal] [System]  : TWR frequency is 118.3"));
        Assert.Equal(
            $"{Prefix}[Terminal] [System]  : VFR departures use runway 30",
            Scrub($"{Prefix}[Terminal] [System]  : VFR departures use runway 30")
        );
    }

    [Fact]
    public void SectorCodeAfterAPositionCallsign_IsKept()
    {
        string line = $"{Prefix}[Terminal] [System]  : CRC position synced: now acting as OAK_TWR (TW)";

        Assert.Equal(line, Scrub(line));
    }

    [Fact]
    public void AssignLines_HideTheTargetUnlessListed()
    {
        Assert.Equal(
            $"{Prefix}[Terminal] [System]  : A0 assigned UAL238, SKW12 to ??",
            Scrub($"{Prefix}[Terminal] [System]  : JD assigned UAL238, SKW12 to XY")
        );
        Assert.Equal($"{Prefix}[Terminal] [System]  : A0 assigned UAL238 to A1", Scrub($"{Prefix}[Terminal] [System]  : JD assigned UAL238 to CM"));
        Assert.Equal(
            $"{Prefix}[Terminal] [System]  : ?? unassigned UAL238 (available to all)",
            Scrub($"{Prefix}[Terminal] [System]  : XY unassigned UAL238 (available to all)")
        );
    }

    [Fact]
    public void BodiesWithoutAWho_AreKept()
    {
        Assert.Equal($"{Prefix}[Terminal] [System]  : Scenario loaded", Scrub($"{Prefix}[Terminal] [System]  : Scenario loaded"));
        Assert.Equal($"{Prefix}[Terminal] [System]  : KOAK weather updated", Scrub($"{Prefix}[Terminal] [System]  : KOAK weather updated"));
        Assert.Equal($"{Prefix}Layout: OAK is loading", Scrub($"{Prefix}Layout: OAK is loading"));
        Assert.Equal($"{Prefix}[Terminal] [System]  : YAAT spawned N123AB", Scrub($"{Prefix}[Terminal] [System]  : YAAT spawned N123AB"));
    }

    [Fact]
    public void OwnProfilePath_BecomesUserProfileVariable()
    {
        Assert.Equal(@"Log: %USERPROFILE%\AppData\Local\yaat\yaat-client.log", Scrub(@"Log: C:\Users\danmi\AppData\Local\yaat\yaat-client.log"));
        Assert.Equal("Log: %USERPROFILE%/AppData/Local", Scrub("Log: c:/users/DANMI/AppData/Local"));
    }

    [Fact]
    public void AnotherWindowsUsersPath_HidesTheName()
    {
        Assert.Equal(@"at C:\Users\{user}\x", Scrub(@"at C:\Users\someone\x"));
        Assert.Equal(@"at C:\Users\{user}\x", Scrub(@"at C:\Users\Dan Miller\x"));
    }

    [Fact]
    public void UnixHomePaths_HideTheName_ButUrlPathsAreKept()
    {
        Assert.Equal("/home/{user}/.local/share/yaat", Scrub("/home/bob/.local/share/yaat"));
        Assert.Equal("path=/Users/{user}/Library/Logs", Scrub("path=/Users/carol/Library/Logs"));
        Assert.Equal("GET https://api.example.com/Users/123/profile", Scrub("GET https://api.example.com/Users/123/profile"));
    }

    [Fact]
    public void UserAndMachineNames_AreReplacedAsWholeWords()
    {
        Assert.Equal("signed in as {user} on {machine}", Scrub("signed in as danmi on DANMI-PC"));
        Assert.Equal("danmiller kept", Scrub("danmiller kept"));
    }

    [Fact]
    public void UserNameInsideTheOfficialHost_IsKept()
    {
        ClientLogScrubContext ctx = Context with { UserName = "leftos" };

        Assert.Equal(
            "https://yaat1.leftos.dev/hubs/training as {user}",
            ClientLogScrubber.Scrub("https://yaat1.leftos.dev/hubs/training as leftos", ctx)
        );
    }

    [Fact]
    public void TerminalLine_WithAParticipantsInitials_UsesTheirPseudonym() =>
        Assert.Equal($"{Prefix}[Terminal] [Command] A0 UAL238: FH 270", Scrub($"{Prefix}[Terminal] [Command] JD UAL238: FH 270"));

    [Fact]
    public void TerminalLine_WithUnknownInitials_HidesThem()
    {
        Assert.Equal($"{Prefix}[Terminal] [Say] ?? UAL238: hello", Scrub($"{Prefix}[Terminal] [Say] XY UAL238: hello"));
        Assert.Equal($"{Prefix}[Terminal] [System] YAAT N123AB: spawned", Scrub($"{Prefix}[Terminal] [System] YAAT N123AB: spawned"));
        Assert.Equal($"{Prefix}[Terminal] [Command] (null) UAL238: FH 270", Scrub($"{Prefix}[Terminal] [Command] (null) UAL238: FH 270"));
    }

    [Fact]
    public void InitialsThatAreACommandVerb_StayInTheCommand_ButNotInTheInitialsSlot() =>
        Assert.Equal($"{Prefix}[Terminal] [Command] A1 UAL238: CM 050", Scrub($"{Prefix}[Terminal] [Command] CM UAL238: CM 050"));

    // A position callsign always holds an underscore, so a one-word upper-case lobby entry is a real name: the listed
    // person's pseudonym, else unknown.
    [Theory]
    [InlineData("BOB", "A0")]
    [InlineData("SAM", "??")]
    public void CrcLobbyLine_AOneWordNameIsNoCallsign(string name, string expected)
    {
        ClientLogScrubContext context = Context with { Participants = [new LogParticipant("1234567", "BOB", "BB")] };

        string scrubbed = ClientLogScrubber.Scrub($"{Prefix}[CrcLobby] Pull returned: 1 clients (abc({name}))", context);

        Assert.Equal($"{Prefix}[CrcLobby] Pull returned: 1 clients (abc({expected}))", scrubbed);
    }

    [Fact]
    public void CrcLobbyLine_ReplacesNamesButKeepsCallsigns()
    {
        string line =
            $"{Prefix}[CrcLobby] Pull returned: 5 clients (abc(OAK_TWR (Jane Doe)), def(SFO_GND (Bob Jones)), ghi(SFO_TWR), jkl(no-name), "
            + "mno(Carl Lee))";

        Assert.Equal(
            $"{Prefix}[CrcLobby] Pull returned: 5 clients (abc(OAK_TWR (A0)), def(SFO_GND (??)), ghi(SFO_TWR), jkl(no-name), mno(??))",
            Scrub(line)
        );
    }

    [Fact]
    public void CrcTerminalLines_HideTheNameAfterAPositionCallsign()
    {
        Assert.Equal(
            $"{Prefix}[Terminal] [System]  : [CRC] OAK_TWR (??) disconnected",
            Scrub($"{Prefix}[Terminal] [System]  : [CRC] OAK_TWR (Bob Jones) disconnected")
        );
        Assert.Equal(
            $"{Prefix}[Terminal] [System]  : [CRC] Pulled SFO_GND (A0) into room",
            Scrub($"{Prefix}[Terminal] [System]  : [CRC] Pulled SFO_GND (Jane Doe) into room")
        );
    }

    [Fact]
    public void MembershipLines_HideWhoUnlessTheyAreAParticipant()
    {
        Assert.Equal($"{Prefix}[Terminal] [System]  : ?? joined the room", Scrub($"{Prefix}[Terminal] [System]  : XY joined the room"));
        Assert.Equal($"{Prefix}[Terminal] [System]  : A0 left the room", Scrub($"{Prefix}[Terminal] [System]  : JD left the room"));
        Assert.Equal(
            $"{Prefix}[Terminal] [System] A1 : A1 is loading 'OAK 28R'…",
            Scrub($"{Prefix}[Terminal] [System] CM : CM is loading 'OAK 28R'…")
        );
    }

    // Context issues A0 and A1 only: B2 is a departed person whose initials look like a pseudonym, never kept by its shape.
    [Fact]
    public void DepartedPseudonymShapedInitials_BecomeQuestionMarks()
    {
        Assert.Equal($"{Prefix}[Terminal] [Command] ?? UAL238: FH 270", Scrub($"{Prefix}[Terminal] [Command] B2 UAL238: FH 270"));
        Assert.Equal($"{Prefix}[Terminal] [System]  : ?? joined the room", Scrub($"{Prefix}[Terminal] [System]  : B2 joined the room"));
        Assert.Equal($"{Prefix}[SendCommand] UAL238: 'FH 270' (from ??)", Scrub($"{Prefix}[SendCommand] UAL238: 'FH 270' (from B2)"));
        Assert.Equal(
            $"{Prefix}[Terminal] [System]  : A0 pulled ?? into the room as an RPO",
            Scrub($"{Prefix}[Terminal] [System]  : JD pulled B2 into the room as an RPO")
        );
        Assert.Equal(
            $"{Prefix}[Terminal] [System]  : [CRC] OAK_TWR (??) connected",
            Scrub($"{Prefix}[Terminal] [System]  : [CRC] OAK_TWR (B2) connected")
        );
        Assert.Equal($"{Prefix}[CrcLobby] Pull returned: 1 clients (abc(??))", Scrub($"{Prefix}[CrcLobby] Pull returned: 1 clients (abc(B2))"));
        Assert.Equal("open /vstrips/?initials=??&cid=??", Scrub("open /vstrips/?initials=B2&cid=B2"));
    }

    [Fact]
    public void IssuedPseudonym_IsKept()
    {
        Assert.Equal($"{Prefix}[Terminal] [Command] A1 UAL238: FH 270", Scrub($"{Prefix}[Terminal] [Command] A1 UAL238: FH 270"));
        Assert.Equal($"{Prefix}[Terminal] [System]  : A0 joined the room", Scrub($"{Prefix}[Terminal] [System]  : A0 joined the room"));
        Assert.Equal($"{Prefix}[SendCommand] UAL238: 'FH 270' (from A1)", Scrub($"{Prefix}[SendCommand] UAL238: 'FH 270' (from A1)"));
        Assert.Equal(
            $"{Prefix}[Terminal] [System]  : [CRC] OAK_TWR (A0) connected",
            Scrub($"{Prefix}[Terminal] [System]  : [CRC] OAK_TWR (A0) connected")
        );
        Assert.Equal($"{Prefix}[CrcLobby] Pull returned: 1 clients (abc(A1))", Scrub($"{Prefix}[CrcLobby] Pull returned: 1 clients (abc(A1))"));
    }

    [Fact]
    public void NestedParenthesesName_IsHiddenWhole()
    {
        Assert.Equal(
            $"{Prefix}[Terminal] [System]  : [CRC] OAK_TWR (??) connected",
            Scrub($"{Prefix}[Terminal] [System]  : [CRC] OAK_TWR (Zed (Jr) Outsider) connected")
        );
        Assert.Equal(
            $"{Prefix}[Terminal] [System]  : [CRC] OAK_TWR (??) activated",
            Scrub($"{Prefix}[Terminal] [System]  : [CRC] OAK_TWR (A (B (C)) D) activated")
        );
        Assert.Equal(
            $"{Prefix}[CrcLobby] Pull returned: 2 clients (abc(OAK_TWR (??)), def(SFO_GND (??)))",
            Scrub($"{Prefix}[CrcLobby] Pull returned: 2 clients (abc(OAK_TWR (Zed (Jr) Outsider)), def(SFO_GND (A (B (C)) D)))")
        );
    }

    [Fact]
    public void UnbalancedName_HidesToTheEndOfTheSlot()
    {
        Assert.Equal(
            $"{Prefix}[Terminal] [System]  : [CRC] OAK_TWR (??)\n{Prefix}[Terminal] [System]  : [CRC] SFO_GND connected",
            Scrub($"{Prefix}[Terminal] [System]  : [CRC] OAK_TWR (Zed (Jr Outsider connected\n{Prefix}[Terminal] [System]  : [CRC] SFO_GND connected")
        );
        Assert.Equal(
            $"{Prefix}[CrcLobby] Pull returned: 1 clients (abc(??)",
            Scrub($"{Prefix}[CrcLobby] Pull returned: 1 clients (abc(Zed (Jr Outsider)")
        );
    }

    [Fact]
    public void Cid_IsReplacedOnlyAsAWholeWord() => Assert.Equal("tcp=C1826127 cid=A0", Scrub("tcp=C1826127 cid=1826127"));

    [Fact]
    public void OfficialLocalAndPublicServers_AreKept()
    {
        Assert.Equal("Connecting to https://yaat1.leftos.dev/hubs/training", Scrub("Connecting to https://yaat1.leftos.dev/hubs/training"));
        Assert.Equal("Connecting to http://localhost:5130", Scrub("Connecting to http://localhost:5130"));
        Assert.Equal("Connecting to http://[::1]:5130", Scrub("Connecting to http://[::1]:5130"));
        Assert.Equal("GET https://api.github.com/repos/x/y/releases", Scrub("GET https://api.github.com/repos/x/y/releases"));
    }

    [Fact]
    public void SavedServerHosts_AreHiddenWithOrWithoutAScheme()
    {
        Assert.Equal("Connecting to https://{server}:5000/hubs", Scrub("Connecting to https://my.private.box:5000/hubs"));
        Assert.Equal("Connecting to ws://{server}:8080/hubs", Scrub("Connecting to ws://lab.example.net:8080/hubs"));
        Assert.Equal("SocketException: refused ({server}:5000)", Scrub("SocketException: refused (my.private.box:5000)"));
    }

    [Fact]
    public void BareIpAddresses_AreHiddenUnlessLoopback()
    {
        Assert.Equal("peer {ip} via 127.0.0.1", Scrub("peer 10.0.0.5 via 127.0.0.1"));
        Assert.Equal("peer {ip} and [{ip}]:5000 via ::1", Scrub("peer fe80::1ff:fe23:4567:890a and [2001:db8::1]:5000 via ::1"));
    }

    [Fact]
    public void AviationNumbers_AreKept()
    {
        string line = $"{Prefix}UAL238 contact ground 121.8 at 37.620165,-122.379960 beacon 4521 Version=10.0.0.0 FL350";

        Assert.Equal(line, Scrub(line));
    }

    [Fact]
    public void Secrets_AreHidden()
    {
        Assert.Equal("token {secret} end", Scrub("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc_DEF-123 end"));
        Assert.Equal("callback http://localhost:5130/cb?code={secret}&state=x", Scrub("callback http://localhost:5130/cb?code=abc123&state=x"));
        Assert.Equal("Authorization: Bearer {secret}", Scrub("Authorization: Bearer abc.def"));
    }

    [Fact]
    public void Email_IsHidden() => Assert.Equal("contact {email} now", Scrub("contact jane.doe+yaat@example.com now"));

    [Fact]
    public void SpeechTranscript_IsKept_WithParticipantNamesReplaced()
    {
        string line = $"{Prefix}PTT transcript: united 238 climb and maintain 5000";

        Assert.Equal(line, Scrub(line));
        Assert.Equal($"{Prefix}PTT transcript: A0 handing off to A1", Scrub($"{Prefix}PTT transcript: Jane Doe handing off to Pat Smith"));
    }
}
