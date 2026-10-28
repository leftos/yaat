# YAAT-1201 — aviation review of runway hold-short placement (KOAK)

Read-only consult. Inputs: `docs/ground/hold-short-placement.md`, `src/Yaat.Sim/Data/Airport/RunwayCrossingDetector.cs` (`BuildRunwayRectangle` lines 129-166, `HoldShortDistanceForWidth` lines 446-477), `AirplaneDesignGroup.FromRunwayWidth` (same buckets), `.tmp/yaat-1201-measure.md`, KOAK records in `tests/Yaat.Sim.Tests/TestData/FAACIFP18.gz`.

## Source note: what is local, what is my own knowledge

- **Local (quoted/cited from `.claude/reference/faa/`)**: AIM 2-3-5 (holding position markings), AIM 2-3-8.b (mandatory signs incl. ILS hold sign), AIM 4-3-18.a.8, AIM 1-1-9.k (ILS critical areas), AIM 2-1-5 (lead-off lights), 7110.65 §3-1-5 NOTE, §3-7-4 NOTE, §3-7-5, §3-7-6, P/CG "RUNWAY SAFETY AREA" and "OBSTACLE FREE ZONE".
- **Not local**: AC 150/5300-13B (Airport Design), AC 150/5340-1 (Markings), FAA Order 6750.16 (ILS siting/critical areas). The local refs only *point* to them: 7110.65 §3-1-5 NOTE and §3-7-4 NOTE ("Establishing hold lines/signs is the responsibility of the airport manager. Standards for surface measurements, markings, and signs are contained in … AC 150/5300-13, Airport Design; AC 150/5340-1 …; AC 150/5340-18"), and P/CG RSA/OFZ ("Refer to AC 150/5300-13, Chapter 3"). Every AC table value below is **from my own knowledge** and should be checked against the AC before it is quoted in code comments.

## 1. What the current rule implements

**What the AIM says the line is.** AIM 2-3-5.a.1: runway holding position markings on taxiways "identify the locations on a taxiway where aircraft MUST STOP when a clearance has not been issued to proceed onto the runway. Generally, runway holding position markings also identify the boundary of the runway safety area (RSA) for aircraft exiting the runway." The NOTE repeats "identify the beginning of an RSA". AIM 2-3-8.b.1.(c): the runway holding position sign "will be installed adjacent to holding position markings on the taxiway pavement". The AIM gives **no distance**; 7110.65 §3-1-5 NOTE puts placement with the airport operator under AC 150/5300-13.

**AC 150/5300-13B runway-to-holding-position separation (own knowledge).** The figure sits in the runway design standards matrices (Table 3-8 in -13B; Tables 3-2/3-3 in older editions). It is keyed on approach category (AAC, A-E), design group (ADG, I-VI) and visibility minimums (visual / not lower than ¾ SM / lower than ¾ SM):

| Case | Runway CL → holding position |
|---|---|
| AAC A/B, small airplanes (ADG I small) | 125 ft visual and ≥ ¾ SM; 175 ft for < ¾ SM |
| AAC A/B, ADG I-II (not small) | 200 ft; 250 ft for < ¾ SM (confidence moderate: the A/B rows are the ones I am least sure of) |
| AAC A/B, ADG III-IV | 200–250 ft |
| AAC C/D/E, ADG I-IV | 250 ft |
| AAC C/D/E, ADG V | 250 ft; 280 ft for < ¾ SM (CAT II/III) |
| AAC C/D/E, ADG VI | 280 ft |

Two footnotes matter:

- **Elevation adjustment**: for AAC C/D/E, the separation increases **1 ft per 100 ft of airport elevation**. The code comment knows this and skips it; it matters at high fields (DEN +54 ft, ABQ +53 ft, SLC +42 ft), not at OAK (TDZE 5–9 ft).
- **Tail-height / OFZ**: the hold must keep a holding design airplane's tail out of the inner-transitional OFZ (P/CG "OBSTACLE FREE ZONE", locally; the POFZ is 7110.65 §3-7-6). For ADG V/VI on a CAT II/III runway this is what pushes the standard out to 280 ft or more, and an airport may set it farther still by survey.

**Distance is to the hold line, not the centre of the taxiway (own knowledge, AC 150/5340-1).** The marking is painted perpendicular to the taxiway centerline. On an angled taxiway the **nearest point** of the marking must meet the separation, so the point where the taxiway centerline crosses the line sits farther out.

**What YAAT has.** `HoldShortDistanceForWidth` is a width-only approximation of the matrix. Its buckets (<75 → 125, <100 → 150, <150 → 200, <200 → 250, ≥200 → 280) use width as an ADG proxy. It has no approach-category or minimums input, no elevation term, and no angle term. The doc claim "the distance is the same regardless of the exit taxiway's angle" holds for the *nearest point of the line*, but not for the point on the taxiway centerline where an aircraft actually stops. That is the main defect the measurement shows (below).

## 2. Why OAK's real lines sit farther out

The measurement table separates two different effects.

**(a) The angle effect: the largest and fully modelable cause.** Every bar that is far out is on an **angled** taxiway. Look at the near/far ends of the painted line:

| Bar | Paint vs runway | Near end | Mid (on twy CL) |
|---|---|---|---|
| 30 W5 | 57° | 283 | 320 (real 316) |
| 30 W4 | 56° | 279 | 310 |
| 30 W3 | 56° | 281 | 313 |
| 28R P | 38° | 279 | 294 (real 293) |
| 28R J | 51° | 243 | 272 |
| 28L B | 44° | 237 | 267 |
| 28L P | 39° | 288 | 303 |

The near ends sit at the same distance as the perpendicular bars on the same runway (30: 276–282; 28R: 246–254). The mid-point excess is (L/2)·sin(φ), with L the line length and φ the line-to-runway angle. For W5: 44 × sin 57° = 37 ft, which takes the 283 ft near end to the 320 ft mid. This is the AC 150/5340-1 geometry, not a different standard.

**(b) The base distance.** It differs per runway:

- **30/12** (10,520 × 150, CIFP: ILS 30 **CAT III** `IINB`, ILS 12 CAT I `IAAZ`): the perpendicular lines are 277–282 ft. That is the AAC C/D, ADG V, < ¾ SM row (**280 ft**): OAK 30 is a CAT III runway serving MD-11/B744F/B77F traffic. YAAT's 250 ft (width 150 → "IV/V" bucket) is the 250 ft *visual/CAT I* row, and is 30 ft short.
- **28R/10L** (5,457 × 150, ILS 28R CAT I `IOAK`): the perpendicular lines are 246–254 ft, which is the 250 ft row. The **authored** vNAS `holdShortDistance` = 225 ft is the error here, not the width fallback (which would give 250 ft). It should go upstream to the vZOA facility data.
- **28L/10R** (6,213 × 150, no ILS): the perpendicular lines are 253–262 ft, the 250 ft row. The authored 250 ft is right.
- **15/33** (3,376 × 75, visual): the lines are 157–172 ft perpendicular. That matches no single AC row; it lies between the 125 ft and 175 ft A/B small rows, so it is a local survey. The authored 175 ft is the best available value (±18 ft).

**(c) ILS critical-area holds.** These are not the cause of the paired outliers, but they are present at OAK. AIM 2-3-5.b and AIM 2-3-8.b.3: "At some airports, when the instrument landing system is being used, it is necessary to hold an aircraft on a taxiway at a location other than the holding position described in Paragraph 2-3-5". These lines have a distinct ladder marking and an "ILS" sign. AIM 4-3-18.a.8: "If a pilot is expected to hold short of a runway approach/departure … hold area or ILS holding position …, ATC will issue instructions." The 7110.65 phraseology is §3-7-5.a, "HOLD SHORT OF (runway) ILS CRITICAL AREA", required when the ceiling is below 800 ft or visibility below 2 SM and the arrival is inside the OM.

The unpaired videomap bars fit this:

- **V12/V14** on A at 378 ft from 28R, behind the runway bars V11/V13 on B, are most likely 28R glideslope critical-area holds.
- **V26/V27** on K at 768/1018 ft, off the runway ends, look like runway approach-area holds (AIM 2-3-5.a.3, "28L APCH").

Neither should become the runway hold point; the pairing correctly left them unpaired. Critical-area dimensions (localizer ≈ 2,000 ft ahead of the array, ±200–300 ft; GS ≈ 800–1,000 ft toward the threshold between runway and antenna) are own knowledge from Order 6750.16. They are not modelable from YAAT's data with any precision.

**What YAAT can model from data it has:**

| Factor | Data | Modelable? |
|---|---|---|
| Angle term | taxiway geometry at the crossing (already in the graph) + a taxiway-width proxy | **Yes**: the main fix |
| CAT II/III → 280 ft | CIFP runway record (PG) ILS ident + category digit (`…IINB3` for 30) and localizer record (PI) category | **Yes** |
| AAC C/D proxy | runway length + width (CIFP PG or the layout) | **Yes**, approximately |
| Elevation term | runway TDZE (CIFP PG) / airport elevation | **Yes** |
| Design group | width proxy only (no ADG field anywhere) | Approximate |
| ILS critical-area hold position | none | No (only "this runway has an ILS") |
| Local survey offsets (OAK P, 15/33) | none | No: paint snap only |

## 3. Recommended rule for unpaired intersections

**Step A: base separation S** (feet from the runway centerline to the *nearest point* of the hold line), by first match:

1. **Per-runway paint calibration**: if the runway has ≥ 3 paint-paired bars, S = the median of their **near-end** cross-track distances (min of the two end distances, not the midpoint). This is data, it beats any table, and it fixes stale authored values. OAK gives 28R/10L 252.1, 28L/10R 258.5, 30/12 280.8, 15/33 164.9.
2. **Authored `holdShortDistance`** when present. Log a data warning when it is more than 15 ft below the step-3 value for a runway in the ≥ 250 class (OAK 28R: authored 225 vs rule 250).
3. **Rule**, using width W_rwy, length L_rwy, ILS category from CIFP (max over both ends) and elevation E_ft:
   - W ≥ 200 → 280
   - W ≥ 150 and ILS CAT II or III → 280
   - W ≥ 100 and (L ≥ 5,000 or any ILS) → 250
   - W ≥ 100 → 200
   - W ≥ 75 → 150 (175 if any ILS)
   - else → 125 (175 if any ILS)
   - For results ≥ 250 (AAC C/D/E rows): add `E_ft / 100` (use the higher threshold elevation of the pair).
   - Only an ILS moves a runway to the instrument rows (the 175 ft row, CAT II/III at 280): a LOC, LDA or LPV approach does not. The category is read from the CIFP localizer records with category 1, 2 or 3; a localizer-only record (category 0) gives none.

**Step B: centerline point.** Place the node where the taxiway centerline is at

`D = S + (W_twy / 2) · |cos θ|`

θ is the angle between the taxiway centerline at the crossing and the runway centerline (90° perpendicular → no term). Measure θ on the boundary edge's straight segment, not on a fillet arc. W_twy proxy, from the **crossed** runway's width (the layout has no taxiway width): W_rwy ≥ 150 → 75 ft; ≥ 100 → 50 ft; ≥ 75 → 35 ft; else 25 ft (ADG/TDG taxiway widths, own knowledge). Apply step B to calibrated and authored S too, since both are nearest-point values.

**Expected OAK results** (rule-only path; authored 175 kept for 15/33; residual = YAAT − paint mid, with real where measured):

| Runway | S | Perpendicular bars | Angled bars | Worst remaining |
|---|---|---|---|---|
| 28R/10L | 250 (rule beats authored 225) | 250–251 vs paint 246–258: −7…+5; E 250 vs real 258: −8 | J 279 vs 272: +7; **P 273 vs 294 / real 293: −21** | P −21 (today −69) |
| 28L/10R | 250 | 250–251 vs 253–262: −12…−3 | B 276 vs 267: +9; **P 274 vs 303: −29** | P −29 (today −53) |
| 30/12 | 280 (CAT III) | 280–281 vs 277–282: −2…+4 | W3/W4 311 vs 313/310: −2/+1; **W5 311 vs 320, real 316: −9 / −5** | W5 −5 vs real (today −70 / −66) |
| 15/33 | 175 authored (rule would give 150) | K/F/D 175–180 vs 158–172: +5…+17 | L 186 vs 157: +29; C 186 vs 193: −7; **C (V07) 186 vs 239: −53** | V07 −53 (today −64) |

Comments:

- OAK P (both runways) keeps a 20–30 ft shortfall: its near ends (279 / 288) are a local placement no table predicts. V07 (C on 15/33, both ends 241 ft) is unexplained; it is probably near a runway end, where cross-track to the extended centerline misleads.
- With step-A calibration, 28R/28L/30 residuals shrink by another 2–7 ft. 15/33 moves to S = 165, which trims the K/F/D overshoot to about +0…+12.
- 28R only reaches 250 with the authored value demoted (step-A or the step-2 warning plus a rule override). If authored strictly wins and nothing pairs, 28R stays at 225, 21–33 ft short.

**Integration footguns:**

- `AirplaneDesignGroup.FromRunwayWidth`, the HoldShortAnnotator wingtip floor and `OccupiedRunwayGoAround` reuse the width buckets or `HoldShortDistanceFt`. Change the hold distance in its own function rather than editing the shared buckets.
- Every moved bar renumbers nodes and shifts routing (doc § Footguns).
- The angle term grows D, so more short taxiways will hit `DeadEndFallback`.
- The doc paragraph "The distance is the same regardless of the exit taxiway's angle" must be rewritten: the nearest point of the line is angle-independent, the centerline stop point is not.

## 4. Snapping to paint: aviation concerns and the choice rule

Snapping is right in principle: paint is the airport operator's surveyed placement (7110.65 §3-1-5 NOTE). Real controllers and pilots use it, and AIM 2-3-5.a.1 makes it the stop point and the "clear of the runway" point. Guard against the following:

1. **ILS critical-area line mistaken for the runway line.** The runway clearance point is the runway holding position marking (AIM 2-3-5.a.1, 4-3-21). The ILS line governs only "clear of the ILS critical area" (AIM 2-3-5.b) and a §3-7-5 hold.

   Snapping the runway node onto an ILS line would make every hold short of the runway, and every exit's "clear", happen 100+ ft late. It would be safe-side for a hold, but wrong for runway-occupancy timing and for what the trainee sees. Lead-off lights are coloured to "whichever is more restrictive" (AIM 2-1-5); that is a lighting convention and does not move the runway hold.
2. **Two bars on one taxiway, same side.** Pair the runway node with the **innermost** bar (nearest the runway) that passes the plausibility band below. A farther bar between it and the next junction is an ILS or approach-area hold (AIM 2-3-5.a.3 / b). Leave it unpaired, and book it as a future distinct hold node with "HOLD SHORT OF RUNWAY 28R ILS CRITICAL AREA" (7110.65 §3-7-5.a; AIM 4-3-18.a.8) and its own marking in the renderer.
3. **A single bar far out.** If the only bar is beyond D + 100 ft (or farther than about 450 ft), do not snap. It is an approach-area or ILS hold (OAK V26/V27 at 768/1018 ft, V12/V14 at 378 ft on A). Keep the rule point and log it. Where an airport really collocates the runway hold with the ILS hold, the line sits within the band and pairs normally.
4. **Far side.** Pair only on the same side of the centerline as the node (the measurement already does). Never move a node across the runway.
5. **Wrong taxiway / wrong pavement.** Snap to the **intersection of the painted segment (extended ≤ 10 ft) with the node's own taxiway centerline**, never to the bar midpoint: videomap bars are often drawn off-centre, by up to 89 ft of pair distance at OAK. No intersection, no pair. A bar crossing a *runway* centerline is a LAHSO / runway-runway hold (AIM 2-3-5.a.2) and never pairs with a taxiway node.
6. **Plausibility band.** Snap only when the snapped centerline distance D_paint lies within D_rule − 60 ft … D_rule + 100 ft, and never inside (runway half-width + 50 ft). The asymmetric band tolerates surveys that sit farther out (OAK P +21/+29) and rejects mis-pairs.

   Every OAK pair passes against the step-B rule except V07 (−53, inside the band anyway). A snap that moves the node more than 40 ft gets a debug log line.
7. **Dead-end / merge interplay.** A snapped point beyond the next junction on the taxiway means the paint belongs to a different branch; do not walk past a junction to reach it. Keep `DropRedundantFallbackBars` semantics: a bar that was snapped is no longer a fallback bar.

**Choice rule when a taxiway carries both a runway hold line and an ILS critical-area line:**

- The runway node binds the inner line.
- The outer line is not a runway hold. It is unpaired for now, and later a separate ILS hold that is armed only by an explicit ILS-critical-area hold instruction and never used for runway clear or occupancy.
- YAAT cannot tell the two line types apart from the videomap bar alone (same colour and thickness at OAK). Order along the taxiway (inner = runway) is the discriminator, backed by the band. "Runway has an ILS in CIFP" only raises the expectation that an outer line exists.
