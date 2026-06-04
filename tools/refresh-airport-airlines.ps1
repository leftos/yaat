param(
    [string[]]$Idents = @(),
    [string[]]$Artccs = @(),
    [string[]]$BtsSegmentFiles = @(),
    [string]$Output = "src/Yaat.Sim/Data/airport-airlines.json.br",
    [string]$MetaOutput = "src/Yaat.Sim/Data/airport-airlines.meta",
    [int]$MinimumArrivals = 2,
    [switch]$SkipDownload
)

$ErrorActionPreference = "Stop"

$DefaultArtccs = @(
    "ZAB", "ZAU", "ZBW", "ZDC", "ZDV", "ZFW", "ZHU", "ZID",
    "ZJX", "ZKC", "ZLA", "ZLC", "ZMA", "ZME", "ZMP", "ZNY",
    "ZOA", "ZOB", "ZSE", "ZTL"
)

$TrainingBase = "https://data-api.vnas.vatsim.net/api/training"
$WorkDir = ".tmp/airport-airlines"
$OurAirportsUrl = "https://davidmegginson.github.io/ourairports-data/airports.csv"
$OpenFlightsAirlinesUrl = "https://raw.githubusercontent.com/jpatokal/openflights/master/data/airlines.dat"
$OpenFlightsRoutesUrl = "https://raw.githubusercontent.com/jpatokal/openflights/master/data/routes.dat"
$DomesticBtsUrl = "https://www.bts.gov/sites/bts.dot.gov/files/docs/airline-data/domestic-segments/DB28SEG.DD.WAC.202502.202601.REL01.07APR2026.zip"
$InternationalBtsUrl = "https://www.bts.gov/sites/bts.dot.gov/files/docs/airline-data/international-segments/DB28SEG.FD.WAC.202502.202601.REL01.07APR2026.zip"
$Headers = @{
    "Accept" = "application/json"
    "User-Agent" = "yaat-refresh-airport-airlines/1.0"
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

function Get-TrainingGeneratorAirports {
    param([string[]]$ArtccList)

    $airports = @{}
    foreach ($artcc in $ArtccList) {
        $upperArtcc = $artcc.Trim().ToUpperInvariant()
        if ([string]::IsNullOrWhiteSpace($upperArtcc)) {
            continue
        }

        Write-Host "Fetching scenario summaries for $upperArtcc..."
        $rawSummaries = Invoke-YaatJson "$TrainingBase/scenario-summaries/by-artcc/$upperArtcc"
        $summaries = @($rawSummaries)
        if ($summaries.Count -eq 1 -and $summaries[0] -is [array]) {
            $summaries = @($summaries[0])
        }
        $scenarioCount = 0
        $generatorScenarioCount = 0
        $artccAirports = @{}
        foreach ($summary in $summaries) {
            $scenarioCount++
            $scenarioId = $summary.id
            if (!$scenarioId) {
                continue
            }

            try {
                $scenario = Invoke-YaatJson "$TrainingBase/scenarios/$scenarioId"
            }
            catch {
                Write-Warning "  ${upperArtcc}: scenario $scenarioId could not be fetched: $($_.Exception.Message)"
                continue
            }
            if (@($scenario.aircraftGenerators).Count -eq 0) {
                continue
            }

            $normalized = Normalize-AirportIdent $scenario.primaryAirportId
            if ($null -ne $normalized) {
                $airports[$normalized] = $true
                $artccAirports[$normalized] = $true
                $generatorScenarioCount++
            }
        }

        $known = ($artccAirports.Keys | Sort-Object) -join ", "
        Write-Host "  ${upperArtcc}: $scenarioCount summaries processed; $generatorScenarioCount generator scenarios; airports: $known"
    }

    @($airports.Keys | Sort-Object)
}

function Save-TextDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if ((Test-Path $Path) -and ((Get-Item $Path).Length -gt 0)) {
        return
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    Write-Host "Downloading $Uri"
    Invoke-WebRequest -Uri $Uri -Headers $Headers -TimeoutSec 60 -OutFile $Path
}

function Save-BtsDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Referer
    )

    if ((Test-Path $Path) -and ((Get-Item $Path).Length -gt 0)) {
        return
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    Write-Host "Downloading BTS segment ZIP via Python: $Uri"
    $script = @"
import pathlib
import urllib.request

url = r'''$Uri'''
out = pathlib.Path(r'''$Path''')
out.parent.mkdir(parents=True, exist_ok=True)
headers = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
    'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
    'Accept-Language': 'en-US,en;q=0.9',
    'Referer': r'''$Referer''',
}
req = urllib.request.Request(url, headers=headers)
with urllib.request.urlopen(req, timeout=120) as response:
    out.write_bytes(response.read())
print(out)
"@

    $script | python -
}

function Read-CsvRows {
    param([Parameter(Mandatory = $true)][string]$Path)

    $reader = [System.IO.StreamReader]::new($Path)
    try {
        $header = $reader.ReadLine()
        if ($null -eq $header) {
            return @()
        }

        $columns = $header.Split(",")
        $rows = New-Object System.Collections.Generic.List[object]
        while (($line = $reader.ReadLine()) -ne $null) {
            $values = $line.Split(",")
            $row = @{}
            for ($i = 0; $i -lt $columns.Length -and $i -lt $values.Length; $i++) {
                $row[$columns[$i]] = $values[$i].Trim('"')
            }
            $rows.Add([pscustomobject]$row)
        }

        return $rows
    }
    finally {
        $reader.Dispose()
    }
}

function Test-AirlineNameConsistent {
    param([string]$A, [string]$B)

    if ([string]::IsNullOrWhiteSpace($A) -or [string]::IsNullOrWhiteSpace($B)) {
        return $false
    }

    $na = ($A -replace '[^a-zA-Z0-9]', '').ToLowerInvariant()
    $nb = ($B -replace '[^a-zA-Z0-9]', '').ToLowerInvariant()
    if ($na -eq $nb) {
        return $true
    }
    if ($na.Length -ge 4 -and $nb.Length -ge 4) {
        if ($na.StartsWith($nb) -or $nb.StartsWith($na) -or $na.Contains($nb) -or $nb.Contains($na)) {
            return $true
        }
    }

    return $false
}

function Read-OpenFlightsAirlines {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][hashtable]$FleetNames
    )

    # OpenFlights reuses 2-letter IATA codes across defunct and current carriers, and the file lists
    # multiple airlines per code. A naive last-wins map lets a defunct foreign carrier shadow the real one
    # (e.g. "8C" -> Shanxi Airlines [inactive] -> ICAO CXI, displayed under airline-fleets.json's Corendon;
    # "N3" -> Omskavia [inactive] -> OMS displayed as SalamAir). Two guards fix this:
    #   1. Trust an INACTIVE airline's mapping only when its name matches the curated fleet airline that
    #      would actually be displayed for that ICAO. This drops the mislabels and lets the correct
    #      inactive-but-real carrier win the code (8C -> ATN / Air Transport International).
    #   2. Prefer a mapping whose ICAO is a curated (displayable) fleet airline over one that is not, so a
    #      real US carrier keeps its code when that code is also an active foreign airline YAAT does not
    #      model (e.g. "EM" -> Empire Airlines, not the unmodeled active alternative). Among equally-curated
    #      candidates, last wins (file order); the curated overrides applied later always take precedence.
    $map = @{}
    $parser = [Microsoft.VisualBasic.FileIO.TextFieldParser]::new($Path)
    try {
        $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
        $parser.SetDelimiters(",")
        $parser.HasFieldsEnclosedInQuotes = $true
        while (!$parser.EndOfData) {
            $fields = $parser.ReadFields()
            if ($null -eq $fields -or $fields.Length -lt 6) {
                continue
            }

            $name = $fields[1]
            $iata = $fields[3]
            $icao = $fields[4]
            $activeFlag = if ($fields.Length -ge 8) { $fields[7] } else { "Y" }
            if ([string]::IsNullOrWhiteSpace($iata) -or $iata -eq "\N" -or [string]::IsNullOrWhiteSpace($icao) -or $icao -eq "\N") {
                continue
            }

            $iataU = $iata.Trim().ToUpperInvariant()
            $icaoU = $icao.Trim().ToUpperInvariant()
            $isActive = ($activeFlag.Trim().ToUpperInvariant() -eq "Y")

            if (-not $isActive -and -not (Test-AirlineNameConsistent $name $FleetNames[$icaoU])) {
                continue
            }

            if ($map.ContainsKey($iataU) -and $FleetNames.ContainsKey($map[$iataU]) -and -not $FleetNames.ContainsKey($icaoU)) {
                continue
            }

            $map[$iataU] = $icaoU
        }
    }
    finally {
        $parser.Dispose()
    }

    return $map
}

function Read-OurAirports {
    param([Parameter(Mandatory = $true)][string]$Path)

    $rows = Import-Csv -Path $Path
    $map = @{}
    foreach ($row in $rows) {
        if ([string]::IsNullOrWhiteSpace($row.iata_code)) {
            continue
        }

        $iata = $row.iata_code.Trim().ToUpperInvariant()
        $map[$iata] = [ordered]@{
            iata = $iata
            icao = ($row.icao_code ?? $row.ident ?? "").Trim().ToUpperInvariant()
            name = $row.name
        }
    }

    return $map
}

function Read-BtsSegmentRows {
    param([Parameter(Mandatory = $true)][string]$Path)

    $rows = New-Object System.Collections.Generic.List[object]
    $extension = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()

    if ($extension -eq ".zip") {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
        try {
            foreach ($entry in $zip.Entries) {
                if ($entry.Length -eq 0) {
                    continue
                }

                $stream = $entry.Open()
                $reader = [System.IO.StreamReader]::new($stream)
                try {
                    while (($line = $reader.ReadLine()) -ne $null) {
                        Add-BtsLine -Line $line -Rows $rows
                    }
                }
                finally {
                    $reader.Dispose()
                    $stream.Dispose()
                }
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    else {
        $reader = [System.IO.StreamReader]::new($Path)
        try {
            while (($line = $reader.ReadLine()) -ne $null) {
                Add-BtsLine -Line $line -Rows $rows
            }
        }
        finally {
            $reader.Dispose()
        }
    }

    return $rows
}

function Add-BtsLine {
    param(
        [Parameter(Mandatory = $true)][string]$Line,
        [Parameter(Mandatory = $true)]$Rows
    )

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return
    }

    $parts = $Line.Split("|")
    if ($parts.Length -lt 28) {
        return
    }

    $year = 0
    $month = 0
    $departuresScheduled = 0
    $departuresPerformed = 0
    $passengers = 0
    [void][int]::TryParse($parts[0], [ref]$year)
    [void][int]::TryParse($parts[1], [ref]$month)
    [void][int]::TryParse($parts[18], [ref]$departuresScheduled)
    [void][int]::TryParse($parts[19], [ref]$departuresPerformed)
    [void][int]::TryParse($parts[22], [ref]$passengers)

    $Rows.Add(
        [pscustomobject]@{
            Year = $year
            Month = $month
            Origin = $parts[2].Trim().ToUpperInvariant()
            Destination = $parts[6].Trim().ToUpperInvariant()
            Carrier = $parts[10].Trim().ToUpperInvariant()
            AircraftType = $parts[16].Trim()
            DeparturesPerformed = [Math]::Max($departuresPerformed, $departuresScheduled)
            Passengers = $passengers
        }
    )
}

function Get-CarrierOverrides {
    @{
        "AA" = "AAL"
        "AS" = "ASA"
        "B6" = "JBU"
        "DL" = "DAL"
        "F9" = "FFT"
        "G4" = "AAY"
        "HA" = "HAL"
        "NK" = "NKS"
        "UA" = "UAL"
        "WN" = "SWA"
        "SY" = "SCX"
        "5X" = "UPS"
        "FX" = "FDX"
        "OO" = "SKW"
        "MQ" = "ENY"
        "OH" = "JIA"
        "9E" = "EDV"
        "YX" = "RPA"
        "YV" = "ASH"
        "C5" = "UCA"
        "QX" = "QXE"
        "G7" = "GJS"
        "ZW" = "AWI"
        "PT" = "PDT"
        "CP" = "CPZ"
    }
}

function Build-OpenFlightsRouteBootstrap {
    param(
        [Parameter(Mandatory = $true)][string]$RoutesPath,
        [Parameter(Mandatory = $true)]$CarrierMap,
        [Parameter(Mandatory = $true)][string[]]$TargetAirports,
        [Parameter(Mandatory = $true)]$KnownFleetAirlines
    )

    $targetSet = @{}
    foreach ($airport in $TargetAirports) {
        $targetSet[$airport] = $true
    }

    $bootstrap = @{}
    foreach ($airport in $TargetAirports) {
        $bootstrap[$airport] = @{}
    }

    $parser = [Microsoft.VisualBasic.FileIO.TextFieldParser]::new($RoutesPath)
    try {
        $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
        $parser.SetDelimiters(",")
        $parser.HasFieldsEnclosedInQuotes = $true
        while (!$parser.EndOfData) {
            $fields = $parser.ReadFields()
            if ($null -eq $fields -or $fields.Length -lt 6) {
                continue
            }

            $airlineIata = $fields[0].Trim().ToUpperInvariant()
            $dest = $fields[4].Trim().ToUpperInvariant()
            if (!$targetSet.ContainsKey($dest)) {
                continue
            }

            $icao = $CarrierMap[$airlineIata]
            if ($null -eq $icao -or !$KnownFleetAirlines.ContainsKey($icao)) {
                continue
            }

            $bootstrap[$dest][$icao] = $true
        }
    }
    finally {
        $parser.Dispose()
    }

    return $bootstrap
}

function Write-JsonOutput {
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
    $Idents = Get-TrainingGeneratorAirports $Artccs
}
else {
    $Idents = @($Idents | ForEach-Object { Normalize-AirportIdent $_ } | Where-Object { $null -ne $_ } | Sort-Object -Unique)
}

if ($Idents.Count -eq 0) {
    throw "No airport idents found."
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$ourAirportsPath = Join-Path $WorkDir "airports.csv"
$openFlightsAirlinesPath = Join-Path $WorkDir "openflights-airlines.dat"
$openFlightsRoutesPath = Join-Path $WorkDir "openflights-routes.dat"

if (!$SkipDownload) {
    Save-TextDownload -Uri $OurAirportsUrl -Path $ourAirportsPath
    Save-TextDownload -Uri $OpenFlightsAirlinesUrl -Path $openFlightsAirlinesPath
    Save-TextDownload -Uri $OpenFlightsRoutesUrl -Path $openFlightsRoutesPath

    if ($BtsSegmentFiles.Count -eq 0) {
        $domesticPath = Join-Path $WorkDir "DB28SEG.DD.WAC.202502.202601.REL01.07APR2026.zip"
        $internationalPath = Join-Path $WorkDir "DB28SEG.FD.WAC.202502.202601.REL01.07APR2026.zip"
        Save-BtsDownload -Uri $DomesticBtsUrl -Path $domesticPath -Referer "https://www.bts.gov/browse-statistical-products-and-data/bts-publications/data-bank-28ds-t-100-domestic-segment-data"
        Save-BtsDownload -Uri $InternationalBtsUrl -Path $internationalPath -Referer "https://www.bts.gov/browse-statistical-products-and-data/bts-publications/%E2%80%A2-data-bank-28is-t-100-and-t-100f"
        $BtsSegmentFiles = @($domesticPath, $internationalPath)
    }
}

if (!(Test-Path $ourAirportsPath)) {
    throw "Missing $ourAirportsPath. Run without -SkipDownload first."
}
if (!(Test-Path $openFlightsAirlinesPath)) {
    throw "Missing $openFlightsAirlinesPath. Run without -SkipDownload first."
}
if (!(Test-Path $openFlightsRoutesPath)) {
    throw "Missing $openFlightsRoutesPath. Run without -SkipDownload first."
}
if ($BtsSegmentFiles.Count -eq 0) {
    throw "No BTS segment files available. Supply -BtsSegmentFiles or run without -SkipDownload."
}

$airportInfo = Read-OurAirports $ourAirportsPath

$fleetJson = Get-Content -Path "src/Yaat.Sim/Data/airline-fleets.json" -Raw | ConvertFrom-Json
$knownFleetAirlines = @{}
$fleetNames = @{}
foreach ($property in $fleetJson.by_airline.PSObject.Properties) {
    $icaoKey = $property.Name.ToUpperInvariant()
    $knownFleetAirlines[$icaoKey] = $true
    $fleetNames[$icaoKey] = $property.Value.name
}

$carrierMap = Read-OpenFlightsAirlines -Path $openFlightsAirlinesPath -FleetNames $fleetNames
$overrides = Get-CarrierOverrides
foreach ($key in $overrides.Keys) {
    $carrierMap[$key] = $overrides[$key]
}

$targetSet = @{}
foreach ($airport in $Idents) {
    $targetSet[$airport] = $true
}

$aggregate = @{}
$monthsByAirportAirline = @{}
$periods = New-Object System.Collections.Generic.List[string]
$unmappedCarriers = @{}
$sourceRecords = @()
foreach ($file in $BtsSegmentFiles) {
    if (!(Test-Path $file)) {
        throw "BTS segment file not found: $file"
    }

    Write-Host "Reading BTS segment file $file"
    $rows = Read-BtsSegmentRows $file
    $sourceRecords += [ordered]@{ path = $file; rows = $rows.Count }
    foreach ($row in $rows) {
        if (!$targetSet.ContainsKey($row.Destination)) {
            continue
        }

        if ($row.DeparturesPerformed -le 0) {
            continue
        }

        $icao = $carrierMap[$row.Carrier]
        if ($null -eq $icao) {
            $unmappedCarriers[$row.Carrier] = $true
            continue
        }

        if (!$knownFleetAirlines.ContainsKey($icao)) {
            continue
        }

        $airport = $row.Destination
        $key = "$airport|$icao"
        if (!$aggregate.ContainsKey($key)) {
            $aggregate[$key] = 0
            $monthsByAirportAirline[$key] = @{}
        }

        $aggregate[$key] += $row.DeparturesPerformed
        $period = "{0:D4}-{1:D2}" -f $row.Year, $row.Month
        $monthsByAirportAirline[$key][$period] = $true
        $periods.Add($period)
    }
}

$bootstrap = Build-OpenFlightsRouteBootstrap -RoutesPath $openFlightsRoutesPath -CarrierMap $carrierMap -TargetAirports $Idents -KnownFleetAirlines $knownFleetAirlines
$airportsOut = [ordered]@{}
$btsAirportCount = 0
foreach ($airport in $Idents) {
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($key in ($aggregate.Keys | Where-Object { $_.StartsWith("$airport|", [StringComparison]::OrdinalIgnoreCase) })) {
        $icao = $key.Split("|")[1]
        $arrivals = [int]$aggregate[$key]
        if ($arrivals -lt $MinimumArrivals) {
            continue
        }

        $months = @($monthsByAirportAirline[$key].Keys).Count
        $confidence = if ($months -ge 3 -and $arrivals -ge 12) { "regular" } elseif ($months -ge 2) { "seasonal" } else { "observed" }
        $entries.Add(
            [ordered]@{
                icao = $icao
                arrivals = $arrivals
                months = $months
                confidence = $confidence
            }
        )
    }

    if ($entries.Count -gt 0) {
        $btsAirportCount++
    }

    if ($entries.Count -eq 0) {
        foreach ($icao in @($bootstrap[$airport].Keys | Sort-Object)) {
            $entries.Add(
                [ordered]@{
                    icao = $icao
                    arrivals = 1
                    months = 0
                    confidence = "route-backfill"
                }
            )
        }
    }

    $sortedEntries = @($entries | Sort-Object -Property @{ Expression = { $_["arrivals"] }; Descending = $true }, @{ Expression = { $_["icao"] } })
    $info = $airportInfo[$airport]
    $airportsOut[$airport] = [ordered]@{
        iata = $airport
        icao = if ($null -ne $info) { $info.icao } else { "" }
        name = if ($null -ne $info) { $info.name } else { "" }
        airlines = $sortedEntries
    }
}

$periodValues = @($periods | Sort-Object -Unique)
$outputObject = [ordered]@{
    metadata = [ordered]@{
        source = "BTS T-100 domestic/international segment rolling 12-month data; OpenFlights routes.dat backfill for target airports without BTS carrier hits"
        period_start = if ($periodValues.Count -gt 0) { $periodValues[0] } else { "" }
        period_end = if ($periodValues.Count -gt 0) { $periodValues[-1] } else { "" }
        generated_utc = (Get-Date).ToUniversalTime().ToString("O")
        tool = "tools/refresh-airport-airlines.ps1"
        target_airports_count = $Idents.Count
        bts_airports_with_data = $btsAirportCount
        minimum_arrivals = $MinimumArrivals
    }
    airports = $airportsOut
}

Write-JsonOutput -Value $outputObject -Path $Output

$meta = [ordered]@{
    generated_utc = (Get-Date).ToUniversalTime().ToString("O")
    output = $Output
    source_records = $sourceRecords
    target_airports = $Idents
    unmapped_bts_carriers = @($unmappedCarriers.Keys | Sort-Object)
}

Write-JsonOutput -Value $meta -Path $MetaOutput

Write-Host "Wrote airport-airline fixture for $($Idents.Count) airports to $Output"
Write-Host "BTS data present for $btsAirportCount target airports; OpenFlights route backfill filled any remaining airport-airline gaps."
