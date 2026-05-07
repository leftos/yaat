param(
    [string[]]$Idents = @(),
    [string[]]$Artccs = @(),
    [string[]]$Classes = @("B", "C"),
    [string]$Output = "src/Yaat.Sim/Data/Airspace/faa-training-primary-class-bc.geojson.br"
)

$ErrorActionPreference = "Stop"

$DefaultArtccs = @(
    "ZAB", "ZAU", "ZBW", "ZDC", "ZDV", "ZFW", "ZHU", "ZID",
    "ZJX", "ZKC", "ZLA", "ZLC", "ZMA", "ZME", "ZMP", "ZNY",
    "ZOA", "ZOB", "ZSE", "ZTL"
)

$TrainingBase = "https://data-api.vnas.vatsim.net/api/training"
$FaaClassAirspaceQuery = "https://services6.arcgis.com/ssFJjBXIUyZDrSYZ/arcgis/rest/services/Class_Airspace/FeatureServer/0/query"
$Headers = @{
    "Accept" = "application/json"
    "User-Agent" = "yaat-refresh-faa-airspace/1.0"
}

function Invoke-YaatJson {
    param([Parameter(Mandatory = $true)][string]$Uri)

    Invoke-RestMethod -Uri $Uri -Headers $Headers -TimeoutSec 30
}

function Normalize-AirportIdent {
    param([string]$Ident)

    if ([string]::IsNullOrWhiteSpace($Ident)) {
        return $null
    }

    $trimmed = $Ident.Trim().ToUpperInvariant()
    if ($trimmed.Length -eq 4 -and ($trimmed[0] -eq 'K' -or $trimmed[0] -eq 'P')) {
        return $trimmed.Substring(1)
    }

    return $trimmed
}

function Get-TrainingPrimaryAirports {
    param([string[]]$ArtccList)

    $airports = @{}
    foreach ($artcc in $ArtccList) {
        $upperArtcc = $artcc.Trim().ToUpperInvariant()
        if ([string]::IsNullOrWhiteSpace($upperArtcc)) {
            continue
        }

        Write-Host "Fetching scenario summaries for $upperArtcc..."
        $summaries = Invoke-YaatJson "$TrainingBase/scenario-summaries/by-artcc/$upperArtcc"
        $count = 0
        $artccAirports = @{}
        foreach ($summary in @($summaries)) {
            $count++
            if ($summary.primaryAirportId) {
                $normalized = Normalize-AirportIdent $summary.primaryAirportId
                if ($null -ne $normalized) {
                    $airports[$normalized] = $true
                    $artccAirports[$normalized] = $true
                }
                continue
            }

            $scenarioId = $summary.id
            if (!$scenarioId) {
                continue
            }

            $scenario = Invoke-YaatJson "$TrainingBase/scenarios/$scenarioId"
            $normalized = Normalize-AirportIdent $scenario.primaryAirportId
            if ($null -ne $normalized) {
                $airports[$normalized] = $true
                $artccAirports[$normalized] = $true
            }
        }

        $known = ($artccAirports.Keys | Sort-Object) -join ", "
        Write-Host "  ${upperArtcc}: $count scenarios processed; primary airports: $known"
    }

    @($airports.Keys | Sort-Object)
}

function Split-IntoChunks {
    param(
        [string[]]$Items,
        [int]$ChunkSize
    )

    for ($i = 0; $i -lt $Items.Count; $i += $ChunkSize) {
        $end = [Math]::Min($i + $ChunkSize - 1, $Items.Count - 1)
        ,$Items[$i..$end]
    }
}

function Quote-SqlLiteralList {
    param([string[]]$Items)

    ($Items | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ","
}

function Write-GeoJsonOutput {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Path
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $jsonText = $Value | ConvertTo-Json -Depth 100
    if ($Path.EndsWith(".br", [StringComparison]::OrdinalIgnoreCase)) {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($jsonText)
        $file = [System.IO.File]::Create($Path)
        try {
            $brotli = [System.IO.Compression.BrotliStream]::new($file, [System.IO.Compression.CompressionLevel]::SmallestSize)
            try {
                $brotli.Write($bytes, 0, $bytes.Length)
            }
            finally {
                $brotli.Dispose()
            }
        }
        finally {
            $file.Dispose()
        }

        return
    }

    $jsonText | Set-Content -Path $Path -Encoding UTF8
}

if ($Artccs.Count -eq 0) {
    $Artccs = $DefaultArtccs
}

if ($Idents.Count -eq 0) {
    $Idents = Get-TrainingPrimaryAirports $Artccs
}
else {
    $Idents = @($Idents | ForEach-Object { Normalize-AirportIdent $_ } | Where-Object { $null -ne $_ } | Sort-Object -Unique)
}

if ($Idents.Count -eq 0) {
    throw "No airport idents found."
}

$Classes = @($Classes | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ } | Sort-Object -Unique)
if ($Classes.Count -eq 0) {
    throw "No airspace classes requested."
}

Write-Host "Querying FAA class airspace for $($Idents.Count) airports: $($Idents -join ', ')"
Write-Host "Classes: $($Classes -join ', ')"

$fields = "OBJECTID,IDENT,ICAO_ID,NAME,CLASS,LOWER_VAL,LOWER_UOM,LOWER_CODE,LOWER_DESC,UPPER_VAL,UPPER_UOM,UPPER_CODE,UPPER_DESC"
$featuresByObjectId = @{}
$chunks = @(Split-IntoChunks -Items $Idents -ChunkSize 30)
foreach ($chunkObject in $chunks) {
    $chunk = @($chunkObject)
    $identList = Quote-SqlLiteralList $chunk
    $classList = Quote-SqlLiteralList $Classes
    $where = "IDENT in ($identList) AND CLASS in ($classList)"
    $query = @(
        "where=$([uri]::EscapeDataString($where))"
        "outFields=$([uri]::EscapeDataString($fields))"
        "returnGeometry=true"
        "outSR=4326"
        "f=geojson"
    ) -join "&"
    $uri = "$FaaClassAirspaceQuery`?$query"
    $json = Invoke-YaatJson $uri

    foreach ($feature in @($json.features)) {
        $objectId = $feature.properties.OBJECTID
        if ($null -ne $objectId) {
            $featuresByObjectId[[string]$objectId] = $feature
        }
    }

    Write-Host "  FAA chunk $($chunk[0])..$($chunk[-1]): $(@($json.features).Count) features"
}

$features = @($featuresByObjectId.Values | Sort-Object { $_.properties.IDENT }, { $_.properties.CLASS }, { $_.properties.OBJECTID })
$collection = [ordered]@{
    type = "FeatureCollection"
    name = "FAA class airspace for YAAT training primary airports"
    source = $FaaClassAirspaceQuery
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    artccs = @($Artccs | ForEach-Object { $_.Trim().ToUpperInvariant() } | Sort-Object -Unique)
    requestedIdents = $Idents
    requestedClasses = $Classes
    features = $features
}

Write-GeoJsonOutput -Value $collection -Path $Output

$matchedIdents = @($features | ForEach-Object { $_.properties.IDENT } | Sort-Object -Unique)
$missingIdents = @($Idents | Where-Object { $matchedIdents -notcontains $_ })
Write-Host "Wrote $($features.Count) FAA class airspace features to $Output"
Write-Host "Matched airports: $($matchedIdents -join ', ')"
if ($missingIdents.Count -gt 0) {
    Write-Host "No requested Class $($Classes -join '/') airspace found for: $($missingIdents -join ', ')"
}
