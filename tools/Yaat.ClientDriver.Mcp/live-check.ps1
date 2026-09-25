<#
.SYNOPSIS
Drives the yaat-client-driver MCP server against a real YAAT client and, when one is running, a real CRC.

.DESCRIPTION
Launches the client into a scratch YAAT_APPDATA_DIR, reads its window tree, screenshots it and reads its
log, then stops it — roughly 15 seconds with a real window on the desktop, taking the foreground once.
No mouse or keyboard input is sent unless -WithInput or -Background is given. Both open the File menu with a click
and close it with {ESC}, type into the command box with send_keys and set_text, and open the Help menu.
-WithInput switches the server to real input (set_input_mode real): every input result must end "(real)", and the
real cursor moves. -Background keeps the default virtual input while another window holds the foreground: every
input result must end "(virtual)", and after every step the foreground window and the real cursor position must
be what they were before the first one — virtual input never takes either.
Prints "LIVE OK" when every expectation holds.

.EXAMPLE
pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1
pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1 -WithInput
pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1 -Background
#>

[CmdletBinding()]
param(
    [string]$Command = 'pwsh',
    [string[]]$Arguments = @('-NoProfile', '-File', 'tools/Yaat.ClientDriver.Mcp/launch.ps1'),
    [string]$AppDataDir = '.tmp/client-driver/live-appdata',
    [switch]$WithInput,
    [switch]$Background
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'McpStdio.ps1')

if ($WithInput -and $Background) { throw 'Pass -WithInput (the client in the foreground) or -Background (another window in it), not both.' }

if (-not ('ClientDriverCheck.User32' -as [type])) {
    Add-Type -Namespace ClientDriverCheck -Name User32 -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId); [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr window); [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command); [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window); [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int size); [DllImport("user32.dll")] public static extern bool GetCursorPos(out System.Drawing.Point point);' -ReferencedAssemblies System.Drawing.Primitives
}

function Get-VisibleWindowCount {
    param([int]$ProcessId)

    # Avalonia files a menu popup under its main window in UI Automation, so list_windows never shows it; the popup is still its
    # own visible top-level HWND, which is what this counts.
    $count = 0
    $window = [ClientDriverCheck.User32]::GetTopWindow([IntPtr]::Zero)
    while ($window -ne [IntPtr]::Zero) {
        [uint32]$ownerPid = 0
        $null = [ClientDriverCheck.User32]::GetWindowThreadProcessId($window, [ref]$ownerPid)
        if (($ownerPid -eq $ProcessId) -and [ClientDriverCheck.User32]::IsWindowVisible($window)) { $count++ }
        $window = [ClientDriverCheck.User32]::GetWindow($window, 2)
    }
    $count
}

function Test-VisibleWindowTitled {
    param([int]$ProcessId, [string]$Title)

    $window = [ClientDriverCheck.User32]::GetTopWindow([IntPtr]::Zero)
    while ($window -ne [IntPtr]::Zero) {
        [uint32]$ownerPid = 0
        $null = [ClientDriverCheck.User32]::GetWindowThreadProcessId($window, [ref]$ownerPid)
        if (($ownerPid -eq $ProcessId) -and [ClientDriverCheck.User32]::IsWindowVisible($window)) {
            $text = [System.Text.StringBuilder]::new(256)
            $null = [ClientDriverCheck.User32]::GetWindowText($window, $text, $text.Capacity)
            if ($text.ToString() -eq $Title) { return $true }
        }
        $window = [ClientDriverCheck.User32]::GetWindow($window, 2)
    }
    $false
}

function Get-CursorPosition {
    $point = [System.Drawing.Point]::Empty
    $null = [ClientDriverCheck.User32]::GetCursorPos([ref]$point)
    "$($point.X),$($point.Y)"
}

function Assert-Untouched {
    param($Baseline, [string]$Step)

    $foreground = Get-ForegroundWindowInfo
    $cursor = Get-CursorPosition
    Assert-That ($foreground.Handle -eq $Baseline.Foreground.Handle) "$Step moved the foreground from $($Baseline.Foreground.Name) to $($foreground.Name) — virtual input must never take it"
    Assert-That ($cursor -eq $Baseline.Cursor) "$Step moved the real cursor from ($($Baseline.Cursor)) to ($cursor) — virtual input must never move it"
}

function Get-ForegroundWindowInfo {
    $handle = [ClientDriverCheck.User32]::GetForegroundWindow()
    [uint32]$ownerPid = 0
    $null = [ClientDriverCheck.User32]::GetWindowThreadProcessId($handle, [ref]$ownerPid)
    $owner = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Handle = [int64]$handle
        Pid    = [int]$ownerPid
        Name   = if ($null -ne $owner) { $owner.ProcessName } else { '(none)' }
    }
}

function Assert-InputPath {
    param([string]$Text, [bool]$ExpectPosted, [string]$Step)

    $expected = if ($ExpectPosted) { '(virtual)' } else { '(real)' }
    Assert-That ($Text.TrimEnd().EndsWith($expected)) "$Step was expected to end with $expected but did not: $Text"
    if ($ExpectPosted) { Assert-Untouched -Baseline $script:untouched -Step $Step }
}

function Assert-That {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function ConvertFrom-DescribeLines {
    param([string]$Text)

    $pattern = '^(?<id>e\d+) \| (?<type>\S+) \| (?<name>.*?) \| id=(?<automationId>.*?) \| enabled=(?<enabled>\w+) \| rect=(?<rect>.*)$'
    foreach ($line in ($Text -split "`r?`n")) {
        $match = [regex]::Match($line.Trim(), $pattern)
        if ($match.Success) {
            [pscustomobject]@{
                Id           = $match.Groups['id'].Value
                Type         = $match.Groups['type'].Value
                Name         = $match.Groups['name'].Value
                AutomationId = $match.Groups['automationId'].Value
                Rect         = $match.Groups['rect'].Value
            }
        }
    }
}

function Find-InProcessWindows {
    param($Session, [int]$ClientPid, [string]$AutomationId)

    # A menu popup is its own top-level window, so an opened menu item is found by scanning every window of the process.
    $windows = @(ConvertFrom-DescribeLines -Text (Get-McpResultText -Result (Invoke-CheckedTool -Session $Session -Name 'list_windows' -ToolArguments @{ pid = $ClientPid })))
    $found = @()
    foreach ($window in $windows) {
        $found += @(ConvertFrom-DescribeLines -Text (Get-McpResultText -Result (Invoke-CheckedTool -Session $Session -Name 'find_elements' -ToolArguments @{
                        rootElementId = $window.Id
                        automationId  = $AutomationId
                    })))
    }
    $found
}

function Invoke-CheckedTool {
    param($Session, [string]$Name, [hashtable]$ToolArguments = @{}, [int]$TimeoutSeconds = 90)

    $result = Invoke-McpTool -Session $Session -Name $Name -ToolArguments $ToolArguments -TimeoutSeconds $TimeoutSeconds
    if ($result.isError) { throw "$Name failed: $(Get-McpResultText -Result $result)" }
    $result
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$session = $null
$clientPid = 0
try {
    $session = Start-McpServer -Command $Command -Arguments $Arguments -WorkingDirectory $repoRoot
    $null = Initialize-McpSession -Session $session

    Write-Host '--- negative: launch_yaat refuses anything but the client'
    $refused = Invoke-McpTool -Session $session -Name 'launch_yaat' -ToolArguments @{ exePath = 'C:\Windows\System32\notepad.exe' }
    $refusedText = Get-McpResultText -Result $refused
    Assert-That ($refused.isError -eq $true) "launch_yaat accepted notepad.exe instead of returning an error: $refusedText"
    Assert-That ($refusedText -like '*only starts Yaat.Client.exe*') "launch_yaat refused notepad.exe with an unexpected message: $refusedText"
    Write-Host $refusedText

    Write-Host '--- negative: find_elements needs a criterion'
    $unfiltered = Invoke-McpTool -Session $session -Name 'find_elements' -ToolArguments @{ rootElementId = 'e1' }
    $unfilteredText = Get-McpResultText -Result $unfiltered
    Assert-That ($unfiltered.isError -eq $true) "find_elements accepted an empty criteria set: $unfilteredText"
    Assert-That ($unfilteredText -like '*at least one of name, automationId or controlType*') "find_elements rejected the empty criteria set with an unexpected message: $unfilteredText"
    Write-Host $unfilteredText

    $foregroundBefore = Get-ForegroundWindowInfo
    Write-Host "foreground before launch: 0x$('{0:X}' -f $foregroundBefore.Handle) ($($foregroundBefore.Name), pid $($foregroundBefore.Pid))"

    Write-Host '--- launch_yaat'
    $launchText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'launch_yaat' -ToolArguments @{ appDataDir = $AppDataDir })
    Write-Host $launchText
    $pidMatch = [regex]::Match($launchText, 'pid=(?<pid>\d+)')
    Assert-That $pidMatch.Success "launch_yaat did not report a pid: $launchText"
    $clientPid = [int]$pidMatch.Groups['pid'].Value

    if ($Background) {
        Start-Sleep -Seconds 2
        $foregroundAfterLaunch = Get-ForegroundWindowInfo
        if ($foregroundAfterLaunch.Handle -ne $foregroundBefore.Handle) {
            # Windows lets a process started from the foreground application take the foreground; hand it back, so the client runs behind.
            Write-Host "the launch moved the foreground to $($foregroundAfterLaunch.Name); handing it back to $($foregroundBefore.Name)"
            $null = [ClientDriverCheck.User32]::SetForegroundWindow([IntPtr]$foregroundBefore.Handle)
            Start-Sleep -Milliseconds 500
            $foregroundAfterLaunch = Get-ForegroundWindowInfo
        }
        Assert-That ($foregroundAfterLaunch.Handle -eq $foregroundBefore.Handle) "The foreground is on $($foregroundAfterLaunch.Name) after the launch and Windows refused to hand it back to $($foregroundBefore.Name) — -Background needs another window in front of the client"
    }

    Write-Host '--- list_windows'
    $windowsText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'list_windows' -ToolArguments @{ pid = $clientPid })
    Write-Host $windowsText
    $windows = @(ConvertFrom-DescribeLines -Text $windowsText)
    Assert-That ($windows.Count -ge 1) "list_windows returned no window for pid $clientPid"
    $mainWindow = $windows | Where-Object { $_.Name -like 'YAAT*' } | Select-Object -First 1
    Assert-That ($null -ne $mainWindow) "No window named YAAT* among: $($windows.Name -join ', ')"

    Write-Host '--- find_elements CommandInput'
    $commandInputText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'find_elements' -ToolArguments @{
            rootElementId = $mainWindow.Id
            automationId  = 'CommandInput'
            controlType   = 'Edit'
        })
    Write-Host $commandInputText
    $commandInputs = @(ConvertFrom-DescribeLines -Text $commandInputText)
    Assert-That ($commandInputs.Count -ge 1) "Expected at least one CommandInput, got: $commandInputText"

    Write-Host '--- find_elements MenuItem (top-level menus)'
    $menuText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'find_elements' -ToolArguments @{
            rootElementId = $mainWindow.Id
            controlType   = 'MenuItem'
        })
    $menuItems = @(ConvertFrom-DescribeLines -Text $menuText)
    Write-Host "menus: $($menuItems.Name -join ', ')"
    Assert-That ($null -ne ($menuItems | Where-Object { $_.Name -eq 'File' })) "No File menu among: $($menuItems.Name -join ', ')"

    # Avalonia builds a menu's popup only when it opens, so ConnectMenuItem and its siblings are absent from the tree
    # until something clicks File. The -WithInput pass is where that expectation can hold.
    $connectItems = @(ConvertFrom-DescribeLines -Text (Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'find_elements' -ToolArguments @{
                    rootElementId = $mainWindow.Id
                    automationId  = 'ConnectMenuItem'
                })))
    Assert-That ($connectItems.Count -eq 0) "ConnectMenuItem was expected to be absent while the File menu is closed, but find_elements returned $($connectItems.Count)"
    Write-Host 'ConnectMenuItem: absent while the File menu is closed (Avalonia builds popup content on open)'

    Write-Host '--- screenshot'
    $shot = Invoke-CheckedTool -Session $session -Name 'screenshot' -ToolArguments @{ windowElementId = $mainWindow.Id }
    $shotText = Get-McpResultText -Result $shot
    Write-Host $shotText
    Assert-That ($null -ne ($shot.content | Where-Object { $_.type -eq 'image' })) 'screenshot returned no image content block'
    $shotPath = ($shotText -split ' — ')[0].Trim()
    Assert-That (Test-Path $shotPath) "screenshot reported '$shotPath' but no file is there"
    $shotBytes = (Get-Item $shotPath).Length
    Assert-That ($shotBytes -gt 10KB) "screenshot wrote only $shotBytes bytes to $shotPath"
    Write-Host "screenshot file: $shotPath ($shotBytes bytes)"

    Write-Host '--- tail_yaat_log'
    # The window must be wider than the client's startup chatter (navdata, CIFP, aliases), or the 'Log file:' line scrolls out of it.
    $logText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'tail_yaat_log' -ToolArguments @{ appDataDir = $AppDataDir; lines = 200 })
    Assert-That ($logText -match 'Log file:') "tail_yaat_log has no 'Log file:' line: $logText"
    Write-Host ($logText -split "`r?`n" | Select-Object -First 4)

    if ($WithInput -or $Background) {
        $expectPosted = [bool]$Background
        $pathName = if ($expectPosted) { 'virtual' } else { 'real' }
        $modeText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'set_input_mode' -ToolArguments @{ mode = $pathName })
        Assert-That ($modeText -eq "input mode: $pathName") "set_input_mode $pathName answered: $modeText"
        Write-Host $modeText

        # Input sent while the client is still loading navdata is dropped, whichever the mode: wait for the status bar to say it is done.
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        do {
            Start-Sleep -Milliseconds 500
            $loaded = @(ConvertFrom-DescribeLines -Text (Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'find_elements' -ToolArguments @{
                            rootElementId = $mainWindow.Id
                            name          = 'Navigation data loaded'
                        })))
        } while (($loaded.Count -eq 0) -and ([DateTime]::UtcNow -lt $deadline))
        Assert-That ($loaded.Count -ge 1) "The client never showed 'Navigation data loaded' within 90 s"

        $script:untouched = [pscustomobject]@{ Foreground = Get-ForegroundWindowInfo; Cursor = Get-CursorPosition }
        if ($expectPosted) {
            Assert-That ($script:untouched.Foreground.Pid -ne $clientPid) 'The client holds the foreground — -Background needs another window in front of it'
            Write-Host "baseline: foreground $($script:untouched.Foreground.Name), cursor ($($script:untouched.Cursor))"
        }
        $windowCountBefore = Get-VisibleWindowCount -ProcessId $clientPid

        Write-Host "--- click the File menu ($pathName), then look for its popup window and ConnectMenuItem in it"
        $fileMenu = $menuItems | Where-Object { $_.Name -eq 'File' } | Select-Object -First 1
        $clickText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $fileMenu.Id })
        Write-Host $clickText
        Assert-InputPath -Text $clickText -ExpectPosted $expectPosted -Step 'click File'
        Start-Sleep -Milliseconds 700
        $windowCountOpen = Get-VisibleWindowCount -ProcessId $clientPid
        Assert-That ($windowCountOpen -gt $windowCountBefore) "The File menu opened no popup window: the client had $windowCountBefore visible top-level windows before the click and $windowCountOpen after"
        $openedConnect = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'ConnectMenuItem')
        Assert-That ($openedConnect.Count -ge 1) 'The File menu did not open: no ConnectMenuItem under any window of the client'
        Write-Host "File menu opened: $($openedConnect[0].Name) ($($openedConnect[0].Id)); visible windows $windowCountBefore -> $windowCountOpen"

        Write-Host "--- send_keys {ESC} ($pathName) closes it"
        $escText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'send_keys' -ToolArguments @{ keys = '{ESC}' })
        Write-Host $escText
        Assert-InputPath -Text $escText -ExpectPosted $expectPosted -Step 'send_keys {ESC}'
        Start-Sleep -Milliseconds 500
        $windowCountClosed = Get-VisibleWindowCount -ProcessId $clientPid
        Assert-That ($windowCountClosed -eq $windowCountBefore) "{ESC} did not close the File menu: $windowCountClosed visible windows, $windowCountBefore before it opened"
        $stillOpen = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'ConnectMenuItem')
        Assert-That ($stillOpen.Count -eq 0) '{ESC} did not close the File menu: ConnectMenuItem is still in the tree'

        Write-Host "--- send_keys 'hello' into CommandInput ($pathName)"
        $typeText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'send_keys' -ToolArguments @{
                keys           = 'hello'
                focusElementId = $commandInputs[0].Id
            })
        Write-Host $typeText
        Assert-InputPath -Text $typeText -ExpectPosted $expectPosted -Step 'send_keys hello'
        $typed = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'get_value' -ToolArguments @{ elementId = $commandInputs[0].Id })
        Assert-That ($typed.Trim() -ceq 'hello') "get_value after send_keys returned '$typed', expected 'hello'"
        Write-Host "get_value: $typed"

        Write-Host '--- set_text CommandInput'
        $setText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'set_text' -ToolArguments @{
                elementId = $commandInputs[0].Id
                text      = 'HELLO'
            })
        Write-Host $setText
        Assert-InputPath -Text $setText -ExpectPosted $expectPosted -Step 'set_text'
        $value = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'get_value' -ToolArguments @{ elementId = $commandInputs[0].Id })
        Assert-That ($value.Trim() -eq 'HELLO') "get_value after set_text returned '$value', expected 'HELLO'"
        Write-Host "get_value: $value"

        Write-Host "--- double click inside the command box's word ($pathName) selects it, so typing Z replaces it"
        $rectMatch = [regex]::Match($commandInputs[0].Rect, '^\((?<x>-?\d+),(?<y>-?\d+) (?<w>\d+)x(?<h>\d+)\)$')
        Assert-That $rectMatch.Success "CommandInput has no parsable rectangle: $($commandInputs[0].Rect)"
        $wordX = [int]$rectMatch.Groups['x'].Value + 14
        $wordY = [int]$rectMatch.Groups['y'].Value + [int]([int]$rectMatch.Groups['h'].Value / 2)
        $doubleText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click_point' -ToolArguments @{
                x           = $wordX
                y           = $wordY
                doubleClick = $true
            })
        Write-Host $doubleText
        Assert-InputPath -Text $doubleText -ExpectPosted $expectPosted -Step 'double click_point in CommandInput'
        $zText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'send_keys' -ToolArguments @{ keys = 'Z' })
        Assert-InputPath -Text $zText -ExpectPosted $expectPosted -Step 'send_keys Z'
        $replaced = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'get_value' -ToolArguments @{ elementId = $commandInputs[0].Id })
        Assert-That ($replaced.Trim() -ceq 'Z') "After a double click on HELLO and typing Z the command box holds '$replaced', expected 'Z' — the double click did not select the word"
        Write-Host "get_value: $replaced"

        Write-Host "--- open File and click Connect... inside its popup ($pathName): the modal Connect to Server dialog opens"
        Assert-That (-not (Test-VisibleWindowTitled -ProcessId $clientPid -Title 'Connect to Server')) 'A Connect to Server window is already open'
        $fileAgain = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $fileMenu.Id })
        Assert-InputPath -Text $fileAgain -ExpectPosted $expectPosted -Step 'click File again'
        Start-Sleep -Milliseconds 700
        $connectItems = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'ConnectMenuItem')
        Assert-That ($connectItems.Count -ge 1) 'The File menu did not reopen: no ConnectMenuItem'
        $connectClick = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $connectItems[0].Id })
        Write-Host $connectClick
        Assert-InputPath -Text $connectClick -ExpectPosted $expectPosted -Step 'click Connect... in the popup'
        Start-Sleep -Milliseconds 1500
        Assert-That (Test-VisibleWindowTitled -ProcessId $clientPid -Title 'Connect to Server') 'Clicking Connect... opened no visible "Connect to Server" window'
        $closeButtons = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'CloseButton')
        Assert-That ($closeButtons.Count -ge 1) 'The Connect to Server dialog has no CloseButton under any window of the client'
        Write-Host 'Connect to Server dialog opened'

        if ($expectPosted) {
            Write-Host '--- negative: a virtual click on the main window behind the modal dialog is refused'
            $blocked = Invoke-McpTool -Session $session -Name 'click' -ToolArguments @{ elementId = $fileMenu.Id }
            $blockedText = Get-McpResultText -Result $blocked
            Assert-That ($blocked.isError -eq $true) "A virtual click on the disabled main window was accepted: $blockedText"
            Assert-That ($blockedText -like '*a modal dialog owns this window*') "The click behind the modal dialog failed with an unexpected message: $blockedText"
            Assert-Untouched -Baseline $script:untouched -Step 'the refused click'
            Write-Host $blockedText
        }

        $closeText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $closeButtons[0].Id })
        Write-Host $closeText
        Assert-InputPath -Text $closeText -ExpectPosted $expectPosted -Step 'click Close on the dialog'
        Start-Sleep -Milliseconds 700
        Assert-That (-not (Test-VisibleWindowTitled -ProcessId $clientPid -Title 'Connect to Server')) 'Close did not close the Connect to Server dialog'
        Write-Host 'Connect to Server dialog closed'

        Write-Host "--- click the Help menu ($pathName)"
        $helpText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'find_elements' -ToolArguments @{
                rootElementId = $mainWindow.Id
                name          = 'Help'
                controlType   = 'MenuItem'
            })
        $helpItems = @(ConvertFrom-DescribeLines -Text $helpText)
        Assert-That ($helpItems.Count -ge 1) "No Help MenuItem on the main window: $helpText"
        $helpClick = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $helpItems[0].Id })
        Write-Host $helpClick
        Assert-InputPath -Text $helpClick -ExpectPosted $expectPosted -Step 'click Help'
        Start-Sleep -Milliseconds 700

        $found = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'HelpGettingStartedMenuItem')
        Assert-That ($found.Count -ge 1) 'The Help menu did not open: no HelpGettingStartedMenuItem under any window of the client'
        Write-Host "Help menu opened: $($found[0].Name) ($($found[0].Id))"
        $helpEsc = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'send_keys' -ToolArguments @{ keys = '{ESC}' })
        Write-Host $helpEsc
        Assert-InputPath -Text $helpEsc -ExpectPosted $expectPosted -Step 'send_keys {ESC} after Help'
    }

    if ($Background) {
        Assert-Untouched -Baseline $script:untouched -Step 'the run'
        $foregroundAfter = Get-ForegroundWindowInfo
        Assert-That ($foregroundAfter.Handle -eq $foregroundBefore.Handle) "The foreground moved from $($foregroundBefore.Name) to $($foregroundAfter.Name) during the run — virtual input must never take it"
        Write-Host "foreground unchanged: 0x$('{0:X}' -f $foregroundAfter.Handle) ($($foregroundAfter.Name)); cursor unchanged ($(Get-CursorPosition))"
    }

    Write-Host '--- stop_process'
    Write-Host (Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'stop_process' -ToolArguments @{ pid = $clientPid }))
    $clientPid = 0

    $crc = Get-Process CRC -ErrorAction SilentlyContinue
    if ($null -eq $crc) {
        Write-Host 'CRC not running — CRC path unverified live'
    }
    else {
        Write-Host '--- CRC'
        Write-Host (Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'list_processes' -ToolArguments @{ nameContains = 'CRC' }))
        $crcWindowsText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'list_windows' -ToolArguments @{ pid = $crc[0].Id })
        Write-Host $crcWindowsText
        $crcWindow = @(ConvertFrom-DescribeLines -Text $crcWindowsText) | Where-Object { $_.Name -like 'CRC :*' } | Select-Object -First 1
        if ($null -eq $crcWindow) {
            Write-Host 'CRC is running but has no "CRC : <n>" display window open'
        }
        else {
            $crcShot = Invoke-CheckedTool -Session $session -Name 'screenshot' -ToolArguments @{ windowElementId = $crcWindow.Id }
            Write-Host "CRC window '$($crcWindow.Name)': $(Get-McpResultText -Result $crcShot)"
        }
    }

    Write-Host 'LIVE OK'
    exit 0
}
catch {
    $failure = $_.Exception.Message
    [Console]::Error.WriteLine("LIVE FAILED: $failure")
    if ($clientPid -eq 0) {
        # A launch that started the client but failed afterwards names its pid in the error; stop it rather than leave it on the desktop.
        $orphan = [regex]::Match($failure, 'pid[= ](?<pid>\d+)')
        if ($orphan.Success) { $clientPid = [int]$orphan.Groups['pid'].Value }
    }
    if ($clientPid -ne 0) {
        try { Stop-Process -Id $clientPid -Force -ErrorAction Stop } catch { Write-Warning "Could not stop the client (pid $clientPid): $($_.Exception.Message)" }
    }
    exit 1
}
finally {
    Stop-McpServer -Session $session
}
