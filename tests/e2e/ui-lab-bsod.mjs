// AEGIS 0.7 browser QA: real telemetry in UI, charts, 5 lab tests, BSOD demo.
// Usage: node claude-qa.mjs <baseUrl> <outDir>
import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';

const base = process.argv[2] || 'http://127.0.0.1:8775';
const out = process.argv[3];
fs.mkdirSync(out, { recursive: true });
const report = { base, started: new Date().toISOString(), steps: [], consoleErrors: [], pageErrors: [] };
const step = (name, data) => { report.steps.push({ name, ...data }); console.log(name, JSON.stringify(data)); };
const save = () => fs.writeFileSync(path.join(out, 'qa-report.json'), JSON.stringify(report, null, 2));

const browser = await chromium.launch({ headless: true, args: ['--enable-unsafe-swiftshader'] });
try {
  const page = await browser.newPage({ viewport: { width: 1540, height: 960 } });
  page.on('console', m => { if (m.type() === 'error') report.consoleErrors.push(m.text()); });
  page.on('pageerror', e => report.pageErrors.push(String(e)));
  await page.goto(base);
  await page.waitForFunction(() => /\d/.test(document.querySelector('#sidebarMachine')?.textContent || '') || document.querySelector('#sidebarMachine')?.textContent !== 'Компьютер не определён', null, { timeout: 30000 });
  await page.waitForTimeout(4000);
  step('overview', await page.evaluate(() => ({
    banner: document.querySelector('#connectionBanner').hidden ? null : document.querySelector('#connectionBanner').innerText,
    legend: document.querySelector('#historyChart .aegis-chart-legend')?.innerText,
    paths: document.querySelectorAll('#historyChart path.aegis-series').length,
    yLabels: [...document.querySelectorAll('#historyChart .aegis-chart-grid text')].map(t => t.textContent),
    machine: document.querySelector('#sidebarMachine').textContent,
    version: document.querySelector('#sidebarVersion').textContent
  })));
  await page.screenshot({ path: path.join(out, '01-overview.png') });

  await page.locator('[data-view=monitoring]').click();
  await page.waitForTimeout(8000);
  step('monitoring', await page.evaluate(() => ({
    cpu: document.querySelector('#cpuLoad').textContent,
    ram: document.querySelector('#ramLoad').textContent,
    gpu: document.querySelector('#gpuLoad').textContent + ' / ' + document.querySelector('#gpuMeta').textContent,
    net: document.querySelector('#networkRate').textContent,
    liveLegend: document.querySelector('#liveLoadChart .aegis-chart-legend')?.innerText,
    livePoints: (document.querySelector('#liveLoadChart path.aegis-series.s-cpu')?.getAttribute('d') || '').split('L').length,
    processes: document.querySelector('#processCount').textContent,
    temps: document.querySelector('#temperatureList').innerText.slice(0, 200)
  })));
  // Hover the live chart to exercise the tooltip
  await page.locator('#liveLoadChart').scrollIntoViewIfNeeded();
  const box = await page.locator('#liveLoadChart svg.aegis-chart-svg').boundingBox();
  if (box) await page.mouse.move(box.x + box.width * 0.8, box.y + box.height / 2);
  await page.waitForTimeout(300);
  step('tooltip', { visible: await page.locator('#liveLoadChart .aegis-chart-tooltip').isVisible() });
  await page.locator('#liveLoadChart').scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(out, '02-monitoring.png') });

  await page.locator('[data-view=lab]').click();
  await page.waitForTimeout(500);
  await page.screenshot({ path: path.join(out, '03-lab.png'), fullPage: true });
  await page.locator('#labConsent').check().catch(() => {});

  async function runTest(kind, opts = {}) {
    await page.locator(`.lab-pick[data-kind=${kind}]`).click();
    await page.locator('#labConsent').check();
    if (opts.duration) await page.selectOption('#labDuration', String(opts.duration));
    if (opts.disk) await page.selectOption('#labDiskSize', String(opts.disk));
    if (opts.memory) await page.locator('#labMemory').fill(String(opts.memory));
    const t0 = Date.now();
    await page.locator('#labStart').click();
    await page.waitForTimeout(opts.midMs || 5000);
    const mid = await page.evaluate(() => ({
      status: document.querySelector('#labStatus').textContent,
      outcome: document.querySelector('#labOutcome').textContent,
      stopEnabled: !document.querySelector('#labStop').disabled,
      cpu: document.querySelector('#labActual-cpu').textContent,
      ram: document.querySelector('#labActual-ram').textContent,
      band: !!document.querySelector('#labLiveChart .aegis-band')
    }));
    await page.locator('#labLiveChart').scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(out, `04-lab-${kind}-running.png`) });
    if (opts.stop) {
      await page.locator('#labStop').click();
    }
    await page.waitForFunction(() => document.querySelector('#labStop').disabled, null, { timeout: 150000 });
    const end = await page.evaluate(() => ({
      summary: document.querySelector('#labResultSummary').innerText,
      status: document.querySelector('#labStatus').textContent
    }));
    step('lab-' + kind + (opts.stop ? '-stop' : ''), { seconds: (Date.now() - t0) / 1000, mid, end });
  }
  await runTest('cpu', { duration: 10 });
  await runTest('ram', { duration: 10, memory: 512 });
  await runTest('gpu', { duration: 10 });
  await runTest('disk', { disk: 256, midMs: 300 });
  await runTest('network', { duration: 10 });
  await runTest('cpu', { duration: 30, stop: true, midMs: 4000 });
  await page.locator('.lab-results-card').scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(out, '05-lab-results.png'), fullPage: true });
  step('lab-history', { rows: await page.locator('.lab-run-row').allInnerTexts() });

  await page.locator('#labBsodButton').click();
  await page.waitForTimeout(2500);
  step('bsod', await page.evaluate(() => {
    const b = document.querySelector('#bsod');
    return { shown: b.classList.contains('show'), bg: getComputedStyle(b).backgroundColor, label: document.querySelector('#bsodModeLabel').textContent };
  }));
  await page.screenshot({ path: path.join(out, '06-bsod.png') });
  await page.keyboard.press('Escape');
  await page.waitForTimeout(600);
  step('bsod-exit', { shown: await page.evaluate(() => document.querySelector('#bsod').classList.contains('show')) });
  await page.screenshot({ path: path.join(out, '07-after-bsod.png') });
} catch (error) {
  report.failure = String(error?.stack || error);
  console.error(error);
  process.exitCode = 1;
} finally {
  report.ended = new Date().toISOString();
  save();
  await browser.close();
}
