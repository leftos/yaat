# csko flight-plans fixtures

Real replies from the csko flight-plans service, used with its operator's permission, trimmed to the fields the route source reads (registration, ICAO address, CID, GUFIs, beacon code and remarks removed).

- `search-KSFO-KLAX.json`: the first 30 flights of `GET https://flight-plans.csko.hu/v1/flight_plan/search?departure=KSFO&arrival=KLAX&limit=50`, captured 2026-11-21. `assigned_altitude` is in feet; `route_text` is null on some flights, whose route is in `last_route_text` (dotted form, `KSFO.SSTIK5.SUSEY..EBAYE..BURGL.IRNMN2.KLAX/0820`).
