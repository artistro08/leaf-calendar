# Renders Leaf's icons into src/LeafCalendar.App/Assets (see tools/make-icons.cjs): the MSIX visual assets and the
# window/exe icon (LeafCalendar.ico) from tools/icons/leaf-calendar-main.svg, and the daily tray icons (white for dark
# mode, black for light mode, at every small-icon size from 100% to 400% scale) from tools/icons/daily.
# Uses resvg (https://github.com/yisibl/resvg-js), installed by npm into a temp folder; needs Node.js, no admin.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tool = Join-Path ([IO.Path]::GetTempPath()) 'leaf-make-icons'

# Renderer (npm, outside the repo)
New-Item -ItemType Directory -Force $tool | Out-Null
if (-not (Test-Path (Join-Path $tool 'node_modules/@resvg/resvg-js'))) {
    npm install --prefix $tool --no-save --silent '@resvg/resvg-js@2' | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Render (from the tool folder, where the renderer resolves)
Push-Location $tool
try {
    node (Join-Path $PSScriptRoot 'make-icons.cjs') (Join-Path $PSScriptRoot 'icons') (Join-Path $root 'src/LeafCalendar.App/Assets')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}

Write-Host 'Icons written to src/LeafCalendar.App/Assets'
