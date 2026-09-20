<#
.SYNOPSIS
Drives the yaat-client-driver MCP server against a real YAAT client and, when one is running, a real CRC.

.DESCRIPTION
Launches the client into a scratch YAAT_APPDATA_DIR, reads its window tree, screenshots it and reads its
log, then stops it — roughly 15 seconds with a real window on the desktop, taking the foreground once.
No mouse or keyboard input is sent unless -WithInput is given, which additionally types into the command
box and opens the Help menu with a real mouse click. Prints "LIVE OK" when every expectation holds.

.EXAMPLE
pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1
pwsh tools/Yaat.ClientDriver.Mcp/live-check.ps1 -WithInput
#>

[CmdletBinding()]
param(
    [string]$Command = 'pwsh',
    [string[]]$Arguments = @('-NoProfile', '-File', 'tools/Yaat.ClientDriver.Mcp/launch.ps1'),
    [string]$AppDataDir = '.tmp/client-driver/live-appdata',
    [switch]$WithInput
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'McpStdio.ps1')

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

    Write-Host '--- launch_yaat'
    $launchText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'launch_yaat' -ToolArguments @{ appDataDir = $AppDataDir })
    Write-Host $launchText
    $pidMatch = [regex]::Match($launchText, 'pid=(?<pid>\d+)')
    Assert-That $pidMatch.Success "launch_yaat did not report a pid: $launchText"
    $clientPid = [int]$pidMatch.Groups['pid'].Value

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

    if ($WithInput) {
        Write-Host '--- set_text CommandInput (real input)'
        Write-Host (Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'set_text' -ToolArguments @{
                    elementId = $commandInputs[0].Id
                    text      = 'HELLO'
                }))
        $value = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'get_value' -ToolArguments @{ elementId = $commandInputs[0].Id })
        Assert-That ($value.Trim() -eq 'HELLO') "get_value after set_text returned '$value', expected 'HELLO'"
        Write-Host "get_value: $value"

        Write-Host '--- click the File menu (real mouse), then look for ConnectMenuItem in its popup'
        $fileMenu = $menuItems | Where-Object { $_.Name -eq 'File' } | Select-Object -First 1
        Write-Host (Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $fileMenu.Id }))
        Start-Sleep -Milliseconds 700
        $openedConnect = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'ConnectMenuItem')
        Assert-That ($openedConnect.Count -ge 1) 'The File menu did not open: no ConnectMenuItem under any window of the client'
        Write-Host "File menu opened: $($openedConnect[0].Name) ($($openedConnect[0].Id))"
        Write-Host (Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'send_keys' -ToolArguments @{ keys = '{ESC}' }))
        Start-Sleep -Milliseconds 400

        Write-Host '--- click the Help menu (real mouse)'
        $helpText = Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'find_elements' -ToolArguments @{
                rootElementId = $mainWindow.Id
                name          = 'Help'
                controlType   = 'MenuItem'
            })
        $helpItems = @(ConvertFrom-DescribeLines -Text $helpText)
        Assert-That ($helpItems.Count -ge 1) "No Help MenuItem on the main window: $helpText"
        Write-Host (Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'click' -ToolArguments @{ elementId = $helpItems[0].Id }))
        Start-Sleep -Milliseconds 700

        $found = @(Find-InProcessWindows -Session $session -ClientPid $clientPid -AutomationId 'HelpGettingStartedMenuItem')
        Assert-That ($found.Count -ge 1) 'The Help menu did not open: no HelpGettingStartedMenuItem under any window of the client'
        Write-Host "Help menu opened: $($found[0].Name) ($($found[0].Id))"
        Write-Host (Get-McpResultText -Result (Invoke-CheckedTool -Session $session -Name 'send_keys' -ToolArguments @{ keys = '{ESC}' }))
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
