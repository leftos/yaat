<#
.SYNOPSIS
    Builds a speech-sandbox copy wired to sherpa-onnx's CUDA 13 build and cuDNN 9.

.DESCRIPTION
    Copies a Release build of tools/Yaat.SpeechSandbox to a scratch folder and overlays the CUDA
    sherpa-onnx native library with its onnxruntime, and cuDNN 9, so a --corpus-wer run can pass
    --device gpu: sherpa-onnx then loads the cuda execution provider instead of falling back to the CPU.

    The archives come from the sherpa-onnx v1.13.8 release and from PyPI's nvidia-cudnn-cu13 wheel, cached
    under %LOCALAPPDATA%\yaat\cache\stt-bakeoff\sherpa-onnx-cuda13 and checked against a pinned SHA-256 on
    every run. The CUDA 13 runtime itself (cudart64_13.dll) is not copied: it has to be on PATH, from the
    CUDA 13 toolkit.

    Nothing in the normal build output is touched, and the scratch copy is replaced on each run, so the
    CPU-only sandbox keeps its own DLLs.

.PARAMETER BuildOutput
    The sandbox build folder to copy. Defaults to tools\Yaat.SpeechSandbox\bin\Release\net10.0 in this
    checkout, which dotnet build tools/Yaat.SpeechSandbox -c Release produces.

.PARAMETER Destination
    Where the GPU copy is built. Defaults to <repo>\.tmp\sandbox-gpu, and must sit under the repo's .tmp
    folder, which the script empties first.

.EXAMPLE
    pwsh tools/Yaat.SpeechSandbox/New-SherpaGpuSandbox.ps1

    Creates .tmp\sandbox-gpu and prints its Yaat.SpeechSandbox.exe path.

.EXAMPLE
    pwsh tools/Yaat.SpeechSandbox/New-SherpaGpuSandbox.ps1 -Destination .tmp\sandbox-gpu-b

    Creates a second copy, for a CPU run and a GPU run side by side.
#>
[CmdletBinding()]
param(
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

function Assert-PinnedHash {
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

$repoRoot = [IO.Path]::GetFullPath((Join-Path -Path $PSScriptRoot -ChildPath '..\..'))
if ([string]::IsNullOrWhiteSpace($BuildOutput)) {
    $BuildOutput = Join-Path -Path $repoRoot -ChildPath 'tools\Yaat.SpeechSandbox\bin\Release\net10.0'
}
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path -Path $repoRoot -ChildPath '.tmp\sandbox-gpu'
}

$buildDir = [IO.Path]::GetFullPath($BuildOutput)
$sandboxExe = Join-Path -Path $buildDir -ChildPath 'Yaat.SpeechSandbox.exe'
if (-not (Test-Path -Path $sandboxExe)) {
    throw "no Yaat.SpeechSandbox.exe in $buildDir; build it with: dotnet build tools/Yaat.SpeechSandbox -c Release"
}

if (-not (Get-Command -Name 'cudart64_13.dll' -ErrorAction SilentlyContinue)) {
    throw 'CUDA 13 runtime (cudart64_13.dll) not on PATH; install the CUDA 13 toolkit'
}

$tmpRoot = [IO.Path]::GetFullPath((Join-Path -Path $repoRoot -ChildPath '.tmp'))
$destDir = [IO.Path]::GetFullPath($Destination)
if (-not $destDir.StartsWith($tmpRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "refusing to replace $destDir`: -Destination must be under $tmpRoot"
}

$cacheRoot = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'yaat\cache\stt-bakeoff\sherpa-onnx-cuda13'
$tarballName = 'sherpa-onnx-v1.13.8-cuda-13.x-cudnn-9.x-onnxruntime1.28.2-win-x64-cuda.tar.bz2'
$tarballUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.8/$tarballName"
$tarballSha256 = '1702e4a1ee68a07422469a7ec1d304a54b43a345ad888daf8e0b0257004a3273'
$wheelName = 'nvidia_cudnn_cu13-9.27.0.42-py3-none-win_amd64.whl'
$wheelSha256 = '7d96f634adafd55c72231eb0500ca77ab109ec8ebff7b33000b76e081bc4558e'
$cudnnReleaseUrl = 'https://pypi.org/pypi/nvidia-cudnn-cu13/9.27.0.42/json'
$tarballStem = [IO.Path]::GetFileNameWithoutExtension([IO.Path]::GetFileNameWithoutExtension($tarballName))

New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null

$tarballPath = Join-Path -Path $cacheRoot -ChildPath $tarballName
if (-not (Test-Path -Path $tarballPath)) {
    Invoke-Download -Url $tarballUrl -Path $tarballPath
}
Assert-PinnedHash -Path $tarballPath -Sha256 $tarballSha256

$wheelPath = Join-Path -Path $cacheRoot -ChildPath $wheelName
if (-not (Test-Path -Path $wheelPath)) {
    Invoke-Download -Url (Get-CudnnWheelUrl -ReleaseUrl $cudnnReleaseUrl -FileName $wheelName) -Path $wheelPath
}
Assert-PinnedHash -Path $wheelPath -Sha256 $wheelSha256

$sherpaRoot = Join-Path -Path $cacheRoot -ChildPath 'sherpa'
$sherpaLib = Join-Path -Path $sherpaRoot -ChildPath (Join-Path -Path $tarballStem -ChildPath 'lib')
if (-not (Test-Path -Path $sherpaRoot)) {
    New-Item -ItemType Directory -Path $sherpaRoot -Force | Out-Null
    Write-Verbose -Message "extracting $tarballName"
    & tar -xjf $tarballPath -C $sherpaRoot
    if ($LASTEXITCODE -ne 0) {
        throw "tar failed to extract $tarballPath (exit code $($LASTEXITCODE))"
    }
}
if (-not (Test-Path -Path $sherpaLib)) {
    throw "the sherpa-onnx CUDA archive has no lib folder at $sherpaLib"
}

$cudnnRoot = Join-Path -Path $cacheRoot -ChildPath 'cudnn'
$cudnnBin = Join-Path -Path $cudnnRoot -ChildPath 'nvidia\cudnn\bin'
if (-not (Test-Path -Path $cudnnRoot)) {
    New-Item -ItemType Directory -Path $cudnnRoot -Force | Out-Null
    Write-Verbose -Message "extracting $wheelName"
    Expand-Archive -Path $wheelPath -DestinationPath $cudnnRoot
}
if (-not (Test-Path -Path $cudnnBin)) {
    throw "the cuDNN wheel has no bin folder at $cudnnBin"
}

if (Test-Path -Path $destDir) {
    Write-Verbose -Message "replacing $destDir"
    Remove-Item -Path $destDir -Recurse -Force
}
Copy-Item -Path $buildDir -Destination $destDir -Recurse

$nativeDir = Join-Path -Path $destDir -ChildPath 'runtimes\win-x64\native'
New-Item -ItemType Directory -Path $nativeDir -Force | Out-Null
Copy-Item -Path (Join-Path -Path $sherpaLib -ChildPath '*.dll') -Destination $nativeDir -Force
Copy-Item -Path (Join-Path -Path $cudnnBin -ChildPath '*.dll') -Destination $destDir -Force

Write-Output -InputObject (Join-Path -Path $destDir -ChildPath 'Yaat.SpeechSandbox.exe')
