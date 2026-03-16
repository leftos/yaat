# Plan: Native macOS Menu Bar Support

## Context
The app has a full in-window `<Menu>` in `MainWindow.axaml` (lines 21-114) with File, Room, View, Scenario, and Settings menus. On macOS, this renders inside the window instead of the native top menu bar. Avalonia's `NativeMenu` renders in the macOS system menu bar on macOS and is ignored on other platforms.

## Approach — AXAML-defined NativeMenu with shared bindings

Avalonia doesn't auto-promote `<Menu>` to `NativeMenu`, but `NativeMenu.Menu` can be defined in AXAML on the Window using the **same `{Binding}` expressions and Click handlers** as the in-window menu. On macOS it renders in the system bar; on Windows/Linux it's simply ignored.

1. Add `<NativeMenu.Menu>` block to `MainWindow.axaml` mirroring the existing `<Menu>` structure
2. Same Command bindings, Click handlers, IsEnabled bindings, ToggleType/IsChecked bindings
3. One code-behind line to hide the in-window `<Menu>` on macOS
4. Small code-behind for the 3 dynamic submenus (Recent Scenarios, Recent Weather, Copy View Settings) — give them `x:Name` and populate via `NativeMenu.Opening` event

## Files to Modify

### `src/Yaat.Client/Views/MainWindow.axaml`

**1. Add `x:Name="InWindowMenu"` to the `<Menu>`** (line 21)

**2. Add `NativeMenu.Menu` block** on the `<Window>` element, mirroring the in-window menu structure. Example structure:

```xml
<NativeMenu.Menu>
    <NativeMenu>
        <!-- File -->
        <NativeMenuItem Header="File">
            <NativeMenuItem.Menu>
                <NativeMenu>
                    <NativeMenuItem Header="Connect..." Click="OnConnectClick" />
                    <NativeMenuItem Header="Disconnect" Command="{Binding DisconnectCommand}" IsEnabled="{Binding IsConnected}" />
                    <NativeMenuItemSeparator />
                    <NativeMenuItem Header="Settings..." Click="OnSettingsClick" />
                    <NativeMenuItemSeparator />
                    <NativeMenuItem Header="Exit" Command="{Binding ExitCommand}" />
                </NativeMenu>
            </NativeMenuItem.Menu>
        </NativeMenuItem>
        <!-- Room, View, Scenario menus follow same pattern -->
    </NativeMenu>
</NativeMenu.Menu>
```

Key differences from in-window menu:
- `Settings` moves into File menu (macOS convention; NativeMenu requires submenus for top-level items)
- `Disconnect` uses `IsEnabled` instead of `IsVisible` (NativeMenuItem doesn't support visibility toggling)
- Click handlers reuse same code-behind methods (e.g., `OnConnectClick`) — NativeMenuItem.Click and MenuItem.Click both resolve to the same code-behind method names
- Checkbox items use `ToggleType="CheckBox"` and `IsChecked="{Binding ..., Mode=TwoWay}"` (same as in-window)
- Dynamic submenus get `x:Name` for code-behind population

### `src/Yaat.Client/Views/MainWindow.axaml.cs`

**1. Constructor** — after `ApplyKeybinds`, hide in-window menu on macOS:
```csharp
if (OperatingSystem.IsMacOS())
{
    var inWindowMenu = this.FindControl<Menu>("InWindowMenu");
    if (inWindowMenu is not null)
        inWindowMenu.IsVisible = false;
}
```

**2. Wire dynamic native submenus** — find the 3 named NativeMenu submenus and subscribe to their `Opening` events to populate dynamically (Recent Scenarios, Recent Weather, Copy View Settings). Same logic as existing `PopulateRecentScenarios` / `PopulateRecentWeather` / `OnCopyViewSettingsSubmenuOpened` but creating `NativeMenuItem` instead of `MenuItem`.

**3. Adapt Click handlers** — The existing `On*Click` methods have signature `(object? sender, RoutedEventArgs e)`. NativeMenuItem.Click uses `(object? sender, EventArgs e)`. Two options:
- Extract dialog logic into parameterless methods, wire both Menu and NativeMenu handlers to them
- Or define separate handlers for NativeMenu items (e.g., `OnNativeConnectClick(object? sender, EventArgs e)`) that delegate to the same logic

The extraction approach is cleaner — extract ~7 parameterless methods, make the existing On*Click handlers one-line wrappers.

## Items requiring special handling

| Item | In-window | NativeMenu | Notes |
|------|-----------|------------|-------|
| Disconnect | `IsVisible` binding | `IsEnabled` binding | NativeMenuItem has no IsVisible |
| Recent Scenarios | `SubmenuOpened` event | `NativeMenu.Opening` event | Code-behind to populate NativeMenuItems |
| Recent Weather | `SubmenuOpened` event | `NativeMenu.Opening` event | Same pattern |
| Copy View Settings | `SubmenuOpened` event | `NativeMenu.Opening` event | Same pattern |
| Edit Weather IsEnabled | Code-behind `PropertyChanged` | Code-behind `PropertyChanged` | Bind to HasActiveWeather |
| Settings | Top-level clickable MenuItem | Nested under File menu | macOS convention |

## Verification
- `dotnet build -p:TreatWarningsAsErrors=true`
- `dotnet format style && dotnet format analyzers && dotnet csharpier format .`
- `dotnet test` for client tests
- Manual test on macOS: menu appears in system menu bar, all items functional
- Manual test on Windows: in-window menu still works, NativeMenu ignored, no regressions
