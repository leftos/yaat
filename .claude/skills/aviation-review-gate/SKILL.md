---
name: aviation-review-gate
description: "Run CLAUDE.md's mandatory aviation-realism review correctly: decide whether the `aviation-sim-expert` review is owed at all, invoke it with the standard local-FAA-references preamble, and weigh what comes back against the evidence hierarchy. Use before or after any change to pilot AI, ATC logic, flight physics, phraseology, phase transitions, ground ops or conflict detection — and whenever a review, a sub-agent or an expert cites 7110.65/AIM to justify simulated behaviour. Trigger phrases: 'aviation review', 'does this need the aviation expert', 'is that citation right', 'review the physics/phraseology'."
---

# Aviation Review Gate

CLAUDE.md makes the `aviation-sim-expert` review mandatory for anything touching
aviation. That rule alone has misfired in both directions: fired on a change the
user had already specified down to the behaviour, and been *accepted* when it
cited a controller-procedure paragraph as grounds for what a simulated VFR pilot
flies. This skill is the gate around it — when it is owed, how to invoke it, and
how to weigh what it returns.

## Step 1: Owed, or not?

**Skip the review** when the user prescribed the exact behaviour. The shape to
recognise is an instruction, not a question:

- "make EXT also extend upwind/crosswind, just not base"
- "stop allowing a takeoff clearance while an arrival is on the runway"
- "TAXI \<rwy\> should only accept an adjacent runway"

The mandatory-review rule is about novel aviation behaviour, not about
user-specified command scope. Running a review over a prescribed tweak wastes a
turn and invites a reviewer to argue against a decision the user already made —
which is exactly what happened on the EXT extension, where the user interrupted
with "just accept that I want this without review."

**Invoke the review** when any of these is true:

- The design is open — the user asked how something *should* behave.
- Physics, geometry, phraseology or a pilot decision rule is being invented
  rather than transcribed from an instruction.
- A number is being chosen (an intercept angle, a rate, a threshold, a spacing).
- A phase transition or automatic aircraft behaviour changes when nobody
  commanded it.

**When it is genuinely ambiguous, ask** (AskUserQuestion) rather than defaulting
either way. A skipped review the user wanted is as bad as a review they did not.

## Step 2: Invoke it with the standard preamble

`Agent` with `subagent_type: "aviation-sim-expert"`. Paste this into every
invocation, verbatim — it is in CLAUDE.md so it is never retyped from memory,
and without it the reviewer burns the turn web-searching documents that are
already on disk:

> "IMPORTANT: The FAA 7110.65 and AIM are available as local markdown files in
> the repo. Read them directly via Read/Grep/Glob at
> `.claude/reference/faa/7110.65/` and `.claude/reference/faa/aim/`. Do NOT use
> web search tools to look up 7110.65 or AIM content."

Alongside it, give the reviewer:

- The diff or the concrete proposal — not a paraphrase of it.
- **Measured** values where the change is geometric (see Step 3), so the expert
  reviews the shape that exists rather than a hypothetical one.
- Which flight rules and which actor the behaviour belongs to (an IFR arrival
  under vectors, a VFR pilot told to follow traffic, a ground vehicle) — the
  answer decides which paragraphs can bind it at all.

## Step 3: Weigh what comes back

A review is an input, not a verdict. Three rules decide what to accept.

### 3a. Every citation must say who it binds, under which flight rules, and — for a number — where the number is

Before a regulatory citation is allowed to ground *simulated-pilot* behaviour,
it must state three things explicitly:

1. **Who the paragraph directs** — the controller, or the pilot.
2. **Its flight-rules scope** — IFR, VFR, or both.
3. **Where the number is**, whenever the citation is attached to a value (an
   interval, a rate, an angle, a distance, a threshold): **quote the sentence
   that states it.**

A citation missing any of the three is not yet evidence; go read the paragraph
before acting on it. The worked case: a 20° close-in intercept cap was added to
`VfrFollowPhase.TryJoinLeadFinal` citing 7110.65 §5-9-2 / TBL 5-9-1. That
paragraph instructs **controllers** vectoring **IFR** aircraft onto a final
approach course. It does not constrain what a VFR pilot flies when told to
follow traffic — VFR visual manoeuvring is AIM pattern and see-and-avoid
guidance. The user stripped the cap.

**Controller-vectoring geometry may inspire a heuristic. It is never presented
as regulatory ground for VFR pilot AI.** If the resulting number is a judgement
call, say so in the code comment and the changelog instead of dressing it in a
citation.

**Topicality is not sourcing**, and it is the more available signal, so it
substitutes for sourcing without anyone noticing. A 24-second track-coast timer
shipped commented "(7110.65 §5-13-8)"; §5-13-8 is *Controller Initiated Coast
Tracks*, is genuinely about coast tracks, and states no duration at all. The
first two questions above pass it cleanly — controllers, en route automation —
while the paragraph is silent on the very quantity it was cited to justify. When
no sentence in the cited paragraph carries the value, drop the citation and
label the number a judgement call.

### 3b. Evidence hierarchy for observable practice

**Before ranking anything, establish whether the publications address the
question at all.** Name the paragraphs you searched and report silence as a
finding: "I could not find a rule" and "there is no rule" are different answers,
and an unreported search miss lets an articulate derivation quietly outrank a
documented value.

Silence is not a fall-through to rank 4 — it *inverts* the ranking. A behaviour
the procedural standard never describes is a property of the equipment being
emulated, so the emulation target's own documentation becomes the top available
evidence, and a first-principles number may only ride *alongside* it as a
cross-check, never in its place. The worked case: a 45-second surface-track
coast interval appears nowhere in 7110.65 Chapter 3 Section 6 or AIM 4-5-5 —
both describe how a controller or a pilot *uses* an ASDE display, never what the
automation does with a track that stops updating — while the vendored display
manual states the 45 seconds verbatim, along with the list grouping and id
format.

When the question is *what a facility actually does* rather than *what is
defensible*, rank the evidence:

| Rank | Evidence | Example |
|------|----------|---------|
| 1 | Measured reference artefact | the reporter's screenshot, a recording, a tick CSV |
| 1 (on silence) | The emulation target's own documentation, where 7110.65 and the AIM say nothing | the vendored display manual stating the coast interval |
| 2 | The emulated **server** reference | vNAS messaging-master / data-master |
| 3 | Client-side observation | the decompiled CRC client at `..\crc-decompiled\CRC\` |
| 4 | First-principles reasoning | the expert's a-priori argument |

Higher beats lower on observable practice. On #312 the expert reasoned from
SFO's 750 ft parallel spacing that no mark could cross the centerline and
prescribed outboard ticks with distance numerals; the reporter's screenshot
showed marks crossing each centerline and no text at all. "Keep it as authentic
to the reference as possible" — practice won.

Two corollaries that travel with it:

- **Measure before proposing geometry.** Measure the reference by *contiguous
  runs*, not pixel counts — counting coloured pixels between two lines made one
  mark per runway read as a single bar spanning both, and printing the runs
  showed the gap immediately. Any "does it connect?" question needs run or
  adjacency analysis. Cross-check the derived scale against a known quantity in
  the same image.
- **Say plainly when a number is eyeballed** rather than measured, and offer to
  measure properly. The user may accept the estimate — but they should be the
  one choosing.

### 3c. The CRC client is not the server we emulate

The decompiled CRC at `..\crc-decompiled\CRC\` is the **client**. The vNAS
**server** YAAT emulates (messaging-master / data-master) is not decompiled and
may enforce rules the client never gates. "The CRC client doesn't prevent X" is
not evidence that the real server allows X.

So: **never relax or delete a server-side guard on the grounds that decompiled
CRC has no such check.** During #176 a sub-agent declared `AmendFlightPlan`'s
`NOT YOUR TRACK` ownership guard wrong because that string appears nowhere in
decompiled CRC — client UI, which proves nothing about server enforcement. If
server semantics are unconfirmed, the guard stays.

And when a review finds a YAAT gate genuinely *stricter* than real CRC/vNAS,
that is a product decision, not a bug: the user may keep the gate as training
behaviour and fix the silent failure instead. Offer the relax-the-gate option;
do not assume it.

## Step 4: Before you act on the review

Run this list against the review's own output. It is short because each item
has been violated in a shipped change:

- [ ] Every 7110.65/AIM citation states who it binds (controller/pilot) and its
      flight-rules scope, and I read the paragraph rather than the citation.
- [ ] No controller-vectoring paragraph is grounding VFR pilot AI.
- [ ] Every cited paragraph backing a constant contains that constant, and I
      quoted the sentence.
- [ ] If the publications are silent, I said so explicitly and named the
      authority I used instead.
- [ ] Where the question was observable practice, the highest-ranked evidence
      available won — not the most articulate argument.
- [ ] Any geometry number is measured, or is labelled as eyeballed to the user.
- [ ] No server-side guard was relaxed on decompiled-client evidence alone.
- [ ] Findings the user has already decided against are dropped, not re-argued.

Report the findings you accepted **and** the ones you rejected with the reason.
A review whose rejections are invisible reads as unanimous agreement.

## Anti-patterns

- **Reviewing a prescribed tweak.** If the user told you the behaviour, build it.
- **Citing a paragraph you did not open.** The local files are two Grep calls
  away; a citation from memory is a guess with a section number attached.
- **Citing a topically-adjacent paragraph as the source of a number.** A
  paragraph about the right subject that states no value is context, not
  authority.
- **Reasoning a number out because the search came up empty.** Report the
  silence, then look to the equipment's own documentation.
- **Letting an expert argument overturn a reference artefact.** The expert is
  the right input for whether geometry is *defensible*, not for what a facility
  already draws and uses daily.
- **Web-searching 7110.65 or the AIM.** They are in `.claude/reference/faa/`.
- **Treating the review as the gate itself.** The gate is the user's intent plus
  this checklist; the review is one input to it.
