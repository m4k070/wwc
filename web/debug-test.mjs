import puppeteer from 'puppeteer-core';
const launch = async (headless) => {
  const browser = await puppeteer.launch({
    executablePath: '/etc/profiles/per-user/makoto/bin/vivaldi',
    headless,
    args: [
      '--enable-webgpu',
      '--ignore-gpu-blocklist',
      '--disable-gpu-driver-workarounds',
      '--no-sandbox',
      '--enable-unsafe-swiftshader',
      headless === 'new' ? '--headless=new' : ''
    ].filter(Boolean),
  });
  const page = await browser.newPage();
  page.on('console', msg => console.log('PAGE:', msg.type(), msg.text()));
  await page.goto('file:///home/makoto/sandbox/wwc/web/index.html', { waitUntil: 'load', timeout: 20000 }).catch(e => console.error('GOTO ERROR:', e.message));
  await new Promise(r => setTimeout(r, 3000));
  const info = await page.evaluate(() => ({
    wwcReady: window._wwcReady,
    status: document.getElementById('status')?.textContent,
  }));
  console.log(`headless=${JSON.stringify(headless)}:`, JSON.stringify(info));
  await browser.close();
};
(async () => {
  for (const h of ['new', 'old', true]) {
    try { await launch(h); } catch (e) { console.log(`headless=${JSON.stringify(h)}:`, e.message?.slice(0, 100)); }
  }
})();
