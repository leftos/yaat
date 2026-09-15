<#
.SYNOPSIS
Drives the running desktop client through Windows UI Automation — the seed for the
client-driving MCP server planned in docs/plans/MAIN.md.

.DESCRIPTION
Dot-source this file, then call the functions from a PowerShell session. Every function
works on System.Windows.Automation elements, so a caller can walk from a window to a menu
item or a button by name and act on it. Facts learned reproducing GitHub #437:

- Avalonia exposes windows, MenuItems and Buttons by Name; menu items also carry their
  x:Name as AutomationId. InvokePattern works on buttons.
- ExpandCollapsePattern is unsupported on Avalonia menus: open a menu with Click-Element
  (a real mouse click at the element's rect), then click the popup item the same way.
- The native file dialog is not listed by a ProcessId-filtered FindAll(Children); drive it
  with [System.Windows.Forms.SendKeys]::SendWait("<path>{ENTER}") once it has focus.
- Launch the client with $env:YAAT_APPDATA_DIR pointing at a scratch dir so the developer's
  preferences and favorites stay untouched; the log then lands at <scratch>/yaat-client.log,
  where a "Unhandled UI-thread exception (recovered)" entry is the usual cause of a button
  that "does nothing".
- The client may sit on a secondary monitor; Save-Screenshot takes an explicit rect rather
  than the primary screen.

.EXAMPLE
. tools/drive-client-uia.ps1
$env:YAAT_APPDATA_DIR = "$env:TEMP\yaat-scratch"
$p = Start-Process src\Yaat.Client\bin\Debug\net10.0\Yaat.Client.exe -PassThru; Start-Sleep 8
$main = (Get-Windows $p.Id) | Where-Object { $_.Current.Name -like 'YAAT*' } | Select-Object -First 1
Click-Element (Find-Elements $main -Name 'View' -ControlType MenuItem)[0]; Start-Sleep 1
$item = Find-Elements $script:Root -ControlType MenuItem | Where-Object { $_.Current.ProcessId -eq $p.Id -and $_.Current.Name -like 'Open Favorites Panel*' }
Click-Element $item[0]
#>

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$script:Root = [System.Windows.Automation.AutomationElement]::RootElement
$script:TreeScope = [System.Windows.Automation.TreeScope]

function Get-Windows([int]$ProcessId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $script:Root.FindAll($script:TreeScope::Children, $cond)
}

function Find-Elements($Parent, [string]$Name, [string]$ControlType) {
    $conds = @()
    if ($Name) { $conds += New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name) }
    if ($ControlType) {
        $ct = [System.Windows.Automation.ControlType]::$ControlType
        $conds += New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
    }
    if ($conds.Count -eq 0) { $cond = [System.Windows.Automation.Condition]::TrueCondition }
    elseif ($conds.Count -eq 1) { $cond = $conds[0] }
    else { $cond = New-Object System.Windows.Automation.AndCondition($conds) }
    $Parent.FindAll($script:TreeScope::Descendants, $cond)
}

function Invoke-Element($Element) {
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Describe($Element) {
    $c = $Element.Current
    "{0} | {1} | id={2} | enabled={3} | rect={4}" -f $c.ControlType.ProgrammaticName, $c.Name, $c.AutomationId, $c.IsEnabled, $c.BoundingRectangle
}

function Dump-Tree($Element, [int]$Depth = 0, [int]$MaxDepth = 6) {
    if ($Depth -gt $MaxDepth) { return }
    ("  " * $Depth) + (Describe $Element)
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $walker.GetFirstChild($Element)
    while ($null -ne $child) {
        Dump-Tree $child ($Depth + 1) $MaxDepth
        $child = $walker.GetNextSibling($child)
    }
}

function Click-Point([double]$X, [double]$Y) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]$X, [int]$Y)
    Start-Sleep -Milliseconds 100
    $sig = '[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, uint dwExtraInfo);'
    if (-not ([System.Management.Automation.PSTypeName]'Native.Win32Mouse').Type) { Add-Type -MemberDefinition $sig -Name Win32Mouse -Namespace Native }
    [Native.Win32Mouse]::mouse_event(0x0002, 0, 0, 0, 0)
    [Native.Win32Mouse]::mouse_event(0x0004, 0, 0, 0, 0)
}

function Click-Element($Element) {
    $r = $Element.Current.BoundingRectangle
    Click-Point ($r.X + $r.Width / 2) ($r.Y + $r.Height / 2)
}

function Save-Screenshot([string]$Path, [int]$X, [int]$Y, [int]$Width, [int]$Height) {
    $bmp = New-Object System.Drawing.Bitmap $Width, $Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen((New-Object System.Drawing.Point $X, $Y), [System.Drawing.Point]::Empty, (New-Object System.Drawing.Size $Width, $Height))
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
