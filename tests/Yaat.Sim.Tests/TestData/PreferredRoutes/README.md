# FAA preferred routes fixture

A real extract of the FAA NASR preferred-routes file, used by `PreferredRouteParserTests` and `PreferredRouteSourceTests`.

- `PFR_BASE-extract.csv`: the header and fourteen data rows of `PFR_BASE.csv` from `https://nfdc.faa.gov/webContent/28DaySub/extra/26_Nov_2026_PFR_CSV.zip` (the 28-day NASR subscription effective 2026-11-26, AIRAC 2612; its rows carry `EFF_DATE` 2026/10/29, the 56-day chart cycle), downloaded 2026-11-21 (zip SHA-256 `cf7b2c449fadd396902e74d01d7f0ae47e0c181ba6b8881c2f2fa2bbe0c1e7f6`).
- Rows kept, unchanged apart from CRLF line ends turned into LF:
  - ABE to ACY (`TEC`, altitude "5000", no aircraft text).
  - ACY to IAD (`TEC`, route 1 "NON-JETS ONLY", route 2 "JETS ONLY").
  - AIR to MRB (`HPD`).
  - ALLEX to ADW (`NAR`, fix endpoints).
  - BUR to LGB (`TEC`, routes 7 to 10, coded "PQ50", "JM90", "M50" and "J50"; routes 7, 9 and 10 share one route string).
  - SFO to DEN (`H`, routes 1 and 2).
  - SFO to SMF (`TEC`, routes 1 to 3, coded "J30" and "J50").
- No row runs the reverse of another.
- Cut with: `sed -n '1,2p;136,137p;176p;285p;1439,1442p;10472,10473p;10496,10498p' PFR_BASE.csv | tr -d '\r'` (line numbers in the 2026-11-26 file).
