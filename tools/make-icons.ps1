# Renders every MSIX visual asset (Square44x44Logo with its target sizes, Square150x150Logo, Wide310x150Logo,
# StoreLogo, SplashScreen, each at scale 100/125/150/200/400) from tools/icons/leaf-calendaricon.svg, and the tray
# glyphs (white for a dark taskbar, dark for a light one, 16/20/24/32 px) from the Fluent UI System Icons "Leaf One"
# SVGs in tools/icons/fluent, into src/LeafCalendar.App/Assets. The window/exe icon (Assets/LeafCalendar.ico) is the
# owner's own file and isn't made here.
# Uses resvg (https://github.com/yisibl/resvg-js), installed by npm into a temp folder; needs Node.js, no admin.
param([string]$Svg = (Join-Path $PSScriptRoot 'icons/leaf-calendaricon.svg'))

$ErrorActionPreference = 'Stop'
$root   = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'src/LeafCalendar.App/Assets'
$tool   = Join-Path ([IO.Path]::GetTempPath()) 'leaf-make-icons'

# Renderer (npm, outside the repo)
New-Item -ItemType Directory -Force $tool | Out-Null
if (-not (Test-Path (Join-Path $tool 'node_modules/@resvg/resvg-js'))) {
    npm install --prefix $tool --no-save --silent '@resvg/resvg-js@2' | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Render Script
$script = Join-Path $tool 'render.cjs'
@'
const fs = require('fs');
const path = require('path');
const { Resvg } = require('@resvg/resvg-js');
const [svgPath, fluentDir, assets] = process.argv.slice(2);

// The logo's inner markup, so it can be placed on a larger canvas
const source = fs.readFileSync(svgPath, 'utf8');
const open = source.match(/<svg\b[^>]*>/)[0];
const viewBox = open.match(/viewBox="([^"]+)"/)[1];
const style = (open.match(/style="([^"]+)"/) || [, ''])[1];
const inner = source.slice(source.indexOf(open) + open.length, source.lastIndexOf('</svg>'));
const namespaces = (open.match(/xmlns(:\w+)?="[^"]*"/g) || []).join(' ');

// The logo at size `logo`, centered on a transparent w x h canvas
function render(file, w, h, logo) {
    const x = (w - logo) / 2, y = (h - logo) / 2;
    const svg = `<svg ${namespaces} width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">` +
        `<svg x="${x}" y="${y}" width="${logo}" height="${logo}" viewBox="${viewBox}" style="${style}">${inner}</svg></svg>`;
    fs.writeFileSync(path.join(assets, file), new Resvg(svg, { fitTo: { mode: 'original' } }).render().asPng());
}

// Scaled Assets (tiles and splash keep the logo at two thirds of the short side)
for (const scale of [100, 125, 150, 200, 400]) {
    const s = scale / 100, q = `.scale-${scale}.png`;
    render(`Square44x44Logo${q}`, Math.round(44 * s), Math.round(44 * s), Math.round(44 * s));
    render(`StoreLogo${q}`, Math.round(50 * s), Math.round(50 * s), Math.round(50 * s));
    render(`Square150x150Logo${q}`, Math.round(150 * s), Math.round(150 * s), Math.round(100 * s));
    render(`Wide310x150Logo${q}`, Math.round(310 * s), Math.round(150 * s), Math.round(100 * s));
    render(`SplashScreen${q}`, Math.round(620 * s), Math.round(300 * s), Math.round(200 * s));
}

// Taskbar And Start Sizes (plated and unplated alike: the logo brings its own background)
for (const size of [16, 24, 32, 48, 256]) {
    render(`Square44x44Logo.targetsize-${size}.png`, size, size, size);
    render(`Square44x44Logo.targetsize-${size}_altform-unplated.png`, size, size, size);
}

// Tray Glyphs (each size from its own hinted Fluent SVG; 32 for anything larger)
fs.mkdirSync(path.join(assets, 'Tray'), { recursive: true });
for (const size of [16, 20, 24, 32]) {
    const glyph = fs.readFileSync(path.join(fluentDir, `ic_fluent_leaf_one_${size}_regular.svg`), 'utf8');
    for (const [name, color] of [['dark-taskbar', '#FFFFFF'], ['light-taskbar', '#1F1F1F']]) {
        const svg = glyph.replace(/fill="#212121"/g, `fill="${color}"`);
        fs.writeFileSync(path.join(assets, 'Tray', `tray-${name}-${size}.png`), new Resvg(svg, { fitTo: { mode: 'width', value: size } }).render().asPng());
    }
}
'@ | Set-Content -Encoding utf8 $script

# Render
Push-Location $tool
try {
    node $script $Svg (Join-Path $PSScriptRoot 'icons/fluent') $assets
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}

Write-Host "Icons written to $assets"
