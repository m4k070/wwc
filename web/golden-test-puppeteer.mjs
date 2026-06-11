import puppeteer from 'puppeteer-core';
import { readFileSync } from 'fs';
import { join } from 'path';
import { fileURLToPath } from 'url';

const __dirname = join(fileURLToPath(import.meta.url), '..');
const WEB_DIR = __dirname;
const VIVALDI_PATH = '/etc/profiles/per-user/makoto/bin/vivaldi';

const TEST_CASES = [
  // Round-trip: load and immediate export should be identical
  { name: 'toggleFF roundtrip', initFile: 'toggle_init.bin', steps: 0, expectedFile: 'toggle_init.bin' },
  { name: 'halfAdder roundtrip', initFile: 'ha_init.bin', steps: 0, expectedFile: 'ha_init.bin' },
  // Half adder: set a=1,b=1 via cell buffer, run 100 steps, compare with settled ha_11
  { name: 'halfAdder a=1,b=1', initFile: 'ha_init.bin', steps: 1000, expectedFile: 'ha_11.bin',
    setPins: [{ x: 0, y: 2, level: 1 }, { x: 0, y: 14, level: 1 }] },
];

function readBin(file) {
  const buf = readFileSync(join(WEB_DIR, file));
  return new Uint8Array(buf.buffer, buf.byteOffset, buf.byteLength);
}

function compareBins(a, b, label) {
  if (a.length !== b.length) {
    throw new Error(`${label}: length mismatch: got ${a.length}, expected ${b.length}`);
  }
  for (let i = 0; i < a.length; i++) {
    if (a[i] !== b[i]) {
      throw new Error(`${label}: byte mismatch at offset ${i}: got 0x${a[i].toString(16).padStart(2, '0')}, expected 0x${b[i].toString(16).padStart(2, '0')}`);
    }
  }
}

async function runGPUSimulation(page, initBin, steps, setPins) {
  await page.evaluate((binBase64) => {
    const binStr = atob(binBase64);
    const bytes = new Uint8Array(binStr.length);
    for (let i = 0; i < binStr.length; i++) {
      bytes[i] = binStr.charCodeAt(i);
    }
    window.loadGridBin(bytes);
  }, Buffer.from(initBin).toString('base64'));

  await page.waitForFunction(() => {
    return window.cells !== null && window.cells !== undefined && window.width > 0;
  }, { timeout: 30000 });

  if (setPins) {
    await page.evaluate((pins) => {
      for (const p of pins) {
        window.setCellLevel(p.x, p.y, p.level);
      }
    }, setPins);
  }

  await page.evaluate(async (n) => {
    await window.runN(n);
  }, steps);

  const result = await page.evaluate(async () => {
    const raw = await window.downloadResult();
    const w = window.width;
    const h = window.height;
    const header = new Uint32Array([w, h]);
    const buf = new Uint8Array(8 + raw.length);
    new DataView(buf.buffer).setUint32(0, w, true);
    new DataView(buf.buffer).setUint32(4, h, true);
    buf.set(raw, 8);
    return Array.from(buf);
  });

  return new Uint8Array(result);
}

async function main() {
  console.log('Launching Vivaldi with WebGPU...');

  const browser = await puppeteer.launch({
    executablePath: VIVALDI_PATH,
    headless: false,
    args: [
      '--enable-webgpu',
      '--use-vulkan=native',
      '--enable-features=Vulkan,WebGPU',
      '--ignore-gpu-blocklist',
      '--disable-gpu-driver-workarounds',
      '--no-sandbox',
      '--disable-setuid-sandbox',
    ],
    defaultViewport: { width: 1024, height: 768 },
  });

  try {
    const page = await browser.newPage();
    await page.goto('file://' + join(WEB_DIR, 'index.html'), {
      waitUntil: 'load',
      timeout: 30000,
    });

    await page.waitForFunction(() => window._wwcReady === true, { timeout: 15000 });

    const webgpuSupported = await page.evaluate(async () => {
      return !!(navigator.gpu && await navigator.gpu.requestAdapter());
    });

    if (!webgpuSupported) {
      console.log('WebGPU not supported, skipping tests');
      await browser.close();
      process.exit(0);
      return;
    }

    console.log('WebGPU supported, running tests...');

    for (const tc of TEST_CASES) {
      console.log(`\n=== ${tc.name} (${tc.steps} steps) ===`);

      const initBin = readBin(tc.initFile);
      const expectedBin = readBin(tc.expectedFile);

      const resultBin = await runGPUSimulation(page, initBin, tc.steps, tc.setPins);

      console.log('Comparing results...');
      compareBins(resultBin, expectedBin, tc.name);

      console.log(`✓ ${tc.name} passed`);
    }

    console.log('\n✓ All GPU golden tests passed!');
  } finally {
    await browser.close();
  }
}

main().catch(err => {
  console.error('Test failed:', err);
  process.exit(1);
});