# Tower Cab aircraft over UDP (vNAS parity)

**Goal.** Make yaat-server deliver Tower Cab aircraft over UDP the way vNAS does. Then CRC's Tower Cab display and TowerCab 3D (a third-party vNAS client) get the same transport on YAAT as on vNAS: SignalR for new aircraft, removals and periodic full sets, and UDP for the ~1 Hz position stream.

**Where YAAT stands today.**
- `Udp/UdpEntityServer.cs` listens on 6809 (hardcoded, `:18`). It does the keepalive/register/ack handshake and keys clients by the **negotiate connection token** (`:8-14`, `:26-30`).
- The only entity it pushes is `EramTargetHistoryEntryDto` (union tag 13, `Udp/UdpEntityContract.cs:90`), sent from `CrcBroadcastService.cs:~2633` via `UdpEntityCodec.SerializeHistoryEntry`.
- Tower Cab aircraft go over the WebSocket only. `CrcBroadcastService.cs:2282-2306` builds the per-tick batch when the `TowerCab` fingerprint changes, and `:2745-2755` sends `ReceiveTowerCabAircrafts`/`DeleteTowerCabAircrafts`. There is also a full resend every `CrcVisibilityTracker.TowerCabResendIntervalSeconds` (10 s). Visibility is per ARTCC tower-cab airport, 20 nm and under the ceiling (`CrcVisibilityTracker.cs:389-409`).
- **Direct connections** (`?access_token=`, no negotiate; vEDST, and TowerCab 3D on YAAT) have no connection token, so they cannot register UDP. `GetServerConfiguration`, which is where a client learns the UDP port, is refused for them (`CrcClientState.Join.cs:20-33`) and is absent from the JSON `Methods` table (`Protocol/CrcJsonTranscoder.cs:39-52`).

**vNAS wire format TowerCab 3D decodes.** Source: `X:/dev/towercab-3d-vnas/src/udp/socket.rs:247-320`, `udp/messages.rs`. Verify every tag and key against `docs/crc-wire/messaging-contract.json` before relying on it.
- Envelope: MessagePack union `[tag, body]`. Tags: 0 KeepAlive `[]`, 1 RegisterConnection `[connectionId]`, 2 Ack `[]`, 3 EntityUpdate. These already match `UdpEntityContract.cs:17-20`.
- EntityUpdate body: `[Topic, Entity]`.
  - Topic: `[category, facilityId, subset, sectorId]`, with category `TowerCabAircraft` and `facilityId` as the tower-cab airport id.
  - Entity: a nested union `[24, TowerCabAircraftDto]`. The DTO is the 9-key index-keyed DTO already in `Dtos/CrcDtos.cs:97-130`.
- The client registers with the SignalR connection id (the negotiate token) and sends a keepalive every 5 s.
- The UDP transport is MessagePack whatever the hub protocol. TowerCab 3D uses the **JSON** hub protocol and MessagePack UDP.

**Work.**
1. Add union tag 24 to `IUdpEntity` (vNAS `IEntity.cs:27`) and a `UdpEntityCodec` helper for a TowerCab `EntityUpdate`, with byte-pinning tests. The hub `TowerCabAircraftDto` cannot go on UDP as is: its `VoiceType` uses `StringEnumFormatter` (`Dtos/CrcDtos.cs` ~:107) and UDP clients decode an int, so the UDP record is its own type built from the hub DTO.
2. In the per-tick TowerCab batch, send position changes for a UDP-registered subscriber (`UdpEntityServer.IsRegistered(token)`) as UDP entity updates instead of a hub `ReceiveTowerCabAircrafts`.
   - Keep the hub path for a newly visible aircraft, for deletes, for the 10 s full resend, and for any subscriber not registered on UDP. That last case is the fallback, and it keeps today's behaviour for unregistered clients.
   - Check CRC's own expectations. Does CRC ignore UDP Tower Cab updates for an aircraft it has not yet received over the hub? Mirror vNAS ordering.
3. Rate: at most about 1 Hz per aircraft per subscriber, whatever the sim tick rate or fast-forward speed.
4. **Direct/joined connections get UDP as negotiated joiners** (decided by the user, option (a)).
   - (a) **Negotiated joiner** (chosen): a connection that negotiates *and* presents a valid YAAT `access_token` on the WebSocket URL is treated as a direct connection. It keeps its negotiate token, so it can register UDP, and `JoinSession` is allowed for it.
     - This matches the real vNAS TowerCab flow exactly: negotiate → `?id=…&access_token=…` → `GetServerConfiguration` → `GetSessions`/`JoinSession` → UDP register.
     - It would let TowerCab 3D drop its YAAT-only "no negotiate, no UDP" mode.
     - It needs `GetServerConfiguration` in `DirectConnectionTargets` and in the JSON `Methods` table.
   - (b) Rejected: leave direct connections hub-only. TowerCab 3D would keep its YAAT special case and receive every position over the hub.
   - Decided (user): a socket with `?id=` and an `access_token` that fails validation is refused with 401 before it is accepted (presenting the token asks to be direct); and `NegotiateHandler` records every token it issues (CID null when no Bearer, with a short expiry, e.g. 60 s), so a joiner's `?id=` must be one the server issued, consumed once. TowerCab 3D negotiates without a Bearer header, so today its token is never in `CrcNegotiateTokenStore` and its `?id=` is client-asserted.
   - Brief B (this step) touches `Hubs/CrcWebSocketHandler.cs` (accept path ~:39-77, extracted into a pure decision helper), `Hubs/NegotiateHandler.cs`, `Hubs/CrcNegotiateTokenStore.cs`, `Hubs/CrcClientState.Join.cs` (`GetServerConfiguration` in `DirectConnectionTargets`), `Protocol/CrcJsonTranscoder.cs` (a `GetServerConfiguration` entry) and a new `Dtos/ServerConfigurationDto` (`[Key(0)] int UdpPort`, contract ~:3730). It follows brief A (wire and send), which also edits `CrcWebSocketHandler`'s release path.
5. A joined connection reads its primary's room (`CrcClientState.cs:66`). Make sure the UDP send keys on the *joiner's* own token, not the primary's.
6. Docs: `docs/crc-protocol-support.md` (UDP rows), `docs/architecture.md` (UDP section, JSON transcoder clients), and `SELF_HOSTING.md` (6809/udp is now needed for Tower Cab, not just ERAM history).

**Out of scope unless asked:** other UDP entity types (STARS/ERAM/ASDE-X tracks). List which entity tags vNAS sends over UDP, taken from the messaging contract, as follow-ups in the plan.

**Coordination.** The TowerCab 3D plan ships first: a YAAT direct connection with no UDP, relying on the hub `ReceiveTowerCabAircrafts`, now mapped for JSON clients. If option 4(a) lands, the TowerCab crate's `Environment::is_yaat()` special case can shrink to URLs plus token source only. File that as a follow-up in `X:/dev/towercab-3d/docs/plans/MAIN.md`.
