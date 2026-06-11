import { test, expect } from '@playwright/test';
import { readFileSync } from 'fs';
import { join } from 'path';

const WEB_DIR = join(__dirname);
// init = ピン設定直後の未収束状態 (F# ExportGrid.fsx が生成)。
// GPU で steps 世代回すと expected (F# settle の収束状態) に一致するはず。
// ピン値はステップで変化しないので、init と expected のピン状態は同一であること。
const TEST_CASES = [
  {
    name: 'toggleFF',
    initFile: 'toggle_clk1_init.bin',
    steps: 1000,
    expectedFile: 'toggle_clk1.bin',
  },
  {
    name: 'halfAdder',
    initFile: 'ha_11_init.bin',
    steps: 1000,
    expectedFile: 'ha_11.bin',
  },
];

function readBin(file: string): Uint8Array {
  const buf = readFileSync(join(WEB_DIR, file));
  return new Uint8Array(buf.buffer, buf.byteOffset, buf.byteLength);
}

function compareBins(a: Uint8Array, b: Uint8Array, label: string): void {
  if (a.length !== b.length) {
    throw new Error(`${label}: length mismatch: got ${a.length}, expected ${b.length}`);
  }
  for (let i = 0; i < a.length; i++) {
    if (a[i] !== b[i]) {
      throw new Error(`${label}: byte mismatch at offset ${i}: got 0x${a[i].toString(16).padStart(2, '0')}, expected 0x${b[i].toString(16).padStart(2, '0')}`);
    }
  }
}

async function runGPUSimulation(page: any, initBin: Uint8Array, steps: number): Promise<Uint8Array> {
  // Load the .bin file via file input
  const fileInput = page.locator('#fileInput');
  await fileInput.setInputFiles({
    name: 'test.bin',
    mimeType: 'application/octet-stream',
    buffer: Buffer.from(initBin),
  });

  // Wait for grid to load
  await page.waitForFunction(() => {
    return (window as any).cells !== null && (window as any).width > 0;
  }, { timeout: 10000 });

  // Run N steps
  for (let i = 0; i < steps; i++) {
    await page.evaluate(async () => {
      await (window as any).stepGPU();
    });
    // Periodically read back to check progress
    if (i % 100 === 0 || i === steps - 1) {
      await page.waitForFunction(() => !(window as any).running, { timeout: 30000 });
    }
  }

  // Download result
  const result = await page.evaluate(async () => {
    return await (window as any).downloadResult();
  });

  return new Uint8Array(result);
}

for (const tc of TEST_CASES) {
  test(`GPU Golden Test: ${tc.name}`, async ({ page }) => {
    // Launch with WebGPU enabled
    await page.goto('file://' + join(WEB_DIR, 'index.html'), {
      waitUntil: 'networkidle',
    });

    // Enable WebGPU flags are needed - but Playwright Chromium may need explicit flags
    // Check if WebGPU is available
    const webgpuSupported = await page.evaluate(async () => {
      const gpu = (navigator as any).gpu;
      return !!(gpu && await gpu.requestAdapter());
    });

    if (!webgpuSupported) {
      test.skip(true, 'WebGPU not supported in this browser instance');
      return;
    }

    const initBin = readBin(tc.initFile);
    const expectedBin = readBin(tc.expectedFile);

    console.log(`Running ${tc.name}: ${tc.steps} steps...`);

    const resultBin = await runGPUSimulation(page, initBin, tc.steps);

    console.log(`Comparing results for ${tc.name}...`);
    // downloadResult() は 8 バイトヘッダなしの生セル配列を返す。
    // 寸法はヘッダから検証し、セル部分のみ比較する。
    const view = new DataView(expectedBin.buffer, expectedBin.byteOffset);
    const w = view.getUint32(0, true);
    const h = view.getUint32(4, true);
    expect(resultBin.length, `${tc.name}: cell count`).toBe(w * h);
    compareBins(resultBin, expectedBin.slice(8), tc.name);

    console.log(`✓ ${tc.name} passed`);
  });
}