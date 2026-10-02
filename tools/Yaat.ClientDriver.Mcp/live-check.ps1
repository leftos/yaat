<#
.SYNOPSIS
Drives the yaat-client-driver MCP server against a real YAAT client and, when one is running, a real CRC.

.DESCRIPTION
Starts the client into a scratch YAAT_APPDATA_DIR, reads its window tree, screenshots it and reads its log, then stops it.
Prints "LIVE OK" when every expectation holds. Each mode drives the client one way:

default      launch_yaat starts the client in automation mode, so every YAAT call goes over its automation pipe. The launch
             must leave the foreground where it was and the main window inactive, and the foreground must still be where it was
             at the end of the run. No input is sent.
-Background  as the default, then opens the File menu with a click and closes it with {ESC}, types into the command box with
             send_keys and set_text, double-clicks a word with click_point, opens the modal Connect to Server dialog (a click
             behind it is refused as ELEMENT_DISABLED) and opens the Help menu, all over the pipe: every input result must end
             "(pipe)", and after every step the foreground window and the real cursor position must be what they were before
             the first one — pipe input never takes either.
-WithInput   the script starts the client itself without its automation pipe (YAAT_AUTOMATION removed from its environment)
             and the tools drive it through UI Automation with real input (set_input_mode real): the same input steps, every
             result must end "(real)", the client takes the foreground and the real cursor moves.

.EXAMPLE
pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1
pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1 -Background
pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1 -WithInput
#>

#Requires -Version 7.4

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

if ($WithInput -and $Background) {
    throw 'Pass -WithInput (UI Automation with real input) or -Background (the pipe, another window in front), not both.'
}

if (-not ('ClientDriverCheck.User32' -as [type])) {
    $signatures = @(
        '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();'
        '[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);'
        '[DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr window);'
        '[DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command);'
        '[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);'
        '[DllImport("user32.dll", CharSet = CharSet.Unicode)]'
        'public static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int size);'
        '[DllImport("user32.dll")] public static extern bool GetCursorPos(out System.Drawing.Point point);'
    )
    Add-Type -Namespace ClientDriverCheck -Name User32 -MemberDefinition ($signatures -join ' ') -ReferencedAssemblies System.Drawing.Primitives
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
    $foregroundMessage = "$Step moved the foreground from $($Baseline.Foreground.Name) to $($foreground.Name) — pipe input must never take it"
    Assert-That ($foreground.Handle -eq $Baseline.Foreground.Handle) $foregroundMessage
    Assert-That ($cursor -eq $Baseline.Cursor) "$Step moved the real cursor from ($($Baseline.Cursor)) to ($cursor) — pipe input must never move it"
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
    param([string]$Text, [string]$Step)

    Assert-That ($Text.TrimEnd() -match $mode.InputEnding) "$Step was expected to end with $($mode.InputSuffix) but did not: $Text"
    if ($Background) { Assert-Untouched -Baseline $script:untouched -Step $Step }
}

function Assert-That {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function ConvertFrom-DescribeLines {
    param([string]$Text)

    # A node row carries enabled=; a pipe window row carries visible= and active=, and a popup marker on an overlay popup.
    $pattern = '^(?<id>e\d+) \| (?<type>\S+) \| (?<name>.*?) \| id=(?<automationId>.*?) \| ' +
        '(?:enabled=(?<enabled>\w+)|visible=(?<visible>\w+) \| active=(?<active>\w+)(?<popup> \| popup)?) \| rect=(?<rect>.*)$'
    foreach ($line in ($Text -split "`r?`n")) {
        $match = [regex]::Match($line.Trim(), $pattern)
        if ($match.Success) {
            [pscustomobject]@{
                Id           = $match.Groups['id'].Value
                Type         = $match.Groups['type'].Value
                Name         = $match.Groups['name'].Value
                AutomationId = $match.Groups['automationId'].Value
                Visible      = $match.Groups['visible'].Value
                Active       = $match.Groups['active'].Value
                IsPopup      = $match.Groups['popup'].Success
                Rect         = $match.Groups['rect'].Value
            }
        }
    }
}

function Get-ClientWindows {
    param($Session, [int]$ClientPid)

    $text = Get-McpResultText -Result (Invoke-CheckedTool -Session $Session -Name 'list_windows' -ToolArguments @{ pid = $ClientPid })
    @(ConvertFrom-DescribeLines -Text $text)
}

function Find-InProcessWindows {
    param($Session, [int]$ClientPid, [string]$AutomationId)

    # A menu popup is its own top-level window, so an opened menu item is found by scanning every window of the process.
    $found = @()
    foreach ($window in (Get-ClientWindows -Session $Session -ClientPid $ClientPid)) {
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

# The mode is decided here, once: the expectations that differ go in $mode, the steps that differ in the functions below.
if ($WithInput) {
    $mode = [pscustomobject]@{
        Name         = 'UI Automation, real input'
        InputSuffix  = '(real)'
        InputEnding  = '\(real\)$'
        TextBoxType  = 'Edit'
        FileHeader   = 'File'
        HelpHeader   = 'Help'
        ShotPattern  = '*'
        BlockedError = $null
    }

    function Start-Client {
        # ForegroundBefore exists only to match the pipe version: in this mode the client takes the foreground.
        param($Session, $ForegroundBefore)

        $exePath = Join-Path $repoRoot 'src/Yaat.Client/bin/Debug/net10.0/Yaat.Client.exe'
        Assert-That (Test-Path $exePath) "No client executable at '$exePath' — build it first with: dotnet build src/Yaat.Client"
        $appData = (New-Item -ItemType Directory -Force (Join-Path $repoRoot $AppDataDir)).FullName
        $environment = @{ YAAT_APPDATA_DIR = $appData; YAAT_AUTOMATION = $null }
        $process = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path $exePath) -Environment $environment -PassThru
        # Recorded at once, so a failure from here on stops this client instead of leaving it on the desktop.
        $script:clientPid = $process.Id
        # Reading the handle keeps it open, so ExitCode can still be read once the process has exited.
        $null = $process.Handle
        Write-Host "started pid=$($process.Id) without its automation pipe; waiting for its main window in UI Automation"

        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        do {
            Start-Sleep -Milliseconds 500
            if ($process.HasExited) {
                throw "The client (pid=$($process.Id)) exited with code $($process.ExitCode) before showing a YAAT* window"
            }
            $windows = Get-ClientWindows -Session $Session -ClientPid $process.Id
        } while ((@($windows | Where-Object { $_.Name -like 'YAAT*' }).Count -eq 0) -and ([DateTime]::UtcNow -lt $deadline))
        # A client that exited during the last poll reports its exit code rather than the deadline that raced it.
        if ($process.HasExited) {
            throw "The client (pid=$($process.Id)) exited with code $($process.ExitCode) before showing a YAAT* window"
        }
        $shown = @($windows | Where-Object { $_.Name -like 'YAAT*' }).Count
        Assert-That ($shown -ge 1) "The client (pid=$($process.Id)) showed no YAAT* window in UI Automation within 60 s"
        $process.Id
    }

    function Set-InputPath {
        param($Session)

        $modeText = Get-McpResultText -Result (Invoke-CheckedTool -Session $Session -Name 'set_input_mode' -ToolArguments @{ mode = 'real' })
        Assert-That ($modeText -eq 'input mode: real (pipe-routed calls ignore it)') "set_input_mode real answered: $modeText"
        Write-Host $modeText
    }

    function Get-MenuWindowCount {
        # Session exists only to match the pipe version, which asks list_windows; this one counts HWNDs.
        param($Session, [int]$ClientPid)

        # Avalonia files a menu popup under its main window in UI Automation, so list_windows never shows it; the popup is still its
        # own visible top-level HWND, which is what this counts.
        $count = 0
        $window = [ClientDriverCheck.User32]::GetTopWindow([IntPtr]::Zero)
        while ($window -ne [IntPtr]::Zero) {
            [uint32]$ownerPid = 0
            $null = [ClientDriverCheck.User32]::GetWindowThreadProcessId($window, [ref]$ownerPid)
            if (($ownerPid -eq $ClientPid) -and [ClientDriverCheck.User32]::IsWindowVisible($window)) { $count++ }
            $window = [ClientDriverCheck.User32]::GetWindow($window, 2)
        }
        $count
    }

    function Get-PointArguments {
        # WindowId exists only to match the pipe version, whose point is relative to that window.
        param([int]$X, [int]$Y, [string]$WindowId)

        # A UI Automation rectangle is in physical screen pixels, which is what click_point takes without a window.
        @{ x = $X; y = $Y; doubleClick = $true }
    }
}
else {
    $mode = [pscustomobject]@{
        Name         = 'pipe'
        InputSuffix  = '(pipe)'
        InputEnding  = '\(pipe\)$'
        TextBoxType  = 'TextBox'
        FileHeader   = '_File'
        HelpHeader   = '_Help'
        ShotPattern  = "*from the client's render at scale * (pipe)*"
        BlockedError = 'ELEMENT_DISABLED'
    }

    function Start-Client {
        param($Session, $ForegroundBefore)

        $launchArguments = @{ appDataDir = $AppDataDir }
        $launchText = Get-McpResultText -Result (Invoke-CheckedTool -Session $Session -Name 'launch_yaat' -ToolArguments $launchArguments)
        $foregroundAfterLaunch = Get-ForegroundWindowInfo
        Write-Host $launchText
        $pidMatch = [regex]::Match($launchText, 'pid=(?<pid>\d+)')
        Assert-That $pidMatch.Success "launch_yaat did not report a pid: $launchText"
        $launchedPid = [int]$pidMatch.Groups['pid'].Value
        $movedMessage = "launch_yaat (pid=$launchedPid) moved the foreground from $($ForegroundBefore.Name) to $($foregroundAfterLaunch.Name)" +
            ' — a client in automation mode must start behind it'
        Assert-That ($foregroundAfterLaunch.Handle -eq $ForegroundBefore.Handle) $movedMessage
        $launchMain = @(ConvertFrom-DescribeLines -Text $launchText) | Where-Object { $_.Name -like 'YAAT*' } | Select-Object -First 1
        Assert-That ($null -ne $launchMain) "launch_yaat (pid=$launchedPid) listed no YAAT* window: $launchText"
        Assert-That ($launchMain.Active -eq 'False') "The main window of pid=$launchedPid is active after launch_yaat: $($launchMain.Active)"
        Write-Host "foreground unchanged by the launch; main window active=$($launchMain.Active)"
        $launchedPid
    }

    function Set-InputPath {
        param($Session)

        Write-Host 'set_input_mode skipped: pipe-routed calls ignore it'
    }

    function Get-MenuWindowCount {
        param($Session, [int]$ClientPid)

        # Over the pipe, list_windows lists an open menu's overlay popup as its own row with the popup marker.
        @(Get-ClientWindows -Session $Session -ClientPid $ClientPid | Where-Object { $_.IsPopup }).Count
    }

    function Get-PointArguments {
        param([int]$X, [int]$Y, [string]$WindowId)

        # A pipe rectangle is already in DIPs relative to its window, which is what click_point takes with windowElementId.
        @{ x = $X; y = $Y; doubleClick = $true; windowElementId = $WindowId }
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$session = $null
$clientPid = 0
try {
    $session = Start-McpServer -Command $Command -Arguments $Arguments -WorkingDirectory $repoRoot
    $null = Initialize-McpSession -Session $session
    Write-Host "mode: $($mode.Name)"

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

    Write-Host '--- launch'
    $clientPid = Start-Client -Session $session -ForegroundBefore $foregroundBefore

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
            controlType   = $mode.TextBoxType
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
    $fileHeaderRow = $menuItems | Where-Object { $_.Name -eq $mode.FileHeader } | Select-Object -First 1
    Assert-That ($null -ne $fileHeaderRow) "No $($mode.FileHeader) menu among: $($menuItems.Name -join ', ')"

    # Avalonia builds a menu's popup only when it opens, so ConnectMenuItem and its siblings are absent from the tree
    # until something clicks File. The input passes are where that expectation can hold.
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
    Assert-That ($shotText -like $mode.ShotPattern) "screenshot was expected to match '$($mode.ShotPattern)' but read: $shotText"
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
        Set-InputPath -Session $session

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
        if ($Background) {
            Assert-That ($script:untouched.Foreground.Pid -ne $clientPid) 'The client holds the foreground — -Background needs another window in front of it'
            Write-Host "baseline: foreground $($script:untouched.Foreground.Name), cursor ($($script:untouched.Cursor))"
        }
        $windowCountBefore = Get-MenuWindowCount -Session $session -ClientPid $clientPid

        Write-Host "--- click the File menu ($($mode.Name)), then look for its popup window and ConnectMenuItem in it"
        $fileMenu = $menuItems | Where-Object { $_.Name -eq $mode.FileHeader } | Select-Object -First 1
        $clickText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $fileMenu.Id })
        Write-Host $clickText
        Assert-InputPath -Text $clickText -Step 'click File'
        Start-Sleep -Milliseconds 700
        $windowCountOpen = Get-MenuWindowCount -Session $session -ClientPid $clientPid
        $noPopupMessage = "The File menu opened no popup window: the client had $windowCountBefore menu windows before the click" +
            " and $windowCountOpen after"
        Assert-That ($windowCountOpen -gt $windowCountBefore) $noPopupMessage
        $openedConnect = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'ConnectMenuItem')
        Assert-That ($openedConnect.Count -ge 1) 'The File menu did not open: no ConnectMenuItem under any window of the client'
        Write-Host "File menu opened: $($openedConnect[0].Name) ($($openedConnect[0].Id)); menu windows $windowCountBefore -> $windowCountOpen"

        Write-Host "--- send_keys {ESC} ($($mode.Name)) closes it"
        $escText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'send_keys' -ToolArguments @{ keys = '{ESC}' })
        Write-Host $escText
        Assert-InputPath -Text $escText -Step 'send_keys {ESC}'
        Start-Sleep -Milliseconds 500
        $windowCountClosed = Get-MenuWindowCount -Session $session -ClientPid $clientPid
        $notClosedMessage = "{ESC} did not close the File menu: $windowCountClosed menu windows, $windowCountBefore before it opened"
        Assert-That ($windowCountClosed -eq $windowCountBefore) $notClosedMessage
        $stillOpen = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'ConnectMenuItem')
        Assert-That ($stillOpen.Count -eq 0) '{ESC} did not close the File menu: ConnectMenuItem is still in the tree'

        Write-Host "--- send_keys 'hello' into CommandInput ($($mode.Name))"
        $typeText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'send_keys' -ToolArguments @{
                keys           = 'hello'
                focusElementId = $commandInputs[0].Id
            })
        Write-Host $typeText
        Assert-InputPath -Text $typeText -Step 'send_keys hello'
        $typed = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'get_value' -ToolArguments @{ elementId = $commandInputs[0].Id })
        Assert-That ($typed.Trim() -ceq 'hello') "get_value after send_keys returned '$typed', expected 'hello'"
        Write-Host "get_value: $typed"

        Write-Host '--- set_text CommandInput'
        $setText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'set_text' -ToolArguments @{
                elementId = $commandInputs[0].Id
                text      = 'HELLO'
            })
        Write-Host $setText
        Assert-InputPath -Text $setText -Step 'set_text'
        $value = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'get_value' -ToolArguments @{ elementId = $commandInputs[0].Id })
        Assert-That ($value.Trim() -eq 'HELLO') "get_value after set_text returned '$value', expected 'HELLO'"
        Write-Host "get_value: $value"

        Write-Host "--- double click inside the command box's word ($($mode.Name)) selects it, so typing Z replaces it"
        $rectMatch = [regex]::Match($commandInputs[0].Rect, '^\((?<x>-?\d+),(?<y>-?\d+) (?<w>\d+)x(?<h>\d+)\)$')
        Assert-That $rectMatch.Success "CommandInput has no parsable rectangle: $($commandInputs[0].Rect)"
        $wordX = [int]$rectMatch.Groups['x'].Value + 14
        $wordY = [int]$rectMatch.Groups['y'].Value + [int]([int]$rectMatch.Groups['h'].Value / 2)
        $pointArguments = Get-PointArguments -X $wordX -Y $wordY -WindowId $mainWindow.Id
        $doubleText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click_point' -ToolArguments $pointArguments)
        Write-Host $doubleText
        Assert-InputPath -Text $doubleText -Step 'double click_point in CommandInput'
        $zText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'send_keys' -ToolArguments @{ keys = 'Z' })
        Assert-InputPath -Text $zText -Step 'send_keys Z'
        $replaced = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'get_value' -ToolArguments @{ elementId = $commandInputs[0].Id })
        Assert-That ($replaced.Trim() -ceq 'Z') "After a double click on HELLO and typing Z the command box holds '$replaced', expected 'Z' — the double click did not select the word"
        Write-Host "get_value: $replaced"

        Write-Host "--- open File and click Connect... inside its popup ($($mode.Name)): the modal Connect to Server dialog opens"
        Assert-That (-not (Test-VisibleWindowTitled -ProcessId $clientPid -Title 'Connect to Server')) 'A Connect to Server window is already open'
        $fileAgain = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $fileMenu.Id })
        Assert-InputPath -Text $fileAgain -Step 'click File again'
        Start-Sleep -Milliseconds 700
        $connectItems = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'ConnectMenuItem')
        Assert-That ($connectItems.Count -ge 1) 'The File menu did not reopen: no ConnectMenuItem'
        $connectClick = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $connectItems[0].Id })
        Write-Host $connectClick
        Assert-InputPath -Text $connectClick -Step 'click Connect... in the popup'
        Start-Sleep -Milliseconds 1500
        Assert-That (Test-VisibleWindowTitled -ProcessId $clientPid -Title 'Connect to Server') 'Clicking Connect... opened no visible "Connect to Server" window'
        $closeButtons = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'CloseButton')
        Assert-That ($closeButtons.Count -ge 1) 'The Connect to Server dialog has no CloseButton under any window of the client'
        Write-Host 'Connect to Server dialog opened'

        if ($null -ne $mode.BlockedError) {
            Write-Host "--- negative: a click on the main window behind the modal dialog is refused ($($mode.Name))"
            $blocked = Invoke-McpTool -Session $session -Name 'click' -ToolArguments @{ elementId = $fileMenu.Id }
            $blockedText = Get-McpResultText -Result $blocked
            Assert-That ($blocked.isError -eq $true) "A click on the disabled main window was accepted: $blockedText"
            $unexpectedMessage = "The click behind the modal dialog failed with an unexpected message: $blockedText"
            Assert-That ($blockedText -like "*$($mode.BlockedError)*") $unexpectedMessage
            if ($Background) { Assert-Untouched -Baseline $script:untouched -Step 'the refused click' }
            Write-Host $blockedText
        }

        $closeText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $closeButtons[0].Id })
        Write-Host $closeText
        Assert-InputPath -Text $closeText -Step 'click Close on the dialog'
        Start-Sleep -Milliseconds 700
        Assert-That (-not (Test-VisibleWindowTitled -ProcessId $clientPid -Title 'Connect to Server')) 'Close did not close the Connect to Server dialog'
        Write-Host 'Connect to Server dialog closed'

        Write-Host "--- click the Help menu ($($mode.Name))"
        $helpText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'find_elements' -ToolArguments @{
                rootElementId = $mainWindow.Id
                name          = $mode.HelpHeader
                controlType   = 'MenuItem'
            })
        $helpItems = @(ConvertFrom-DescribeLines -Text $helpText)
        Assert-That ($helpItems.Count -ge 1) "No $($mode.HelpHeader) MenuItem on the main window: $helpText"
        $helpClick = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $helpItems[0].Id })
        Write-Host $helpClick
        Assert-InputPath -Text $helpClick -Step 'click Help'
        Start-Sleep -Milliseconds 700

        $found = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'HelpGettingStartedMenuItem')
        Assert-That ($found.Count -ge 1) 'The Help menu did not open: no HelpGettingStartedMenuItem under any window of the client'
        Write-Host "Help menu opened: $($found[0].Name) ($($found[0].Id))"
        $helpEsc = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'send_keys' -ToolArguments @{ keys = '{ESC}' })
        Write-Host $helpEsc
        Assert-InputPath -Text $helpEsc -Step 'send_keys {ESC} after Help'
    }

    if (-not $WithInput) {
        if ($Background) {
            Assert-Untouched -Baseline $script:untouched -Step 'the run'
            Write-Host "cursor unchanged ($(Get-CursorPosition))"
        }
        $foregroundAfter = Get-ForegroundWindowInfo
        $foregroundMessage = "The foreground moved from $($foregroundBefore.Name) to $($foregroundAfter.Name) during the run" +
            ' — a pipe-driven client must never take it'
        Assert-That ($foregroundAfter.Handle -eq $foregroundBefore.Handle) $foregroundMessage
        Write-Host "foreground unchanged: 0x$('{0:X}' -f $foregroundAfter.Handle) ($($foregroundAfter.Name))"
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
