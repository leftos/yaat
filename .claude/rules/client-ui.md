---
paths:
  - "src/Yaat.Client*/**"
  - "tools/Yaat.*.Web/**"
  - "**/*.axaml"
---

# Client UI

- **No newlines in text strings**: Never split `Text="..."`, `Content="..."`, or interpolated strings across lines in `.axaml`/`.cs` — indentation whitespace shows at runtime.
- **Window geometry**: Every window uses `WindowGeometryHelper(window, preferences, "Name", defaultW, defaultH).Restore()`.
