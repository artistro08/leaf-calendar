# Prints the size of Leaf's MSIX and fails when it carries Windows App SDK AI/ML/Search/Widgets
# files or is over budget. Used after tools/publish-aot.ps1 (locally and in the manual CI job).
# Requires PowerShell 7 on Windows.
# Measured 2026-10-01: 30.6 MB (was 55.9 MB with the AI libraries); budget is that rounded up to the next 5.
param(
    [string]$Msix,
    [int]$MaxMb = 35
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# Newest MSIX Under AppPackages Unless One Was Given
if (-not $Msix) {
    $Msix = (Get-ChildItem (Join-Path $root 'src/LeafCalendar.App/AppPackages') -Recurse -Filter '*.msix' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $Msix) { throw 'No .msix found. Run tools/publish-aot.ps1 first.' }

# Forbidden Payload (Leaf has no AI features; spec 1.4 and 13)
$forbidden = @('onnxruntime.dll', 'DirectML.dll', 'NPUDetect.dll', 'PerceptiveStreaming.dll',
    'Microsoft.Windows.Widgets.dll', 'Microsoft.Windows.Search.dll',
    'Microsoft.Asg.SemanticIndex.*', 'Microsoft.Windows.AI.*', 'Microsoft.Windows.Workloads*', 'workloads*.json')

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($Msix)
try {
    $hits = $zip.Entries | Where-Object { $name = $_.Name; $forbidden | Where-Object { $name -like $_ } }
} finally {
    $zip.Dispose()
}

$mb = [math]::Round((Get-Item $Msix).Length / 1MB, 1)
Write-Host "MSIX: $mb MB ($Msix)"

if ($hits) {
    $hits | ForEach-Object { Write-Host "Forbidden: $($_.FullName)" }
    exit 1
}
if ($MaxMb -gt 0 -and $mb -gt $MaxMb) {
    Write-Host "Over budget: $mb MB > $MaxMb MB"
    exit 1
}
