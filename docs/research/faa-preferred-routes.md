# FAA preferred IFR routes data

Research note, 2026-11-21. Primary FAA sources only; every file below was downloaded and inspected on that date.

## Sources and URLs

Two FAA publications carry the same NASR preferred-route data:

1. **ATCSCC Route Management Tool (RMT) export**, still published.
   - Query page: https://www.fly.faa.gov/rmt/nfdc_preferred_routes_database.jsp (shows "RMT WebService: 1.50", "Effective 10/29/2026 until 12/24/2026")
   - Direct CSV: https://www.fly.faa.gov/rmt/data_file/prefroutes_db.csv (HTTP 200, `text/csv`, 1,368,814 bytes, 13,178 lines, `Last-Modified: Thu, 29 Oct 2026 05:04:46 GMT`; UTF-8 with BOM). Fixed URL, overwritten each cycle.
2. **NFDC 28-Day NASR Subscription**, the authoritative source.
   - Index: https://www.faa.gov/air_traffic/flight_info/aeronav/aero_data/NASR_Subscription/ (lists cycles `2026-10-29`, `2026-11-26`)
   - Per-cycle page: `.../NASR_Subscription/<YYYY-MM-DD>` (effective date)
   - PFR CSV set: https://nfdc.faa.gov/webContent/28DaySub/extra/26_Nov_2026_PFR_CSV.zip (pattern `<DD>_<Mon>_<YYYY>_PFR_CSV.zip`)
   - Fixed-width text: https://nfdc.faa.gov/webContent/28DaySub/2026-11-26/PFR.zip, layout https://nfdc.faa.gov/webContent/28DaySub/2026-11-26/Layout_Data/pfr_rf.txt
   - Whole subscription: https://nfdc.faa.gov/webContent/28DaySub/28DaySubscription_Effective_2026-11-26.zip; README https://nfdc.faa.gov/webContent/28DaySub/2026-11-26/README.txt

## Formats

### RMT `prefroutes_db.csv`

Header (quoted from the file): `Orig,Route String,Dest,Hours1,Hours2,Hours3,Type,Area,Altitude,Aircraft,Direction,Seq,DCNTR,ACNTR`

Example rows:

```
ABE,ABE FJC ARD CYN ACY,ACY,,,,TEC,,5000,,,1,ZNY,ZDC
ABE,ABE FJC T299 HUO IGN PWL BRISS BDL,BDL,,,,TEC,,5000,,,1,ZNY,ZBW
```

Airport ids have no `K` prefix; the route string includes origin and destination; DCNTR/ACNTR are departure/arrival ARTCCs; Seq is the route number within an origin/destination/type.

### NASR PFR CSV zip

Contains `PFR_BASE.csv`, `PFR_SEG.csv`, `PFR_RMT_FMT.csv`, `PFR_CSV_DATA_STRUCTURE.csv` (column name, max length, type, nullable), `CSV_README.pdf`, `PFR DATA LAYOUT.pdf`.

- `PFR_BASE.csv`: `EFF_DATE,ORIGIN_ID,ORIGIN_CITY,ORIGIN_STATE_CODE,ORIGIN_COUNTRY_CODE,DSTN_ID,DSTN_CITY,DSTN_STATE_CODE,DSTN_COUNTRY_CODE,PFR_TYPE_CODE,ROUTE_NO,SPECIAL_AREA_DESCRIP,ALT_DESCRIP,AIRCRAFT,HOURS,ROUTE_DIR_DESCRIP,DESIGNATOR,NAR_TYPE,INLAND_FAC_FIX,COASTAL_FIX,DESTINATION,ROUTE_STRING`
  - `"2026/10/29","ABE","ALLENTOWN","PA","US","ACY","ATLANTIC CITY","NJ","US","TEC",1,"","5000","","","","","","","","","FJC ARD CYN"` (route string excludes endpoints)
- `PFR_SEG.csv`: `EFF_DATE,ORIGIN_ID,DSTN_ID,PFR_TYPE_CODE,ROUTE_NO,SEGMENT_SEQ,SEG_VALUE,SEG_TYPE,STATE_CODE,COUNTRY_CODE,ICAO_REGION_CODE,NAV_TYPE,NEXT_SEG`
  - `"2026/10/29","ABE","ACY","TEC",1,5,"FJC","NAVAID","PA","US","","VORTAC","ARD"`
- `PFR_RMT_FMT.csv`: the RMT layout with a single `Hours1` column: `Orig,Route String,Dest,Hours1,Type,Area,Altitude,Aircraft,Direction,Seq,DCNTR,ACNTR`

Recent format changes (README, effective the 03 September 2026 AIRAC): Designator, Special Area Description and Aircraft fields widened; route types `HSD`/`LSD` renamed `HPD` (High Altitude Preferred Direction) and `LPD` (Low Altitude Preferred Direction). Other types seen include `TEC`, `H`, `L`, `NAR`.

## Update cadence and cycle identification

- Preferred routes change only on the **56-day** chart cycle (every other 28-day AIRAC). The NASR README: ARB, ATS, AWY, CDR, MTR, **PFR**, PJA, STARDP and WXL files in the 28-day "Change Notice" set "will not contain new data ... the same files produced for the previous 56 day AIRAC". So the 2026-11-26 subscription still carries `EFF_DATE` 2026/10/29 data.
- A cycle is identified by its effective date: the NASR folder `YYYY-MM-DD`, the zip name `DD_Mon_YYYY`, and `EFF_DATE` in each row. The RMT page states the range ("Effective 10/29/2026 until 12/24/2026"); the RMT CSV itself has no date column, so use its `Last-Modified` header or the page.

## Licensing

No licence or terms statement appears on the RMT page, the CSV, or the NASR README. As a work of the US federal government it is not subject to US copyright (17 U.S.C. 105); this is an inference, not an FAA statement found in these sources. Not for navigation use; the data is advisory reference for simulation.

## Status

Both sources are live and current as of 2026-11-21: the RMT CSV was regenerated 2026-10-29, and NASR lists the 2026-11-26 cycle with a PFR file.
