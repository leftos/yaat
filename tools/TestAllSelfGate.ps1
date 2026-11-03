#!/usr/bin/env pwsh
# The self-wrap's decision and its argument list, dot-sourced by tools/test-all.ps1.
#
# test-all.ps1 re-invokes itself once under `tools/gate.ps1 run` (heavy), so one gate call carries
# the whole run -- both builds, both suites, the stall watchdog and its dumps -- and the gate routes
# it to a builder where one is configured, or takes a local slot where none is. A run already
# started by a gate runs directly: the marker the wrap sets, a heavy, light or critical slot marker,
# and the job name a routed run's box gives its command all say so. A `none` slot marker takes no
# slot, so it is no gate to hide a heavy run from and does not count.
#
# Nothing here runs on dot-source: test-all.ps1 calls the two functions itself.

function Test-InsideGate {
    # Returns the name of the variable that says this process is a gated command, or $null when the
    # run started bare and has to wrap itself.
    if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('YAAT_TEST_ALL_GATED'))) {
        return 'YAAT_TEST_ALL_GATED'
    }
    $slot = [Environment]::GetEnvironmentVariable('GATE_SLOT_HELD')
    if ($slot -match '^(heavy|light|critical):') {
        return 'GATE_SLOT_HELD'
    }
    if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('GATE_BOX_JOB'))) {
        return 'GATE_BOX_JOB'
    }
    return $null
}

function Get-SelfGateArguments {
    # The full word list for the `pwsh ... tools/gate.ps1 run ...` call that carries this run.
    # A parameter the caller bound is forwarded to the inner run; one it left out is not, so the
    # inner run resolves its own default and a bare call sends the bare command word the repo's
    # routing patterns match. The command travels as `pwsh tools/test-all.ps1`, never with
    # `-NoProfile -File`. Both checkouts travel as words, resolved, because a routed run does not
    # carry the caller's environment and the router rewrites words, not `--env` values.
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '',
        Justification = 'The plural is the point: this returns the whole argument array of one gate call, and Arguments is the name the brief, test-all.ps1 and its case script use.')]
    param(
        [Parameter(Mandatory)]
        [string]$YaatDir,

        [string]$Config,
        [string]$ServerDir,

        # Set when the caller named the yaat checkout, so the inner run reads the same one rather
        # than its own default: $YaatDir is already this run's checkout either way.
        [switch]$ForwardYaatDir,

        [switch]$Full,
        [switch]$BuildOnly,
        [switch]$NoBuild
    )

    $gateArguments = @('run', '--cd', $YaatDir, '--log', '.tmp/test-all.log')
    $gateArguments += '--timeout', $(if ($Full) { '2700' } else { '900' })
    $gateArguments += '--slot', 'heavy'
    $gateArguments += '--env', 'YAAT_TEST_ALL_GATED=1'
    $gateArguments += '--', 'pwsh', 'tools/test-all.ps1'

    if ($PSBoundParameters.ContainsKey('Config') -and $Config) {
        $gateArguments += '-Config', $Config
    }
    if ($ForwardYaatDir) {
        $gateArguments += '-YaatDir', $YaatDir
    }
    if ($PSBoundParameters.ContainsKey('ServerDir') -and $ServerDir) {
        $gateArguments += '-ServerDir', $ServerDir
    }
    foreach ($switch in 'Full', 'BuildOnly', 'NoBuild') {
        if ($PSBoundParameters.ContainsKey($switch) -and $PSBoundParameters[$switch]) {
            $gateArguments += "-$switch"
        }
    }
    return $gateArguments
}
