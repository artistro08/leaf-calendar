# Publishes Leaf Calendar as a Native AOT MSIX (Release, x64, unsigned). With -Register, it also
# unpacks the MSIX and registers it for this user (for memory budget tests and release checks).
# Requires .NET SDK 10 (VS C++ build tools for the AOT linker) and Windows Developer Mode for -Register.
param([switch]$Register)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$app  = Join-Path $root 'src/LeafCalendar.App'

# The AOT linker setup calls vswhere.exe by bare name
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

# Publish
Remove-Item (Join-Path $app 'AppPackages') -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $app 'LeafCalendar.App.csproj') -c Release -r win-x64 -p:Platform=x64 `
    -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$msix = Get-ChildItem (Join-Path $app 'AppPackages') -Recurse -Filter '*.msix' | Select-Object -First 1
if (-not $msix) { throw 'No .msix produced under AppPackages.' }
Write-Host "MSIX: $($msix.FullName)"
if (-not $Register) { return }

# Unpack And Register
$layout = Join-Path $app 'AppPackages/aot-layout'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($msix.FullName, $layout)

$existing = Get-AppxPackage LeafCalendar
if ($existing -and -not $existing.IsDevelopmentMode) {
    throw 'Leaf is installed from a package; uninstall it from Settings > Apps first.'
}
if ($existing -and $existing.InstallLocation -ne $layout) {
    Write-Warning "Replacing Leaf Calendar registered from $($existing.InstallLocation). Your accounts and settings are kept."
    Remove-AppxPackage $existing.PackageFullName -PreserveApplicationData
}
Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ForceApplicationShutdown
Write-Host "Registered AOT build from $layout"
