// Renders Leaf's icons from the SVGs in tools/icons into src/LeafCalendar.App/Assets (run by tools/make-icons.ps1):
//   - every MSIX visual asset (Square44x44Logo with its target sizes, Square150x150Logo, Wide310x150Logo, StoreLogo,
//     SplashScreen, each at scale 100/125/150/200/400) and the window/exe icon (LeafCalendar.ico) from
//     leaf-calendar-main.svg;
//   - the tray icons from icons/daily: one per day of the month, white for dark mode and black for light mode, at every
//     small-icon size Windows uses from 100% to 400% display scale, so the taskbar never scales one (which blurs it).
// Every PNG is drawn at its exact pixel size; nothing is resized after rendering.
// Usage: node make-icons.cjs <tools/icons> <Assets>   (needs @resvg/resvg-js resolvable from the working folder)

const fs = require('fs');
const path = require('path');
const { Resvg } = require(require.resolve('@resvg/resvg-js', { paths: [process.cwd()] }));
const [iconsDir, assets] = process.argv.slice(2);

// =========================================================================
// MAIN ICON
// =========================================================================

// The logo's inner markup, so it can be placed on a larger canvas
const source = fs.readFileSync(path.join(iconsDir, 'leaf-calendar-main.svg'), 'utf8');
const open = source.match(/<svg\b[^>]*>/)[0];
const viewBox = open.match(/viewBox="([^"]+)"/)[1];
const style = (open.match(/style="([^"]+)"/) || [, ''])[1];
const inner = source.slice(source.indexOf(open) + open.length, source.lastIndexOf('</svg>'));
const namespaces = (open.match(/xmlns(:\w+)?="[^"]*"/g) || []).join(' ');

// The logo at size `logo`, centered on a transparent w x h canvas
function render(w, h, size) {
    const x = (w - size) / 2, y = (h - size) / 2;
    const svg = `<svg ${namespaces} width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">` +
        `<svg x="${x}" y="${y}" width="${size}" height="${size}" viewBox="${viewBox}" style="${style}">${inner}</svg></svg>`;
    return new Resvg(svg, { fitTo: { mode: 'original' } }).render();
}

function logo(w, h, size) {
    return render(w, h, size).asPng();
}

// The logo as an uncompressed 32-bit icon image (a BITMAPINFOHEADER with the height doubled, BGRA rows bottom up, then
// an all-clear AND mask): the taskbar and title bar show a generic icon for PNG-compressed images below 256 px. The
// renderer's pixels are premultiplied by alpha and an icon's are not, so each color is divided back out.
function dib(size) {
    const px = render(size, size, size).pixels;
    const rowBytes = size * 4, maskBytes = Math.ceil(size / 32) * 4;
    const buf = Buffer.alloc(40 + rowBytes * size + maskBytes * size);
    buf.writeUInt32LE(40, 0);
    buf.writeInt32LE(size, 4);
    buf.writeInt32LE(size * 2, 8);
    buf.writeUInt16LE(1, 12);
    buf.writeUInt16LE(32, 14);
    buf.writeUInt32LE(0, 16);
    buf.writeUInt32LE(rowBytes * size + maskBytes * size, 20);
    for (let y = 0; y < size; y++) {
        const src = (size - 1 - y) * rowBytes, dst = 40 + y * rowBytes;
        for (let x = 0; x < size; x++) {
            const s = src + x * 4, d = dst + x * 4, a = px[s + 3];
            const unmultiply = c => a === 0 ? 0 : Math.min(255, Math.round(c * 255 / a));
            buf[d] = unmultiply(px[s + 2]);
            buf[d + 1] = unmultiply(px[s + 1]);
            buf[d + 2] = unmultiply(px[s]);
            buf[d + 3] = a;
        }
    }
    return buf;
}

function write(file, png) {
    fs.writeFileSync(path.join(assets, file), png);
}

// Scaled Assets (tiles and splash keep the logo at two thirds of the short side)
for (const scale of [100, 125, 150, 200, 400]) {
    const s = scale / 100, q = `.scale-${scale}.png`;
    write(`Square44x44Logo${q}`, logo(Math.round(44 * s), Math.round(44 * s), Math.round(44 * s)));
    write(`StoreLogo${q}`, logo(Math.round(50 * s), Math.round(50 * s), Math.round(50 * s)));
    write(`Square150x150Logo${q}`, logo(Math.round(150 * s), Math.round(150 * s), Math.round(100 * s)));
    write(`Wide310x150Logo${q}`, logo(Math.round(310 * s), Math.round(150 * s), Math.round(100 * s)));
    write(`SplashScreen${q}`, logo(Math.round(620 * s), Math.round(300 * s), Math.round(200 * s)));
}

// Taskbar And Start Sizes (plated, unplated, and light-mode unplated alike: the logo brings its own background; without
// the light-mode set, a light taskbar scales another size, which leaves jagged edges)
for (const size of [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256]) {
    write(`Square44x44Logo.targetsize-${size}.png`, logo(size, size, size));
    write(`Square44x44Logo.targetsize-${size}_altform-unplated.png`, logo(size, size, size));
    write(`Square44x44Logo.targetsize-${size}_altform-lightunplated.png`, logo(size, size, size));
}

// In-App Logo (title bars and the About page): one large image that each place decodes straight to its size on screen
// (BitmapImage.DecodePixelWidth, logical), which scales with a smooth filter; a shown image shrunk later is jagged
write('AppLogo.png', logo(512, 512, 512));

// Window And Exe Icon: one image per size, so Windows picks an exact one instead of scaling; uncompressed below 256
const icoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
const images = icoSizes.map(size => size >= 256 ? logo(size, size, size) : dib(size));
const header = Buffer.alloc(6 + 16 * images.length);
header.writeUInt16LE(0, 0);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(images.length, 4);
let offset = header.length;
images.forEach((png, i) => {
    const entry = 6 + 16 * i, size = icoSizes[i];
    header.writeUInt8(size >= 256 ? 0 : size, entry);
    header.writeUInt8(size >= 256 ? 0 : size, entry + 1);
    header.writeUInt8(0, entry + 2);
    header.writeUInt8(0, entry + 3);
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(png.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += png.length;
});
write('LeafCalendar.ico', Buffer.concat([header, ...images]));

// =========================================================================
// TRAY ICONS
// =========================================================================

// SM_CXSMICON at 100, 125, 150, 175, 200, 225, 250, 300, 350, and 400% (TrayGlyph.Sizes in the app)
const traySizes = [16, 20, 24, 28, 32, 36, 40, 48, 56, 64];
const trayDir = path.join(assets, 'Tray');
fs.rmSync(trayDir, { recursive: true, force: true });
fs.mkdirSync(trayDir, { recursive: true });
for (const [color, theme] of [['white', 'dark'], ['black', 'light']]) {
    for (let day = 1; day <= 31; day++) {
        const svg = fs.readFileSync(path.join(iconsDir, 'daily', `leaf-calendar-icon-mono-${color}-${day}.svg`), 'utf8');
        for (const size of traySizes) {
            const png = new Resvg(svg, { fitTo: { mode: 'width', value: size } }).render();
            if (png.width !== size || png.height !== size) {
                throw new Error(`Day ${day} (${color}) rendered at ${png.width} x ${png.height}, not ${size}.`);
            }

            fs.writeFileSync(path.join(trayDir, `tray-${theme}-${day}-${size}.png`), png.asPng());
        }
    }
}
