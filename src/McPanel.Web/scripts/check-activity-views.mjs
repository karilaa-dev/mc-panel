// MCPANEL_WEB_URL=http://127.0.0.1:5174 node scripts/check-activity-views.mjs
import assert from 'node:assert/strict';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { chromium } from 'playwright';
const base = process.env.MCPANEL_WEB_URL || 'http://127.0.0.1:5174';
const evidence = await mkdtemp(join(tmpdir(), 'mcpanel-activity-'));
const browser = await chromium.launch({ executablePath: process.env.MCPANEL_CHROMIUM_PATH || undefined });
try {
  for (const [theme, width, height] of [['dark', 1440, 1000], ['light', 1440, 1000], ['dark', 390, 844]]) {
    const page = await browser.newPage({ viewport: { width, height } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(theme => localStorage.setItem('theme', theme), theme);
    let incidents = [
      {id:'one',code:'RECOVERY_BUNDLE_FAILED',message:"Access to the path '/etc/mcpanel/mcpanel.env' is denied.",openedAt:'2026-09-05T00:00:00Z'},
      {id:'two',serverId:'survival',code:'BACKUP_FAILED',message:'The backup destination is full. Free some disk space and retry.',openedAt:'2026-09-05T01:00:00Z'},
    ];
    await page.route('**/api/v1/**', async route => {
      const p = new URL(route.request().url()).pathname.replace('/api/v1', '');
      assert.equal(route.request().method(), 'GET');
      let body = [];
      if (p === '/auth/status') body = {authenticated:true, setupRequired:false};
      else if (p === '/auth/antiforgery') body = {token:'test-token'};
      else if (p === '/servers') body = [{id:'survival',name:'Survival',kind:'Paper',version:'1.21',state:'Stopped'}, {id:'creative',name:'Creative',kind:'Vanilla',version:'1.21',state:'Stopped'}];
      else if (p === '/incidents') body = incidents;
      else if (p === '/jobs') body = [
        {id:'job',type:'Backup',serverId:'survival',state:'Failed',canRetry:true,message:'Failed',error:"Access to the path '/etc/mcpanel/mcpanel.env' is denied.",createdAt:'2026-09-05T00:00:00Z'},
        {id:'active',type:'Backup',serverId:'survival',state:'Running',progress:67,message:'Copying world files',createdAt:'2026-09-05T01:00:00Z'},
      ];
      else if (p === '/recovery') body = {configured:false,intervalMinutes:30,points:[]};
      else if (p === '/system/info') body = {version:'test',memoryAllocationLimitBytes:8e9};
      else if (p === '/system/settings') body = {keepServersRunningOnPanelStop:true,revision:'1'};
      await route.fulfill({json:body});
    });
    await page.goto(base + '/activity');
    await page.getByText('Panel backup failed', {exact:true}).waitFor();
    assert.equal(await page.getByText('Machine recovery', {exact:true}).count(),0);
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Activity overflows');
    const operations = page.getByRole('region', {name:'Operations', exact:true});
    await operations.getByText('Failed', {exact:true}).first().waitFor();
    const activeRow = operations.getByRole('row').filter({has: page.getByRole('progressbar')});
    if (width < 600) {
      const title = await activeRow.locator('[data-field=title]').boundingBox();
      const progress = await activeRow.getByRole('progressbar').boundingBox();
      assert.ok(title && progress && progress.y >= title.y + title.height, 'Progress crowds the operation title');
    }
    await operations.getByRole('button', {name:'Retry', exact:true}).waitFor();
    const actionsFit = await operations.locator('table').evaluate(table => {
      const panel = table.closest('section').getBoundingClientRect();
      return [...table.querySelectorAll('tbody tr, tbody button, tbody a')].every(element => {
        const bounds = element.getBoundingClientRect();
        return bounds.left >= panel.left - 1 && bounds.right <= panel.right + 1;
      });
    });
    assert.ok(actionsFit, `Operation details or actions extend beyond the panel at ${width}px`);
    await page.screenshot({path:join(evidence,`activity-${theme}-${width}.png`), fullPage:true});
    await page.getByRole('link',{name:'Panel backups',exact:true}).click();
    await page.getByRole('button',{name:'Create panel backup'}).waitFor();
    await page.getByRole('button', {name:'Choose instances',exact:true}).click();
    await page.getByRole('checkbox', {name:'Survival',exact:true}).check();
    assert.ok(await page.getByRole('button', {name:'Export 1 selected instance',exact:true}).isEnabled());
    assert.ok(await page.getByRole('tab',{name:'Backups',exact:true}).getAttribute('aria-selected') === 'true');
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Settings overflows');
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({path:join(evidence,`backups-${theme}-${width}.png`), fullPage:true});
    incidents = [];
    await page.goto(base + '/activity');
    await page.getByText('Nothing needs attention').waitFor();
    assert.deepEqual(errors, []);
    await page.close();
  }
  console.log(`Activity and backups desktop/mobile checks passed. Screenshots: ${evidence}`);
} finally { await browser.close(); }
