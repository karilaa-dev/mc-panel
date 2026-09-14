// All API requests use fixtures. Run against Vite or the installed panel.
// MCPANEL_WEB_URL=http://127.0.0.1:6050 node scripts/check-gate-settings.mjs
import assert from 'node:assert/strict';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { chromium } from 'playwright';
import { defaultGateClassicConfiguration } from '../src/lib/contracts.ts';

const base = process.env.MCPANEL_WEB_URL || 'http://127.0.0.1:5173';
const evidence = await mkdtemp(join(tmpdir(), 'mcpanel-gate-settings-'));
const server = { id:'gate',name:'Edge Gate',kind:'Gate',version:'0.71.1',state:'Stopped',port:25565,memoryMb:2048,playerCount:0,maxPlayers:20,cpuPercent:0,memoryUsedMb:0,uptimeSeconds:0,restartRequired:false,startOnBoot:false };
const lobby = {...server,id:'lobby',name:'Lobby',kind:'Paper',port:25566};
const survival = {...lobby,id:'survival',name:'Survival',port:25567};
const browser = await chromium.launch({ executablePath: process.env.MCPANEL_CHROMIUM_PATH || undefined });
try {
  for (const [theme,width,height] of [['light',1440,1000],['dark',1440,1000],['dark',390,844]]) {
    const page = await browser.newPage({viewport:{width,height}});
    const errors = [];
    const saves = [];
    let gate = {
      serverId:'gate',installation:{installed:true,version:'0.71.1',latestVersion:'0.73.0',updateAvailable:true},
      runtime:{state:'Stopped',desiredRunning:false,activeConnections:0,onlinePlayers:0},
      configuration:{mode:'Classic',defaultServerId:'lobby',backendServerIds:['lobby'],externalBackends:[],backendHostnames:{lobby:'lobby.example.com'},classicForwardingMode:'Velocity',hasVelocitySecret:true,hasBungeeGuardSecret:false,revision:'1',configurationDirty:false,listenerPort:25565,startOnBoot:false,crashRecovery:true,memoryMb:2048,classic:{...defaultGateClassicConfiguration}},
      routes:[{serverId:'lobby',serverName:'Lobby',backendAddress:'127.0.0.1:25566',publicHost:'lobby.example.com',routeKind:'Direct',backendKind:'Managed'}],warnings:[],
    };
    page.on('pageerror',error=>errors.push(error.message));
    page.on('console',message=>{ if(message.type()==='error' && message.text().startsWith('Base UI:')) errors.push(message.text()); });
    await page.addInitScript(theme=>localStorage.setItem('theme',theme),theme);
    await page.route('**/api/v1/**',async route=>{
      const req=route.request(), path=new URL(req.url()).pathname.replace('/api/v1','');
      if(req.method()==='POST' && path.endsWith('/check-backends')) return route.fulfill({json:[]});
      if(req.method()==='PUT' && path.endsWith('/gate/config')) {
        const request=req.postDataJSON(); saves.push(request);
        gate={...gate,configuration:{...gate.configuration,...request,revision:String(Number(gate.configuration.revision)+1)}};
        return route.fulfill({json:gate});
      }
      assert.equal(req.method(),'GET',`Unexpected mutation: ${path}`);
      let body=[];
      if(path==='/auth/status') body={authenticated:true,setupRequired:false};
      else if(path==='/auth/antiforgery') body={token:'fixture-token'};
      else if(path==='/servers') body=[server,lobby,survival];
      else if(path==='/servers/gate') body=server;
      else if(path==='/servers/gate/gate') body=gate;
      else if(path==='/catalog/gate') body=['0.73.0','0.71.1'];
      else if(path==='/system/status') body={cpuPercent:5,memoryUsedBytes:1e9,memoryTotalBytes:8e9,diskUsedBytes:2e9,diskTotalBytes:1e11,samples:[]};
      else if(path==='/system/settings') body={keepServersRunningOnPanelStop:true,globalServerHost:'localhost',revision:'1'};
      await route.fulfill({json:body});
    });
    const fits=async label=>assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),`${label} overflows at ${width}px`);
    const shot=async name=>{await fits(name);await page.screenshot({path:join(evidence,`${name}-${theme}-${width}.png`),fullPage:true});};
    await page.goto(base+'/servers/gate/backends');
    await page.getByRole('radio',{name:'Default backend for Lobby',exact:true}).waitFor();
    assert.ok(await page.getByRole('radio',{name:'Default backend for Lobby',exact:true}).isChecked());
    assert.equal(await page.getByRole('group',{name:'Backend Survival',exact:true}).count(),0);
    await page.getByRole('button',{name:'Add backend',exact:true}).click();
    await page.getByRole('combobox',{name:'Server',exact:true}).click();
    await page.getByRole('option',{name:'Survival · Paper',exact:true}).click();
    await page.getByRole('button',{name:'Add backend',exact:true}).click();
    const defaultSurvival=page.getByRole('radio',{name:'Default backend for Survival',exact:true});
    await defaultSurvival.focus();
    await page.keyboard.press('Space');
    assert.ok(await defaultSurvival.isChecked());
    await page.getByRole('tab',{name:'External address',exact:true}).click();
    await page.getByRole('textbox',{name:'Display name',exact:true}).fill('Remote creative');
    await page.getByRole('textbox',{name:'Backend address',exact:true}).fill('creative.internal:25565');
    await page.getByRole('button',{name:'Add backend',exact:true}).click();
    await page.getByRole('textbox',{name:'Hostname for Remote creative',exact:true}).fill('creative.example.com');
    await page.getByRole('button',{name:'Cancel adding',exact:true}).click();
    await page.getByRole('button',{name:'Save backends',exact:true}).click();
    await page.getByText('Gate backends saved',{exact:true}).waitFor();
    assert.equal(saves[0].defaultServerId,'survival');
    assert.equal(saves[0].externalBackends.length,1);
    await shot('backends');
    await page.getByRole('button',{name:'Remove Survival',exact:true}).click();
    assert.ok(await page.getByRole('button',{name:'Save backends',exact:true}).isDisabled());
    await page.getByRole('radio',{name:'Default backend for Remote creative',exact:true}).click();
    assert.ok(await page.getByRole('button',{name:'Save backends',exact:true}).isEnabled());

    await page.goto(base+'/servers/gate/gate');
    await page.getByRole('tab',{name:'General',exact:true}).waitFor();
    await shot('general');
    await page.getByRole('tab',{name:'Players',exact:true}).click();
    await page.getByRole('button',{name:'Show authentication details',exact:true}).click();
    await page.getByRole('textbox',{name:'Session server URL',exact:true}).fill('https://session.example/hasJoined');
    await shot('players');
    await page.getByRole('tab',{name:'Server list',exact:true}).click();
    await page.getByRole('switch',{name:'Enable GameSpy query',exact:true}).click();
    await page.getByRole('spinbutton',{name:'Query UDP port',exact:true}).fill('25580');
    await shot('server-list');
    await page.getByRole('tab',{name:'Compatibility',exact:true}).click();
    await page.getByRole('switch',{name:'Via protocol translation',exact:true}).click();
    await page.getByRole('switch',{name:'Bedrock cross-play',exact:true}).click();
    await page.getByRole('button',{name:'Show bedrock overrides',exact:true}).click();
    await shot('compatibility');
    await page.getByRole('tab',{name:'Network',exact:true}).click();
    await page.getByRole('textbox',{name:'Connection timeout',exact:true}).fill('12s');
    await shot('network');
    await page.getByRole('tab',{name:'Maintenance',exact:true}).click();
    assert.equal(await page.getByRole('combobox',{name:'Gate release',exact:true}).innerText(),'0.73.0');
    await shot('maintenance');
    await page.getByRole('button',{name:'Save settings',exact:true}).click();
    await page.getByText('Gate settings saved',{exact:true}).waitFor();
    assert.equal(await page.getByRole('tab',{name:'Maintenance',exact:true}).getAttribute('aria-selected'),'true');
    assert.equal(saves[1].classic.connectionTimeout,'12s');
    assert.equal(saves[1].classic.sessionServerUrl,'https://session.example/hasJoined');
    assert.equal(saves[1].classic.queryPort,25580);
    assert.equal(saves[1].classic.viaEnabled,true);
    assert.equal(saves[1].classic.bedrockEnabled,true);
    assert.equal(saves[1].backendHostnames.lobby,'lobby.example.com');
    assert.deepEqual(errors,[]);
    console.log(`${theme} ${width}px: backend add/default/remove, keyboard selection, category edits and layout passed`);
    await page.close();
  }
  console.log(`Screenshots: ${evidence}`);
} finally {await browser.close();}
