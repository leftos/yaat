#!/usr/bin/env pwsh
# Case table for tools/TestAllSelfGate.ps1: the in-gate test, and the argument list the self-wrap
# builds for tools/gate.ps1. Nothing busy runs here, so the script is not gated. Each case sets the
# environment variables it needs in this process and puts back the ones it found.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'The case results are the script''s console output, as in test-all.ps1.')]
param([string]$SelfGateScript = (Join-Path $PSScriptRoot 'TestAllSelfGate.ps1'))
$ErrorActionPreference = 'Stop'
. $SelfGateScript

$markerNames = @('YAAT_TEST_ALL_GATED', 'GATE_SLOT_HELD', 'GATE_BOX_JOB')
$initial = @{}
foreach ($name in $markerNames + 'YAAT_SERVER_DIR') {
    $initial[$name] = [Environment]::GetEnvironmentVariable($name)
}

function Restore-CaseEnvironment {
    foreach ($name in $initial.Keys) {
        [Environment]::SetEnvironmentVariable($name, $initial[$name])
    }
}

function Clear-MarkerVariable {
    foreach ($name in $markerNames) {
        [Environment]::SetEnvironmentVariable($name, $null)
    }
}

$failures = @()
function Add-CaseResult {
    param([string]$Name, [string]$Detail)
    if ($Detail) {
        $script:failures += "${Name}: $Detail"
        Write-Host "FAIL: $Name - $Detail"
    } else {
        Write-Host "PASS: $Name"
    }
}

# Case-sensitive and in order: the gate reads the option words in this order, and the child binds
# the rest by name only because they arrive after the bare `--`.
function Test-WordsEqual {
    param([string[]]$Words, [string[]]$Expected)
    return (($Words -join "`n") -ceq ($Expected -join "`n"))
}

$yaatRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# The list every call shares, up to the inner run's own words.
function Get-ExpectedWordList {
    param([string]$Timeout = '900', [string[]]$Forwarded = @())
    $words = @(
        'run'
        '--cd'
        $yaatRoot
        '--log'
        '.tmp/test-all.log'
        '--timeout'
        $Timeout
        '--slot'
        'heavy'
        '--env'
        'YAAT_TEST_ALL_GATED=1'
        '--'
        'pwsh'
        'tools/test-all.ps1'
    )
    return $words + $Forwarded
}

function Add-WordsCase {
    param([string]$Name, [string[]]$Words, [string[]]$Expected)
    $detail = if (Test-WordsEqual $Words $Expected) {
        ''
    } else {
        "expected [$($Expected -join ' ')], got [$($Words -join ' ')]"
    }
    Add-CaseResult -Name $Name -Detail $detail
}

# A run already inside a gate runs directly: a marker alone says so, naming itself. A `none` slot
# marker takes no slot, so it is no gate to hide a nested heavy run from.
Clear-MarkerVariable
[Environment]::SetEnvironmentVariable('YAAT_TEST_ALL_GATED', 'set')
$found = Test-InsideGate
Add-CaseResult -Name 'inside on YAAT_TEST_ALL_GATED alone' -Detail $(if ($found -ceq 'YAAT_TEST_ALL_GATED') { '' } else { "Test-InsideGate returned '$found' instead" })
Clear-MarkerVariable
[Environment]::SetEnvironmentVariable('GATE_SLOT_HELD', 'heavy:1:2')
$found = Test-InsideGate
Add-CaseResult -Name 'inside on a heavy GATE_SLOT_HELD' -Detail $(if ($found -ceq 'GATE_SLOT_HELD') { '' } else { "Test-InsideGate returned '$found' instead" })
Clear-MarkerVariable
[Environment]::SetEnvironmentVariable('GATE_SLOT_HELD', 'light:1:2')
$found = Test-InsideGate
Add-CaseResult -Name 'inside on a light GATE_SLOT_HELD' -Detail $(if ($found -ceq 'GATE_SLOT_HELD') { '' } else { "Test-InsideGate returned '$found' instead" })
Clear-MarkerVariable
[Environment]::SetEnvironmentVariable('GATE_BOX_JOB', 'set')
$found = Test-InsideGate
Add-CaseResult -Name 'inside on GATE_BOX_JOB alone' -Detail $(if ($found -ceq 'GATE_BOX_JOB') { '' } else { "Test-InsideGate returned '$found' instead" })

# A no-slot marker holds nothing a heavy run could use, so the run wraps itself.
foreach ($marker in 'none:1:2', 'heavy', '') {
    Clear-MarkerVariable
    [Environment]::SetEnvironmentVariable('GATE_SLOT_HELD', $marker)
    $found = Test-InsideGate
    Add-CaseResult -Name "not inside with GATE_SLOT_HELD='$marker'" -Detail $(if ($null -eq $found) { '' } else { "Test-InsideGate returned '$found' instead" })
}

# No marker, and each marker emptied, is a bare run that must wrap.
Clear-MarkerVariable
$found = Test-InsideGate
$detail = if ($null -eq $found) { '' } else { "Test-InsideGate returned '$found' with no marker set" }
Add-CaseResult -Name 'not inside with no marker' -Detail $detail
foreach ($name in $markerNames) {
    Clear-MarkerVariable
    [Environment]::SetEnvironmentVariable($name, '')
    $found = Test-InsideGate
    $detail = if ($null -eq $found) { '' } else { "Test-InsideGate returned '$found' with $name empty" }
    Add-CaseResult -Name "not inside with $name empty" -Detail $detail
}

# The bare case, exactly as test-all.ps1 builds it: the checkout for --cd, nothing forwarded, and
# the command worded as callers spell it so the repo's routing patterns match.
Clear-MarkerVariable
[Environment]::SetEnvironmentVariable('YAAT_SERVER_DIR', $null)
Add-WordsCase -Name 'bare argument list' -Words @(Get-SelfGateArguments -YaatDir $yaatRoot) -Expected (Get-ExpectedWordList)

# Every parameter the caller bound reaches the child, and the two checkouts by name.
Add-WordsCase -Name '-Full' -Words @(Get-SelfGateArguments -YaatDir $yaatRoot -Full) -Expected (Get-ExpectedWordList -Timeout '2700' -Forwarded @('-Full'))
Add-WordsCase -Name '-BuildOnly' -Words @(Get-SelfGateArguments -YaatDir $yaatRoot -BuildOnly) -Expected (Get-ExpectedWordList -Forwarded @('-BuildOnly'))
Add-WordsCase -Name '-NoBuild' -Words @(Get-SelfGateArguments -YaatDir $yaatRoot -NoBuild) -Expected (Get-ExpectedWordList -Forwarded @('-NoBuild'))
Add-WordsCase -Name '-Config Debug' -Words @(Get-SelfGateArguments -YaatDir $yaatRoot -Config Debug) -Expected (Get-ExpectedWordList -Forwarded @('-Config', 'Debug'))
Add-WordsCase -Name 'a caller-given -YaatDir is forwarded' -Words @(Get-SelfGateArguments -YaatDir $yaatRoot -ForwardYaatDir) -Expected (Get-ExpectedWordList -Forwarded @('-YaatDir', $yaatRoot))

# A path with a space and a drive colon survives as one element.
$serverPath = 'C:\Some Dir\yaat-server'
Add-WordsCase -Name '-ServerDir keeps the path whole' -Words @(Get-SelfGateArguments -YaatDir $yaatRoot -ServerDir $serverPath) -Expected (Get-ExpectedWordList -Forwarded @('-ServerDir', $serverPath))

# A routed run carries no caller environment: test-all.ps1 resolves YAAT_SERVER_DIR and forwards
# the path as a word, so the call never names the variable.
[Environment]::SetEnvironmentVariable('YAAT_SERVER_DIR', $serverPath)
Add-WordsCase -Name 'YAAT_SERVER_DIR is never forwarded by name' -Words @(Get-SelfGateArguments -YaatDir $yaatRoot) -Expected (Get-ExpectedWordList)

Restore-CaseEnvironment

if ($failures.Count -gt 0) {
    Write-Host ''
    exit 1
}
Write-Host ''
Write-Host 'PASS: the self-gate test and argument list hold'
exit 0
