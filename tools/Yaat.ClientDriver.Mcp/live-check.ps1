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
-Record      with any mode, also records the client's main window for about 3 s (record_start, record_mark, record_stop): the
             MP4 must exist with an ffprobe duration above 0 and the marks file must hold the one mark, with its sim time when the
             client has a pipe; then the window is minimized without activation and record_start must refuse it. Needs ffmpeg
             and ffprobe on PATH.

.EXAMPLE
pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1
pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1 -Background
pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1 -WithInput
pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1 -Record
#>

#Requires -Version 7.4

[CmdletBinding()]
param(
    [string]$Command = 'pwsh',
    [string[]]$Arguments = @('-NoProfile', '-File', 'tools/Yaat.ClientDriver.Mcp/launch.ps1'),
    [string]$AppDataDir = '.tmp/client-driver/live-appdata',
    [switch]$WithInput,
    [switch]$Background,
    [switch]$Record
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
        # GWL_EXSTYLE read across processes; GetWindowLongPtrW is the 64-bit entry point, so this needs a 64-bit pwsh.
        '[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);'
        '[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);'
        '[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);'
    )
    Add-Type -Namespace ClientDriverCheck -Name User32 -MemberDefinition ($signatures -join ' ') -ReferencedAssemblies System.Drawing.Primitives
}

function Test-VisibleWindowTitled {
    param([int]$ProcessId, [string]$Title)

    @(Get-ClientTopLevelWindows -ProcessId $ProcessId | Where-Object { $_.Title -eq $Title }).Count -ge 1
}

# Every top-level window in the desktop's Z chain, top of the stack first, each with its 1-based rank. Reads handles,
# pids, titles and extended styles across processes; touches nothing.
function Get-TopLevelWindows {
    $rank = 0
    $window = [ClientDriverCheck.User32]::GetTopWindow([IntPtr]::Zero)
    while ($window -ne [IntPtr]::Zero) {
        $rank++
        [uint32]$ownerPid = 0
        $null = [ClientDriverCheck.User32]::GetWindowThreadProcessId($window, [ref]$ownerPid)
        $text = [System.Text.StringBuilder]::new(256)
        $null = [ClientDriverCheck.User32]::GetWindowText($window, $text, $text.Capacity)
        # GWL_EXSTYLE (-20), read across processes; touches nothing.
        $exStyle = [int64][ClientDriverCheck.User32]::GetWindowLongPtr($window, -20)
        [pscustomobject]@{
            Handle  = [int64]$window
            Pid     = [int]$ownerPid
            Title   = $text.ToString()
            ExStyle = $exStyle
            Visible = [ClientDriverCheck.User32]::IsWindowVisible($window)
            Rank    = $rank
        }
        $window = [ClientDriverCheck.User32]::GetWindow($window, 2)
    }
}

function Get-ClientTopLevelWindows {
    param([int]$ProcessId)

    @(Get-TopLevelWindows | Where-Object { ($_.Pid -eq $ProcessId) -and $_.Visible })
}

# Every visible client window carries WS_EX_NOACTIVATE, so the system never hands it the foreground when the user's
# foreground window is minimized. Reads styles across processes; touches nothing.
function Assert-ClientWindowsNoActivate {
    param([int]$ProcessId, [string]$Step)

    $windows = @(Get-ClientTopLevelWindows -ProcessId $ProcessId)
    Assert-That ($windows.Count -ge 1) "$Step found no visible top-level window of pid $ProcessId"
    foreach ($window in $windows) {
        $missing = "$Step '$($window.Title)' (0x$('{0:X}' -f $window.Handle)) has extended style 0x$('{0:X}' -f $window.ExStyle) without " +
            'WS_EX_NOACTIVATE (0x08000000): Windows can hand it the foreground when the window in front of it is minimized'
        Assert-That (($window.ExStyle -band 0x08000000) -ne 0) $missing
    }
    Write-Host "$Step`: $($windows.Count) client windows carry WS_EX_NOACTIVATE"
}

# A client in automation mode must open behind whatever the user already had on screen. Windows creates a new top-level
# window at the top of the Z-order whenever the creating process may set the foreground window — an agent's shell usually
# may — and a never-activated show leaves the window where it was created, so without the gate's move to the bottom the
# client paints over the focused app. Ranks are counted from the top of the Z chain (rank 1); a visible client window whose
# rank is lower than the launch foreground's is above it. Reads the Z chain and window titles; touches nothing.
function Assert-ClientWindowsBelowForegroundAtLaunch {
    param([int]$ProcessId, [int64]$ForegroundHandle, [string]$Step)

    Assert-That ($ForegroundHandle -ne 0) "$Step cannot check the Z order: nothing held the foreground when the client was launched"

    $chain = @(Get-TopLevelWindows)
    $foreground = $chain | Where-Object { $_.Handle -eq $ForegroundHandle } | Select-Object -First 1
    $noForegroundMessage = "$Step cannot check the Z order: the window that held the foreground at launch " +
        "(0x$('{0:X}' -f $ForegroundHandle)) is not a top-level window any more"
    Assert-That ($null -ne $foreground) $noForegroundMessage

    $clientWindows = @($chain | Where-Object { ($_.Pid -eq $ProcessId) -and $_.Visible })
    Assert-That ($clientWindows.Count -ge 1) "$Step found no visible top-level window of pid $ProcessId to check the Z order"
    $ranksText = @($clientWindows | ForEach-Object {
            $label = if ([string]::IsNullOrEmpty($_.Title)) { '(untitled)' } else { $_.Title }
            "$label rank $($_.Rank)"
        }) -join ', '

    $above = @($clientWindows | Where-Object { $_.Rank -lt $foreground.Rank })
    if ($above.Count -gt 0) {
        $aboveMessage = "$Step '$($above[0].Title)' (0x$('{0:X}' -f $above[0].Handle), rank $($above[0].Rank)) opened above the window " +
            "already on screen (rank $($foreground.Rank)) — a client in automation mode must start behind it; client windows: $ranksText"
        Assert-That $false $aboveMessage
    }
    Write-Host "$Step`: $($clientWindows.Count) visible client windows, all below the launch foreground (rank $($foreground.Rank)): $ranksText"
}

# The montage's trigger, reproduced: the window in front of the client is minimized, and Windows activates the next window
# in Z-order. A destroyed window hands the foreground back to the previous one, so closing never triggers it — only a
# minimize does. The client must not be that window. Sampled every 10 ms for 1 s, so a transient surfacing is caught too.
# Nothing but the console this function starts is ever minimized: the console must first take the foreground in a window of
# its own, and only that console's own processes count as its owner.
function Assert-ForegroundHandoffSkipsClient {
    param([int]$ProcessId)

    # conhost.exe started directly hosts its own classic console window, which Windows Terminal cannot adopt as a tab. A pwsh
    # started on its own would open as a tab in the owner's terminal instead, and minimizing that window would take their tabs.
    $childIds = @()
    $console = Start-Process conhost.exe -ArgumentList 'pwsh', '-NoProfile', '-Command', 'Start-Sleep -Seconds 60' -PassThru
    try {
        $before = $script:untouched.Foreground.Handle
        $deadline = [DateTime]::UtcNow.AddSeconds(3)
        do {
            Start-Sleep -Milliseconds 50
            $front = Get-ForegroundWindowInfo
        } while (($front.Handle -eq $before) -and ([DateTime]::UtcNow -lt $deadline))
        # Windows may report a console window's owner as either the host or the attached client, so both count.
        $childIds = @(
            Get-CimInstance Win32_Process -Filter "ParentProcessId = $($console.Id)" -ErrorAction Stop |
                Select-Object -ExpandProperty ProcessId
        )
        $tookForeground = ($front.Handle -ne $before) -and (($front.Pid -eq $console.Id) -or ($front.Pid -in $childIds))
        $noConsoleMessage = "The console never took the foreground; the foreground went to $($front.Name) (pid $($front.Pid)) instead"
        Assert-That $tookForeground $noConsoleMessage

        # The window directly behind the one about to be minimized has to be a client window, or the minimize proves nothing.
        $behind = [ClientDriverCheck.User32]::GetWindow([IntPtr]$front.Handle, 2)
        while (($behind -ne [IntPtr]::Zero) -and (-not [ClientDriverCheck.User32]::IsWindowVisible($behind))) {
            $behind = [ClientDriverCheck.User32]::GetWindow($behind, 2)
        }
        $behindTitle = '(none)'
        [uint32]$behindPid = 0
        if ($behind -ne [IntPtr]::Zero) {
            $behindText = [System.Text.StringBuilder]::new(256)
            $null = [ClientDriverCheck.User32]::GetWindowText($behind, $behindText, $behindText.Capacity)
            $null = [ClientDriverCheck.User32]::GetWindowThreadProcessId($behind, [ref]$behindPid)
            $behindTitle = $behindText.ToString()
        }
        $behindMessage = 'The hand-off check needs a client window directly behind the window it minimizes; ' +
            "found $behindTitle ($behindPid) instead"
        Assert-That (($behind -ne [IntPtr]::Zero) -and ($behindPid -eq $ProcessId)) $behindMessage

        $null = [ClientDriverCheck.User32]::ShowWindow([IntPtr]$front.Handle, 6)   # SW_MINIMIZE activates the next window in Z-order
        # ShowWindow's return value is not a substitute: a handle that died since the wait reports success without minimizing.
        $minimizeMessage = 'The console in front was not minimized; the hand-off was never exercised'
        Assert-That ([ClientDriverCheck.User32]::IsIconic([IntPtr]$front.Handle)) $minimizeMessage
        $deadline = [DateTime]::UtcNow.AddSeconds(1)
        $seen = [System.Collections.Generic.List[string]]::new()
        do {
            $foreground = Get-ForegroundWindowInfo
            if ($seen.Count -eq 0 -or $seen[$seen.Count - 1] -ne $foreground.Name) { $seen.Add($foreground.Name) }
            $handoffMessage = 'The window in front of the client minimized and Windows handed the foreground to the client ' +
                "(foreground sequence: $($seen -join ' -> ')): its windows lack WS_EX_NOACTIVATE"
            Assert-That ($foreground.Pid -ne $ProcessId) $handoffMessage
            Start-Sleep -Milliseconds 10
        } while ([DateTime]::UtcNow -lt $deadline)

        # The console moved the run's foreground; Windows picks the next window in Z-order, which this script does not
        # control, so only report where it landed.
        $baselineName = $script:untouched.Foreground.Name
        $afterForeground = Get-ForegroundWindowInfo
        if ($afterForeground.Handle -ne $script:untouched.Foreground.Handle) {
            $landedMessage = "The run's foreground was $baselineName (0x$('{0:X}' -f $script:untouched.Foreground.Handle)); " +
                "after the hand-off it is $($afterForeground.Name) (0x$('{0:X}' -f $afterForeground.Handle))"
            Write-Warning $landedMessage
        }
    }
    finally {
        # Stops the console's pwsh child and conhost itself, whichever are still running.
        foreach ($consolePid in @($childIds) + @($console.Id)) {
            try {
                Stop-Process -Id $consolePid -ErrorAction Stop
            }
            catch {
                # A console process that exited on its own is expected; only a real failure to stop one earns a warning.
                if ($_.Exception -isnot [Microsoft.PowerShell.Commands.ProcessCommandException]) {
                    Write-Warning "Could not stop the console process (pid=$consolePid): $_"
                }
            }
        }
    }
    Write-Host "foreground hand-off after minimizing the window in front: $($seen -join ' -> ') (client never took it)"
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
        $found += @(ConvertFrom-DescribeLines -Text (Get-McpResultText -Result (
            Invoke-CheckedTool -Session $Session -Name 'find_elements' -ToolArguments @{
                        rootElementId = $window.Id
                        automationId  = $AutomationId
                    })))
    }
    $found
}

function Invoke-RecordCheck {
    param($Session, [int]$ClientPid, [string]$WindowsText, [string]$MainWindowId)

    Write-Host '--- record_start / record_mark / record_stop'
    Assert-That ($null -ne (Get-Command ffprobe -ErrorAction SilentlyContinue)) 'ffprobe is not on PATH; -Record needs it. Fix: winget install Gyan.FFmpeg'
    $startText = Get-McpResultText -Result (Invoke-CheckedTool -Session $Session -Name 'record_start' -ToolArguments @{ pid = $ClientPid })
    Write-Host $startText
    $clip = [regex]::Match($startText, '^recording (?<mp4>.+?\.mp4) \(')
    Assert-That $clip.Success "record_start did not name its MP4: $startText"
    $mp4 = $clip.Groups['mp4'].Value
    Start-Sleep -Seconds 3
    $markText = Get-McpResultText -Result (Invoke-CheckedTool -Session $Session -Name 'record_mark' -ToolArguments @{ label = 'live check' })
    Write-Host $markText
    $stopText = Get-McpResultText -Result (Invoke-CheckedTool -Session $Session -Name 'record_stop' -TimeoutSeconds 60)
    Write-Host $stopText
    Assert-That ($stopText -like "stopped $mp4*") "record_stop did not report the clip record_start named ($mp4): $stopText"
    Assert-That (Test-Path -LiteralPath $mp4) "record_stop reported '$mp4' but no file is there"
    $duration = & ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $mp4
    Assert-That (($LASTEXITCODE -eq 0) -and ([double]$duration -gt 0)) "ffprobe found no duration in ${mp4}: $duration"
    $marksPath = [regex]::Match($stopText, 'marks: (?<path>.+)$', 'Multiline').Groups['path'].Value.Trim()
    Assert-That (Test-Path -LiteralPath $marksPath) "record_stop named no marks file that exists: '$marksPath'"
    $marks = @((Get-Content -LiteralPath $marksPath -Raw | ConvertFrom-Json).marks)
    Assert-That ($marks.Count -eq 1) "The marks file $marksPath holds $($marks.Count) marks, expected the one record_mark wrote"
    if (-not $WithInput) {
        Assert-That ($null -ne $marks[0].simSeconds) "The mark in $marksPath has no simSeconds although the client has a pipe"
    }
    Write-Host "recorded $mp4 ($duration s); mark '$($marks[0].label)' at clip $($marks[0].clipSeconds) s, sim $($marks[0].simSeconds) s"

    Write-Host '--- record_start refuses a minimized window'
    $handle = [regex]::Match($WindowsText, "$([regex]::Escape($MainWindowId))\b.*hwnd=0x(?<hwnd>[0-9A-F]+)")
    Assert-That $handle.Success "list_windows gave no hwnd for the main window $MainWindowId"
    $mainHandle = [IntPtr][Convert]::ToInt64($handle.Groups['hwnd'].Value, 16)
    # SW_SHOWMINNOACTIVE (7) minimizes without activating another window, and SW_SHOWNOACTIVATE (4) restores the same way,
    # so the foreground the run started with is untouched.
    $null = [ClientDriverCheck.User32]::ShowWindow($mainHandle, 7)
    try {
        Start-Sleep -Milliseconds 500
        $refused = Invoke-McpTool -Session $Session -Name 'record_start' -ToolArguments @{ pid = $ClientPid }
        $refusedText = Get-McpResultText -Result $refused
        Write-Host $refusedText
        Assert-That ($refused.isError -eq $true) "record_start accepted a minimized window: $refusedText"
        Assert-That ($refusedText -like '*is minimized*') "record_start refused the minimized window with an unexpected message: $refusedText"
    }
    finally {
        $null = [ClientDriverCheck.User32]::ShowWindow($mainHandle, 4)
    }
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
        @(Get-ClientTopLevelWindows -ProcessId $ClientPid).Count
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
        Assert-ClientWindowsBelowForegroundAtLaunch -ProcessId $launchedPid -ForegroundHandle $ForegroundBefore.Handle -Step 'launch'
        if ($Background) { Assert-ClientWindowsNoActivate -ProcessId $launchedPid -Step 'launch' }
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
    if ($Background -and (-not [Environment]::Is64BitProcess)) {
        throw 'The -Background hand-off check reads 64-bit window handles (GetWindowLongPtrW), so it needs a 64-bit pwsh.'
    }

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
    $unfilteredMessage = "find_elements rejected the empty criteria set with an unexpected message: $unfilteredText"
    Assert-That ($unfilteredText -like '*at least one of name, automationId or controlType*') $unfilteredMessage
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
    $connectItems = @(ConvertFrom-DescribeLines -Text (Get-McpResultText -Result (
        Invoke-CheckedTool -Session $session -Name 'find_elements' -ToolArguments @{
                    rootElementId = $mainWindow.Id
                    automationId  = 'ConnectMenuItem'
                })))
    $absentMessage = "ConnectMenuItem was expected to be absent while the File menu is closed, but find_elements returned $($connectItems.Count)"
    Assert-That ($connectItems.Count -eq 0) $absentMessage
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
    $logText = Get-McpResultText -Result (
        Invoke-CheckedTool -Session $session -Name 'tail_yaat_log' -ToolArguments @{ appDataDir = $AppDataDir; lines = 200 }
    )
    Assert-That ($logText -match 'Log file:') "tail_yaat_log has no 'Log file:' line: $logText"
    Write-Host ($logText -split "`r?`n" | Select-Object -First 4)

    if ($WithInput -or $Background) {
        Set-InputPath -Session $session

        # Input sent while the client is still loading navdata is dropped, whichever the mode: wait for the status bar to say it is done.
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        do {
            Start-Sleep -Milliseconds 500
            $loaded = @(ConvertFrom-DescribeLines -Text (Get-McpResultText -Result (
                Invoke-CheckedTool -Session $session -Name 'find_elements' -ToolArguments @{
                            rootElementId = $mainWindow.Id
                            name          = 'Navigation data loaded'
                        })))
        } while (($loaded.Count -eq 0) -and ([DateTime]::UtcNow -lt $deadline))
        Assert-That ($loaded.Count -ge 1) "The client never showed 'Navigation data loaded' within 90 s"

        $script:untouched = [pscustomobject]@{ Foreground = Get-ForegroundWindowInfo; Cursor = Get-CursorPosition }
        if ($Background) {
            $holdsMessage = 'The client holds the foreground — -Background needs another window in front of it'
            Assert-That ($script:untouched.Foreground.Pid -ne $clientPid) $holdsMessage
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
        $typed = Get-McpResultText -Result (
            Invoke-CheckedTool -Session $session -Name 'get_value' -ToolArguments @{ elementId = $commandInputs[0].Id }
        )
        Assert-That ($typed.Trim() -ceq 'hello') "get_value after send_keys returned '$typed', expected 'hello'"
        Write-Host "get_value: $typed"

        Write-Host '--- set_text CommandInput'
        $setText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'set_text' -ToolArguments @{
                elementId = $commandInputs[0].Id
                text      = 'HELLO'
            })
        Write-Host $setText
        Assert-InputPath -Text $setText -Step 'set_text'
        $value = Get-McpResultText -Result (
            Invoke-CheckedTool -Session $session -Name 'get_value' -ToolArguments @{ elementId = $commandInputs[0].Id }
        )
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
        $replaced = Get-McpResultText -Result (
            Invoke-CheckedTool -Session $session -Name 'get_value' -ToolArguments @{ elementId = $commandInputs[0].Id }
        )
        $replacedMessage = "After a double click on HELLO and typing Z the command box holds '$replaced', expected 'Z'" +
            ' — the double click did not select the word'
        Assert-That ($replaced.Trim() -ceq 'Z') $replacedMessage
        Write-Host "get_value: $replaced"

        Write-Host "--- open File and click Connect... inside its popup ($($mode.Name)): the modal Connect to Server dialog opens"
        Assert-That (-not (Test-VisibleWindowTitled -ProcessId $clientPid -Title 'Connect to Server')) 'A Connect to Server window is already open'
        $fileAgain = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $fileMenu.Id })
        Assert-InputPath -Text $fileAgain -Step 'click File again'
        Start-Sleep -Milliseconds 700
        $connectItems = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'ConnectMenuItem')
        Assert-That ($connectItems.Count -ge 1) 'The File menu did not reopen: no ConnectMenuItem'
        $connectClick = Get-McpResultText -Result (
            Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $connectItems[0].Id }
        )
        Write-Host $connectClick
        Assert-InputPath -Text $connectClick -Step 'click Connect... in the popup'
        Start-Sleep -Milliseconds 1500
        $openedMessage = 'Clicking Connect... opened no visible "Connect to Server" window'
        Assert-That (Test-VisibleWindowTitled -ProcessId $clientPid -Title 'Connect to Server') $openedMessage
        $closeButtons = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'CloseButton')
        Assert-That ($closeButtons.Count -ge 1) 'The Connect to Server dialog has no CloseButton under any window of the client'
        Write-Host 'Connect to Server dialog opened'
        # The dialog is a second top-level window of the client, so it carries WS_EX_NOACTIVATE too.
        if ($Background) { Assert-ClientWindowsNoActivate -ProcessId $clientPid -Step 'Connect to Server open' }

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

        $closeText = Get-McpResultText -Result (
            Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $closeButtons[0].Id }
        )
        Write-Host $closeText
        Assert-InputPath -Text $closeText -Step 'click Close on the dialog'
        Start-Sleep -Milliseconds 700
        $stillOpenMessage = 'Close did not close the Connect to Server dialog'
        Assert-That (-not (Test-VisibleWindowTitled -ProcessId $clientPid -Title 'Connect to Server')) $stillOpenMessage
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

    if ($Record) {
        Invoke-RecordCheck -Session $session -ClientPid $clientPid -WindowsText $windowsText -MainWindowId $mainWindow.Id
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

        if ($Background) {
            # Last, because it minimizes the window in front and so ends the run's untouched foreground: nothing after it
            # may assume the foreground the run started with.
            Assert-ForegroundHandoffSkipsClient -ProcessId $clientPid
        }
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
    exit 1
}
finally {
    # Runs on success, on failure and on Ctrl+C (which skips the catch above), so a launched client is never left behind.
    # The success path stops it through stop_process and clears the pid, so this never stops it twice or too early.
    if ($clientPid -ne 0) {
        try {
            Stop-Process -Id $clientPid -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "Could not stop the client (pid $clientPid): $($_.Exception.Message)"
        }
    }
    Stop-McpServer -Session $session
}
