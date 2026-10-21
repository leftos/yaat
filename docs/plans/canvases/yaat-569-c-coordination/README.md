Mock for YAAT-569 part C (coordination): the aircraft menu's Track › Initiate handoff… and Track › Point out…, which today open the bare "Position ID" input popup and send `HO <typed>` / `PO <typed>` (`src/Yaat.Client.Core/ContextMenus/MenuCatalog.cs` lines 79–93, `BuildInput`). Open `index.html` in a browser; it works offline and loads no web fonts. It has no image files: every surface is drawn in HTML and inline SVG.

The page draws the part-B flyout language (title line, glyph box row with its command column, separator, a 300 px list panel with group headers, rows with a command column, the outline on the row Enter sends) in three lanes. Each lane draws four states: Initiate handoff as it opens, the box filtered by `oak`, a typed code with no staffed match (`3S`), and Point out with the same layout. Boxes, rows and chips are live; the line under each lane says what a click or Enter sends.

- A (recommended): one list grouped by facility, the student's facility first (NorCal Approach, Towers, Oakland Center, Travis); each row is the code, the position name (the radio name for a tower), callsign and frequency small, and the whole command.
- B: lane A with a Likely group on top: the destination's tower (derivable today), then dashed placeholders for the next sector on the route (handoff) and the next airspace on the route (point out), which need new data.
- C: the codes as chips grouped by facility, with a readout line naming the outlined or pointed-at chip.

The situation: scenario S3-NCTC-3 | Area C Complete (`docs/atctrainer-scenario-examples/01J02M96SPYP4JV55R5RMVCQBS.json`), student 3Y (OAK_APP, NorCal Area C Combined), its 19 auto-connect ATC positions. Callsigns, names, radio names, frequencies and TCPs come from `docs/vnas-artcc-config-examples/zoa.json` (towers resolve to their NCT TCPs, 3O for OAK_TWR; MER_TWR has none; OAK_44_CTR is ERAM sector 44).

What the client has, found while drawing (static reads, not run):

- `MainViewModel.OnlineControllers` (`MainViewModel.Controllers.cs:55`) holds `OnlineControllerDto` (`ServerConnection.cs:1879`: Callsign, Name, Frequency, FacilityId, FacilityName, PositionType, Tcp, RadioName, RealName, IsActive, IsScenarioPosition) for the room's live CRC clients and the scenario's auto-connect positions. `IMenuHost` has no member that hands it to a menu.
- `OnlineControllerDto.Tcp` is null for an ERAM sector and for a position without a STARS TCP; `HandoffId` then falls back to the facility id (`ZOA`, `MER`), which `HO` does not resolve. The server computes the shortest code naming one position for `AS` (`RoomEngine.SelectionCodeFor`); no DTO carries it.
- Unstaffed positions are not listed anywhere on the client: `ScenarioPositionDto` (`ServerConnection.cs:1742`) has Id, Callsign and Name only.
- Nothing names the next sector on the route or the position that handed the aircraft over; `IMenuAircraft` carries Owner, OwnerSectorCode, HandoffPeer, HandoffPeerSectorCode, PointoutStatus, Destination and NavigationRoute.

Flyout sizes, measured from the headless render (open the page with `#measure`): every flyout is 380 px wide. Lane A: 406 px tall as it opens, 296 filtered by `oak`, 168 on no match. Lane B: 406, 321, 168. Lane C: 441, 309, 208. Point out matches Initiate handoff in each lane. Today's popup is 280 × 46.

Not from real data, or not checked:

- KFB7's state (6,000 ft, 210 kt) is illustrative; the scenario spawns it at MYJAW at 28,000 ft handed off by 1M.
- That the student's own 3Y appears in the list assumes the student's CRC client is connected (the server lists live CRC clients, not the student position itself).
- That `HO 1S` reaches Travis rather than NCT's Seca in this scenario is a static read of `TrackResolver.ResolveTcpToOwner` (the scenario's ATC list is searched before the facility config), not run.
- The filter rule (code, callsign or frequency prefix; a name word or callsign substring from two letters) is the page's proposal.
- Point out has no glyph; the page draws a dashed draft.

Source files read for it: `MenuCatalog.cs` (the two entries, `BuildInput`, `InputLeaf`), `IMenuHost.cs`, `IMenuAircraft.cs`, `SharedMenuGroups.cs`, `QuickCommandGlyphs.cs`, `FlyoutPanelParts.cs`, `FixListView.cs`, `ServerConnection.cs` (`OnlineControllerDto`, `ScenarioPositionDto`, `PositionSummaryDto`, `FacilityTreeDto`), `MainViewModel.Controllers.cs`, `MainViewModel.ActivePosition.cs`, `RadarViewModel.cs`, `src/Yaat.Sim/Commands/TrackResolver.cs`, `ArtccConfigResolver.cs`, COMMANDS.md (Track Operations), the part-B mock `yaat-569-b-fix-pickers/`, and in yaat-server `OnlineControllerService.cs`, `ArtccConfigService.BuildOnlineControllerDto`, `RoomEngine.SelectionCodeFor`, `ScenarioLifecycleService.BuildPositionDtos`, `TrainingHub.GetOnlineControllers`.
