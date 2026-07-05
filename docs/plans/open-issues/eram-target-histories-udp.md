# #252 — EramTargetHistories over a UDP entity transport

**Finding (why this is big):** ERAM target history entries are **UDP-only**. CRC's WebSocket `IClient` has no `ReceiveEramTargetHistories` method (only `DeleteEramTargetHistoryEntries`); the history *data* is delivered exclusively as UDP `EntityUpdate` messages (`Vatsim.Nas.Crc.Networking/UdpMessenger.cs` → `entitySubscriptionManager.PublishEntityReceivedEvent`). YAAT sends all entities over WebSocket and runs only a UDP **keepalive stub** (`Udp/UdpStubServer.cs`), so histories need a new server-side UDP entity-send path. The audit mis-scoped this as a WebSocket `BuildTopicData` case.

## Wire contract (verified against vNAS + decompiled CRC)
- `IUdpMessage` union (`vatsim-vnas/messaging/Udp/IUdpMessage.cs`): `[0]=KeepAlive`, `[1]=RegisterConnection(connectionId)`, `[2]=AcknowledgeConnectionRegistration`, `[3]=EntityUpdate(Topic, IEntity)`.
- MessagePack union framing: `[unionTag, value]`. So a history push = **`[3, [ Topic, [13, <entryFields>] ]]`**:
  - `EntityUpdate` (`[MessagePackObject]`, Key0 Topic, Key1 Entity) → `[Topic, Entity]`.
  - `Topic` → confirm exact shape in `vatsim-vnas/messaging/Topics/Topic.cs`; expected `[categoryInt, facilityId, null, null]`. **TopicCategory.EramTargetHistories = 15** (0-indexed enum).
  - `Entity` is the `IEntity` union; **`EramTargetHistoryEntryDto` = Union(13)** → `[13, [Id, AircraftId, CreatedAt, BeaconCode, Location, PressureAltitude, AdjustedAltitude, SymbolType]]` (`EramTargetHistoryEntryDto.cs`: Key0..7).
- CRC keepalive/register already handled by `UdpStubServer` (`[0,[]]`→pong, `[1,[connId]]`→ack `[2,[]]`).

## Correlation (the crux)
- CRC registers over UDP with `mHubConnection.ConnectionId` = the **negotiate connection token** (`NegotiateHandler` returns `connectionId = connectionToken`, a 15-char token).
- YAAT's `CrcClientState._clientId` is a **fresh `Guid`** (`CrcWebSocketHandler.cs:34`) — NOT the token. The token arrives as `context.Request.Query["id"]` and is currently only `Consume`d to resolve the CID, then discarded.
- **Fix:** thread the connection token into `CrcClientState` (store `_connectionToken`, expose `ConnectionToken`). Then UDP endpoint (keyed by token in `UdpStubServer._clients`) ↔ `CrcClientState` (by `ConnectionToken`) ↔ subscriptions/facility.
  - Note `tokenStore.Consume(token)` currently removes the token; keep the token string on the handler after consuming for the CID (it's still the connection id CRC will register with).

## Implementation steps
1. **Connection token on the client.** `CrcWebSocketHandler`: pass `token` into `CrcClientState` ctor; store `_connectionToken`; expose `string? ConnectionToken`.
2. **UdpStubServer → injectable entity sender.** Add `public void SendEntity(string connectionId, byte[] bytes)` (send to `_clients[connectionId]` if present) and expose `bool IsRegistered(string connectionId)`. Register as a **singleton** and back the hosted service with it: `builder.Services.AddSingleton<UdpEntityServer>(); AddHostedService(sp => sp.GetRequiredService<UdpEntityServer>());`. (Rename to `UdpEntityServer`; keep keepalive/register behavior.)
3. **DTO.** `EramTargetHistoryEntryDto` in `CrcDtos.Session.cs` — Key0 Id (string), Key1 AircraftId (string), Key2 CreatedAt (DateTime), Key3 BeaconCode (int?), Key4 Location (Point), Key5 PressureAltitude (int?), Key6 AdjustedAltitude (int?), Key7 SymbolType (EramTargetSymbolType, StringEnumFormatter). (Only needed if serializing via types; hand-writing bytes avoids it.)
4. **Converter.** `DtoConverter.ToEramTargetHistory(ac, createdAt, floor, asrSites)` → one entry per `ac.PositionHistory` point: `Id = "CALLSIGN{cs}_{i}"`, `Location` = the point, `BeaconCode`/`AdjustedAltitude`/`SymbolType` = **current** values (PositionHistory stores only Lat/Lon — per-point symbol is an accepted approximation), `PressureAltitude` = null, `CreatedAt` = passed-in time. Standby → null beacon/alt (reuse the #250 standby logic).
5. **UDP EntityUpdate serialization.** A small `UdpEntityWriter` that hand-writes `[3, [ [15, facilityId, null, null], [13, [id, aircraftId, createdAt, beaconCode, location, null, adjAlt, symbolStr]] ]]` with `MessagePackWriter` (mirror `BuildPayload`'s topic writing; enum → int for category, symbol → string via the StringEnumFormatter convention). Confirm DateTime + enum encodings against how `BuildPayload` already writes them for WebSocket.
6. **Broadcast.** In the per-tick loop (or a dedicated ERAM history pass): for each CRC client subscribed to `EramTargetHistories` whose `ConnectionToken` is UDP-registered, build entries for each visible aircraft (via `ResolveEramTargetConfig(sub.FacilityId)` for the symbol) and `SendEntity`. Delete on aircraft removal via `DeleteEramTargetHistoryEntries` over the **WebSocket** (that method exists in IClient) — send `["CALLSIGN{cs}_0".."_{n}"]`. Inject `UdpEntityServer` into `CrcBroadcastService` (primary-ctor param).
7. **Tests.** Unit-test the serialization bytes (`[3,[...]]` round-trips to a CRC-deserializable `EntityUpdate`) and `ToEramTargetHistory` (entry count == history points, ids stable, standby nulls). The UDP send/endpoint path can't be E2E-tested without a real CRC — cover the byte format + converter, and `log()` the send.

## Open confirmations at implementation time
- Exact `Topic` MessagePack shape (array vs map; nullable fields) — read `Topic.cs`.
- DateTime + enum MessagePack encoding YAAT uses (match `BuildPayload`).
- Whether `tokenStore.Consume` removing the token breaks anything if we also keep it on the client (it shouldn't — consume is for CID resolution).

Delete this file once #252 is implemented.
