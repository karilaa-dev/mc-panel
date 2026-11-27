// Run against Vite or the installed panel. All API operations use fixtures.
import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const base = process.env.MCPANEL_WEB_URL || 'http://127.0.0.1:5173';
const server = { id:'survival', name:'Survival', kind:'Paper', state:'Running', version:'1.21.8', port:25565, memoryMb:4096, playerCount:2, maxPlayers:20, cpuPercent:1, memoryUsedMb:128, uptimeSeconds:180, restartRequired:false, startOnBoot:false };
const properties = { revision:'1', minecraftVersion:server.version, available:[], entries:[
  { key:'max-players', value:'20', type:'integer', section:'General', catalogued:true, compatibility:'Supported', supportedRanges:[] },
] };
const browser = await chromium.launch({ executablePath:process.env.MCPANEL_CHROMIUM_PATH || undefined });
try {
  const page = await browser.newPage({viewport:{width:1280,height:800}});
  let job;
  let restarts = 0;
  await page.route('**/api/v1/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname.slice('/api/v1'.length);
    if (path === '/servers/survival/actions/restart' && request.method() === 'POST') {
      restarts++;
      job = {id:'restart-1', type:'Restart', state:'Queued', serverId:server.id, progress:0, canCancel:true};
      return route.fulfill({json:job});
    }
    if (path === '/jobs/restart-1/cancel' && request.method() === 'POST') {
      job = {...job, state:'Canceled', canCancel:false};
      return route.fulfill({json:job});
    }
    assert.equal(request.method(), 'GET', path);
    const data = {
      '/auth/status':{authenticated:true,setupRequired:false}, '/auth/antiforgery':{token:'fixture'},
      '/servers':[server], '/servers/survival':server, '/servers/survival/properties':properties,
      '/jobs':job ? [job] : [], '/jobs/restart-1':job,
    };
    await route.fulfill({json:data[path] ?? []});
  });
  await page.goto(base + '/servers/survival/files');
  await page.getByRole('heading', {name:'Files', exact:true}).waitFor();
  await page.keyboard.press('Tab');
  assert.equal(await page.getByRole('link', {name:'Skip to content'}).evaluate(el => el === document.activeElement), true);
  await page.keyboard.press('Enter');
  assert.equal(await page.locator('main').evaluate(el => el === document.activeElement), true);
  await page.getByRole('link', {name:'Server properties', exact:true}).click();
  const players = page.getByRole('textbox', {name:'Max players', exact:true});
  await players.fill('24');
  await page.evaluate(() => history.back());
  const discard = page.getByRole('alertdialog', {name:'Discard unsaved changes?'});
  await discard.getByRole('button', {name:'Keep editing'}).click({timeout:4000});
  assert.equal(await players.inputValue(), '24');
  await page.evaluate(() => history.back());
  await discard.getByRole('button', {name:'Discard changes'}).click();
  await page.waitForURL(base + '/servers/survival/files');
  console.log('Skip link focuses main and browser Back preserves the draft until discard.');

  const restart = page.getByRole('banner').getByRole('button', {name:'Restart', exact:true});
  await restart.click();
  await page.getByRole('banner').getByText('Restarting…', {exact:true}).waitFor();
  await page.getByRole('link', {name:'Activity', exact:true}).click();
  await page.getByRole('button', {name:'Cancel', exact:true}).click();
  await page.getByText('Canceled', {exact:true}).waitFor();
  await page.evaluate(() => history.back());
  await restart.waitFor({timeout:4000});
  assert.ok(await restart.isEnabled());
  assert.equal(restarts, 1);
  console.log('Canceling the queued restart restores controls on return to the server.');
} finally { await browser.close(); }
