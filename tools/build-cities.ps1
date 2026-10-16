# Writes src/LeafCalendar.Core/Views/Cities.tsv.gz: every city of 5,000 people or more with the IANA time zone it's
# in, biggest first, one per line as name, ASCII name (empty when the same), region, country, and zone, tab separated.
# Time zone boxes search it to suggest cities. Run it again to refresh the list from GeoNames.
# Source: GeoNames (https://www.geonames.org), CC BY 4.0, credited in src/LeafCalendar.App/Assets/ThirdPartyNotices.txt.
# Requires PowerShell 7 and an internet connection (or the GeoNames files already in -Data).
param(
    [string]$Data = (Join-Path ([IO.Path]::GetTempPath()) 'leaf-geonames')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'src/LeafCalendar.Core/Views/Cities.tsv.gz'

# GeoNames Files, Downloaded Once
New-Item -ItemType Directory -Force $Data | Out-Null
foreach ($file in 'cities5000.zip', 'admin1CodesASCII.txt', 'countryInfo.txt') {
    $path = Join-Path $Data $file
    if (-not (Test-Path $path)) {
        Invoke-WebRequest "https://download.geonames.org/export/dump/$file" -OutFile $path
    }
}
Expand-Archive (Join-Path $Data 'cities5000.zip') -DestinationPath $Data -Force

# Country Names By ISO Code ("US" -> "United States")
$countries = @{}
foreach ($line in Get-Content (Join-Path $Data 'countryInfo.txt') -Encoding utf8) {
    if ($line.StartsWith('#')) { continue }
    $f = $line.Split("`t")
    $countries[$f[0]] = $f[4]
}

# Region Names By Code ("US.TX" -> "Texas")
$regions = @{}
foreach ($line in Get-Content (Join-Path $Data 'admin1CodesASCII.txt') -Encoding utf8) {
    $f = $line.Split("`t")
    $regions[$f[0]] = $f[1]
}

# Populated Places (not neighborhoods, historical, abandoned, or destroyed ones), Biggest First
$skip = 'PPLX', 'PPLH', 'PPLQ', 'PPLW'
$rows = foreach ($line in Get-Content (Join-Path $Data 'cities5000.txt') -Encoding utf8) {
    $f = $line.Split("`t")
    if ($skip -contains $f[7] -or -not $f[17]) { continue }
    $name = $f[1].Trim()
    $ascii = $f[2].Trim()
    $region = $regions["$($f[8]).$($f[10])"]
    [pscustomobject]@{
        Population = [long]$f[14]
        Line       = @(
            $name
            $(if ($ascii -ceq $name) { '' } else { $ascii })
            $(if ($region -and $region -ne $name) { $region } else { '' })
            $countries[$f[8]]
            $f[17]
        ) -join "`t"
    }
}
$lines = $rows | Sort-Object Population -Descending | ForEach-Object Line

# Gzip, UTF-8, LF Line Ends
$file = [IO.File]::Create($out)
try {
    $zip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::SmallestSize)
    $writer = [IO.StreamWriter]::new($zip, [Text.UTF8Encoding]::new($false))
    $writer.NewLine = "`n"
    foreach ($line in $lines) { $writer.WriteLine($line) }
    $writer.Dispose()
}
finally {
    $file.Dispose()
}

"Wrote $($lines.Count) cities to $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"
