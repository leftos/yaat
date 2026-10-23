# ATC speech corpora and readback statistics for YAAT (YAAT-1112)

Research date: 2026-10-23. Purpose: find public data for (a) calibrating solo-training pilot readback variation (shortening, dropped words, chatter, effect of frequency load) and (b) real VHF audio with transcripts for testing and tuning the Whisper STT path and the TTS to STT round trip.

Each source was checked against its own page. Anything not confirmed on a primary page is marked **unverified**.

## Summary table

| Source | Region, years | Size | Speakers | Pilot readbacks transcribed | Cost | Licence | Commit to public repo |
| --- | --- | --- | --- | --- | --- | --- | --- |
| LDC ATC0 (LDC94S14A) | DFW, BOS, DCA; collected early 1990s, published 1994 | ~70 h | pilots and controllers | yes, full transcripts with times | $1,150 non-member, $575 reduced licence, $0 for 1994/1997 members | LDC User Agreement for Non-Members | no audio or transcripts; aggregate statistics only (check the agreement) |
| CABank English ATC | KLAX, KEWR towers | 20 recordings + 6 accident recordings (ASR-transcribed) | tower controllers and pilots | yes (CHAT, one line per push-to-talk) | free | TalkBank rules; citation required; licence text **unverified** (TalkBank usually CC BY-NC-SA) | statistics with citation; raw data probably not |
| ATCO2 test set 1 h | Europe (Prague, Sion, Bern, Zurich...) | 1 h | both, role-tagged | yes | free | ATCO2 end-user data agreement, research use | no redistribution; statistics only |
| ATCO2 ELRA-S0484 (4 h transcribed + ~4,000 h untranscribed / 5,281 h after VAD) | Europe | as stated | both | 4 h gold; large set pseudo-transcribed | EUR 1,000 academic non-commercial; EUR 5,000 commercial / VAR | ELRA END USER or ELRA VAR | no |
| UWB-ATCC (Jzuluaga/uwb_atcc) | Czech airspace (Praha) | ~20 h | both, segments tagged `_PI`/`_AT`/`PIAT` | yes | free | CC BY-NC-SA 4.0 | yes, with attribution, NC and share-alike (fits a free OSS project, but the NC clause binds every downstream user) |
| NTSB docket ATC transcripts | US, current | per accident, partial | both | yes, verbatim | free | US government work | text yes (public domain); audio is not in the docket |
| FAA FOIA ATC audio | US | on request | both | no (audio only, unless a transcript exists) | FOIA search/review/duplication fees | public record | yes once released, in practice |
| LiveATC.net | US and world | live + 7-day archive (older for a fee) | both | no | free to listen | non-commercial personal use; no robots; no redistribution | no |
| Cardosi / Prinzo FAA reports | US ARTCC/TRACON, 1990s-2004 | 47-50 h per study | both | coded statistics, not corpora | free | US government reports | yes, cite the figures |

## 1. LDC Air Traffic Control Complete (ATC0, LDC94S14A)

Primary page: https://catalog.ldc.upenn.edu/LDC94S14A

- Author John J. Godfrey; collected by Texas Instruments under DARPA contract, produced by NIST, distributed by LDC (1994).
- Three subcorpora: Dallas Fort Worth, Boston Logan, Washington National; "approximately 70 hours of controller and pilot transmissions" recorded from receivers near each airport.
- Audio: 8 kHz 16-bit linear PCM, NIST SPHERE, continuous monitoring of one FAA frequency for 1 to 2 hours per file, no squelch removal, plus AM carrier amplitude every 10 ms.
- "Full transcripts, including the start and end times of each transmission, are provided for each audio file. Each flight is identified by its flight number." So pilot readbacks are transcribed and linkable to the controller instruction.
- Price (from the catalogue page's pricing data, fetched 2026-10-23): Non-Member $1,150.00; Reduced-License $575.00; 1994/1997 Members $0.00. Licence: "LDC User Agreement for Non-Members".
- Fit: the best US source for purpose (a), with real pilot readbacks per flight and continuous monitoring (so load per hour can be measured). Old (1990s phraseology, before many 7110.65 changes). The LDC agreement does not allow redistributing the data; publishing derived aggregate counts is normal research practice, but read the agreement before committing even per-utterance excerpts.

## 2. CABank English ATC Corpus (TalkBank)

Primary page: https://ca.talkbank.org/access/ATC.html (DOI 10.21415/2SPQ-VF14)

- Contributor Mark McAndrews, Western Kentucky University. Location USA; KLAX and KEWR tower frequencies; 20 recordings sampled at random dates and times "to capture a representative sample of average air traffic control tower interactions". Recording date and UTC start time are in each file name.
- Format: CHAT transcripts (CLAN), one main line per push-to-talk, with linked audio. An extra folder has 6 accident recordings transcribed by ASR (not manual; do not use for calibration).
- Total hours not stated on the page (**unverified**; 20 recordings).
- Terms: "In accordance with TalkBank rules, any use of data from this corpus must be accompanied by at least one of the above references." The TalkBank licence text (https://talkbank.org/share/rules.html) could not be fetched; TalkBank data is generally CC BY-NC-SA, which is compatible with a free OSS project but should be confirmed before committing anything.
- Fit: free, modern, US tower, pilot readbacks transcribed. Best free source for purpose (a) at the tower level and a good small US test set for (b).

## 3. ATCO2 and UWB-ATCC

Primary pages: https://www.atco2.org/data, https://catalogue.elra.info/en-us/repository/browse/ELRA-S0484/, https://huggingface.co/datasets/Jzuluaga/uwb_atcc

- ATCO2-test-set-1h: subset of the 4 h gold test set, free download (https://www.replaywell.com/atco2/download/ATCO2-ASRdataset-v1_beta.tgz) plus XML with verified callsign/command/value tags. "License: available for research purposes"; terms in "ATCO2-ASRdataset-v1_beta - End-User Data Agreement" inside the archive. Also mirrored as `Jzuluaga/atco2_corpus_1h` on Hugging Face.
- ELRA-S0484: two packages, ~4 h manually transcribed (about 1 h human re-checked) and ~4,000 h untranscribed (5,281 h after VAD, 4,465 h English only; 6,225 h raw). Collected at European airports (Sion, Bern, Zurich, Prague...), native and non-native English, with ADS-B metadata. ELRA price table (member and non-member identical): Non Commercial Use, ELRA END USER: EUR 1,000 academic / EUR 5,000 commercial; Commercial Use, ELRA VAR: EUR 5,000.
- UWB-ATCC: University of West Bohemia, ~20 h, 8 kHz 16-bit mono, manually transcribed, speaker role per segment (`_PI` pilot, `_AT` controller, `PIAT` both). Licence CC BY-NC-SA 4.0.
- Fit: all European ICAO phraseology ("flight level", "descending", "bye bye"), so not usable to calibrate FAA pilot behaviour. Useful for (b) as a noisy-VHF STT stress test, especially pilot speech and non-native accents. UWB-ATCC is the only one here whose licence plainly allows committing data to a public repo (with NC and share-alike).

## 4. NTSB docket transcripts and FAA FOIA

Primary pages: https://data.ntsb.gov/carol-main-public/, https://www.ntsb.gov/Documents/CAROL-Guide.pdf, https://www.faa.gov/air_traffic/publications/atpubs/foa_html/chap4_section_8.html

- NTSB "ATC Group Chair's Factual Report" attachments carry verbatim transcripts "transcribed from certified copies of audio recordings provided by the FAA", one line per transmission with time, ident and text (example: DCA23FA149, Austin runway incursion, https://data.ntsb.gov/Docket/Document/docBLOB?FileExtension=pdf&FileName=DCA23FA149+-+Attachment+1+-+ATC+Audio+Transcript-Rel.pdf&ID=16354943).
- Finding them in bulk: CAROL search results export to CSV/JSON with a Docket link per investigation (legacy CAROL retired 2026-10-01; now CAROL.NTSB). NTSB also points bulk users at its Enterprise API (`GetCasesByDateRangeV2`); the downloadable avall.zip moves to the API on 2027-04-05.

  There is no single bulk file of ATC transcripts: the route is list investigations (filter to major / DCA cases), then fetch each docket and pick documents titled "ATC Audio Transcript" or "ATC ... Attachment". Pub. L. 118-63 §1217 requires all public dockets to be online by May 2026.
- Coverage bias: accidents and incidents only, mostly partial transcripts around the event, often abnormal traffic. Still real US pilots and controllers in modern phraseology, with readbacks.
- Licence: NTSB records are US government works, so the text can be committed.
- FAA FOIA: ATC voice recordings are released by Service Center FOIA offices on request, with fees for search, review and duplication in quarter-hour units (FAA JO 7210.3 §4-8). No bulk reading room of ATC transcripts was found (**unverified** beyond the order text).

## 5. LiveATC.net

Primary page: https://www.liveatc.net/legal/

- "LiveATC.net is for non-commercial (personal) use only".
- "You will only access the LiveATC.net web site with an interactive web browser ... and not with any program, collection agent, or 'robot' for the purpose of automated retrieval of content, unless you are granted permission by LiveATC.net".
- Archives are kept 7 days publicly; older archives (kept 4 to 5 years) cost a fee (https://www.liveatc.net/archive.php).
- The terms do not mention transcription or ML training explicitly; redistribution of the audio is not permitted, and Hugging Face uploaders treat it that way (e.g. `k3rb3l/atc-asr-eval` is marked "Private, not for redistribution"). `nyuuzyou/liveatc` (21,172 scraped recordings, 2024-08-26) is a scrape whose legality against these terms is doubtful; do not use it.
- Fit: fine for a developer listening and hand-checking locally; not for automated download, committed audio, or a committed transcript set. Written permission from LiveATC would be needed for anything automated.

## 6. FAA / Volpe and CAMI readback studies (calibration numbers)

These give calibration numbers without a corpus, and can be cited freely.

### Cardosi 1993, en route (DOT/FAA/RD-93/11, DOT-VNTSC-FAA-93-2)

https://rosap.ntl.bts.gov/view/dot/8659

- 47 h of tape from 8 ARTCCs; 5,032 controller transmissions; 3,576 clearances.
- Complexity: 48% of clearances had 1 element, 30% two, 11% three, 6% four, 5% five or more.
- Pilot response: 71% full readback, 12% partial readback, 6% acknowledgement only ("roger"), 8% indirect (question or request); 2% requests for repeat.
- 27 communication errors (< 1% of clearances). Readback error rate 0.4% at one element, doubling from 0.7% to 1.4% between three and four elements; 5+ element clearances are 4% of clearances but 26% of errors. Controllers missed 3 of the readback errors (11%).

### Cardosi, Brett and Han 1996, TRACON (Volpe, DOT/FAA/AR-96/66 per ROSAP record **unverified**)

https://rosap.ntl.bts.gov/view/dot/8711

- 48 h from 8 TRACONs; 13,089 controller transmissions; 9,409 clearances; 59% had one or two pieces of information.
- About 1% of readbacks had an error; 40% of those were not caught. 1% of messages drew a request for repeat.
- Call sign in the readback: full call sign 56%, partial 31% (airline name alone or number alone), none 13%.
- Full readback given to 60% of controller messages; full readback with complete call sign only 37%.
- Readback error rate by complexity: 0.7% (1), 0.5% (2), 0.9% (3), 1.5% (4), 3.6% (5), 0.7% (6), 1.7% (7+).
- Errors: headings 36%, frequency changes 26%, altitude 17%; per readback, frequencies most error-prone and altitudes least.

### Prinzo 1996, approach control (DOT/FAA/AM-96/26)

https://www.faa.gov/sites/faa.gov/files/data_research/research/med_humanfacs/oamtechreports/AM96-26.pdf

- 3 TRACONs; 4,500 transmissions; 12,200 communication elements. 59% of 5,900 pilot elements had at least one deviation from standard phraseology (40% for controllers).
- Pilots "only partially read back instructions involving heading, radio frequency, and airspeed" and grouped numbers; communications got "more conversational and verbose" with advisories and requests. Omitting and grouping numbers is framed as a strategy to minimise time on frequency.

### Prinzo, Hendrix and Hendrix 2006 (DOT/FAA/AM-06/25)

https://www.faa.gov/sites/faa.gov/files/data_research/research/med_humanfacs/oamtechreports/200625.pdf

- 50 h from 5 of the busiest US TRACONs, October 2003 to February 2004. One aircraft every 1 min 26 s (approach) and 1 min 6 s (departure); ~13 messages and ~1 min 16 s of airtime per aircraft.
- Full readbacks rose from 60% (1996) to more than 82%; full readback with complete call sign from 37% to 61%. 18.8% of full readbacks had a partial call sign, 13.4% dropping the prefix but keeping all numbers.
- Observed shortenings: rounding call sign numbers, truncating values ("twenty five knots", "one four" for heading 140, "down to five hundred"), "point" in altitudes and speeds ("three point five", "two point seven on the speed").
- Readback errors and pilot requests rise with message complexity and number of aviation topics, especially on approach.

### Related

- Prinzo and McClellan 2005 (DOT/FAA/AM-05/19): ~8,000 transmissions from the 5 busiest TRACONs; mean transmission 2,849 ms, mean silence between transmissions 1,736 ms; 1.2% blocked, stepped-on or clipped. Gives a congestion baseline for "busy".
- Cardosi, Falzarano and Han 1998 (DOT/FAA/AR-98/17): 386 ASRS reports; top factors similar call signs, expectation, and high controller workload.

### What the reports do not give

None of them tabulates readback form directly against frequency congestion. Congestion appears only indirectly: message complexity and length (Cardosi, Prinzo 2006), approach vs departure workload (Prinzo 2006), and the time-on-frequency explanation for omissions (Prinzo 1996). A congestion-conditioned rate has to come from a corpus with continuous monitoring (ATC0, CABank), by measuring transmissions per minute around each readback.

## 7. Other corpora

- No other real, transcribed, publicly licensed US-airspace ATC corpus was found beyond ATC0, CABank ATC and NTSB transcripts.
- Synthetic: `twangodev/radiotalk-us-transcripts-qwen3-100k` (100k LLM-generated US transcripts, Qwen3-32B) and its v2 `-25k`. Samples show errors (a KNFG scenario spoken as `KSFO_TWR`, nonsense readbacks). Not usable for calibration or as STT ground truth.
- Models, not data: `jeffreysuu/whisper-atc-finetuned` (Apache 2.0) was tuned on 55 LiveATC clips (6 minutes) from IAH, JFK and SFO; its training audio carries LiveATC's restrictions.

## Recommended next steps for YAAT

1. **Calibration table from the FAA reports** (licence: US government, free to cite and commit). Put the Cardosi 1993/1996 and Prinzo 2006 figures (full / partial / roger rates, call sign forms, error rates by complexity) into a short doc beside the pilot phraseology catalogue, and map them to the terse / normal / chatty traits and quiet / normal / busy loads. No data purchase needed.
2. **One-off CABank statistics study** (licence: TalkBank rules, citation required; confirm NC terms first). Script over the CHAT files that pairs each controller instruction with the next pilot transmission and counts full vs partial readback, call sign position and form, dropped "runway"/"knots"/verbs, and courtesy words, bucketed by transmissions per minute. Commit only the script and aggregate counts, not transcripts.
3. **NTSB transcript harvester** (licence: public domain). Small tool that lists major investigations via the CAROL export or the NTSB API, fetches "ATC Audio Transcript" attachments, and extracts controller/pilot pairs.

   This gives a modern US text set that can be committed; tag it as biased toward abnormal events.
4. **SpeechSandbox STT harness over UWB-ATCC** (licence: CC BY-NC-SA 4.0). A `--corpus-wer` mode that runs the Whisper pipeline over a fixed subset and reports WER for pilot vs controller segments. Fetch at run time from Hugging Face rather than vendoring audio.
5. **Same harness over the ATCO2 1 h test set** (licence: research end-user agreement; local only, never committed). Compare WER with and without callsign boosting from its XML tags.
6. **Decide on ATC0** (cost $1,150 non-member, or $575 reduced licence). Buy only if steps 2 and 3 leave the congestion question open; it is the only continuous multi-hour US corpus with flight-linked pilot transcripts. Results stay as aggregates.
7. **Do not use LiveATC or scraped LiveATC sets** for anything automated or committed unless LiveATC grants written permission; ask them if a US audio test set becomes necessary.

## References

- LDC ATC0: https://catalog.ldc.upenn.edu/LDC94S14A
- CABank ATC: https://ca.talkbank.org/access/ATC.html
- ATCO2 data: https://www.atco2.org/data
- ELRA-S0484: https://catalogue.elra.info/en-us/repository/browse/ELRA-S0484/
- UWB-ATCC: https://huggingface.co/datasets/Jzuluaga/uwb_atcc
- ATCO2 1 h on HF: https://huggingface.co/datasets/Jzuluaga/atco2_corpus_1h
- LiveATC terms: https://www.liveatc.net/legal/
- NTSB CAROL guide: https://www.ntsb.gov/Documents/CAROL-Guide.pdf
- NTSB datasets / API notice: https://data.ntsb.gov/avdata
- FAA JO 7210.3 §4-8: https://www.faa.gov/air_traffic/publications/atpubs/foa_html/chap4_section_8.html
- Cardosi 1993: https://rosap.ntl.bts.gov/view/dot/8659
- Cardosi et al. 1996: https://rosap.ntl.bts.gov/view/dot/8711
- Prinzo 1996: https://www.faa.gov/sites/faa.gov/files/data_research/research/med_humanfacs/oamtechreports/AM96-26.pdf
- Prinzo et al. 2006: https://www.faa.gov/sites/faa.gov/files/data_research/research/med_humanfacs/oamtechreports/200625.pdf
- Prinzo and McClellan 2005: https://www.faa.gov/sites/faa.gov/files/data_research/research/med_humanfacs/oamtechreports/0519.pdf
- Cardosi et al. 1998: https://rosap.ntl.bts.gov/view/dot/8490
