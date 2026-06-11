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
        launchOptions: {
          args: [
            '--enable-webgpu',
            '--use-vulkan=native',
            '--enable-features=Vulkan,WebGPU',
            '--ignore-gpu-blocklist',
            '--disable-gpu-driver-workarounds',
            '--no-sandbox',
            '--disable-setuid-sandbox',
          ],
        },
        env: {
          LD_LIBRARY_PATH: '/nix/store/3x4lz5q9k2l4vz5q9k2l4vz5q9k2l4vz5/lib:/nix/store/glib-2.80.0/lib:/nix/store/nss-3.99/lib:/nix/store/nspr-4.35/lib:/nix/store/dbus-1.14.10/lib:/nix/store/at-spi2-core-2.50.0/lib:/nix/store/cups-2.4.7/lib:/nix/store/libxkbfile-1.1.2/lib:/nix/store/libxcomposite-0.4.5/lib:/nix/store/libxdamage-1.1.6/lib:/nix/store/libxfixes-6.0.0/lib:/nix/store/libxrandr-1.5.3/lib:/nix/store/libgbm-24.0.7/lib:/nix/store/alsa-lib-1.2.10/lib:/nix/store/pulseaudio-17.0/lib',
        },
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