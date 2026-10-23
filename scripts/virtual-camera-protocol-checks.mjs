import dgram from 'node:dgram';
import {spawn} from 'node:child_process';
import {mkdir,writeFile} from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
const ip=process.env.AXISLAB_CAMERA_IP??'172.19.0.1',out=path.resolve('artifacts/virtual-camera-protocol',String(Date.now()));await mkdir(out,{recursive:true});
const probe=dgram.createSocket('udp4');await new Promise((r,j)=>{probe.once('error',j);probe.bind(0,ip,r);});const port=probe.address().port;await new Promise(r=>probe.close(r));
const camera=spawn(path.resolve(process.env.AXISLAB_CAMERA_EXE??'src/VirtualCamera/bin/Release/net10.0-windows/AxisLab.VirtualCamera.exe'),['--test-pattern',ip,String(port)],{windowsHide:true});let log='';camera.stdout.on('data',b=>log+=b);camera.stderr.on('data',b=>log+=b);
const control=dgram.createSocket('udp4'),other=dgram.createSocket('udp4'),stream=dgram.createSocket('udp4');
await Promise.all([control,other,stream].map(s=>new Promise((r,j)=>{s.once('error',j);s.bind(0,ip,r);})));let id=0;const frames=[];
stream.on('message',p=>{if(p.length>=8&&(p.readUInt32BE(4)>>>24)===1)frames.push(p.readUInt16BE(2));});
const checks=[];function check(label,condition){assert.ok(condition,label);checks.push(label);console.log('PASS: '+label);}
async function request(cmd,words=[],socket=control,requestId=++id){
 const p=Buffer.alloc(8+words.length*4);p[0]=0x42;p[1]=1;p.writeUInt16BE(cmd,2);p.writeUInt16BE(p.length-8,4);p.writeUInt16BE(requestId,6);words.forEach((w,i)=>p.writeUInt32BE(w>>>0,8+4*i));
 return await new Promise((resolve,reject)=>{const timer=setTimeout(()=>{socket.off('message',receive);reject(new Error('GVCP timeout '+cmd.toString(16)));},2000);function receive(ack){if(ack.readUInt16BE(6)!==requestId)return;clearTimeout(timer);socket.off('message',receive);resolve(ack);}socket.on('message',receive);socket.send(p,port,ip);});
}
async function write(a,v){const ack=await request(0x82,[a,v]);assert.equal(ack.readUInt16BE(0),0);}
const delay=ms=>new Promise(r=>setTimeout(r,ms));
try {
 for(let n=0;!log.includes('camera ')&&n<40;n++)await delay(50);
 const discovery=await request(2);check('discovery provides AxisLab identity',discovery.length===256&&discovery.subarray(8+0x48,8+0x50).toString().startsWith('AxisLab'));
 check('image dimensions are read from the feature map',(await request(0x80,[0x10000])).readUInt32BE(8)===320);
 check('read-only image dimensions reject writes',(await request(0x82,[0x10000,1])).readUInt16BE(0)!==0&&(await request(0x80,[0x10000])).readUInt32BE(8)===320);
 await write(0xa00,1);check('another control endpoint cannot change acquisition registers',(await request(0x82,[0x10010,1],other)).readUInt16BE(0)===0x8006);
 check('stream destination cannot leave the selected local interface',(await request(0x82,[0xd18,0x7f000002])).readUInt16BE(0)!==0);
 check('XML memory reads outside the map return a protocol error',(await request(0x84,[0xfffffff0,512])).readUInt16BE(0)!==0);
 const address=ip.split('.').reduce((v,o)=>(v*256+Number(o))>>>0,0);await write(0xd18,address);await write(0xd00,stream.address().port);await write(0x10018,1);await write(0x10010,1);
 const triggerId=++id;await request(0x82,[0x1001c,1],control,triggerId);await delay(150);await request(0x82,[0x1001c,1],control,triggerId);await delay(350);
 check('retransmitted software trigger does not expose a second frame',frames.length===1);
 await write(0x938,1000);await write(0x10018,0);await delay(1600);const stopped=frames.length;await delay(500);
 check('expired controller heartbeat stops acquisition',frames.length===stopped&&stopped>1);
 await write(0xa00,0);check('unimplemented packet resend is reported explicitly',(await request(0x40,[0,0,1])).readUInt16BE(0)===0x8001);
 await writeFile(path.join(out,'results.json'),JSON.stringify({success:true,checks},null,2));console.log('Artifacts: '+out);
} finally{control.close();other.close();stream.close();camera.kill();await writeFile(path.join(out,'camera.log'),log);}
