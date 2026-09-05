// Run against a local Vite server or installed panel. All API traffic uses fixtures.
// MCPANEL_WEB_URL=http://127.0.0.1:5173 npm run test:browser
import assert from 'node:assert/strict';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { chromium } from 'playwright';

const base = process.env.MCPANEL_WEB_URL || 'http://127.0.0.1:5173';
const evidence = await mkdtemp(join(tmpdir(), 'mcpanel-file-views-'));
const browser = await chromium.launch({ executablePath: process.env.MCPANEL_CHROMIUM_PATH || undefined });
const original = '[05:58:05] [ServerMain/INFO]: Starting server\n[05:58:06] [Server thread/WARN]: Offline mode\n[05:58:07] [Server thread/ERROR]: Failed\njava.lang.IllegalStateException: Closed\n\tat net.minecraft.Main.main(Main.java:42)\n'
  + '[05:58:08] [Server thread/INFO]: Environment: ' + 'https://sessionserver.mojang.com/'.repeat(120) + '\n';
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII=', 'base64');
const server = { id: 'one', name: 'Survival server with a long name', iconRevision: 'icon', kind: 'Paper', version: '1.21.8', state: 'Running', port: 25565, memoryMb: 4096, playerCount: 2, maxPlayers: 20, cpuPercent: 5, memoryUsedMb: 1240, uptimeSeconds: 120, restartRequired: false, startOnBoot: false };
const files = ['latest.log', 'server-icon.PNG', 'broken.webp', 'settings.json'].map(name => ({name, path:name, isDirectory:false, size:2048, modifiedAt:'2026-09-05T00:00:00Z'}));

try {
  for (const [theme, width, height] of [['light', 1440, 1000], ['dark', 1440, 1000], ['dark', 390, 844]]) {
    const page = await browser.newPage({ viewport: { width, height } });
    const errors = [];
    const saves = [];
    let textReads = 0;
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(theme => localStorage.setItem('theme', theme), theme);
    await page.route('**/api/v1/**', async route => {
      const req = route.request();
      const url = new URL(req.url());
      const p = url.pathname.replace('/api/v1', '');
      if (req.method() === 'PUT' && p.endsWith('/files/content')) {
        saves.push(req.postDataJSON());
        return route.fulfill({status:204});
      }
      assert.equal(req.method(), 'GET', `Unexpected mutation: ${p}`);
      if (p.endsWith('/icon') || p.startsWith('/icons/')) return route.fulfill({contentType:'image/png', body:png});
      if (p.endsWith('/files/download')) return route.fulfill({contentType:'application/octet-stream', body:url.searchParams.get('path') === 'broken.webp' ? Buffer.from('not an image') : png});
      let body = [];
      if (p === '/auth/status') body = {authenticated:true, setupRequired:false};
      else if (p === '/auth/antiforgery') body = {token:'test-token'};
      else if (p === '/servers') body = [server, {...server,id:'two',name:'Proxy',kind:'Gate',iconRevision:null}];
      else if (p === '/servers/one') body = server;
      else if (p === '/system/status') body = {cpuPercent:5,memoryUsedBytes:1e9,memoryTotalBytes:8e9,diskUsedBytes:2e9,diskTotalBytes:1e11,samples:[]};
      else if (p === '/system/settings') body = {keepServersRunningOnPanelStop:true,globalServerHost:'localhost',revision:'1'};
      else if (p === '/icons') body = [{revision:'icon',createdAt:'2026-09-05T00:00:00Z'}];
      else if (p.endsWith('/files')) body = files;
      else if (p.endsWith('/files/content')) { textReads++; body = {content:url.searchParams.get('path').endsWith('.json') ? '{"online": true}' : original, revision:'revision-1'}; }
      await route.fulfill({json:body});
    });

    await page.goto(base + '/servers/one/files');
    await page.getByRole('button', {name:'latest.log',exact:true}).click();
    const dialog = page.getByRole('dialog');
    await page.locator('.cm-content').waitFor();
    await page.locator('.log-error').first().waitFor();
    const dimensions = await page.evaluate(() => {
      const d = document.querySelector('[role=dialog]').getBoundingClientRect();
      const e = document.querySelector('.cm-editor').getBoundingClientRect();
      const s = document.querySelector('.cm-scroller');
      const f = document.querySelector('[data-slot=dialog-footer]').getBoundingClientRect();
      return {contained:e.right<=d.right && e.left>=d.left && e.bottom<=f.top+1, wrapped:s.scrollWidth<=s.clientWidth+1, footerVisible:f.bottom<=innerHeight, fontSize:parseFloat(getComputedStyle(document.querySelector('.cm-content')).fontSize)};
    });
    assert.ok(dimensions.contained && dimensions.wrapped && dimensions.footerVisible, JSON.stringify(dimensions));
    assert.ok(dimensions.fontSize >= 14);
    const colors = await page.evaluate(() => ['.log-timestamp','.log-info','.log-warning','.log-error'].map(selector => getComputedStyle(document.querySelector(selector)).color));
    assert.equal(new Set(colors).size, 4, 'Log tokens need distinct colors');
    await page.screenshot({path:join(evidence,`logs-${theme}-${width}.png`)});
    await dialog.getByRole('button',{name:'Wrap lines',exact:true}).click();
    await page.waitForFunction(() => document.querySelector('.cm-scroller').scrollWidth > document.querySelector('.cm-scroller').clientWidth);
    assert.ok(await page.evaluate(() => document.querySelector('.cm-editor').getBoundingClientRect().right <= document.querySelector('[role=dialog]').getBoundingClientRect().right));
    await dialog.getByRole('button',{name:'Wrap lines',exact:true}).click();
    await page.locator('.cm-content').focus();
    await page.keyboard.press('Control+End');
    await page.keyboard.type('Saved edit');
    await dialog.getByRole('button',{name:'Close',exact:true}).first().click();
    await page.getByRole('alertdialog').getByRole('button',{name:'Keep editing',exact:true}).click();
    await dialog.getByRole('button',{name:'Save file',exact:true}).click();
    await dialog.waitFor({state:'hidden'});
    assert.equal(saves.length, 1);
    assert.deepEqual(saves[0], {content:original+'Saved edit',revision:'revision-1'});

    const beforeImage = textReads;
    await page.getByRole('button',{name:'server-icon.PNG',exact:true}).click();
    await dialog.getByRole('img',{name:'server-icon.PNG',exact:true}).waitFor();
    await page.waitForFunction(() => document.querySelector('[role=dialog] img')?.naturalWidth === 1);
    assert.equal(textReads, beforeImage, 'Images must not use the text API');
    const source = await dialog.locator('img').getAttribute('src');
    assert.ok(source.startsWith('blob:'));
    await page.screenshot({path:join(evidence,`image-${theme}-${width}.png`)});
    await dialog.getByRole('button',{name:'Close',exact:true}).first().click();
    await dialog.waitFor({state:'hidden'});
    assert.equal(await page.evaluate(async url => {try {await fetch(url); return false} catch {return true}}, source), true, 'Preview URL must be revoked');
    await page.getByRole('button',{name:'broken.webp',exact:true}).click();
    await page.getByText('Could not preview image',{exact:true}).waitFor();
    await dialog.getByRole('button',{name:'Close',exact:true}).first().click();
    await dialog.waitFor({state:'hidden'});
    await page.getByRole('button',{name:'settings.json',exact:true}).click();
    await page.locator('.cm-content').waitFor();
    assert.ok(await page.locator('.cm-line span').count() > 0, 'JSON highlighting must remain');
    await dialog.getByRole('button',{name:'Close',exact:true}).first().click();
    await dialog.waitFor({state:'hidden'});

    await page.goto(base+'/');
    await page.getByRole('heading',{name:'Servers',exact:true}).waitFor();
    const assertIcons = async () => {
      const icons = await page.locator('[data-slot=avatar]').evaluateAll(elements => elements.map(e => {
        const rect=e.getBoundingClientRect(), style=getComputedStyle(e);
        const child=e.querySelector('[data-slot=avatar-image], [data-slot=avatar-fallback]');
        return {width:rect.width,height:rect.height,radius:style.borderRadius,childRadius:child && getComputedStyle(child).borderRadius};
      }));
      assert.ok(icons.length > 0);
      for (const icon of icons.filter(icon=>icon.width>0)) { assert.equal(icon.width,icon.height); assert.equal(icon.radius,'20%'); assert.equal(icon.childRadius,'20%'); }
    };
    await assertIcons();
    if (width > 800) { await page.locator('[data-slot=sidebar-trigger]').click(); await page.waitForTimeout(250); await assertIcons(); }
    assert.deepEqual(errors, []);
    console.log(`${theme} ${width}px: editor bounds, wrapping, highlights, save/discard, image preview, cleanup, and icons passed`);
    await page.close();
  }
  console.log(`Screenshots: ${evidence}`);
} finally { await browser.close(); }
