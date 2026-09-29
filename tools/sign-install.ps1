# Creates (or reuses) a self-signed code-signing certificate matching the package Publisher,
# trusts it for MSIX installs, publishes the signed Release (Native AOT) MSIX, and installs it.
# Used to install Leaf Calendar from a real .msix on this PC.
# Run from an elevated PowerShell (trusting the certificate writes to LocalMachine\TrustedPeople).
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
        -FriendlyName 'Leaf Calendar Dev Signing' -NotAfter (Get-Date).AddYears(3) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

# Trust (public certificate only; private key stays in CurrentUser\My)
$cer = Join-Path $env:TEMP 'LeafCalendar-dev.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

# Publish Signed MSIX (the AOT linker setup calls vswhere.exe by bare name)
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
Remove-Item (Join-Path $app 'AppPackages') -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $app 'LeafCalendar.App.csproj') -c Release -r win-x64 -p:Platform=x64 `
    -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint=$($cert.Thumbprint)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$msix = Get-ChildItem (Join-Path $app 'AppPackages') -Recurse -Filter '*.msix' | Select-Object -First 1
if (-not $msix) { throw 'No .msix produced under AppPackages.' }

# Install (replaces the dev registration; Leaf's local cache is cleared, sign-ins in Credential Locker stay)
Get-AppxPackage LeafCalendar | Remove-AppxPackage
Add-AppxPackage -Path $msix.FullName
Write-Host "Installed $($msix.Name) signed by $Publisher"
