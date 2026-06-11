import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: 'golden-test.ts',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: 1,
  reporter: 'list',
  use: {
    baseURL: 'http://localhost:8080',
    trace: 'on-first-retry',
    headless: true,
  },
  projects: [
    {
      name: 'chromium-webgpu',
      use: {
        ...devices['Desktop Chrome'],
        // headless-shell ビルドは WebGPU adapter を持たないため、
        // フル Chromium の新 headless モードを使う
        channel: 'chromium',
        launchOptions: {
          // WebGPU はヘッドレスでは SwiftShader (CPU) adapter で動かす。
          // --enable-unsafe-webgpu が必須 (--enable-webgpu は実在しないスイッチ)。
          args: [
            '--enable-unsafe-webgpu',
            '--enable-unsafe-swiftshader',
            '--enable-features=Vulkan',
            '--ignore-gpu-blocklist',
            '--no-sandbox',
            '--disable-setuid-sandbox',
          ],
        },
        // LD_LIBRARY_PATH は run-test.sh が export し、子プロセスに継承される
      },
    },
  ],
  webServer: {
    command: 'python3 -m http.server 8080',
    cwd: __dirname,
    port: 8080,
    reuseExistingServer: !process.env.CI,
    timeout: 30000,
  },
});