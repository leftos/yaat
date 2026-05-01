using System.Text.Json.Serialization;

namespace Yaat.Client.Services;

/// <summary>
/// Source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>
/// covering every type that crosses <c>/hubs/training</c>. SignalR's
/// <c>JsonHubProtocol</c> calls <c>JsonSerializer.Serialize&lt;object&gt;(...)</c>
/// on every method argument and broadcast payload, which under default
/// .NET 10 WASM publishes throws <c>JsonSerializerIsReflectionDisabled</c>
/// — reflection-based metadata is off to keep the bundle slim.
///
/// The fix is compile-time metadata: for every type the hub sees, register
/// a <c>[JsonSerializable]</c> attribute on this partial. The
/// <see cref="ServerConnection.ConfigureHubJsonProtocol"/> hook then inserts
/// <c>YaatHubJsonContext.Default</c> at the head of the
/// <c>TypeInfoResolverChain</c>, so every serialize/deserialize call routes
/// through compiled code instead of runtime reflection.
///
/// Top-level types are split into three groups for readability — broadcast
/// payloads (server-to-client), method return types (server-to-client), and
/// method argument types (client-to-server). Nested types are picked up
/// automatically from public properties on the listed types, but a few that
/// only appear inside lists or arrays are listed explicitly so the source
/// generator emits collection metadata for them.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNameCaseInsensitive = true)]
// --- Built-in primitives + arrays / lists used as raw method args. ---
// Nullable reference types can't appear in typeof, but at runtime
// `string?` and `string` are the same Type, so one entry covers both.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<string>))]
// --- Broadcast payloads (server.On<T>). ---
[JsonSerializable(typeof(AircraftDto))]
[JsonSerializable(typeof(TerminalBroadcastDto))]
[JsonSerializable(typeof(RoomMemberChangedDto))]
[JsonSerializable(typeof(CrcLobbyChangedDto))]
[JsonSerializable(typeof(CrcRoomMembersChangedDto))]
[JsonSerializable(typeof(WeatherChangedDto))]
[JsonSerializable(typeof(PositionDisplayConfigDto))]
[JsonSerializable(typeof(ScenarioLoadedDto))]
[JsonSerializable(typeof(AircraftAssignmentsDto))]
[JsonSerializable(typeof(SessionSettingsDto))]
// Strip-side broadcast payloads (FlightStripsStateDto, List<StripItemDto>)
// live in YaatStripsHubJsonContext (Yaat.Client.Strips) so the WASM client
// can ship without Core. The resolver chain in
// ServerConnection.ConfigureHubJsonProtocol consults both contexts.
// --- Hub method return types (client InvokeAsync<T>). ---
[JsonSerializable(typeof(RoomStateDto))]
[JsonSerializable(typeof(List<TrainingRoomInfoDto>))]
[JsonSerializable(typeof(TrainingRoomInfoDto))]
[JsonSerializable(typeof(LoadScenarioResultDto))]
// List<AccessibleFacilityDto>, FlightStripsConfigDto, CommandResultDto live
// in YaatStripsHubJsonContext.
[JsonSerializable(typeof(UnloadScenarioResultDto))]
[JsonSerializable(typeof(RewindResultDto))]
[JsonSerializable(typeof(TimelineInfoDto))]
[JsonSerializable(typeof(GroundLayoutDto))]
[JsonSerializable(typeof(FacilityVideoMapsDto))]
[JsonSerializable(typeof(ApproachReportDto))]
[JsonSerializable(typeof(List<CrcLobbyClientDto>))]
[JsonSerializable(typeof(List<CrcRoomMemberDto>))]
// --- Method argument types beyond primitives. ---
[JsonSerializable(typeof(FlightPlanAmendmentDto))]
// --- Nested types that appear inside lists / arrays of registered types
//     and are also occasionally referenced bare (defensive). ---
// StripItemDto, StripBayConfigDto, StripBayContentsDto, AccessibleFacilityDto
// are registered in YaatStripsHubJsonContext (Yaat.Client.Strips).
[JsonSerializable(typeof(RoomMemberDto))]
[JsonSerializable(typeof(CrcLobbyClientDto))]
[JsonSerializable(typeof(CrcRoomMemberDto))]
[JsonSerializable(typeof(WindLayerDto))]
[JsonSerializable(typeof(GroundNodeDto))]
[JsonSerializable(typeof(GroundEdgeDto))]
[JsonSerializable(typeof(GroundArcDto))]
[JsonSerializable(typeof(GroundRunwayDto))]
[JsonSerializable(typeof(VideoMapInfoDto))]
[JsonSerializable(typeof(StarsAreaDto))]
[JsonSerializable(typeof(MapGroupDto))]
[JsonSerializable(typeof(AssignableMemberDto))]
[JsonSerializable(typeof(ApproachScoreDto))]
[JsonSerializable(typeof(RunwayStatsDto))]
internal partial class YaatHubJsonContext : JsonSerializerContext;
