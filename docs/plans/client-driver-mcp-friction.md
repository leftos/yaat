# yaat-client-driver MCP friction log

Friction met while driving YAAT or CRC through `tools/Yaat.ClientDriver.Mcp` ([docs/client-driver-mcp.md](../client-driver-mcp.md)). Append to this file after every session that uses the MCP; tick an item off (or delete it) when a change to the MCP fixes it. Each entry gives the tool and arguments, what happened against what was needed, and the workaround.

## Session 2026-09-26: first live CRC session (#463 FPE validation)

1. **`list_windows(pid)` misses owned top-level windows.** CRC's STARS display (`CRC : 2 : STARS : OAK-3O`), the Connect/General Settings dialogs and the FPE are owned windows with their own HWNDs. `list_windows` returned only `CRC : 1 : Cab : OAK`. Workaround: `find_elements(root=<main window>, controlType=Window)`, or UIA through PowerShell.
2. **No window move/resize tool.** Arranging a profile's windows needed a PowerShell `SetWindowPos` script (`.tmp/crc-layout.ps1`). Wanted: `set_window_bounds(elementId, x, y, w, h)`, using UIA TransformPattern or SetWindowPos with NOACTIVATE.
3. **WPF ComboBox items can't be picked.** After `click` on the combo, a `click` on an item fails with "offscreen" because the popup closes between calls. The items' UIA Name is the view-model type name (`Vatsim.Nas.Crc.Ui.ViewModels.FacilityViewModel`); the label is a child Text.

   Workaround: `focus` the combo + `send_keys {DOWN n}`, counting the index from a `find_elements(controlType=Text)` taken while it was open. Wanted: `select_item(comboId, text)`, via SelectionItemPattern/ExpandCollapse, matching descendant text.
4. **`find_elements` failed with the bare message "An error occurred invoking 'find_elements'."** This happened twice, right after a click that opened a new CRC window. A retry succeeded. The message should carry the exception (likely ElementNotAvailable while the tree was changing), or the tool should retry itself.
5. **`screenshot(windowElementId)` in real mode copies the window's screen rectangle,** so it showed the STARS window stacked on top while the element was the Cab window. Nothing in the result says the pixels belong to another window. Wanted: PrintWindow for non-YAAT windows too, or a warning when the target isn't topmost at that rect.
6. **`set_text` on YAAT's Load Scenario `FolderPathBox` (virtual mode)** reported success, but `get_value` read back the old path; the box reverts or ignores typed input. `set_text` should verify with a read-back and report a mismatch. Workaround: copied the scenario into the folder already shown.
7. **No way to start CRC.** `launch_yaat` only starts Yaat.Client (by design), so CRC was started with `Start-Process`. A `launch_crc` (or a generic allowlisted launcher) would keep the session inside the MCP.
8. **`click_point` on the STARS window's hamburger (15,13) showed no menu.** A screenshot right after showed a "Menu" tooltip but no popup; the Ctrl+F12 shortcut worked. Unclear whether the click toggled the menu open and shut. A way to read popup menus of non-focused CRC windows would help.
9. **`dump_tree` on a CRC ListItem prints the VM type name.** The readable label needs one more depth level. Consider showing the first descendant Text's name as a hint for items named like a type.
11. **A modal that an action opens goes unreported.** Save Profile (menu item and Ctrl+S) opens a "Save profile "OAK_GND"?" Confirm dialog. `click`/`send_keys` returned success and said nothing about it. Two confirms stacked up unseen until the user pointed them out, and the agent had meanwhile concluded "save did nothing". Wanted: after an input action, list any new top-level/owned windows (especially modals) of the target process in the result.
12. **`send_keys` real mode refused because the foreground belonged to 'rustling-tulip-app'** right after a `click_point` on the CRC Cab window. The modal from #11 had probably taken over, or another app came forward. The refusal itself is right; the message should name the target window's modal if one is up.

10. **Real-mode input races with the user's own keyboard.** The route box got `E THE` in front of the value because the user was typing while the agent drove CRC in real mode. CRC needs real mode (virtual mode reaches YAAT windows only), so nothing today protects a shared desktop. Wanted:
    - virtual (posted-message) input for WPF/CRC windows, where CRC accepts it;
    - otherwise, a visible "agent is typing" cue (e.g. a result line or a toast);
    - a read-back after every `set_text`, so a race is caught instead of submitted.

## Session 2026-09-27: ERAM coast/freeze check (CRC 2.18.2, ZOA_ERAM profile created through CRC's own dialogs)

Recurrences: #7 (no CRC launcher; `Start-Process` on `%LOCALAPPDATA%\CRC\Application\CRC.exe`), #1 (also for YAAT: `list_windows` missed its Connect to Server and Load Scenario windows, so File > Connect looked like a no-op until `tail_yaat_log` showed the window), #3 (CRC combo popups closed between calls; `{F4}` + `dump_tree` + `{DOWN n}`, off by one when nothing starts selected).

13. **`screenshot` of YAAT's main window in virtual mode leaves out its open dialogs.** The Connect dialog was open but absent from the capture. Wanted: composite owned windows in, or list them in the result.
14. **A YAAT combo item scrolled out of its popup can't be clicked.** `click(e105)` (ZOA in Create Room's ARTCC combo, rect y=1245) failed in virtual mode with "no shown window of its process is under its centre"; a real-mode `click_point` there did not select it either. Workaround: real mode `{DOWN 18}{ENTER}` with the index from item rects. Wanted: `select_item(combo, text)` for Avalonia combos too.
15. **`click` reports a rectangle other than the one it clicked.** `click(e101)` said "clicked at (2743,882)" while printing the element rect as `(2828,870 …) enabled=False`, apparently read after the click. Report the rect actually clicked.
16. **`send_keys` to CRC's ERAM scope succeeds without reaching the MCA.** `send_keys("QT UAL790{ENTER}", focusElementId=e58)` returned success; nothing reached the MCA or the server, because window focus does not reach the scope's embedded WinForms input.

    The first `click_point` on the MCA picked it up (moved it) rather than focusing it; the next click dropped it and only then did typing work. Wanted: a way to focus the scope's input control, and a warning when keys land somewhere other than the intended target.
17. **No wait primitive.** A timed check (screenshot, wait ~30 s, screenshot) needed background timers because the harness blocks foreground `sleep`. Wanted: `wait(seconds)` or wait-until-changed in the MCP (see also the plan's `wait_until` item).
18. **An open popup can't be screenshotted.** Real-mode screenshots bring the window forward, which closes an open combo popup; only UIA can read it.

## Session 2026-10-01: FOLLOW montage clip A1 replay (YAAT client from a worktree build, server from the paired worktree)

Recurrences: #1 (`list_windows` misses Connect to Server, Load Recording and Save Recording; found only via `find_elements controlType=Window` under the main window), #15 (`click` on a menu item or button echoes the post-click state, `enabled=False rect=offscreen`, reading like a failure), #17 (no wait primitive: hitting a given sim second depends on tool-call latency).

19. **`screenshot` always saves under the MCP server's own checkout** (`X:/dev/yaat/.tmp/client-driver/shots`, the main checkout) with no parameter to choose the folder, so a worktree session writes into the main checkout. Wanted: an `outDir` parameter, or save under the launched client's `appDataDir`.
20. **`launch_yaat` defaults resolve against the main checkout.** `appDataDir` and `exePath` default there; a worktree session must pass both as absolute paths. Wanted: defaults relative to the caller's working tree, or a required parameter.
21. **The native file dialog runs in `PickerHost.exe`.** Virtual `set_text` reported "typed …" but left the value empty with no read-back; real-mode `send_keys` refused ("the foreground window belongs to 'PickerHost'"). Only real-mode `set_text` through ValuePattern followed by `invoke` on Open/Save worked, so the docs' `send_keys("<path>{ENTER}")` recipe fails. Wanted: a `pick_file(path)` tool, and a read-back after `set_text`.
22. **`invoke` on a dialog's Open/Save button reports "element disappeared mid-call"** although the action succeeded (the dialog closed because of it). Treat a target that vanishes after the invoke as success.
23. **A menu item found with `find_elements` goes stale before `click`** ("no rectangle on screen"); the menu had to be reopened and searched again.
24. **`stop_process` kills the client's process tree** instead of closing it gracefully, so preferences may not be saved. Wanted: a close-window-first stop with a kill fallback.
25. **No radar zoom or pan tool.** The radar stayed at range 40 with tiny targets; the montage's capture step needs framing. Wanted: `set_radar_view(center, range)` or a documented scroll/drag recipe.
26. **Docs drift:** the sim-rate dropdown's items open below the window edge, yet virtual clicks on them worked, contrary to `docs/client-driver-mcp.md`.
