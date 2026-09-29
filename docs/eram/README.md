# ERAM command reference

This is the reference for yaat-server's handling of ERAM commands, the ones CRC's ERAM display sends through `ProcessEramMessage`. It records, for every ERAM command, its accepted forms, the success text CRC shows, and each check a field must pass along with the error that check returns. A conformance test in yaat-server (`EramReferenceConformanceTests`) holds the server's feedback to these files, so change the reference first and the code second.

## Source

The reference is extracted from the FAA's ERAM En Route Display System Management requirements: *ERAM EDSM Appendices for R-Position and General EDSM Requirements, Volume 1 Book 2* (ERAM_EDSM_SRS_210.04_V1B2, release EAF100, March 2023), Appendix C:

- the command validation and error response table;
- the command format and routing table;
- the flight data fields table used by `AM`.

The document itself is not in the repo, and neither are the extraction scripts, since they run over its text. A controller on VATSIM shared it; the maintainer keeps it as `ERAM_EDSM_SRS_210.04_V1B2_SDR-088All_Commands.pdf`. To work from its text, run `pdftotext -layout <pdf> eram.txt` and split the result on form feeds (`\f`) into one file per page, reading it as latin-1. Page-file index = printed page number + 15. The validation table (C.8) runs from printed page 624 to 846, the field definitions (C.1) from 401 to 527, the command routing table (C.2) from 528 to 568, and the flight data fields table (C.9) from 847 to 864. Code and tests cite these files, never the source's page numbers.

## Files

- **`commands/<ID>.yaml`**: one file per command ID. Every ID in the source table has a file, including the ones yaat will never handle, so that "no file" never means "not looked at".
- **`rulings.md`**: the decisions YAAT's ERAM emulation follows where the SRS, CRC and `vatsim-server-rs` leave a choice, what `vatsim-server-rs` does, the C.7 dynamic parameters YAAT models, and what a full read of the SRS appendices found consistent.
- **`dm-design.md`**: how YAAT models `DM`'s coordination fix, time and altitude, with its sources and the guesses the SRS leaves open.
- **`error-responses.yaml`**: every error ID used by an in-scope command, with the literal text yaat-server sends for it.

## Command files

```yaml
id: QZ
variants:
  - title: Assigned Altitude        # the source's name for the variant
    descriptor: ASSIGNED ALT        # shown under ACCEPT on success (the message type descriptor)
    format: "QZ (60) 08 02"         # field order; parentheses mark an optional field
    multiple_flids: false           # whether several aircraft can be named in one entry: true when at least one
                                    # of the formats allows it, null when the command names no aircraft
    handler: QZ.AssignedAltitude    # yaat-server's key for the code that handles it
    fields:                          # one entry per check, in the source's order
      - field: 08 Assigned Altitude # field number and name, or a label (Position, Message)
        rule: |
          One of ddd, OTP, OTP/ddd, VFR, VFR/ddd, (d)ddB(d)dd.
        error: MsgALTFormat         # a key in error-responses.yaml
        na: <reason>                # present when the check cannot apply on VATSIM
```

`handler` values:

- **a key such as `QT.Track`**: yaat-server implements this variant. The server's descriptor table uses the same key.
- **`crc-local`**: CRC answers the command itself and it never reaches the server (AR, QD, MR, WR, SR, and the QB code-list forms).
- **`not implemented`**: nothing handles it.

Other variant keys:

- **`na`**: the whole variant cannot apply on VATSIM, and why (CPDLC uplinks, sign-in, ARTS, traffic flow management, frequencies, adaptation and maintenance). Those variants carry no `fields`.
- **`source: yaat`**: the variant is ours, not the source's (RD route-display toggle, the QB voice type). Its descriptor is coined, and `note` says what it does.

Only the in-scope variants carry `format` and filled `fields`: the commands yaat handles now or has planned. Out-of-scope variants keep just title, descriptor, `handler` and `na`.

### Amendable fields

`AM.yaml` also carries `amendable_fields`, the source's flight data fields table: the fields `AM` amends and `QF` and `FR` name by field reference (field 12). One row per field:

```yaml
amendable_fields:
  - { number: "06", abbr: FIX, name: Coordination Fix, format: "aa(a)(a)(a) | ...", deletable: false, notes: "...", na: "YAAT keeps no coordination fix" }
```

- **`number`**: the field reference number, or null when the source gives none.
- **`abbr`**: the field abbreviation.
- **`name`**, **`format`**, **`deletable`**: the source's name, value format, and whether a minus sign deletes the field.
- **`notes`** (optional): the source's remarks on the field, and yaat's where they start `yaat:`.
- **`na`** (optional): YAAT stores nothing for this field, and why. `AM`, `QF` and `FR` answer a reference to it with `INVALID FIELD REF`. The rows without `na` are the fields YAAT models.

### Format notation

As in the source:

| Symbol | Meaning |
|---|---|
| `L` | a letter |
| `d` | a digit |
| `a` | a letter or digit |
| `c` | any character |
| `( )` | the enclosed part is optional |

`Laa(a)(a)(a)(a)` is therefore a letter followed by one to five letters or digits: an aircraft ID. Two-digit field numbers (`02`, `08`, `60`) are the source's field numbers. The ones that recur:

| Field | Meaning |
|---|---|
| 02 | Flight identification (FLID) |
| 08 | Altitude |
| 60 | `/OK` override |
| 64 | Action type |
| 65 | Trackball coordinates |
| 68 | Location |

## Error texts

The source names each error by ID only, such as `MsgFlidFormat`, and defines no wording. `error-responses.yaml` gives each ID the text yaat-server sends. Its `source` flag says where the wording comes from:

- **`crc`**: CRC uses this exact text for the commands it handles itself (`MESSAGE TOO LONG`, `BCN CODE FORMAT`, `INVALID LENGTH`, `NO FLIGHT PLAN`, `SESSION NOT ACTIVE`). The pattern `<field> FORMAT` is also CRC's.
- **`spec`**: the source gives the literal (`NOT YOUR CONTROL`).
- **`coined`**: our wording in the same style: short, upper case, with the field in error first where there is one.

`{cofie}` in a text stands for the contents of the field in error, as in `AB12 FORMAT`.

IDs beginning with `Yaat` are errors the table does not list but yaat-server needs: checks the source leaves to other documents (ownership, a track already owned) or that come from how VATSIM works.

## Feedback convention

This matches CRC's own locally handled commands:

- **Success:** CRC's feedback area shows `ACCEPT`, then the descriptor, then the object acted on (usually the aircraft ID).
- **Failure:** one line, the error text.
- **Response Area:** only for readouts (`QF`, `FR`, `LA`, `LB`, `LC`, `LD`, `LE`, `RK`, `SM` readout). A command that only changes something never writes there.
