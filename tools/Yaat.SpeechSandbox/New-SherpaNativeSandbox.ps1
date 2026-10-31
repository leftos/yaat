<#
.SYNOPSIS
    Builds a speech-sandbox copy with YAAT's own sherpa-onnx native overlaid, CPU or CUDA.

.DESCRIPTION
    Copies a Release build of tools/Yaat.SpeechSandbox to a scratch folder and overlays YAAT's own
    sherpa-onnx native libraries, so the sandbox runs the ABI its managed org.k2fsa.sherpa.onnx 2.0.1
    wrapper was built against instead of the DLLs the NuGet package ships.

    The native libraries come from %LOCALAPPDATA%\yaat\cache\sherpa-native\<NativeId>-<Variant>, where the
    sherpa-onnx build stages them with a MANIFEST.txt (the commit it was built from and every DLL's
    SHA-256). Every DLL copied is checked against that hash on each run; the gtest DLLs a test build leaves
    beside them are not copied.

    The cuda variant also overlays cuDNN 9, from PyPI's nvidia-cudnn-cu13 wheel cached under
    %LOCALAPPDATA%\yaat\cache\stt-bakeoff\sherpa-onnx-cuda13 and checked against a pinned SHA-256 on every
    run, so a --corpus-wer run can pass --device gpu: sherpa-onnx then loads the cuda execution provider
    instead of falling back to the CPU. The CUDA 13 runtime itself (cudart64_13.dll) is not copied: it has
    to be on PATH, from the CUDA 13 toolkit, and the script refuses to run without it.

    Nothing in the normal build output is touched, and the scratch copy is replaced on each run, so the
    CPU-only sandbox keeps its own DLLs.

.PARAMETER Variant
    cpu for the CPU native, cuda for the CUDA 13 native plus cuDNN 9.

.PARAMETER NativeId
    The commit prefix of the staged native folder to overlay, as named under
    %LOCALAPPDATA%\yaat\cache\sherpa-native. Defaults to edad9a7fe1, the commit these natives were built
    from.

.PARAMETER BuildOutput
    The sandbox build folder to copy. Defaults to tools\Yaat.SpeechSandbox\bin\Release\net10.0 in this
    checkout, which dotnet build tools/Yaat.SpeechSandbox -c Release produces.

.PARAMETER Destination
    Where the copy is built. Defaults to <repo>\.tmp\sandbox-native-<variant>, and must sit under the repo's
    .tmp folder, which the script empties first.

.EXAMPLE
    pwsh tools/Yaat.SpeechSandbox/New-SherpaNativeSandbox.ps1 -Variant cpu

    Creates .tmp\sandbox-native-cpu and prints its Yaat.SpeechSandbox.exe path.

.EXAMPLE
    pwsh tools/Yaat.SpeechSandbox/New-SherpaNativeSandbox.ps1 -Variant cuda -Destination .tmp\sandbox-native-cuda-b

    Creates a second CUDA copy, for a CPU run and a GPU run side by side.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('cpu', 'cuda')][string] $Variant,
    [string] $NativeId = 'edad9a7fe1',
    [string] $BuildOutput,
    [string] $Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Download {
    param(
        [Parameter(Mandatory = $true)][string] $Url,
        [Parameter(Mandatory = $true)][string] $Path
    )

    Write-Verbose -Message "downloading $Url"
    & curl.exe -L --fail --progress-bar --output $Path $Url
    if ($LASTEXITCODE -ne 0) {
        throw "download failed with exit code $($LASTEXITCODE): $Url"
    }
}

function Test-PinnedHash {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Sha256
    )

    $actual = (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) {
        throw "SHA-256 mismatch for $Path`: expected $Sha256, got $actual"
    }
}

function Get-CudnnWheelUrl {
    param(
        [Parameter(Mandatory = $true)][string] $ReleaseUrl,
        [Parameter(Mandatory = $true)][string] $FileName
    )

    $release = Invoke-RestMethod -Uri $ReleaseUrl
    $match = $release.urls | Where-Object -Property 'filename' -EQ -Value $FileName | Select-Object -First 1
    if ($null -eq $match) {
        throw "PyPI lists no $FileName under $ReleaseUrl"
    }

    return $match.url
}

function Get-NativeManifestEntry {
    param(
        [Parameter(Mandatory = $true)][string] $Path
    )

    if (-not (Test-Path -Path $Path)) {
        throw "no native manifest at $Path; stage the sherpa-onnx build there first"
    }

    $inTable = $false
    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($line in (Get-Content -Path $Path)) {
        $trimmed = $line.Trim()
        if (-not $inTable) {
            $inTable = $trimmed -eq 'DLL SHA-256:'
            continue
        }
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        $parts = $trimmed -split '\s+', 2
        if (($parts.Count -ne 2) -or ($parts[0] -notmatch '^[0-9a-fA-F]{64}$')) {
            throw "unreadable manifest line in $Path`: $trimmed"
        }

        $entries.Add(
            [pscustomobject]@{
                Name = $parts[1]
                Sha256 = $parts[0].ToLowerInvariant()
            }
        )
    }

    if ($entries.Count -eq 0) {
        throw "no DLL listed under 'DLL SHA-256:' in $Path"
    }

    return $entries
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path -Path $PSScriptRoot -ChildPath '..\..'))
if ([string]::IsNullOrWhiteSpace($BuildOutput)) {
    $BuildOutput = Join-Path -Path $repoRoot -ChildPath 'tools\Yaat.SpeechSandbox\bin\Release\net10.0'
}
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path -Path $repoRoot -ChildPath ".tmp\sandbox-native-$Variant"
}

$buildDir = [IO.Path]::GetFullPath($BuildOutput)
$sandboxExe = Join-Path -Path $buildDir -ChildPath 'Yaat.SpeechSandbox.exe'
if (-not (Test-Path -Path $sandboxExe)) {
    throw "no Yaat.SpeechSandbox.exe in $buildDir; build it with: dotnet build tools/Yaat.SpeechSandbox -c Release"
}

$nativeRoot = Join-Path -Path $env:LOCALAPPDATA -ChildPath "yaat\cache\sherpa-native\$NativeId-$Variant"
$manifestPath = Join-Path -Path $nativeRoot -ChildPath 'MANIFEST.txt'
$nativeDlls = @(Get-NativeManifestEntry -Path $manifestPath | Where-Object -FilterScript { $_.Name -notlike 'gtest*.dll' })
if ($nativeDlls.Count -eq 0) {
    throw "MANIFEST.txt in $nativeRoot lists no DLL to overlay"
}

$tmpRoot = [IO.Path]::GetFullPath((Join-Path -Path $repoRoot -ChildPath '.tmp'))
$destDir = [IO.Path]::GetFullPath($Destination)
if (-not $destDir.StartsWith($tmpRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "refusing to replace $destDir`: -Destination must be under $tmpRoot"
}

$cudnnBin = $null
if ($Variant -eq 'cuda') {
    if (-not (Get-Command -Name 'cudart64_13.dll' -ErrorAction SilentlyContinue)) {
        throw 'CUDA 13 runtime (cudart64_13.dll) not on PATH; install the CUDA 13 toolkit'
    }

    $cacheRoot = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'yaat\cache\stt-bakeoff\sherpa-onnx-cuda13'
    $wheelName = 'nvidia_cudnn_cu13-9.27.0.42-py3-none-win_amd64.whl'
    $wheelSha256 = '7d96f634adafd55c72231eb0500ca77ab109ec8ebff7b33000b76e081bc4558e'
    $cudnnReleaseUrl = 'https://pypi.org/pypi/nvidia-cudnn-cu13/9.27.0.42/json'

    New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null

    $wheelPath = Join-Path -Path $cacheRoot -ChildPath $wheelName
    if (-not (Test-Path -Path $wheelPath)) {
        Invoke-Download -Url (Get-CudnnWheelUrl -ReleaseUrl $cudnnReleaseUrl -FileName $wheelName) -Path $wheelPath
    }
    Test-PinnedHash -Path $wheelPath -Sha256 $wheelSha256

    $cudnnRoot = Join-Path -Path $cacheRoot -ChildPath 'cudnn'
    if (-not (Test-Path -Path $cudnnRoot)) {
        New-Item -ItemType Directory -Path $cudnnRoot -Force | Out-Null
        Write-Verbose -Message "extracting $wheelName"
        Expand-Archive -Path $wheelPath -DestinationPath $cudnnRoot
    }

    $cudnnBin = Join-Path -Path $cudnnRoot -ChildPath 'nvidia\cudnn\bin'
    if (-not (Test-Path -Path $cudnnBin)) {
        throw "the cuDNN wheel has no bin folder at $cudnnBin"
    }
}

if (Test-Path -Path $destDir) {
    Write-Verbose -Message "replacing $destDir"
    Remove-Item -Path $destDir -Recurse -Force
}
Copy-Item -Path $buildDir -Destination $destDir -Recurse

$nativeDir = Join-Path -Path $destDir -ChildPath 'runtimes\win-x64\native'
New-Item -ItemType Directory -Path $nativeDir -Force | Out-Null
foreach ($dll in $nativeDlls) {
    $source = Join-Path -Path $nativeRoot -ChildPath $dll.Name
    if (-not (Test-Path -Path $source)) {
        throw "no $($dll.Name) in $nativeRoot; MANIFEST.txt lists SHA-256 $($dll.Sha256)"
    }
    Test-PinnedHash -Path $source -Sha256 $dll.Sha256
    Copy-Item -Path $source -Destination $nativeDir -Force
}

if ($null -ne $cudnnBin) {
    Copy-Item -Path (Join-Path -Path $cudnnBin -ChildPath '*.dll') -Destination $destDir -Force
}

Write-Output -InputObject (Join-Path -Path $destDir -ChildPath 'Yaat.SpeechSandbox.exe')
