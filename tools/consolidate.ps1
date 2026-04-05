set-strictmode -version latest
$ErrorActionPreference = 'Stop'
dotnet run --project "$PSScriptRoot\Yaat.RecordingConsolidator" -- "$PSScriptRoot\..\tests\Yaat.Sim.Tests\TestData"
