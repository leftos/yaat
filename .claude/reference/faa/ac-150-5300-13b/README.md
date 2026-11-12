# AC 150/5300-13B — Airport Design

**Source:** owner-supplied PDF, `AC-150-5300-13B-Airport-Design-Chg1-w-errata.pdf`: AC 150/5300-13B, Airport Design, 3/31/2022, consolidated to include Change 1 (8/16/2024), errata updates as of 4/3/2025. The AC prints no URL for its own PDF; it lists all ACs at https://www.faa.gov/regulations_policies/advisory_circulars/.
**Initiated by:** AAS-100 (FAA Airport Engineering Division)
**Converted via:** `pdftotext -layout`, then hand-corrected against rendered page images; every table checked cell by cell against the rendered page.

This is a partial, verbatim extract covering taxiway and taxilane widths, separations, wingtip clearance and object free areas. The rest of the AC (runways, gradients, fillets, intersections, markings and so on) is not extracted.

## Files

- `ch1-adg-tdg.md` — selected definitions from 1.5 (ADG, CMG, critical aircraft, MGW, OFA, taxilane, taxiway, taxiway centerline, TDG, TESM, TOFA, TSA, wingspan); 1.6.2 and Table 1-2 (ADG by tail height and wingspan); 1.6.5, Figure 1-1 and Table 1-4 (TDG by CMG and MGW).
- `ch4-taxiway-taxilane-design.md` — 4.1, 4.2, 4.4 (straight-segment width, Figure 4-4), 4.5 (separations, Table 4-1 by ADG, Table 4-2 by TDG, dissimilar ADGs, TSA, TOFA/TLOFA, Figures 4-5 to 4-9), 4.6 (parallel taxiways, Figures 4-10 and 4-11), 4.8.6 (crossover taxiway separation, Figures 4-22 to 4-24), 4.11 (apron taxiways and taxilanes).
- `ch5-aprons.md` — 5.4.1 (apron taxilane separation, parking positions clear of OFAs), 5.6 (lateral object clearance on aprons, Table 5-1 parking position clearance, Figure 5-2), 5.7 (apron taxilanes), 5.19 and 5.19.1 (service road clearance to taxilanes and parked aircraft), 5.20.1 (passenger terminal apron, Figure 5-3).
- `appendices-e-j.md` — E.2.4.1, E.2.4.2 and E.2.4.5 (GA tiedown spacing and nested parking, Figures E-2 to E-5); J.4 in full (TOFA/TLOFA methodology, Tables J-9 and J-10, parallel separation method, B767-300 worked example, Figures J-11 and J-12).

## Key content for taxilane and wingtip work

- **Table 4-1** — the ADG-based standards: TSA, TOFA, TLOFA, centerline to parallel centerline, centerline to fixed or movable object, and taxiway and taxilane wingtip clearance.
- **J.4, Tables J-9 and J-10** — how Table 4-1 is built: ½ TOFA (or ½ TLOFA) = ½ ADG wingspan + lateral deviation + safety buffer, and the wingtip clearance in Table 4-1 equals lateral deviation + safety buffer.
- **4.5.2 and J.4.2** — parallel separation = half the OFA of one taxiway/taxilane + half the wingspan on the other; 4.5.2 applies it to dissimilar ADGs.
- **4.5.3.1 and 4.5.4.1** — TSA and TOFA/TLOFA widening at turns: (TSA Width – W)/2 and (TOFA Width – W)/2 from the taxiway edge.
- **Table 5-1** — recommended minimum parking position clearance: 10 ft for ADG I and II, 25 ft for ADG III to VI.

13B states no wingspan-multiple formulas (of the form "1.2W + 10"); the only formulas are the additive ones above.

## Extraction notes

- Paragraph numbers four levels deep (for example 4.5.1.1) are printed in a font `pdftotext` drops; they were restored from the rendered pages.
- A table cell's footnote marker is written `<sup>n</sup>`; the notes follow the table.
- A figure carrying text is given as its caption, its printed labels and its notes, with a bracketed one-line description of the drawing. `℄` stands for the printed centerline symbol.
- Verbatim slips kept as printed: 5.7.1.2 cites Table 4-2 for TLOFA (the values are in Table 4-1); 4.5.2 item 4.b has "39.5 (12.0 m)" without "ft"; 4.6.2 item 2 has "50-foot (15.2)" without "m"; J.4.3 converts 78.04 ft as 22.6 m.
- Table 4-1 gives 70 ft for the ADG I taxiway centerline to parallel taxiway centerline, while the J.4.2 method with Table J-9 gives 44.5 + 24.5 = 69 ft. Every other Table 4-1 separation matches the J.4 method.
