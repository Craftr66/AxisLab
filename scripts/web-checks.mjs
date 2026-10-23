import {spawn,execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {createServer} from 'node:net';
import {mkdir,writeFile} from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
const out=path.resolve('artifacts/web-checks',String(Date.now()));await mkdir(out,{recursive:true});
const socket=createServer();await new Promise(r=>socket.listen(0,'127.0.0.1',r));const port=socket.address().port;await new Promise(r=>socket.close(r));const base=`http://127.0.0.1:${port}`;
const host=spawn('dotnet',[path.resolve('src/Host/bin/Release/net10.0/AxisLab.Host.dll')],{cwd:path.resolve('src/Host'),windowsHide:true,env:{...process.env,AXISLAB_URL:base,AXISLAB_DATA:path.join(out,'projects'),AXISLAB_MODBUS_PORT:'0',Logging__LogLevel__Default:'Warning'}});
let log='';host.stdout.on('data',b=>log+=b);host.stderr.on('data',b=>log+=b);
try{
 const end=Date.now()+25000;while(!await fetch(base+'/api/health').then(r=>r.ok).catch(()=>false)){if(Date.now()>end)throw new Error(log);await new Promise(r=>setTimeout(r,100));}
 for(const endpoint of ['/program','/program/status','/program/run','/program/stop'])assert.equal((await fetch(base+'/api/v1'+endpoint,{method:endpoint.endsWith('/run')||endpoint.endsWith('/stop')?'POST':'GET'})).status,404);
 console.log('PASS: retired program endpoints return 404');
 for(const script of ['ui-checks.mjs','camera-checks.mjs','motion-checks.mjs']){const r=await promisify(execFile)(process.execPath,[path.resolve('scripts',script)],{windowsHide:true,env:{...process.env,AXISLAB_TEST_URL:base},timeout:180000,maxBuffer:2*1024*1024});console.log(r.stdout.trim());}
}finally{host.kill();await writeFile(path.join(out,'host.log'),log);}
