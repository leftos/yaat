#!/usr/bin/env pwsh
# Case table for tools/TestAllWatchdog.ps1. Every wait is on a process handle with a hang guard;
# no case reads the wall clock.
#
# ManagedStackCaptureTests takes a ClrMD snapshot of its own host (DataTarget.CreateSnapshotAndAttach,
# PssCaptureSnapshot with PSS_CAPTURE_VA_CLONE). The VA clone is a child of the host with the host's
# image name, no threads and no CPU. It never records an exit time or exit code, so once ClrMD kills it
# the gate, which judges a member ended by its exit time, holds its handle until the run ends and the
# clone stays listed. Find-TestHost must not offer a clone, live or killed, to Test-StalledHost, whose
# 90 s idle clock would call it STALLED.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'The case results are the script''s console output, as in test-all.ps1.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '',
    Justification = 'New-VaClone only snapshots this case''s own child process, which the script cleans up.')]
param([string]$WatchdogScript = (Join-Path $PSScriptRoot 'TestAllWatchdog.ps1'))
$ErrorActionPreference = 'Stop'
. $WatchdogScript

Add-Type -Namespace WatchdogCase -Name Pss -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern int PssCaptureSnapshot(System.IntPtr process, int flags, int contextFlags, out System.IntPtr snapshot);
[DllImport("kernel32.dll")] public static extern int PssQuerySnapshot(System.IntPtr snapshot, int infoClass, out System.IntPtr buffer, int length);
[DllImport("kernel32.dll")] public static extern int PssFreeSnapshot(System.IntPtr process, System.IntPtr snapshot);
[DllImport("kernel32.dll")] public static extern int GetProcessId(System.IntPtr process);
[DllImport("kernel32.dll")] public static extern System.IntPtr GetCurrentProcess();
'@

$hangGuardMs = 600000
$folder = Join-Path ([IO.Path]::GetTempPath()) ("watchdog-case-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
$fakeHost = Join-Path $folder 'Fake.Tests.exe'
Copy-Item (Join-Path $env:SystemRoot 'System32\cmd.exe') $fakeHost

# A VA clone of $target as ClrMD takes it (PSS_CAPTURE_VA_CLONE = 1, PSS_QUERY_VA_CLONE_INFORMATION = 1),
# with its snapshot handle to free and a Process for the clone that holds the clone's handle open.
function New-VaClone([System.Diagnostics.Process]$target) {
    $snapshot = [IntPtr]::Zero
    $status = [WatchdogCase.Pss]::PssCaptureSnapshot($target.Handle, 1, 0x10001F, [ref]$snapshot)
    if ($status -ne 0) { throw "PssCaptureSnapshot of pid $($target.Id) failed: $status" }
    $cloneHandle = [IntPtr]::Zero
    $status = [WatchdogCase.Pss]::PssQuerySnapshot($snapshot, 1, [ref]$cloneHandle, [IntPtr]::Size)
    if ($status -ne 0) { throw "PssQuerySnapshot failed: $status" }
    $clone = [System.Diagnostics.Process]::GetProcessById([WatchdogCase.Pss]::GetProcessId($cloneHandle))
    $null = $clone.Handle
    return [pscustomobject]@{ Snapshot = $snapshot; Process = $clone }
}

$failures = @()
$psi = [System.Diagnostics.ProcessStartInfo]::new($fakeHost, '/c pause')
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$live = [System.Diagnostics.Process]::Start($psi)
$clones = @()
try {
    $liveClone = New-VaClone $live
    $killedClone = New-VaClone $live
    $clones = @($liveClone, $killedClone)
    # ClrMD's Dispose frees the snapshot and kills the clone; this case's own handle stands in for the gate's.
    [void][WatchdogCase.Pss]::PssFreeSnapshot([WatchdogCase.Pss]::GetCurrentProcess(), $killedClone.Snapshot)
    $killedClone.Process.Kill()
    if (-not $killedClone.Process.WaitForExit($hangGuardMs)) { throw "hung: clone pid $($killedClone.Process.Id) did not end" }

    $found = @(Find-TestHost -RootPid $PID -HostNamePattern '\.Tests$|^testhost' -ProcessNamePattern '^pwsh$|^dotnet$|^testhost|\.Tests$')
    $foundIds = @($found | ForEach-Object Id)

    if ($foundIds -notcontains $live.Id) {
        $failures += "a live host is a host: pid $($live.Id) not found (found: $($foundIds -join ', '))"
    }
    if ($foundIds -contains $liveClone.Process.Id) {
        $failures += "a live VA clone is no host: pid $($liveClone.Process.Id) was offered to the idle check"
    }
    if ($foundIds -contains $killedClone.Process.Id) {
        $failures += "a killed VA clone still listed is no host: pid $($killedClone.Process.Id) was offered to the idle check"
    }
} finally {
    if ($clones.Count -gt 0) {
        [void][WatchdogCase.Pss]::PssFreeSnapshot([WatchdogCase.Pss]::GetCurrentProcess(), $clones[0].Snapshot)
        try { $clones[0].Process.Kill() } catch { Write-Host "note: live clone kill: $($_.Exception.Message)" }
        foreach ($c in $clones) { $c.Process.Dispose() }
    }
    $live.StandardInput.Close()
    if (-not $live.WaitForExit($hangGuardMs)) { $live.Kill() }
    $live.Dispose()
    Remove-Item -Recurse -Force $folder
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host 'PASS: Find-TestHost offers live hosts only, never a VA clone'
exit 0
