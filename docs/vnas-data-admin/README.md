# vNAS Data Admin reference docs (cached)

Local mirror of the **vNAS Data Admin** documentation — the Facility Engineer (FE)
configuration reference for ARTCC data consumed by CRC, vStrips, vTDLS, and ATCTrainer.
Useful when YAAT needs to understand how upstream config (facilities, aliases, restrictions,
video maps, auto-ATC, transceivers, controller feed, training) is structured, since YAAT
loads the same vNAS data-api ARTCC config.

## Source

- **Upstream**: <https://docs.virtualnas.net/data-admin/overview/>
- **Fetched**: 2026-06-12
- **Platform**: Material for MkDocs (see [`../crc/README.md`](../crc/README.md) for why this
  is generated, not verbatim).

## Refresh

```bash
uv run tools/refresh-crc-docs.py --section data-admin
```

Same converter as the CRC manual. When upstream changes, re-run and bump **Fetched** above.

## Pages

`overview`, `aliases`, `auto-atc`, `common-urls`, `controller-feed`, `facilities`,
`foreign-facilities`, `restrictions`, `training`, `transceivers`, `user-management`,
`video-maps`. Images in [`img/`](img/).

> ℹ️ This is the Facility-Engineer *configuration* reference. It is background context, not
> a YAAT feature spec — most of it is out of scope for controller-side emulation.
