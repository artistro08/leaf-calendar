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

// Tray Glyphs: the Fluent leaf scaled to fill the square (the Fluent art keeps 2-3 px of room around it, which read
// as a small icon), each size from the hinted SVG nearest its scale. Its stem (a cutout and the stalk below, between
// the two x values) is set to a whole number of pixels at that scale, and the leaf is drawn where it renders sharpest:
// as placed or moved half a pixel either way, whichever leaves the fewest half-covered pixels
fs.mkdirSync(path.join(assets, 'Tray'), { recursive: true });
const trays = {
    16: { source: 20, stem: ['9.49995', '10.5'], px: 1 },
    20: { source: 24, stem: ['11.25', '12.75'], px: 1 },
    24: { source: 32, stem: ['15', '17'], px: 2 },
    32: { source: 32, stem: ['15', '17'], px: 2 },
};
function blur(png) {
    const pixels = png.pixels;
    let soft = 0;
    for (let i = 3; i < pixels.length; i += 4) {
        if (pixels[i] > 24 && pixels[i] < 232) {
            soft++;
        }
    }
    return soft;
}
function token(text, from, to) {
    return text.replace(new RegExp(`(?<![0-9.])${from.replace('.', '[.]')}(?![0-9.])`, 'g'), to);
}
for (const [size, tray] of Object.entries(trays).map(([s, t]) => [Number(s), t])) {
    const glyph = fs.readFileSync(path.join(fluentDir, `ic_fluent_leaf_one_${tray.source}_filled.svg`), 'utf8');
    const box   = new Resvg(glyph).getBBox();
    const k     = Math.min(size / box.height, size / box.width);

    // Stem: Whole Pixels At This Scale
    const [left, right] = tray.stem.map(Number);
    const middle = (left + right) / 2, half = tray.px / (2 * k);
    let body = glyph.slice(glyph.indexOf('>') + 1, glyph.lastIndexOf('</svg>'));
    body = token(token(body, tray.stem[0], (middle - half).toFixed(4)), tray.stem[1], (middle + half).toFixed(4));

    for (const [name, color] of [['dark-taskbar', '#FFFFFF'], ['light-taskbar', '#1F1F1F']]) {
        const paint = body.replace(/fill="#212121"/g, `fill="${color}"`);
        let best = null;
        for (const dx of [0, 0.5, -0.5]) {
            for (const dy of [0, 0.5, -0.5]) {
                const tx  = (size - box.width * k) / 2 - box.x * k + dx;
                const ty  = (size - box.height * k) / 2 - box.y * k + dy;
                const svg = `<svg width="${size}" height="${size}" viewBox="0 0 ${size} ${size}" xmlns="http://www.w3.org/2000/svg">` +
                    `<g transform="translate(${tx} ${ty}) scale(${k})">${paint}</g></svg>`;
                const png  = new Resvg(svg, { fitTo: { mode: 'original' } }).render();
                const soft = blur(png);
                if (best === null || soft < best.soft) {
                    best = { soft, png };
                }
            }
        }
        fs.writeFileSync(path.join(assets, 'Tray', `tray-${name}-${size}.png`), best.png.asPng());
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
