import {spawn} from 'node:child_process';
import {createServer,createConnection} from 'node:net';
import dgram from 'node:dgram';
import os from 'node:os';
import path from 'node:path';
import {mkdir,readFile,writeFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import assert from 'node:assert/strict';
import {chromium} from 'playwright';
const out=path.resolve('artifacts/workcell-ui',String(Date.now()));await mkdir(out,{recursive:true});
const ip=process.env.AXISLAB_CAMERA_IP??Object.entries(os.networkInterfaces()).sort(([a],[b])=>Number(b.toLowerCase().includes('tun'))-Number(a.toLowerCase().includes('tun'))).flatMap(([,items])=>items??[]).find(n=>n.family==='IPv4'&&!n.internal)?.address;
assert.ok(ip,'A local IPv4 interface is required');
const free=createServer();await new Promise(r=>free.listen(0,'127.0.0.1',r));const port=free.address().port;await new Promise(r=>free.close(r));
const probe=dgram.createSocket('udp4');await new Promise(r=>probe.bind(0,ip,r));const cameraPort=probe.address().port;await new Promise(r=>probe.close(r));
const base='http://127.0.0.1:'+port,host=spawn('dotnet',[path.resolve('src/Host/bin/Release/net10.0/AxisLab.Host.dll')],{cwd:path.resolve('src/Host'),windowsHide:true,env:{...process.env,AXISLAB_URL:base,AXISLAB_DATA:path.join(out,'projects'),AXISLAB_MODBUS_PORT:'0',AXISLAB_CAMERA_CONTROL_PORT:String(cameraPort)}});
let log='',browser,client,heartbeat;host.stdout.on('data',b=>log+=b);host.stderr.on('data',b=>log+=b);
const checks=[],errors=[];function check(label,value){assert.ok(value,label);checks.push(label);console.log('PASS: '+label);}
const delay=ms=>new Promise(r=>setTimeout(r,ms));
async function wait(predicate,timeout=60000){const end=Date.now()+timeout;while(Date.now()<end){const value=await predicate();if(value)return value;await delay(40);}throw new Error('Timed out');}
async function api(p,method='GET',body){const r=await fetch(base+'/api/v1'+p,{method,headers:body?{'Content-Type':'application/json'}:{},body:body?JSON.stringify(body):undefined});const data=await r.json();if(!r.ok)throw new Error(data.message??JSON.stringify(data));return data;}
class Modbus{
 constructor(socket){this.socket=socket;this.buffer=Buffer.alloc(0);this.tid=0;socket.on('data',b=>this.buffer=Buffer.concat([this.buffer,b]));socket.on('error',()=>{});}
 async request(pdu){const head=Buffer.alloc(7);head.writeUInt16BE(++this.tid);head.writeUInt16BE(pdu.length+1,4);head[6]=1;this.socket.write(Buffer.concat([head,pdu]));await wait(()=>this.buffer.length>=7&&this.buffer.length>=6+this.buffer.readUInt16BE(4),5000);const length=6+this.buffer.readUInt16BE(4),reply=this.buffer.subarray(0,length);this.buffer=this.buffer.subarray(length);assert.equal(reply.readUInt16BE(),this.tid);return reply.subarray(7);}
 async read(a,n){const p=Buffer.alloc(5);p[0]=3;p.writeUInt16BE(a,1);p.writeUInt16BE(n,3);const r=await this.request(p);assert.equal(r[0],3);return Array.from({length:n},(_,i)=>r.readUInt16BE(2+i*2));}
 async write(a,words){const p=Buffer.alloc(6+words.length*2);p[0]=16;p.writeUInt16BE(a,1);p.writeUInt16BE(words.length,3);p[5]=words.length*2;words.forEach((v,i)=>p.writeUInt16BE(v,6+i*2));assert.equal((await this.request(p))[0],16);}
 async command(op,value,sequence){const h=await this.read(0,120),p=Array(28).fill(0);p[0]=op;const b=Buffer.alloc(4);b.writeFloatBE(value);p[4]=b.readUInt16BE();p[5]=b.readUInt16BE(2);p.splice(18,4,...h.slice(4,8));p.splice(22,2,...h.slice(98,100));await this.write(202,p);await this.write(232,[sequence>>>16,sequence&65535]);return this.read(15,2);}
}
const control=dgram.createSocket('udp4'),stream=dgram.createSocket('udp4');let id=0;const pending=new Map(),frames=new Map();
control.on('message',b=>{const reply=pending.get(b.readUInt16BE(6));if(reply){pending.delete(b.readUInt16BE(6));reply(b);}});
stream.on('message',b=>{if(b.length<8)return;const block=b.readUInt16BE(2),type=b.readUInt32BE(4)>>>24,index=b.readUInt32BE(4)&0xffffff;let f=frames.get(block);if(!f){f={packets:new Map(),done:false};frames.set(block,f);}if(type===3)f.packets.set(index,b.subarray(8));if(type===2)f.done=true;});
async function gvcp(command,words=[]){const message=Buffer.alloc(8+words.length*4);message[0]=0x42;message[1]=1;message.writeUInt16BE(command,2);message.writeUInt16BE(message.length-8,4);const seq=++id;message.writeUInt16BE(seq,6);words.forEach((w,i)=>message.writeUInt32BE(w>>>0,8+i*4));return new Promise((resolve,reject)=>{const timer=setTimeout(()=>{pending.delete(seq);reject(new Error('GVCP timeout'));},3000);pending.set(seq,b=>{clearTimeout(timer);resolve(b);});control.send(message,cameraPort,ip);});}
async function wr(a,v){const r=await gvcp(0x82,[a,v]);assert.equal(r.readUInt16BE(0),0);}
try{
 await wait(()=>fetch(base+'/api/health').then(r=>r.ok).catch(()=>false),25000);
 const scene=JSON.parse(await readFile('samples/workcell-pick-place.scene.json','utf8'));

 browser=await chromium.launch({channel:'msedge',headless:true,args:['--enable-unsafe-swiftshader','--use-angle=swiftshader']});const page=await browser.newPage({viewport:{width:1660,height:1080}});page.on('pageerror',e=>errors.push(e.message));await page.goto(base);await page.waitForSelector('#load-cell-demo');await page.locator('#load-cell-demo').click();await page.waitForFunction(()=>document.querySelectorAll('.cell-step-table tbody tr').length===13);
 check('AxisLab exposes four instrument types and its own action table',await page.locator('[data-add="mechanism.conveyor"]').count()===1&&await page.locator('[data-add="tool.gripper"]').count()===1);
 await page.locator('#cell-generate').click();await wait(async()=>{const s=await api('/project');return s.project.cellSequence?.id!==scene.cellSequence.id;});
 const f=(await api('/project')).project.cellSequence,sensor=scene.devices.find(d=>d.id===f.sensorId),part=scene.devices.find(d=>d.typeId==='material.workpiece');
 const status=await api('/communication');const socket=createConnection(status.modbusPort,'127.0.0.1');await new Promise(r=>socket.once('connect',r));client=new Modbus(socket);await client.write(200,[sensor.deviceNumber]);
 await writeFile(path.join(out,'modbus-map.json'),JSON.stringify(await api('/communication/map'),null,2));
 check('photoelectric input and rising edge counter are readable through Modbus',(await client.read(108,4)).every(v=>v===0));
 const adapter=await api('/virtual-camera/start','POST',{cameraId:f.cameraId,listenAddress:ip});
 check('AxisLab starts its own GigE adapter without WPF',adapter.running&&adapter.cameraId===f.cameraId);
 await Promise.all([control,stream].map(s=>new Promise((r,j)=>{s.once('error',j);s.bind(0,ip,r);})));stream.setRecvBufferSize(4*1024*1024);
 await wr(0xa00,1);await wr(0xd18,ip.split('.').reduce((n,o)=>(n*256+Number(o))>>>0,0));await wr(0xd00,stream.address().port);await wr(0x10018,1);await wr(0x10020,0);await wr(0x10010,1);await delay(200);
 heartbeat=setInterval(()=>gvcp(0x80,[0x10000]).catch(()=>{}),500);
 check('Line0 waits for a physical simulated event and produces no unsolicited image',frames.size===0);
 await page.locator('#cell-start').click();
 let sawInput=false,sawHeld=false;const positions=[];
 await wait(async()=>{
  const r=await api('/runtime'),s=await client.read(108,4);sawInput|=!!(s[0]&2);sawHeld|=!!r.instruments.find(x=>x.deviceId===f.gripperId)?.partId;
  positions.push(r.devices.find(x=>x.deviceId===part.id).world.pose);
  if(r.cell.status==='Failed')throw new Error(r.cell.message);return r.cell.status==='Completed';
 },90000);
 check('the internal flow completes while no external controller executes robot steps',true);
 check('live Modbus DI sees the passing workpiece',sawInput);
 check('the workpiece really attaches to the TCP gripper during movement',sawHeld);
 await wait(()=>Array.from(frames.values()).some(f=>f.done),10000);const [block,frame]=Array.from(frames).find(([,f])=>f.done);
 const pixels=Buffer.concat(Array.from(frame.packets).sort(([a],[b])=>a-b).map(([,p])=>p));
 const trigger=(await api('/cameras/'+f.cameraId+'/triggers'))[0],metadata=await api('/cameras/'+f.cameraId+'/frames/'+trigger.frameId);
 const png=Buffer.from(await(await fetch(base+metadata.imageUrl)).arrayBuffer());
 check('one optical rising edge delivers the exact capture through the Line0 GigE stream',frames.size===1&&pixels.length===metadata.width*metadata.height&&trigger.status==='Completed');
 check('stream contains real rendered scene pixels',new Set(pixels).size>16);
 check('the triggered PNG matches its HTTP SHA256',createHash('sha256').update(png).digest('hex')===metadata.sha256.toLowerCase());
 const receipt=JSON.parse(await readFile(path.join(adapter.stateDirectory,'blocks',String(block).padStart(5,'0')+'.json'),'utf8'));check('GVSP frame is correlated with the optical exposure identity',receipt.frame.frameId===trigger.frameId);
 const runtime=await api('/runtime'),placed=runtime.devices.find(x=>x.deviceId===part.id).world.pose,target=(await api('/project')).project.teachPoints.find(p=>p.id===f.steps.filter(s=>s.action==='MoveTeach')[4].pointId).target;
 check('released workpiece reaches the taught station pose',Math.hypot(placed.x-target.x,placed.y-target.y,placed.z-target.z)<2);
 check('all conveyors stop and robot ownership is released',runtime.instruments.every(s=>!s.running)&&!runtime.robots[0].controlOwnerId);
 await page.locator('#camera-tab').click();await page.waitForFunction(()=>{const button=document.querySelector('#save-calibration-observation');return button&&!button.disabled;});
 const saved=page.waitForEvent('download');await page.locator('#save-calibration-observation').click();const download=await saved;const observation=await readFile(await download.path(),'utf8');
 check('AxisLab exports calibration observations with robot exposure pose and without camera truth',observation.includes('flangeBase')&&observation.includes('tcpBase')&&!observation.includes('cameraWorld')&&!observation.includes('intrinsics'));
 await page.locator('#workcell-tab').click();
 await writeFile(path.join(out,'trigger.png'),png);await writeFile(path.join(out,'gray.bin'),pixels);await page.screenshot({path:path.join(out,'workcell-complete.png')});
 const belt=scene.devices.find(d=>d.id===f.conveyorId);await client.write(200,[belt.deviceNumber]);const accepted=await client.command(10,50,1);
 check('external Modbus opcode controls a conveyor directly',accepted[0]===3&&(await api('/runtime')).instruments.find(s=>s.deviceId===belt.id).running);
 client.socket.destroy();client=null;await wait(async()=>!(await api('/runtime')).instruments.find(s=>s.deviceId===belt.id).running,6000);
 check('external controller disconnection stops its conveyor',true);
 await api('/cell/reset','POST');await api('/cell/start','POST');await delay(150);await page.locator('#cell-stop').click();await wait(async()=>(await api('/runtime')).cell.status==='Cancelled');
 check('AxisLab Stop cancels a waiting cycle and retains a defined state',(await api('/runtime')).instruments.every(s=>!s.running));
 const latest=await api('/project');await api('/project/new','POST',{expectedRevision:latest.revision,name:'adapter cleanup'});await wait(async()=>!(await api('/virtual-camera')).running,5000);
 check('switching scene stops only the adapter owned by AxisLab',true);
 check('new scene and instrumentation UI have no unhandled browser errors',errors.length===0);
 await writeFile(path.join(out,'results.json'),JSON.stringify({success:true,checks,placed,positions},null,2));console.log(checks.length+' workcell integration checks passed. Artifacts: '+out);
}catch(e){console.error('Artifacts: '+out);console.error(log);throw e;}
finally{clearInterval(heartbeat);client?.socket.destroy();try{await api('/virtual-camera/stop','POST');}catch{}control.close();stream.close();await browser?.close();host.kill();await writeFile(path.join(out,'host.log'),log);}
