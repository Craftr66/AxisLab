import {spawn,execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {createServer} from 'node:net';
import {mkdir,readFile,writeFile} from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import {chromium} from 'playwright';
const out=path.resolve('artifacts/wpf-v2-checks',String(Date.now()));await mkdir(out,{recursive:true});
async function freePort(){const s=createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));const p=s.address().port;await new Promise(r=>s.close(r));return p;}
const base=`http://127.0.0.1:${await freePort()}`,port=await freePort();
const host=spawn('dotnet',[path.resolve('src/Host/bin/Release/net10.0/AxisLab.Host.dll')],{cwd:path.resolve('src/Host'),windowsHide:true,env:{...process.env,AXISLAB_URL:base,AXISLAB_DATA:path.join(out,'projects'),AXISLAB_MODBUS_PORT:'0',Logging__LogLevel__Default:'Warning'}});
let log='',browser;host.stdout.on('data',b=>log+=b);host.stderr.on('data',b=>log+=b);
try {
 const end=Date.now()+25000;while(!await fetch(base+'/api/health').then(r=>r.ok).catch(()=>false)){if(Date.now()>end)throw new Error('Host startup timeout '+log);await new Promise(r=>setTimeout(r,100));}
 const scene=JSON.parse(await readFile('samples/oriented-positioning.scene.json','utf8'));const state=await(await fetch(base+'/api/v1/project')).json();
 const imported=await fetch(base+'/api/v1/project/import',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({expectedRevision:state.revision,project:scene})});assert.ok(imported.ok,await imported.text());
 browser=await chromium.launch({channel:'msedge',headless:true,args:['--enable-unsafe-swiftshader','--use-angle=swiftshader']});const page=await browser.newPage({viewport:{width:1440,height:1000}});await page.goto(base);await page.waitForSelector('#viewport canvas');
 const config=path.join(out,'config.json'),report=path.join(out,'results.json');await writeFile(config,JSON.stringify({url:base,port}));
 await promisify(execFile)(path.resolve(process.env.AXISLAB_WPF_EXE??'src/WpfDemo/bin/Release/net9.0-windows/AxisLab.WpfDemo.exe'),['--direct-test',config,report],{cwd:out,windowsHide:true,timeout:120000,env:{...process.env,AXISLAB_WPF_DATA:path.join(out,'wpf-data'),...(process.env.AXISLAB_WPF_EXE?{DOTNET_ROOT:path.join(out,'no-dotnet'),DOTNET_ROOT_X64:path.join(out,'no-dotnet'),DOTNET_MULTILEVEL_LOOKUP:'0'}:{})}});
 const result=JSON.parse(await readFile(report,'utf8'));assert.equal(result.success,true);for(const check of result.checks)console.log('PASS: '+check);console.log('Artifacts: '+out);
}catch(e){console.error(await readFile(path.join(out,'results.json'),'utf8').catch(()=>''));throw e;}
finally{await browser?.close();host.kill();await writeFile(path.join(out,'host.log'),log);}
