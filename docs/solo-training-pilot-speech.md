# Solo-Training Pilot Speech Pipeline

Reference for how solo-training mode produces pilot transmissions — both the terminal-visible radio transcripts and the optional Piper TTS audio. Read this before touching `src/Yaat.Sim/Pilot/`, `AircraftState.PendingPilotTransmissions`, `SimulationWorld.ActiveFrequency`, or `src/Yaat.Client/Services/PilotVoiceService.cs`.

For the controller-side STT (PTT → canonical command), see [`speech-recognition-pipeline.md`](speech-recognition-pipeline.md). The two pipelines share nothing beyond the radio metaphor.

## Why a queue at all

Solo training puts one student on every frequency, playing every controller role. A compound command issued to one aircraft used to push the entire readback into `PendingNotifications` synchronously, so a busy session would draw orange-bracket notification text faster than any real pilot could speak. Worse, every aircraft on the frequency could "talk over" each other in the same tick.

The pipeline now serializes pilot speech the way a real radio works:

- A pilot can be **awaited** for a readback — the controller just spoke to them, so the next mic key on the frequency is theirs (with a bounded wait, not a freeze — see below).
- Other pilots' proactive calls (ready-to-taxi, holding short, on final, going around, position reports) wait their turn.
- Each transmission consumes airtime proportional to its word count. The next dequeue can't happen until the current transmission's airtime has elapsed.

The end result: solo training feels like one frequency with many aircraft, not a controller looking at a notification firehose.

## Per-aircraft typed queue

`AircraftState.PendingPilotTransmissions` is a `List<PilotTransmission>` (transient — not snapshot-serialized).

```csharp
public sealed record PilotTransmission(
    string Callsign,
    string Text,         // compact terminal form, no callsign — the SAY column carries it ("cleared to land runway 28R")
    string SpeechText,   // TTS form ("cleared to land runway two eight right, november one two three alpha bravo")
    string SourceKind,   // "Response" or "SayReadback" — drives terminal channel
    PilotTransmissionKind Kind  // Readback / Proactive / Report / SayReadback
);
```

Builders push entries via `PilotResponder.QueueSoloPilotTransmission` (Readback/Proactive/Report) or `QueueSoloPilotReadback` (SayReadback). **Don't push directly to `PendingNotifications` from solo-mode pilot code paths.** The queue is what makes airtime serialization work; bypassing it is the bug.

## Frequency airtime serialization

`SimulationWorld.ActiveFrequency` is a single `FrequencyState` instance. Each tick, `SimulationEngine.TickPostPhysics` calls `World.DrainReadyPilotTransmissions(elapsedSeconds)`:

1. **Drain.** Move every aircraft's `PendingPilotTransmissions` into the frequency's pending queue.
2. **Awaited-readback gate.** `FrequencyState` holds a FIFO of awaited readbacks (`_awaitingReadbacks`: the exact `PilotTransmission` awaited, with the second it was added; a callsign already awaited is not added again). While it is non-empty, the head's readback gets airtime priority — if it's already in the queue, dequeue it first; if it isn't, hold other transmissions back so it can land. Two instructions outstanding therefore both come back, in instruction order, before any third pilot speaks.

   The gate has an `AwaitedReadbackTimeoutSeconds` ceiling (8 s); past that, it releases the frequency to FIFO so a missing readback (deleted aircraft, future bug, etc.) can never silence the airport indefinitely. The 8 s counts from the later of the instruction and the frequency's next free second, so a readback waiting on airtime behind a long transmission keeps its priority. A timed-out entry leaves the FIFO — if its readback arrives later, it dequeues normally without veto power over the rest.
3. **Airtime gate.** A transmission can only dequeue when `elapsedSeconds >= _nextAvailableAtSeconds`. After dequeue, set the next slot to `now + (~0.25 s/word, min 1 s)` (`FrequencyState.EstimateAirtimeSeconds`, the one estimate behind the gate and the meter's durations).
4. **Activity meter.** `FrequencyActivityMeter` records each transmission as an interval (`FrequencyTransmission(Start, Duration)`) and judges the frequency by `DeadAirShare(elapsed)`, the share of the last 60 s that no transmission occupied: Quiet at 70% or more, Normal from 35% to under 70%, Busy under 35%. Overlapping intervals count once, and an interval still on air counts only up to now, so a transmission adds nothing to the band stamped on it at the second it keys.

   Each transmission lasts 0.25 s per word, 1 s minimum, counted from the canonical spoken (TTS) text, never the load-varied text, so a busy pilot's shorter readback cannot make the frequency read quieter (`PilotTransmission.CanonicalAirtimeSeconds`). Placement:

   - A pilot transmission runs [dequeue, dequeue + d].
   - A controller dispatch, human or AI, runs [dispatch − d, dispatch] with d estimated from the canonical readback of the instruction as issued (`SimulationWorld.RecordControllerTransmission`, called by `SimulationEngine.ApplyPostDispatch` before the readback is built), once per successful dispatch however many commands it chained.
   - A solo SAY-channel answer counts as airtime from the second it is emitted when its callsign names an aircraft (`SimulationEngine.RecordSoloSayAnswer`); a scenario timer reminder, global or naming an aircraft, never counts (`ProcessTimers` emits it without metering).
   - A queued but unspoken call does not count.

   `FrequencyState.GetLoadBand` trims the window and returns the three-way `FrequencyLoadBand`. `SimulationEngine.ApplyPostDispatch` reads the band into the `PilotSpeechContext` the readback builders take (see Activity-aware readback shortening).

   The recorded intervals are snapshotted (`StateSnapshotDto.FrequencyTransmissions`, restored by `Restore`), so a rewind or restore resumes in the load band the run was in: see [snapshots-and-replay.md](snapshots-and-replay.md). `SimulationEngine.LoadScenario` empties the meter, so a reload replayed from zero never keeps intervals from later in the run.

Drained transmissions become terminal entries with kind `SayReadback` (for readbacks) or `SayPilot` (for proactive/reports). The client renders both as green SAY lines and routes them to `PilotVoiceService` if Piper is installed.

When solo mode is OFF, `World.DiscardAllPilotTransmissions()` runs each tick to keep RPO/test state clean.

## Awaited readback after dispatch

The readback text itself comes from `PilotResponder.BuildReadbackAsApplied` — when the dispatch rewrote the command (`CommandResult.EffectiveCommand`, e.g. a TAXI whose gate lead-out lane was dropped), the pilot reads back the route it will actually taxi, prefixed "unable <lane>," — see [pilot-phraseology.md](pilot-phraseology.md#readback-of-a-rewritten-clearance).

`SimulationEngine.BuildDispatchContext` records which `Readback` entries the aircraft already had pending; after the dispatch, `ApplyPostDispatch` calls `World.ExpectPilotReadback` when a `Readback` entry it did not have before is now pending. Whichever site queued it arms the gate: the applied readback, the pilot's "unable" after a refused command, a contact/handoff (CT/FCA) readback or a pattern-size reply. A reply the pilot had already queued on its own does not arm it.

In normal operation the readback emerges on the very next drain (1 s) and its entry leaves the FIFO immediately, so the gate is invisible. Two consecutive controller commands to two different aircraft queue both readbacks; the second readback waits for the first.

An aircraft that leaves the world (`SimulationWorld.RemoveAircraft`) or is replaced by a same-callsign spawn (`AddAircraft`'s replace path) has its awaited readbacks dropped at once (`FrequencyState.ForgetAircraft`), so the next pilot speaks on the next drain.

If the awaited readback never arrives otherwise — an instruction whose readback never queues, or a future bug that calls `ExpectReadback` without a paired enqueue — the gate releases after `AwaitedTimeoutSeconds` (8 s) and other pilots resume mic'ing up under FIFO. Gate state, like the pending queue, is not snapshotted. **The gate is best-effort ordering, not a hard lock.** Real pilots get impatient too.

## Awaiting controller response after a proactive call

The mirror-image gate: when a pilot keys up proactively (initial check-in, ready-for-departure, midfield-downwind reminder, etc.), the controller is expected to respond. Other pilots' proactive/report transmissions hold back so they don't step on the unanswered call.

`FrequencyState._awaitingControllerResponseTo` is set on every `Proactive` or `Report` dequeue (in `TryDequeueReady`) and cleared by `SimulationEngine.SendCommand` calling `World.AcknowledgeControllerResponse(callsign)` after a successful dispatch. The same 8-second `AwaitedTimeoutSeconds` ceiling applies — past that, FIFO resumes so a non-responsive controller never silences the airport.

**The timer starts at end-of-airtime, not start** — so the timeout means "8 seconds of silence after the pilot stops talking", not "8 seconds total including the pilot's transmission". Otherwise the airtime (3-5 s) would eat into the controller's response window.

What still passes the gate:

- The awaiting pilot's own follow-up calls (same callsign).
- Any `Readback` or `SayReadback` from any pilot — those are responses to controller-issued commands, not new requests.

For commands that *produce* a readback, both gates run sequentially: dispatch clears `_awaitingControllerResponseTo` (controller has spoken) and adds the awaited readback to `_awaitingReadbacks` (waiting for the reply). For commands that don't produce a readback (e.g. `ROGER`/`STBY` via `AcknowledgePilotContactCommand`), the controller-response gate clears and other pilots can mic up immediately.

### Check-in butt-in

While the gate waits on an airborne or pattern initial-contact check-in (`PilotTransmission.IsInitialContact`, passed as the required `initialContact` of `PilotResponder.QueueSoloPilotTransmission`), a second pilot's own initial-contact check-in may key up early instead of waiting the full 8 s. It gets one draw, `SpokenFormDraws.DrawCheckInButtIn`: 10% (`CheckInButtInPercent`), seeded by the callsign, the second the first call's airtime ended and the scenario seed through `VariantPick`, so a replay draws the same.

On a win it keys up once 3 s of silence have passed (`CheckInButtInDelaySeconds`), counted from the end of the most recent transmission and never earlier than the end of the first check-in. A butt-in never keys over another transmission and never goes ahead of a pending readback, and it does not happen if the controller answered first. A losing draw, or any other call, waits up to 8 s as before.

After a butt-in the gate waits on the butting pilot, so the next call queues behind that check-in. Ground and departure first calls (ready to taxi, pushback, clearance, taxi-in, release request, ready for departure) are not initial contacts and never butt in. The required `rngSeed` of `FrequencyState.TryDequeueReady` and `SimulationWorld.DrainReadyPilotTransmissions` carries the seed.

## Activity-aware readback shortening

`PilotPersonality.Varied` unlocks a rule's `PhraseologyRule.PilotVariants` (a `PilotVariantSet`: Quiet, Normal and Busy lists of `PilotVariant(Template, PilotVariantTags)` rows, a template over the rule pattern's own `{capture}` placeholders), in the spoken form only: the Terminal and RPO readback text in a Varied session is always the canonical (verbatim) form. The pilot's effective band picks the rows (`PhraseologyVerbalizer.BandRows`): Quiet and Normal speak the canonical or one of the band's rows, every candidate equally likely; Busy speaks only its rows, or the canonical when it has none or none can be spoken ("two eighty" instead of "fly heading two eight zero" at Busy). A busy speed readback, for instance, is "two five zero knots", "speed two five zero", "two fifty knots" or "speed two fifty".

Speeds are separate digits ("two five zero knots", `PhraseologyVerbalizer.SpeedWords`) in the canonical and in every band, the cross-fix readback with a speed included; the group form ("two fifty", `SpeedGroupWords`) is spoken only by a busy row, and always with "knots" or "speed". A row speaks a value in group form through the capture modifier `{spd:group}` / `{alt:group}` (`PilotVariant.GroupFormSuffix`), rendered from the same value as the plain capture; for altitudes the group form differs from the digits only from 10,000 to 17,999 ft ("eleven thousand", `AltitudeGroupWords`), so those rows carry `GroupFormRange` and are candidates only in that range. A flight level is never spoken in group form. The Normal band also speaks "left/right heading {hdg}", "{deg} degrees left/right", "heading {hdg}", "direct {fix}", "{spd} knots" and, at a flight level, "climbing to / descending to flight level …"; Busy speaks those rows too, and may also drop the climb/descend verb, say "climbing / descending {alt}", say "present heading" for FPH and "mach point seven eight" for Mach.

`PhraseologyVerbalizer.PickVariant` draws uniformly among the band's renderable rows with `VariantPick.Index(callsign, "readback-variant:" + canonical, second, seed, count)`, not shortest-first. A row is renderable when every placeholder resolves and, if tagged `FlightLevelOnly`, the command's altitude is a flight level (`AtcNumberParser.IsFlightLevel`: at least 18000 ft and a multiple of 100). So "up to / down to / climbing to / descending to" are candidates only at or above FL180, and below it a busy pilot reads back "climb/descend and maintain {alt}". With no renderable row the canonical is spoken. Terminal is always canonical. `PhraseologyVerbalizer.SpokenCandidates(cmd, aircraft, band, currentIndicatedAirspeed)` lists the texts the pick chooses among, builder-rendered clauses included (an altitude restriction, an expedite, a cross-fix without a speed: the canonical plus the builder set's rows for the band, chosen by the same dispatch the readback uses; a CVA naming its runway and an airborne FOLLOW are covered too, while the airport-level CVA, whose runway is the pilot's pick resolved at dispatch, is carried only by the handler readback; a held-canonical clause, such as an at-or-above/below cross-fix with a speed or any military route clearance, returns its canonical only; texts come before the readback's final `NormalizeForTts` cleanup), and `RenderSpokenVariant(cmd, template)` renders one template over the command's captures with no band or tag filter.

`PilotResponder.BuildReadback*` take a `PilotSpeechContext(Personality, Band, RngSeed, ElapsedSecond)` plus the required `ShortenCallsign`, `GroupContactFrequency` and `CurrentIndicatedAirspeed` (the aircraft's `IndicatedAirspeed`, null when unknown) members, and `PhraseologyVerbalizer.Verbalize(cmd, callsign, context)` takes the whole `PilotSpeechContext` (`VerbalizeTerminal(cmd)` takes none, so the Terminal never varies). `SimulationEngine.PilotSpeechContextFor` builds the context (for `ApplyPostDispatch` and, through `DispatchContext.SpeechContextFor`, for the CT and FCA readbacks `ContactCommandHandler` builds) from `PilotPersonality.Varied`, the pilot's effective band (the meter's load band shifted by the pilot's trait), the scenario's `RngSeed`, the whole elapsed second and the two spoken-form draws below, so shortening is automatic on the live path; with no pilot answering it returns `PilotSpeechContext.Canonical`. Tests that need stable output use the parameterless `BuildReadback(compound, aircraft)` overload, which passes `Canonical`: Verbatim, Normal, seed 0, second 0, full callsign, digit frequency, no current speed.

Quiet rows replace the quiet flavour on the readbacks they cover. The family-1 rules (TL/TR, RELL/RELR, FH, FPH, CM/DM, SPD, RNS, DCT, TLDCT/TRDCT) carry Quiet rows, so a readback with a clause spoken from one of them skips `ApplyQuietFlavor` and speaks the canonical or a Quiet row, whose wording carries the courtesy.

Clause builders render their own canonical text instead of reading a rule, so their rows live in `BuilderVariantSet`s (`PhraseologyRules.BuilderVariantSets`, 28 sets): the approach readbacks (clearance, straight-in clearance, join, expect; six sets, built by `ApproachCommandHandler` and carried on the `HandlerReadback`'s `Variants` / `Captures`, so only the final clause varies), the PTAC readback (six sets by turn direction or fly heading and climb, descend or maintain; `ApproachCommandHandler.PtacVariants`, also dispatched from `PilotResponder.VariedBuilderClause`, so `SpokenCandidates` covers it), the altitude restriction ("maintain [VFR] at or above|at or below {alt}", four sets by VFR and direction), the expedite (climb or descend with an altitude, bare climb or descent, ground taxi, runway exit) and the cross-fix restriction with no speed (at, at or above, at or below). The builder returns its canonical with its set, and `PilotResponder.VaryBuilderClause` calls `PhraseologyVerbalizer.VerbalizeBuilderClause`, which picks among the canonical and the band's rows as a rule's rows are picked and reports `OfferedQuietRows` the same way, so a quiet readback of these clauses takes no "alright, " / ", thanks" flavour.

A chained readback, two or more commands (a CAPP with a route or crossing before it included), is never chatty: `PilotResponder.IsChained` marks it, a quiet frequency speaks the normal-band words in every clause with no quiet flavour, and a busy one keeps its terse rows on every clause. A single command, conditioned or not, keeps its band.

A cross-fix restriction with a speed ("cross sunol at or above four thousand nine hundred at two five zero knots", Terminal "cross SUNOL at or above 4900 at 250 knots") and the military-route at-or-below stay canonical in every band; the speed follows the altitude for at, at or above and at or below alike.

`PhraseologyVerbalizer.VerbalizeClause` returns a `ClauseVerbalization` whose `OfferedQuietRows` says whether the Quiet pick offered at least one row beside the canonical after filtering; the skip follows that, so a readback whose Quiet rows were all filtered out (a compound, a later speed block) keeps the flavour. Three row kinds depend on the readback around them:

- **Speed change.** Quiet and Normal SPD rows tagged `SlowingOnly`, `SpeedingOnly` or `MaintainingOnly` say "slowing to / speeding up to N knots" when `CurrentIndicatedAirspeed` is more than 10 kt (`PilotVariant.SpeedChangeThresholdKnots`) above / below the assigned speed and "maintaining N knots" within 10 kt (AIM 4-4-12.c).

  They are candidates only for a speed with no modifier and no "until", in the readback's first block with no condition (`PilotResponder.SpeedChangeIsNow`); any other speed readback gets a null current speed and speaks no such row.
- **Bare "wilco".** Busy RNS, RPS and RFAS rows are `IsBareWilco` ("wilco" with at most the callsign), a candidate only when the command is the whole readback (a single, unconditioned command, `PilotResponder.IsSingleUnconditionedCommand`); never in a compound or an "until" form. DSR keeps its words (7110.65 5-7-4.a NOTE against 5-7-4.d).
- **Callsign inside the row.** A row may place the callsign itself (`{cs}`, `PilotVariant.PlacesCallsign`: "left heading two seven zero, {cs}, thanks"), again only in a whole single readback; the readback fills it with the same callsign form it would otherwise close with, and a multi-clause readback closes with the callsign once.

An implicit "cleared into/through the bravo" clause sits right before the readback's last callsign, and a trailing ", thanks" gives way to it.

Two deterministic draws sit beside the band, derived on each call and never stored, so a replay or rewind reproduces them. `PilotTraits.For(callsign, rngSeed)` gives the pilot's `PilotTrait`: terse 25%, normal 50%, chatty 25%, from `DeterministicHash.Mix` over `Fnv1a("pilot-trait:{seed}:", callsign)`. `PilotTraits.EffectiveBand(band, trait)` is the band the pilot speaks at: terse one band busier, chatty one quieter, clamped to Quiet..Busy. `VariantPick.Index(callsign, canonicalBody, elapsedSecond, rngSeed, count)` picks among a phrase's variants for one pilot, one canonical body and one second. The spoken form reads both: the trait shifts the band the context carries (the speech-rate nudge still uses the raw band), and the pick chooses the quiet flavour (for a readback with no Quiet-row clause) and the variant row.

`SpokenFormDraws` holds the two spoken-form draws, each a `VariantPick.Index` pick. The abbreviated callsign: a readback's closing callsign, and the CT and FCA readbacks' callsign, is "november" plus the last three characters ("november three alpha bravo" for N123AB; `CallsignParser.ShortenedNNumberSpoken`, `PilotResponder.SpokenReadbackCallsign`, the heavy/super suffix kept) in terse 80%, normal 50% and chatty 15% of readbacks (body `"callsign-form"`). `CanShortenCallsign` gates it, from AIM 4-2-4.a.1 to a.3 and 7110.65 2-4-9: only for `N` + digit callsigns of at least five characters, only once the controller has acknowledged the pilot's contact (so never on the readback to the acknowledging transmission, including a compound with STBY/ROGER), only while the aircraft is still on the student's frequency, and not while another aircraft that is not shadow, completed or left has a callsign ending in the same two characters. Proactive calls, check-ins, requests and unable replies keep the full callsign, and the Class B clause stays before the callsign.

The grouped contact frequency: a spoken CT readback drops the leading 1 and pairs the digits ("twenty five thirty five" for 125.35; one decimal keeps "point": "twenty one point five") in 75% of readbacks at the pilot's effective Busy band, 15% at Normal, never at Quiet (`DrawGroupedContactFrequency`, `PhraseologyVerbalizer.FrequencyToWords(double, bool grouped)`). Only 110 to 199 MHz with a first decimal other than 0 groups, and the Terminal keeps "125.35". The no-frequency CT readback carries the heavy/super suffix like every other.

The quiet flavour (`PilotResponder.ApplyQuietFlavor`) is picked by `VariantPick.Index` over the canonical Terminal body, so a pilot's flavour does not change with the band's wording, and is never added where it could mislead, nor to a readback with a clause spoken from a rule that has Quiet rows (above). "alright, " is not added when the body says left or right, cleared, cancel, cross or crossing, follow, behind, bravo, option, line up and wait, touch and go, stop and go, low approach, go around, hold position, holding position, hold short or holding short, or names a runway with a side (28R). ", thanks" is not added to go around, cancel, hold position, holding position, or a LAHSO (land and hold short) clearance.

`AviationFloor.HoldsTemplate(canonicalPattern, CanonicalCommandType, PilotVariant, band)` is the value floor every `PilotVariants` row must pass. It is enforced at test time over every rule row (`PhraseologyRules_EveryVariant_HoldsTheFloor`) and every `BuilderVariantSet` row, not at speak time. It compares the variant with the rule's pattern by value: the same captures in the same order, and the same ordered keyword classes: heading, degrees, knots (knots and speed merge), mach, squawk, runway, left/right; climb/descend ("up to" and "down to" only as phrases, "speeding up" and "slowing down" carry no class); hold short, cross, expedite; the qualifiers at or above, at or below, at, cruise, VFR, except maintain, until; the verbs cleared, land, takeoff, line up and wait, go around, hold position, follow; and expect, not, via, when able, immediately, and the speed qualifiers or less, or greater, between, maximum, minimum. Only a Busy row may drop climb/descend (never swap it), and the word runway only on a runway clearance (takeoff, land, line up and wait, touch and go, stop and go, low approach, option, the approach clearances; never right after hold short or cross). An expedite row may also drop the direction word right after expedite in any band ("expediting" for "expedite climb"), never the verb before an altitude, and a row that drops "VFR" from a VFR canonical is refused. A group-form placeholder (`{alt:group}`, `{spd:group}`) counts as its plain capture (`alt`, `spd`), since both render from one value, and the floor refuses it outside the Busy band.

The floor also ties each speed-change verb to its tag (a tagged row sits on the speed rule outside Busy, says its own verb and "knots" right after the speed; an untagged row never says "slowing" or "speeding"), refuses any capture or keyword-class word after `{cs}` and a second `{cs}`, and allows a bare "wilco" only for RNS and RFAS in the Busy band (`WilcoTypes`).

`AviationFloor.Holds(string, string)` is the text floor, kept for clause builders that only produce strings: the variant carries the same ordered anchors as the canonical clause (digit-leading tokens including runway designators; the keywords heading, degrees, knots, mach, squawk and runway; left/right and climb/descend; hold short; cross) and the same ordered altitude unit words (flight level, thousand, hundred). Its summary lists what it does not enforce.

## Dual-output builders (terminal vs TTS)

> **Wording, output forms, and routing-by-mode live in [`pilot-phraseology.md`](pilot-phraseology.md).** This section covers only what the queue/airtime plumbing needs.

Every builder returns a `PilotSpeechText` whose `Terminal` (compact, no callsign) and `Tts`
(spelled, ends with the spoken callsign) forms are built **independently** — never by regex-stripping
the TTS string. A third `RpoTerminal` form carries instructor-only diagnostics (the lead/target
callsign in traffic & follow calls). When a transmission lands in `PendingPilotTransmissions`, its
`PilotTransmission.Text` is the terminal form and `SpeechText` is the TTS form; the drain emits
`Text` to the SAY transcript and `SpeechText` to the voice broadcast.

`PilotResponder` has three routers — `RouteSoloOrRpoTransmission`, `RouteRpoSayReadback`,
`RouteRpoTransmission` (two overloads) — that pick the channel from `(soloTrainingMode, rpoShowPilotSpeech, studentPositionType)`.
The full mode → channel matrix (and how `RpoTerminal` is resolved per branch) is in
[`pilot-phraseology.md`](pilot-phraseology.md#routing-the-forms-by-mode). The two well-known position
lists are `PilotResponder.SoloPositionsTower` (`["TWR"]`) and `SoloPositionsTowerApproach`
(`["TWR","APP"]`).

Stored follow-up lines re-queued by `PilotRequestTracker` keep **both** forms on the pending
request — `PilotPendingRequest.LastPilotLine` (terminal) and `LastPilotLineTts` (spoken), both
snapshot-serialized — and re-queue through the `PilotSpeechText` overload, so a reminder reads
identically to the original call (issue #297). The one remaining string overload is
`QueueSoloPilotReadback`, used for notification-style SAY readbacks ("looking for the field");
it strips the bracketed callsign prefix for the terminal form and normalizes the TTS form.

## Position reports vs intent declarations

Towered-field pilots do not self-announce every pattern leg on entry. Two kinds of transmission with different triggers:

- **Position reports are no-clearance-driven.** Midfield-downwind and short-final calls fire from `OnTick`, gated on still being uncleared (`!HasLandingClearance`), so a pilot only "reminds tower" when a clearance is overdue. They never fire on phase entry.
- **Intent declarations are entry-driven.** Initial-contact statements ("request closed traffic", "ready for departure") fire from `OnStart` on first contact — they are not position reports and must happen up front, not after a delay.

Output channel follows the usual split: solo mode lands in `PendingNotifications` (gray pilot speech, AI voices the pilot); RPO mode lands in `PendingWarnings` (orange controller-facing nag) unless `RpoShowPilotSpeech` routes it to `PendingPilotSpeech`, a `List<PilotSpeechText>` whose drain broadcasts the written `TerminalForRpo` line as a green `PilotSpeech` terminal entry (terminal, speech bubble, terminal history). RPO has no TTS consumer: the client voices pilots only in solo (`MainViewModel.OnPilotTransmissionReceived` gates on `SessionSoloTrainingMode`), so the `Tts` form in `PendingPilotSpeech` is never spoken. Never delete the `PendingWarnings` text — it is the default fallback when pilot-speech mode is off.

## Who the pilot calls — `PilotContactRoster` (`Pilot/PilotContactRoster.cs`)

Pilot-initiated calls are addressed to whoever **answers** pilots this session, not to solo mode as such. The roster (`SimScenarioState.PilotContacts`, memoized on solo mode / student position / lowest worked level / the scenario's ATC positions / AI staffing version / ARTCC config, and handed to phases as `PhaseContext.PilotContacts`) holds every AI-staffed position (`SimScenarioState.AiStaffedPositions`, published by the host that runs the controller AI) plus the solo student.

The initial-contact sites resolve their addressee with `ResolveFor(aircraft, expectedPositionType, atAirportId, eligibility, checkEligibility)`:

| Site | Wants | At airport | SOP eligibility check |
|---|---|---|---|
| Initial call-up, ready to taxi (`InitialCallupCall.TryMake`, from `AtParkingPhase`, `HoldingAfterPushbackPhase`, `HoldingInPositionPhase`, `HoldingShortPhase`) | `GND`; an uncleared IFR departure from the airport asks the student for its clearance first: a student who works delivery there (`StudentWorksDeliveryAt`), else the student answering ground when nobody else covers delivery (`DeliveryCoveredByAnother`) | `PilotContactRoster.SurfaceAirportOf` (ground layout, else spawn airport) | yes (`CanInitiateWith`; `CanInitiateWithStudent` for the delivery student) |
| `HoldingShortPhase` ready for departure | `TWR` | `SurfaceAirportOf` | no (as before) |
| `LinedUpAndWaitingPhase` lined-up reminder | `TWR` | the departure runway's airport | no (as before) |
| `LinedUpAndWaitingPhase` release request (`RunwaySpawnCall.TryRequestRelease`) | the student's `APP` or `CTR` | the runway's airport | no |
| `FinalApproachPhase` spawn-on-final check-in | `TWR` | the runway's airport, else the approach clearance's | yes |
| `PatternEntryPhase` closed-traffic request | `TWR` | the pattern runway's airport | yes |
| `HoldingAfterExitPhase` / `HoldingInPositionPhase` taxi-in call (`Pilot/TaxiInRequest.cs`) | `GND` | `SurfaceAirportOf` | no — a landed aircraft is the cab's already |

Resolution order: an AI position of the wanted type covering the aircraft first; else the student, when solo training is on and the student's worked range takes in the wanted type at the airport (`StudentAnswers`; with no range set the student answers every type).

Else, for a **ground** call only, an AI local position covering the airport — a tower working the cab alone works ground too, and is addressed by the generic word like a tower-only student; else null — nobody to call, so no call and no `PilotPendingRequest`, which is the pre-roster instructor-mode behavior.

Coverage for a **tower-cab** AI position (GND/TWR) is the airport the call is physically made at, never the filed destination — an OAK departure filed to SFO calls Oakland Ground even with AI SFO Ground staffed; radar-role AI positions match the destination/current/primary candidates (`PilotInitialContactEligibility.CandidateAirportIds`).

Where the site asks for the SOP check, it applies to **every** candidate (`PilotInitialContactEligibility.CanInitiateWith(aircraft, position, type, ctx)`): an arrival still owned by approach with no handoff inbound does not call the tower whoever staffs it — the transferring controller hands the pilot over (7110.65 §2-1-17), the pilot never self-initiates with the next position.

An AI entry whose callsign is the student's own position is dropped while solo training is on (compare by callsign: tower-cab positions share a TCP). `PilotResponder.ResolveAnsweringCallName` picks the addressee's vNAS radio name only when its type matches the call — a tower-only student still gets "ground, ready to taxi" — exactly the old `StudentRadioName` rule.

**The positions the student works.** A solo student works its own position and every position type below it, down to `SimScenarioState.StudentLowestWorkedLevel` (CTR > APP > TWR > GND > DEL; `StudentWorkedRange`, kept on the roster as `PilotContactRoster.StudentRange`).

`StudentWorkedRange.Works(level, airport)` is true when the level is inside that span and no online scenario position of that level covers the airport: a scenario `atc` position with `AutoConnect` counts as online, and an online DEL, GND or TWR covers only its own airport while an online APP or CTR covers every airport. An AI-staffed position of the wanted type still answers first.

The lowest level is seeded when a solo scenario loads or a loaded one turns solo: the room's carried answer for the scenario, else the ARTCC sidecar's `studentLowestPosition`, else the student's own level ([scenario-loading-and-generation.md](scenario-loading-and-generation.md)).

A null level means no range: the student answers every call type, `StudentWorksDeliveryAt` falls back to the student's own `…_DEL` callsign at the airport, `StudentWorksTowerAt` to "the student's position type is not APP or CTR", and no tower is simulated.

**The silent simulated tower.** `PilotContactRoster.TowerIsSimulatedAt(airport)` is true when the airport has a tower (`ToweredAirports`, the ARTCC config's tower-cab rule), the student has a range that does not take in the tower there, and no AI-staffed tower covers the airport.

Then no tower-type call reaches the student (`ResolveFor` finds nobody for `TWR`, so the ready-for-departure call, the lined-up reminder, the final-approach check-in and the closed-traffic request are never made), and the simulation plays the tower without a word:

- **Departures** (`SimulationEngine.ProcessSimulatedTowerDepartures`, in `TickPrePhysics` right after `ProcessRunwaySpawnAutoTakeoffs`, solo rooms only). A ground departure the student transferred is cleared for takeoff (`AutoIssueTakeoffClearance`, scripted, terminal note "[Sim tower] Cleared for takeoff, runway 28R").

  It must be at its departure runway (holding short of it, or lined up with its release), `SimulatedTowerCtoDelaySeconds` after the transfer, and `SimulatedTowerRunwayGuard.ReasonToWait` must find the runway clear. The delay is 5–20 s, an FNV-1a draw on the callsign salted `simulated-tower-cto:` (replay-safe). A departure the student never transferred holds. A departure held for release or released is left to the hold-for-release path ([hold-for-release.md](hold-for-release.md)).
- **The transfer** is `AircraftGroundOps.LeftStudentFrequencyAtSeconds`, stamped by `ContactCommandHandler` on the first `CT` to a tower-type position, or `FCA`, given to an aircraft on the ground (scenario-scripted commands and a `CT` to a ground position do not stamp it; a later transfer keeps the first stamp).
- **The runway guard** waits while anyone else is departing, landing, on, crossing, on short final to or fouling the same physical runway at either end, or an arrival to it is inside the controller-AI crossing gate's final gate.

  It also waits while the wake turbulence interval behind the preceding departure from that runway has not run (timed from its `AircraftGroundOps.TakeoffRollStartedAtSeconds`, `RunwayDepartureSeparation`, 7110.65 3-9-6), or traffic on a runway crossing it has not passed the intersection (3-9-8). It is checked every second and the reason is logged at debug level once per change.
- **Arrivals** land without a clearance and are sent around when the runway is not clear: `SimScenarioState.IsAutoClearedToLandFor` is the room's auto-cleared-to-land toggle or a simulated tower at the assigned runway's airport, and `BuildPhaseContext` derives `PhaseContext.AutoClearedToLand` and `AutoGoAroundOnOccupiedRunway` from it per aircraft (7110.65 3-10-3.a).

  Takeoffs and landings at such an airport are the simulated tower's operations and `SoloTrainingEvaluator` does not score them.
- **After landing** the aircraft calls the student for taxi-in, below.

**Which spawns make the first call.** The scenario loader classifies every ground and runway spawn once, at load, into `AircraftGroundOps.InitialCallup` (an `InitialCallupPlan`, from `InitialCallupClassifier.Classify`); nothing else arms a call, so an aircraft that taxis to a stand, a warped or generated aircraft, one the instructor adds, or an airborne spawn keeps the default `None` and never calls.

The classifier parses each timed preset with `CommandParser.ParseCompound` (so `WAIT n TAXI …` and `RWY x …` count as taxis; a preset that does not parse is skipped with a warning) and decides, first match wins:

1. A runway spawn (`OnRunway`): a takeoff preset (`CTO`, `CTOPP`) → `None`; else a `SAY` preset → `RunwaySayOnly`; else `RunwayNoPreset` (see **Runway spawns** below).
2. An airport with no taxi graph (no layout, or one with no nodes, as the SJC, FAT and RNO maps are: they carry runway features only) → `None`; a `SAY` preset is still said, and the aircraft makes no call of its own.
3. A `TAXI`, `TAXIAUTO` or `ATXI` preset to a runway → `None`: a scripted taxi never calls while it runs, and at the runway the tower call takes over. A runway hold short counts as the destination when it is the route's last stop (no spot or parking destination, and no spot hold short listed after it), so `TAXI B HS 28L $5` is not a taxi to a runway.
4. A taxi preset to parking (a stand or helipad) → `None`.
5. A `FOLLOWG` preset → `None`: a scripted follow ends wherever its leader leads.
6. Any other taxi preset → `AfterTaxiArrival` when it names the stop the call is made at, kept as `AircraftGroundOps.PresetTaxiStop` (a `PresetTaxiStop`): its `$spot` destination, else a trailing spot hold short, else its first taxiway hold short, else, for a taxi with no destination, the last taxiway of its path. A taxi that names none of these → `None`.
7. A `PUSH` or `PUSHM` preset and no taxi → `AfterPush`.
8. Otherwise → `StandCall`.

A coordinate ground spawn is **on a taxiway** when the taxi edge `GroundSpawnSnap.Apply` found, measured from where the aircraft stood before the snap, is within 150 ft (`RampLaneReposition.CurrentLaneMaxFt`), names a movement-area taxiway, and no parking or helipad node and no straight ramp connector edge is nearer (`GroundSpawnSnap.SpawnTaxiwayAt`): a stand within a lane's width of a taxiway is at the stand.

The taxiway's name is kept as `AircraftGroundOps.SpawnTaxiway`; a parking spawn never has one.

**The ground calls.** Each call waits its own delay and then goes through `InitialCallupCall.TryMake`: the pacing rate must be above zero, someone must answer ground at the airport, and a pacing slot must be free (otherwise it retries next tick); the call queues the line, records a `Taxi` pending request and marks the decision processed (`InitialCallupDecisionProcessed`).

- **Stand call** (`StandCall`, `AtParkingPhase`): 5 s after the phase starts (`ReadyToTaxiDelaySeconds`), from the stand ("at gate F8", "at parking GA13"), else from the spawn taxiway ("on taxiway K"), else "at the ramp".

  A jet or turboprop on a `PushBack` stand (`ParkedStandDeparture.Of`) asks for a push instead (`InitialCallupCall.RequestsPushback`): "request pushback", recorded as a `Pushback` pending request. An uncleared IFR departure whose call goes to the student asks for its clearance first and requests the push 30 s after it is answered (below). The plan re-arms as `AfterPush` with the decision still open, so the after-push ready-to-taxi call follows the push.

  A piston, a helicopter and any aircraft on an `Either` or `TaxiOut` stand still call "ready to taxi". AI Ground answers the request with a recorded `PUSH`; a `TAXI` straight off the stand answers it too, and `AtParkingPhase.OnEnd` then drops the owed after-push call.
- **After-push call** (`AfterPush`): the crew's post-push setup delay — jet 90 s, turboprop 60 s, piston and helicopter 30 s (`InitialCallupCall.PostPushSetupDelaySeconds`) — from the start of `HoldingAfterPushbackPhase`, naming the spot the tow ended on ("at spot 5") or the stand it left ("pushed back from gate F8").

  A tow that ends on another stand calls from `AtParkingPhase` there after the same delay, as a stand call from the new stand; an after-push aircraft still at its spawn stand never calls.

  `GroundCommandHandler.InstallTugMove` runs `InitialCallupCall.BeforeTow` / `AfterTow` around every tug move: they record `AircraftGroundOps.PushedBackFrom` (kept through a further tow from the alley) and `PushEndSpot`, carry an uncalled after-push call over a further tow, and turn an uncalled stand call into an after-push call when a controller pushes the aircraft first.
- **After-taxi-arrival call** (`AfterTaxiArrival`): 10–20 s (10 s plus an FNV-1a draw on the callsign, `InitialCallupCall.AfterTaxiArrivalDelaySeconds`, replay-safe) after the aircraft comes to rest at its recorded stop: in `HoldingInPositionPhase` at the spot ("at spot 9") or at the end of a route with no destination on the recorded taxiway ("on taxiway K"), or in `HoldingShortPhase` at the recorded taxiway bar ("holding short of C at T41W") or spot bar; never at a runway bar.

  A controller's TAXI that takes the aircraft elsewhere never triggers it. At that bar the usual "holding short of C at T41W" report is not made when the call can come (`InitialCallupCall.CanCall`: someone answers and the pacing rate is above zero), so the pilot does not say it twice and never sits at the bar with neither.

An aircraft that leaves uncalled loses its call: `AtParkingPhase.OnEnd` drops a stand call (and an after-push call owed on a stand a tow ended on) when the aircraft leaves the stand, and `HoldingAfterPushbackPhase.OnEnd` drops an after-push call when the aircraft leaves the post-push hold; a tug move restores the call it carries over (`AfterTow`, above). A dropped plan is `None` for good.

A call from a taxiway by an aircraft whose filed destination is the field it is at asks for parking instead — "ground, on taxiway K, with information A, request taxi to parking." (`PilotResponder.RequestsTaxiToParking`) — and records the `Taxi` request with a parking the pilot has in mind, picked as an arrival's is (`ArrivalParkingPicker`), so the taxi-in path (`AnswerTaxiInRule`, which applies in `AtParkingPhase` too) answers it; a layout with no parking records the plain request.

**An uncleared IFR departure asks the student for its clearance first.**

The rule is top-down coverage, as on VATSIM: a stand call-up for an IFR departure goes to the lowest position the student works, for the first request it needs. When the solo student works clearance delivery at the aircraft's airport (`PilotContactRoster.StudentWorksDeliveryAt`) and the SOP lets the aircraft call them (`PilotInitialContactEligibility.CanInitiateWithStudent`), the first call — at whichever point the plan reaches — goes to the student as a clearance request, even when an AI ground answers there.

`StudentWorksDeliveryAt` is true, with a range, when delivery is inside it and no online scenario DEL and no AI-staffed `…_DEL` covers the airport; with no range, when the student's own callsign is that airport's `…_DEL`. A GND student whose lowest position is DEL gets the request too, addressed by the student's own radio name ("Oakland Clearance" for an OAK_DEL student, "Oakland Ground" for an OAK_GND student covering delivery).

With the student's lowest position at GND, an IFR departure from the airport with no clearance yet calls the student on ground for the clearance first, then requests pushback or taxi once it is answered (`InitialCallupCall.AsksForItsClearanceFirst`). The pilot knows delivery is offline, so the request is addressed "ground" when the student has no radio name there. Both clearance-first branches ask only from the stand call (`InitialCallup` still `StandCall`): an aircraft a scenario preset or a controller already pushed back or taxied counts as cleared and makes its ordinary ground call.

It applies only when nothing else covers delivery (`PilotContactRoster.DeliveryCoveredByAnother`: an online scenario DEL or an AI-staffed `…_DEL`) and the student, not an AI position, answers ground: an AI ground never gets the request, since nothing would answer it and the aircraft would wait forever. A VFR departure's ordinary ground call is unchanged. An aircraft on a taxiway makes the request only at its departure airport, on both branches (`DepartsFrom`).

IFR: "Oakland Clearance, at gate F8, with information A, IFR to Los Angeles Airport." (the addressee is the student's own radio name, "Oakland Ground" for a GND student); VFR, delivery student only: "Oakland Clearance, at gate F8, with information A, VFR departure to the north, at 4500."

(a direction of flight, never the destination, and the filed cruise altitude, left out when none is filed).

The direction (`VfrDepartureDirection.Choose`) is a cardinal whose ±45° sector has no other airport within 10 NM of the field, picked per callsign by an FNV-1a draw among the clear ones, else the cardinal whose nearest airport is farthest; it is chosen at the first request and kept on `AircraftGroundOps.VfrDepartureDirection`, so a follow-up repeats it.

The request is recorded as a `Clearance` pending request, and no other call-up goes out while it is open. It closes the decision, so no ready-to-taxi call follows, unless the student also answers ground at the airport.

Then the decision stays open, and once the request is answered (`AircraftGroundOps.ClearanceObtainedAtSeconds`, stamped by `PilotRequestTracker` on the beacon code or the PDC) the ordinary ground call (ready to taxi, or the pushback request from a push-back stand) goes to the same student `InitialCallupCall.TaxiCallAfterClearanceSeconds` (30 s) later, never a second clearance request. An IFR ready-to-taxi call after a clearance leaves out the "IFR to …" clause the request already gave.

A beacon code (`SQ <code>`, `RANDSQ`, `SQVFR`) answers it through `PilotRequestTracker.ApplyControllerResponse`; a PDC sent by `TDLSS` answers it in `TdlsCommandHandler.HandleSend` (`PilotRequestTracker.SatisfyOpenRequest`, plus `SimulationWorld.AcknowledgeControllerResponse`); a `TDLSS` in a scenario's timed preset sends no PDC and answers nothing (the dispatcher reports it unapplied). No AI position answers a clearance request (`UnansweredPilotRequestRule`).

**Runway spawns.** `LinedUpAndWaitingPhase` decides at its lined-up call point what a runway spawn does (`RunwaySpawnCall.Decide`). A `RunwaySayOnly` spawn stays silent: its `SAY` preset is what it says.

Under a student who does not work the tower at the runway's airport (`PilotContactRoster.StudentWorksTowerAt`: with a range, the tower is outside it or an online scenario TWR covers the airport; with no range, the student's `StudentPositionType` is `APP` or `CTR`), a `RunwayNoPreset` spawn does not make the lined-up call to a tower nobody staffs.

In an RPO room nothing below departs on its own: a towered or VFR spawn holds lined up with no lined-up call and no "[Auto]" line, a spawn released through the spawn gate holds too (its release clock is never started in an RPO room), as does one released by `REL` (`ProcessReleasedGroundDepartures` clears its release clock), and each leaves on the RPO's `CTO` or its timed preset. In solo training:

- **Towered field** (the loaded ARTCC config has a tower-cab facility — `Atct`, `AtctTracon` or `AtctRapcon` — for the runway's airport, `AiPositionResolver.IsTowered`, evaluated by the engine and passed in as `PhaseContext.IsRunwaySpawnFieldTowered`).

  After `LinedUpReadyDelaySeconds` (90 s) the engine clears it for takeoff (`SimulationEngine.ProcessRunwaySpawnAutoTakeoffs` → `AutoIssueTakeoffClearance`, terminal note "[Auto] Towered field — cleared for takeoff by the simulated tower"), scripted, so the airborne check-in follows.
- **Untowered field, VFR**: after the same 90 s it departs on its own, the same way ("[Auto] Untowered field — VFR departure on its own").
- **Untowered field, IFR**: it never departs on its own.

  Under a student working a radar position (`StudentPositionType` `APP` or `CTR`), 5–10 s after lining up (5 s plus an FNV-1a draw on the callsign, `RunwaySpawnCall.ReleaseRequestDelaySeconds`) it asks the student for its release — "NorCal Approach, runway 25 at {airport's spoken name}, ready for departure, request release." (`RunwaySpawnCall.TryRequestRelease` → `PilotResponder.BuildReleaseRequest`).

  It holds for release (`Ground.HeldForRelease`, so it shows in the release rundown and `REL` finds it) and records a `Release` pending request.

  `REL` or `HFROFF` answers it, and the departure then takes off on the line-up-and-wait auto-clearance after the 5–20 s release jitter ([hold-for-release.md](hold-for-release.md)). The request is not the pilot's initial contact (it does not call `MarkInitialContact`), so the airborne check-in still follows the takeoff. With no ARTCC config loaded the field counts as untowered.

  Under a student working neither a radar position nor the tower there (GND or DEL), the spawn makes no call and waits lined up, silent, for its release as a `CTO` from whoever works the overlying approach or center (the student working it, or the RPO).
- A spawn released through the hold-for-release spawn gate (`Ground.ReleasedAtSpawnGate`) already has its release and never asks for one: at an untowered field an IFR one starts the released departure's clock as soon as it is lined up and departs after the release jitter; a towered or VFR one departs as above.

Under a student who works the tower there a `RunwayNoPreset` spawn makes the lined-up call ("tower, runway 28R, ready.") after 90 s, as every other runway spawn without a takeoff clearance does. `ProcessRunwaySpawnAutoTakeoffs` closes the decision of a `RunwayNoPreset` spawn whose student works the tower there, or that has left `LinedUpAndWaitingPhase`, so the towered check is never evaluated for it again.

The solo pacing slider shows when any loaded aircraft's plan is one of the paced ground plans (`StandCall`, `AfterPush`, `AfterTaxiArrival`; `ScenarioLoadResult.HasParkingSpawns` — a runway spawn's calls are not paced).

The client's pre-load hint, `ScenarioDifficultyHelper.HasParkingSpawns`, runs the classifier's preset rules (`InitialCallupClassifier.ClassifyPresets`) over each `Parking` spawn and each `Coordinates` / `FixOrFrd` spawn with no altitude and no speed in the scenario JSON; the server's post-load value stays authoritative.

**The taxi-in call.** `RunwayExitPhase.CompleteExit` arms `AircraftGroundOps.AwaitingTaxiInCall`; the idle phase the exit ends in makes the call three seconds after stopping — `Oakland Ground, clear of runway 28R at W, taxi to gate 29.` (`PilotResponder.BuildTaxiInRequest`, AIM 4-3-21.c) — naming the parking the pilot picked itself.

The pick is `ArrivalParkingPicker`: the operator's own ramp when the layout names one, a cargo apron or numbered gate for an airline, a non-gate spot for a registration; occupied, taxied-to and already-requested spots skipped; an FNV draw on the callsign, so replays agree.

It records a Taxi `PilotPendingRequest` carrying `ParkingName`, follows up every 120 s like the parking call, and any taxi clearance closes it. Nobody answering ⇒ the call waits. While a *separately* staffed Local answers at the airport, the pilot stays with the tower until sent to ground (AIM 4-3-14.c, 4-3-21.c): a `CT` to a ground position or an `FCA` sets `AircraftGroundOps.ReleasedToGround`, which the call consumes; a combined cab (one answering position under both hats) needs no release.

A silently simulated tower is no separate Local: under a GND student the aircraft the simulated tower landed calls the student for taxi without any `CT` or `FCA` first.

**Non-gate names stay out of arrival picks (decided, not built yet).** A per-airport denylist data file lists the names in a layout that are not gates, and `ArrivalParkingPicker` excludes them from every pool: SMF's digit-led markers off the concourses (`1`–`4`, `30`–`32`, `40`–`43`), FLL's terminal markers (`1`–`4`), and layout placeholders such as SMF's `FAA`, `GA` and `TEXT`.

Today `ArrivalParkingPicker.Candidates` keeps every gate name when the digit-led names are under half of them, so at SMF `UAL123` can draw `41`.

**`HasMadeInitialContact` stays student-scoped; the AI latch is per position.** A call answered by an AI position
adds only that position's id to `AircraftState.AiInitialContactPositionIds` (`PilotAnsweringPosition.MarkInitialContact`
/ `HasInitialContact`), so a departure handled by AI Ground and AI Local still makes its airborne check-in with a
student radar position — the same rule scripted clearances follow (`IsScenarioScripted`) — and an aircraft that called
AI Oakland Ground still makes a fresh call to AI San Francisco Local on final at SFO (AIM 4-2-3.a.1.1: each new
facility or controller is a new initial contact). The radar-side proactive calls (`TickAirborneCheckIn`,
`TickArrivalApproachRequest`, `TickAirspaceBoundaryRespect`) stay student-only until AI radar positions exist, with
one guard added for the mixed case: `TickAirborneCheckIn` skips aircraft inside the tower's arrival side
(`TowerCabPhases.IsArrivalSide`: final, landing, pattern, go-around, tower maneuvers on final), so an arrival whose
on-final call an AI tower answered does not also make an initial call to the student approach position from a
three-mile final (AIM 5-4-3.a) — departures in initial climb still check in with departure (AIM 5-2-9).
`TickPendingRequests` (follow-ups) and both `PendingPilotTransmissions` drain gates
(`SimulationEngine.TickPostPhysics`, `TickProcessor.BroadcastPilotTransmissions`) key on `PilotContacts.AnyAnswering`.
Known v1 limitations (tracked in `docs/plans/MAIN.md`): one shared `SimulationWorld.ActiveFrequency` airtime model for
every answering position (an AI Ground answering a taxi request clears the awaiting-response gate held for a pilot
waiting on tower); first-by-position-id wins between two same-type AI positions at one airport (no LC1/LC2 split);
pilots holding short self-switch to the tower without a `CT` (AIM 4-3-14.a-conformant on the pilot side; the AI
Ground's own §2-1-17 transfer duty lands with the CA2 brain).

## Initial contact and scripted clearances

`AircraftState.HasMadeInitialContact` gates the proactive check-ins (`PilotProactive.TickAirborneCheckIn` and the parking/pattern call-ups) — once set, the pilot has already established two-way comms with the student and won't volunteer another check-in. It is set two ways:

- The pilot makes the call (the proactive paths set it on success).
- The **student** issues a clearance to a ground aircraft. `CommandDispatcher.DispatchCompound` sets it on any successful command to an on-ground aircraft (the pilot read back the clearance the student spoke). This deliberately suppresses a redundant post-takeoff check-in for a departure the student cleared during taxi.

  `PilotInitialContactEligibility.RegisterControllerContact` additionally marks the controller side (`HasControllerAcknowledgedInitialContact`) and, for aircraft the pilot could never call first (owned by another position, no handoff inbound), the pilot side too.

**Scenario-scripted clearances are not the student talking.**

A `CTO` preset (`DispatchSinglePreset` / `ProcessTimedPresets`) and, in solo rooms only, the automated-tower auto-CTO flow through the same `DispatchCompound`. The auto-CTO is the one on a hold-for-release release, for a runway spawn under a student who does not work the tower there, or from the silent simulated tower (`AutoIssueTakeoffClearance`, from `ProcessReleasedGroundDepartures`, `ProcessRunwaySpawnAutoTakeoffs` and `ProcessSimulatedTowerDepartures`).

Both pass `DispatchContext.IsScenarioScripted = true`, so they do **not** set `HasMadeInitialContact`.

A runway-spawn CTO-preset departure (or a released held departure) handed to a non-tower student via auto-track therefore still makes its airborne check-in. The flag rides through deferral on `DeferredDispatch.IsScenarioScripted` (so a preset `WAIT … ; CTO` firing on the ground stays scripted), and across a snapshot restore on a queued `CommandBlock.IsScenarioScripted` (so a preset queued behind a trigger and restored by a rewind still neither sets `HasMadeInitialContact` nor marks its speed controller-issued); live and reaction-delay deferrals default to non-scripted. Live and replayed *controller* commands pass `IsScenarioScripted = false` and keep the suppression.

## "Unable" routing on rejected commands

`CommandResult` carries the rejected `CanonicalCommandType` (`RejectedCommandType`). `CommandDispatcher.WithRejectedCommand` stamps the type onto every failure path before it returns. `SimulationEngine.QueueSoloUnableIfNeeded` then checks `CommandRegistry.Get(rejectedType)?.ProducesPilotUnable` and, if true, queues the handler's own `CommandResult.PilotUnable` or else `PilotResponder.BuildUnable(aircraft, result.Message)`.

A refusal about YAAT's own command forms is never voiced: the RPO-only commands refused in solo (FOLLOWF, CVAF, RFISF, RTISF, CLANDF) and a bare RFIS / RTIS are built with `CommandDispatcher.OperatorGuidanceRefusal`, which sets `CommandResult.IsOperatorGuidance`, and `QueueSoloUnableIfNeeded` returns before queuing anything. The student sees the terminal error only; no pilot says "unable, CLANDF is RPO-only", and the next readback is not queued behind one. A new solo-only refusal that tells the student which command form to use goes through the same factory.

`ProducesPilotUnable` is set on each `CommandDefinition` and defaults from category:

- **True**: Heading, Altitude / Speed, Navigation, Tower, Pattern, Hold, Helicopter, Ground, Approach.
- **False**: global commands and other categories. Tracking, coordination, flight-plan, scenario, etc. don't get a pilot "unable" — those failures are controller-side problems.

`PatternEntry`-built definitions are explicitly `true` regardless of category (every pattern-entry verb is pilot-aviated). When adding a new command, accept the category default unless the command demonstrably is or isn't pilot-aviated.

## Pending-request reminders

`AircraftState.PendingPilotRequest` (snapshot-serialized — schema v5) tracks one open pilot request per aircraft. `PilotRequestTracker`:

- `RecordRequest(kind, nowSeconds, line, context)` — fired by the originating site (e.g. `InitialCallupCall`'s ready-to-taxi or clearance request, `RunwaySpawnCall`'s release request, `FinalApproachPhase`'s arrival check-in, `PilotProactive.TryRequestBravoClearance`'s Class B request — see [airspace-database.md](airspace-database.md) "Solo Class B request and implicit clearance"). Takes the builder's `PilotSpeechText` and stores both forms (`LastPilotLine` / `LastPilotLineTts`).
- `ApplyControllerResponse(compound, nowSeconds)` — maps command type to `Satisfied` / `Denied` / `Superseded` / `Standby` per request kind. Reached from `SimulationEngine.ApplyPostDispatch` on the user-issued path, from `ApplyRecordedCommand` on replay, from `RecordingManager.ApplyRecordedCommand` on snapshot reconstruction, and directly from `AutoIssueTakeoffClearance` / `ProcessTimedPresets` for clearances the automated tower or the scenario script issues.
- `SatisfyOpenRequest(aircraft, kind)` — answers the open request of one kind by an action that is not an aircraft command and so never passes `ApplyControllerResponse`: a release (`REL`, `HFROFF`, in `HeldReleaseService`) answers a `Release` request, a PDC sent by `TDLSS` (`TdlsCommandHandler.HandleSend`) a `Clearance` request. An open request of any other kind is left alone.
- `TryQueueFollowUp(nowSeconds)` — called from `PilotProactive.TickPendingRequests` each tick. Re-queues both stored forms (`LastPilotLine` + `LastPilotLineTts`) via the `PilotSpeechText` overload after 120 s normally, 90 s after `STBY`/`ROGER` (`AcknowledgePilotContactCommand`). The shorter STBY/ROGER delay reflects that a bare acknowledgment isn't substantive direction — a pilot expecting a clearance won't sit silent for several minutes after only "roger".

  `Taxi`, `Pushback`, `Takeoff`, `Clearance` and `Release` are surface-only kinds: an airborne aircraft closes them as `Superseded` instead of following up, so no one re-announces "holding short … ready for departure" from 3000 ft.

  A `Taxi`, `Pushback` or `Clearance` request is also closed as `Superseded` once the aircraft is in any phase but one where a pilot still waits for a taxi clearance (`AtParkingPhase`, `HoldingAfterPushbackPhase`, `HoldingAfterExitPhase`, `HoldingInPositionPhase`, or `HoldingShortPhase` at a bar that protects no runway), so an aircraft taxiing or following never repeats "at parking GA16 … ready to taxi" from the taxiway.

Eight request kinds: `Taxi`, `Pushback` (a jet or turboprop on a push-back stand asking for a push; mapped as Taxi is), `Takeoff`, `Landing`, `Approach`, `AirspaceEntry`, `Clearance` (an IFR or VFR departure's request to the lowest position the student works) and `Release` (an untowered runway spawn's release request).

Each maps controller commands to terminal states (e.g. `ClearedForTakeoffCommand` → Satisfied for Takeoff; `FollowGroundCommand`, like any taxi or push, → Satisfied for Taxi; `SquawkCommand` / `RandomSquawkCommand` / `SquawkVfrCommand` → Satisfied for Clearance; `ExpectApproachCommand` → Standby for Approach; `LineUpAndWaitCommand` → Superseded for Takeoff). A `Release` request is answered only through `SatisfyOpenRequest`.

## `ApplyPostDispatch` — one hook, two hosts

`ApplyPostDispatch(aircraft, compound, result, DispatchOrigin origin, BravoClearanceWait? bravoWait)` also takes the solo Class B wait state (`ImplicitBravoClearance.CaptureWait`), which `ActionArms.Aviation` captures **before** dispatching because the dispatch can replace the phase list the wait is read from. It takes who issued the command
(`Commands/DispatchOrigin.cs`; hosts derive it from the connection id — `AiConnectionId.Format(positionId)` =
`"AI:{positionId}"` — so recorded AI commands replay with the same origin). The pilot-voice side (request resolution,
frequency-gate release, read-back, "unable") runs whenever `PilotContacts.AnyAnswering`; two-way-comms registration
(`RegisterControllerContact`) and solo-evaluator scoring run only for a `Human` origin. The `ActionRouter`'s aviation arm
derives the origin from the connection id on every run kind — fresh or recorded — and passes `IsScenarioScripted` for an AI
origin; the server's reconstruction and tape playback go through the same router, so they apply the same split.

Everything that happens *after* a user-issued command dispatches lives in `SimulationEngine.ApplyPostDispatch(aircraft, compound, result, origin)`: two-way-comms registration, `SoloTrainingEvaluator.RecordControllerCommand`, `PilotRequestTracker.ApplyControllerResponse`, `SimulationWorld.AcknowledgeControllerResponse`, `QueueSoloUnableIfNeeded` on failure, and the pilot read-back.

The router's aviation arm calls it after every dispatch it makes, so a replayed command produces the same read-back and frequency-gate state the live one did (the host decides whether to broadcast the transmission).

**One caller** — the `ActionRouter`'s aviation arm, on every run kind (`SimulationEngine.SendCommand`, `RoomEngine.SendCommandAsync`, replay and reconstruction all route through it). The server used to carry its own partial copy of this block; the pending-request resolution and the frequency-gate release were missing from it, so in the real app no pilot request was ever satisfied and departed aircraft re-announced "ready for departure" every 120 s forever (issue #307).

A Yaat.Sim test driving `SendCommand` cannot catch that, and neither can a recording replay — `yaat-server/tests/Yaat.Server.Tests/PilotRequestResponseServerParityTests.cs` drives the real `RoomEngine` path instead.

## Solo pacing rates

Two scenario knobs control pilot-AI cadence (persisted via `RecordedSettingChange` so replay round-trips):

- `SoloParkingInitialCallupRatePercent` (0–200) — interval = 20 s × 100 / rate. Setting to 0 suppresses the ground spawns' initial call-up (ready to taxi or clearance request) entirely; aircraft sit on `InitialCallupDecisionProcessed = false` until rate goes positive, and an aircraft at its preset taxi's bar makes the usual hold-short report instead (`InitialCallupCall.CanCall`). A runway spawn's release request is not paced.
- `SoloArrivalGeneratorRatePercent` (0–100) — multiplier on each generator's `IntervalTime`. Setting to 0 suspends generators; generators reschedule from now when rate goes positive.

`SimulationEngine.ApplySoloPacingRates(parking, arrival, rescheduleFromNow)` is the only setter. Live UI changes pass `rescheduleFromNow=true`; scenario-load and replay pass `false`. `ScenarioPacing.TryReserveParkingInitialCallupSlot` is the slot-allocator threaded into `PhaseContext.TryReserveSoloParkingInitialCallupSlot`; `InitialCallupCall.TryMake` consults it before queueing any initial call-up, wherever it is made.

## Client-side TTS (optional)

`PilotVoiceService` (in `Yaat.Client`) consumes `PilotTransmissionBroadcastDto` from the server. The service is unbounded-channel-backed and runs a single worker.

- **Synthesizer.** `SherpaOnnxPilotVoiceSynthesizer` loads the Piper LibriTTS-R medium voice pack (904 speakers). The voice pack is downloaded by `PiperVoiceInstaller` from the sherpa-onnx GitHub release into `YaatPaths.Combine("voices", ...)` so Velopack upgrades don't wipe it.
- **Phonemization.** sherpa-onnx 2.0 has no text frontend for Piper, so `EspeakPhonemizer` turns the text into IPA codepoints first (NuGet `com.github.csukuangfj.piper.phonemize`, espeak-ng, GPL-3.0-or-later) and synthesis gets them as `OfflineTtsGenerationConfig.PhonemeCodepoints`.

  The phonemizer reads the voice pack's own `espeak-ng-data`. espeak-ng is process-global, so one data directory serves the whole process. Synthesis that yields no speech throws (`EspeakPhonemizer.RequireSpeech`); without the phonemes, 2.0 renders silence and raises no error.
- **Speaker assignment.** `PilotVoiceAssigner` (yaat-server) maps `(scenarioRngSeed, callsign)` → speaker id 0–903 deterministically. Same callsign in the same scenario keeps the same voice.
- **Speaking rate.** `UserPreferences.PilotVoiceSpeechRate` (0.75–1.5 in 0.05 steps, default 1.1) is read with volume and radio FX when `MainViewModel.OnPilotTransmissionReceived` enqueues a transmission, and becomes the synthesis call's `OfflineTtsGenerationConfig.Speed`, which shortens phoneme durations without shifting pitch.

  `OnPilotTransmissionReceived` first multiplies the setting by the broadcast's `SpeechRateFactor` (`PilotSpeechRateNudge.Apply`): the server sets it from the frequency's load band when the line went on air, 1.1 when busy and 1.0 when normal or quiet, so pilots never speak slower than the setting.

  The product is clamped to the setting's 0.75–1.5 range, and a non-finite or non-positive factor counts as 1.0. The prewarm call and the model's `LengthScale` stay at 1.0. The rate is client playback only: the server's airtime serialization does not read it.
- **Radio FX.** `RadioAudioFx` applies a band-pass (1450 Hz, Q=0.9), highpass at 200 Hz, soft tanh saturation, and a 120 ms squelch tail. Toggle via `UserPreferences.PilotVoiceRadioFxEnabled`.
- **Output.** `PortAudioFloatPlayer` writes mono float32 to the default output device.

The pipeline is silent and harmless if Piper isn't installed — `IsAvailable` returns false and `Enqueue` no-ops. The terminal SAY lines still render either way.

- **Missing-voice warning.** Solo students only enter commands and are meant to hear pilots, so `MainViewModel.PilotVoiceWarning.cs` flags a solo session (in a room, scenario loaded, `SessionSoloTrainingMode`) whose pilot voice is off or unavailable (`!(PilotVoiceEnabled && IsAvailable)`): a persistent banner, plus a once-per-session modal on the first student resume (`ConfirmResumeAsync`, gating `TogglePauseAsync`, typed `UNPAUSE` and `TogglePlayback`).

  The session flag resets on a scenario load (`ApplyScenarioResult`, `OnScenarioLoaded`), a recording load (`ApplyRecordingResult`) and a room change, not on a reconnect's `ApplyRoomState`.

## Benchmarking TTS models

`Yaat.SpeechSandbox --tts-bench` compares small TTS models (size, cold load, first-audio latency, real-time factor) on the pilot readbacks the solo pipeline speaks, and builds a listening page with each model's clips. Run it before swapping or adding a pilot voice model; the flags, screen thresholds and exit codes are in the Speech Sandbox Tool table of [speech-recognition-pipeline.md](speech-recognition-pipeline.md).

The findings are in `docs/research/2026-10-24-tts-benchmark/report.md`, the listening page in `docs/plans/canvases/tts-benchmark/` (`README.md`), and `RECIPE.md` there rebuilds the clips and the review copy. The client and the sandbox both run sherpa-onnx 2.0.1; the bench phonemizes the Piper, Kitten and Matcha rows through `EspeakPhonemizer` with each model archive's `espeak-ng-data`, and ZipVoice tokenizes through its own lexicon.

## Pitfalls

- **Don't push solo-mode pilot speech to `PendingNotifications`.** Use `QueueSoloPilotTransmission` / `QueueSoloPilotReadback`. Pre-queue paths bypass airtime serialization and can step on awaited readbacks.
- **Don't regex-strip the TTS form to recover the terminal form.** Builders that want different terminal/TTS output should return `PilotSpeechText` directly.
- **Wire new sim-control toggles through `RecordedSettingChange`.** `SimulationEngine.ApplySettingChange` must handle the new key, and the UI emitter must record one. Otherwise the bundle replays with stale values. The pacing rates and `RpoShowPilotSpeech` are the live examples.
- **`PilotPendingRequest.LastPilotLine` is replayed verbatim.** When the follow-up fires, the original line is re-queued — including any transient airport/runway IDs. Don't bake user-mutable identifiers into the line if they could change.
- **Two `ResolveAltitudeGoal` implementations.** `FlightPhysics.UpdateAltitude` uses `AltitudeSnapFt` hysteresis; `AirspaceDatabase.ProjectAltitude` doesn't (projection wants raw trajectory). Both are correct for their use cases — keep them in sync if you change the floor/ceiling resolution rules.
