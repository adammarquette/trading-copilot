// Regenerates the PWA PNG icon set (gh#650, R-19 / ADR-0010) from the source SVG marks in this folder.
//
// The PNGs are COMMITTED artifacts under the client's public/icons/ — they are NOT generated at build time,
// because documentation/ is .dockerignore'd (the container's client build never sees this folder). Re-run this
// only when the source art changes:
//
//     cd documentation/design/logo && npm i sharp && node generate-pwa-icons.mjs
//
// sharp is a dev-only, on-demand tool; it is deliberately NOT a client dependency (the client build needs the
// PNGs, not the rasterizer).
import sharp from 'sharp';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { mkdirSync } from 'node:fs';

const here = dirname(fileURLToPath(import.meta.url));
const outDir = join(here, '..', '..', '..', 'src', 'MarqSpec.TradingCopilot.Client', 'public', 'icons');
mkdirSync(outDir, { recursive: true });

const primary = join(here, 'app-icon.svg');
const maskable = join(here, 'app-icon-maskable.svg');

// [source, size, output] — "any"-purpose icons + apple-touch + favicons from the primary mark; the maskable
// icon from the full-bleed variant so Android's mask never clips the art.
const targets = [
  [primary, 192, 'icon-192.png'],
  [primary, 512, 'icon-512.png'],
  [maskable, 512, 'icon-maskable-512.png'],
  [primary, 180, 'apple-touch-icon.png'],
  [primary, 32, 'favicon-32.png'],
  [primary, 16, 'favicon-16.png'],
];

for (const [src, size, name] of targets) {
  await sharp(src, { density: 384 })
    .resize(size, size, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
    .png()
    .toFile(join(outDir, name));
  console.log(`  ${name}  (${size}x${size})`);
}
console.log(`\nWrote ${targets.length} icons to ${outDir}`);
