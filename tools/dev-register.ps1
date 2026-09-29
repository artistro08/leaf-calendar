# Builds Leaf Calendar (Debug, JIT, x64) and registers its loose package layout for this user.
# Used for local runs and UI tests. Requires Windows Developer Mode.
# Warning: replacing a registration from a different folder clears Leaf's LocalState (the cache
# re-syncs from Google; secrets in Credential Locker are kept).
$ErrorActionPreference = 'Stop'
$root    = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/LeafCalendar.App/LeafCalendar.App.csproj'

# Build
dotnet build $project -c Debug -p:Platform=x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Find Layout
$manifest = Get-ChildItem (Join-Path $root 'src/LeafCalendar.App/bin/x64/Debug') -Recurse -Filter 'AppxManifest.xml' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $manifest) { throw 'AppxManifest.xml not found under bin/x64/Debug. Check the build output.' }

# Register
$existing = Get-AppxPackage LeafCalendar
if ($existing -and $existing.InstallLocation -ne $manifest.DirectoryName) {
    Write-Warning "Replacing Leaf Calendar registered from $($existing.InstallLocation). Local cache will be cleared."
    Remove-AppxPackage $existing.PackageFullName
}
Add-AppxPackage -Register $manifest.FullName -ForceApplicationShutdown
Write-Host "Registered $((Get-AppxPackage LeafCalendar).PackageFamilyName) from $($manifest.DirectoryName)"
