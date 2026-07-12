import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';
import toIco from 'to-ico';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const srcSvg = path.join(root, 'branding/pim-mark.svg');

if (!fs.existsSync(srcSvg)) {
  console.error(`export-icons: missing source ${srcSvg}`);
  process.exit(1);
}

const svg = fs.readFileSync(srcSvg, 'utf8');
const requiredColors = ['#00a4ef', '#f25022', '#7fba00', '#ffb900'];
for (const c of requiredColors) {
  if (!svg.includes(c)) {
    console.error(`export-icons: mark missing color ${c}`);
    process.exit(1);
  }
}

const svgBuffer = Buffer.from(svg);

async function rasterPng(size, { whiteBg = true } = {}) {
  const resized = await sharp(svgBuffer).resize(size, size).png().toBuffer();
  if (!whiteBg) {
    return resized;
  }
  return sharp({
    create: { width: size, height: size, channels: 3, background: '#ffffff' },
  })
    .composite([{ input: resized }])
    .png()
    .toBuffer();
}

async function writePng(size, out, options) {
  fs.mkdirSync(path.dirname(out), { recursive: true });
  const buf = await rasterPng(size, options);
  fs.writeFileSync(out, buf);
}

async function writeIco(sizes, out) {
  fs.mkdirSync(path.dirname(out), { recursive: true });
  const buffers = [];
  for (const size of sizes) {
    buffers.push(await rasterPng(size, { whiteBg: true }));
  }
  const ico = await toIco(buffers);
  fs.writeFileSync(out, ico);
}

const webPublic = path.join(root, 'src/client-web/public');
const windowsApp = path.join(root, 'src/client-windows/Pim.Client.App');
const androidRes = path.join(root, 'src/client-android/app/src/main/res');

// 1. favicon.svg (identical copy)
fs.mkdirSync(webPublic, { recursive: true });
fs.writeFileSync(path.join(webPublic, 'favicon.svg'), svg);

// 2. favicon.ico (16/32/48)
await writeIco([16, 32, 48], path.join(webPublic, 'favicon.ico'));

// 3. apple-touch-icon.png 180x180 white bg
await writePng(180, path.join(webPublic, 'apple-touch-icon.png'), { whiteBg: true });

// 4. Windows app.ico (16/32/48/256)
await writeIco([16, 32, 48, 256], path.join(windowsApp, 'app.ico'));

// 5. Android mipmaps
const androidSizes = [
  ['mipmap-mdpi', 48],
  ['mipmap-hdpi', 72],
  ['mipmap-xhdpi', 96],
  ['mipmap-xxhdpi', 144],
  ['mipmap-xxxhdpi', 192],
];

for (const [folder, size] of androidSizes) {
  const dir = path.join(androidRes, folder);
  await writePng(size, path.join(dir, 'ic_launcher.png'), { whiteBg: true });
  await writePng(size, path.join(dir, 'ic_launcher_round.png'), { whiteBg: true });
}

console.log('export-icons: ok');
