# Creates (or reuses) a self-signed code-signing certificate matching the package Publisher,
# trusts it for MSIX installs, publishes the signed Release (Native AOT) MSIX, and installs it.
# Used to install Leaf Calendar from a real .msix on this PC. Each build gets a newer version and installs as an
# update, so settings, the OAuth client, and sign-ins carry over.
# Run from an elevated PowerShell the first time (trusting the certificate writes to LocalMachine\TrustedPeople);
# later runs reuse the trusted certificate and don't need elevation.
param([string]$Publisher = 'CN=Artistro08')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$app  = Join-Path $root 'src/LeafCalendar.App'

# Certificate (CurrentUser\My, reused while valid)
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
    Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Publisher `
        -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature `
        -FriendlyName 'Leaf Calendar Dev Signing' -NotAfter (Get-Date).AddYears(3) -KeyExportPolicy NonExportable `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

# Trust (public certificate only; private key stays in CurrentUser\My). Only when it isn't trusted yet, so a
# rebuild after the first install doesn't need an elevated PowerShell
$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint
if (-not $trusted) {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run from an elevated PowerShell the first time (trusting the certificate writes to LocalMachine\TrustedPeople).'
    }
    $cer = Join-Path $env:TEMP 'LeafCalendar-dev.cer'
    try {
        Export-Certificate -Cert $cert -FilePath $cer | Out-Null
        Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    }
    finally {
        Remove-Item $cer -Force -ErrorAction SilentlyContinue
    }
}

# Build Version: the manifest's major.minor, then days since 2026 and minutes into the day, so every build is newer
# than the last and installs as an update (an update keeps Leaf's settings and sign-in; a remove and reinstall
# deletes them). Stamped into the manifest for the publish only, then put back
$manifest = Join-Path $app 'Package.appxmanifest'
$original = [IO.File]::ReadAllText($manifest)
$now      = Get-Date
$base     = [regex]::Match($original, '<Identity [^>]*Version="(\d+\.\d+)\.').Groups[1].Value
$days     = ($now.Date - [datetime]'2026-01-01').Days
$minutes  = [int][math]::Floor($now.TimeOfDay.TotalMinutes)
$version  = "$base.$days.$minutes"
[IO.File]::WriteAllText($manifest, [regex]::Replace($original, '(<Identity [^>]*Version=")[^"]+', "`${1}$version"))

# Publish Signed MSIX (the AOT linker setup calls vswhere.exe by bare name)
try {
    $env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
    Remove-Item (Join-Path $app 'AppPackages') -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish (Join-Path $app 'LeafCalendar.App.csproj') -c Release -r win-x64 -p:Platform=x64 `
        -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true `
        -p:PackageCertificateThumbprint=$($cert.Thumbprint)
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    [IO.File]::WriteAllText($manifest, $original)
}

$msix = Get-ChildItem (Join-Path $app 'AppPackages') -Recurse -Filter '*.msix' | Select-Object -First 1
if (-not $msix) { throw 'No .msix produced under AppPackages.' }

# Install As An Update (keeps Leaf's settings, OAuth client, and cache). A dev registration can't be updated by a
# package, so it's removed first with its app data kept (only a development registration can keep it that way)
$existing = Get-AppxPackage LeafCalendar
if ($existing -and $existing.IsDevelopmentMode) {
    Remove-AppxPackage $existing.PackageFullName -PreserveApplicationData
}

Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown
Write-Host "Installed $($msix.Name) $version signed by $Publisher"
