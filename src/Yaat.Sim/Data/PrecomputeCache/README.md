# Precompute cache

One file per airport, `<FAA>.json.br`: a Brotli-compressed UTF-8 JSON document holding the airport's precomputed layout payload and push targets, as `PrecomputeStore` reads and writes it.

`tools/Yaat.PrecomputeCache` writes these files; nothing here is edited by hand.
