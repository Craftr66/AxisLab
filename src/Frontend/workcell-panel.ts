import {escape} from './icons';
import type {Snapshot,RuntimeSnapshot,CellSequence,CellStep} from './types';
async function request<T>(path:string,method='GET',body?:unknown):Promise<T>{
 const r=await fetch('/api/v1'+path,{method,headers:body===undefined?{}:{'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(30000)});
 const data=await r.json();if(!r.ok)throw new Error(data.message??'请求失败');return data;
}
const actions:Record<string,string>={Conveyor:'传送带速度',WaitSensor:'等待光电上升沿',WaitCapture:'等待相机图像',Grip:'夹爪开合',MoveTeach:'运动到示教点',Delay:'延时'};
const statuses:Record<string,string>={Idle:'待机',Running:'运行中',Completed:'已完成',Cancelled:'已停止',Failed:'故障'};
export class WorkcellPanel{
 private snapshot?:Snapshot;private key='';private draft?:CellSequence;
 constructor(private element:HTMLElement,private notify:(m:string,error?:boolean)=>void,private refresh:()=>Promise<void>){}
 setSnapshot(s:Snapshot){
  this.snapshot=s;const key=s.sessionId+':'+s.revision;
  if(key!==this.key){this.key=key;this.draft=s.project.cellSequence?structuredClone(s.project.cellSequence):undefined;this.draw();}
  this.update(s.runtime);
 }
 private async run(action:()=>Promise<unknown>){try{await action();await this.refresh();}catch(e){this.notify(e instanceof Error?e.message:'操作失败',true);}}
 update(runtime:RuntimeSnapshot){
  const cell=runtime.cell;const state=this.element.querySelector<HTMLElement>('#cell-state');
  if(state&&cell)state.textContent=(statuses[cell.status]??cell.status)+' · '+cell.stepIndex+'/'+cell.stepCount+' · '+cell.message;
  for(const item of runtime.instruments??[]){const line=this.element.querySelector<HTMLElement>('[data-io-state="'+item.deviceId+'"]');if(line)line.textContent=item.running?'运行 '+item.speed.toFixed(0)+' mm/s':this.snapshot?.project.devices.find(d=>d.id===item.deviceId)?.photoelectric?'DI '+(item.input?'1 · 已遮挡':'0 · 未遮挡')+' / 上升沿 '+item.risingEdges:this.snapshot?.project.devices.find(d=>d.id===item.deviceId)?.gripper?(item.closed?'闭合':'打开')+(item.partId?' · 已夹持':' · 无工件'):'已停止';}
  const busy=cell?.status==='Running'||runtime.robots.some(r=>r.status==='Running'||r.controlOwnerId)||runtime.instruments?.some(i=>i.running);
  this.element.querySelectorAll<HTMLButtonElement|HTMLInputElement|HTMLSelectElement>('[data-config]').forEach(c=>c.disabled=!!busy);
 }
 private draw(){
  const s=this.snapshot!;const f=this.draft;const devices=s.project.devices;
  const select=(name:string,type:string,id?:string)=>'<select data-config id="'+name+'">'+devices.filter(d=>type==='robot'?!!d.robot:d.typeId===type).map(d=>'<option value="'+d.id+'" '+(d.id===id?'selected':'')+'>'+escape(d.name)+'</option>').join('')+'</select>';
  const ids=f?.steps.filter(x=>x.action==='MoveTeach').map(x=>x.pointId)??[];
  this.element.innerHTML='<div class="cell-layout"><section><h3>现场设备与 I/O</h3><div class="cell-io">'+devices.filter(d=>d.conveyor||d.gripper||d.photoelectric).map(d=>'<div class="cell-io-row"><strong>'+escape(d.name)+' <small>#'+d.deviceNumber+'</small></strong><span data-io-state="'+d.id+'">—</span>'+(d.conveyor?'<button class="btn outline" data-config data-belt="'+d.id+'">启动</button><button class="btn outline" data-belt-stop="'+d.id+'">停止</button>':d.gripper?'<button class="btn outline" data-config data-grip="'+d.id+'" data-close="1">闭合</button><button class="btn outline" data-config data-grip="'+d.id+'" data-close="0">打开</button>':'')+'</div>').join('')+'</div><p class="properties-note">光电信号及上升沿计数来自物品遮挡；可经 Modbus 读取。夹爪仅夹持捕获范围内的工件。</p><div class="cell-actions"><button id="cell-start" class="btn primary" data-config>运行取放流程</button><button id="cell-stop" class="btn danger">停止工作单元</button><button id="cell-reset" class="btn outline" data-config>复位物料</button></div><p id="cell-state"></p></section>'+
  '<section><h3>从示教点生成取放流程</h3><div class="cell-selects"><label>机器人'+select('cell-robot','robot',f?.robotId)+'</label><label>传送带'+select('cell-conveyor','mechanism.conveyor',f?.conveyorId)+'</label><label>光电'+select('cell-sensor','sensor.photoelectric',f?.sensorId)+'</label><label>TCP 夹爪'+select('cell-gripper','tool.gripper',f?.gripperId)+'</label><label>触发相机'+select('cell-camera','camera.pinhole',f?.cameraId)+'</label><label>运动速度 %<input data-config id="cell-speed" type="number" min="1" max="100" value="'+(f?.speedPercent??35)+'"></label></div><div class="cell-selects">'+['取料接近','取料','抬升','放料接近','放料','退回'].map((label,i)=>'<label>'+label+'<select data-config data-cell-point="'+i+'">'+s.project.teachPoints.map(p=>'<option value="'+p.id+'" '+(p.id===ids[i]?'selected':'')+'>'+escape(p.name)+'</option>').join('')+'</select></label>').join('')+'</div><button id="cell-generate" data-config class="btn outline">生成流程并绑定光电 → 停带 / 相机</button><p class="properties-note">先在“运动与示教”记录六个点。首个案例是示教取放；VM 可独立采图、检测和调试标定。</p></section></div>'+
  '<section class="cell-sequence"><h3>设备动作表 '+(f?'· '+escape(f.name):'')+'</h3><p class="properties-note">按顺序执行，支持示教运动、传送带、光电等待、夹爪、图像完成等待和延时；每步有超时，故障停止机器人和传送带，保持已夹持工件。</p><div id="cell-editor"></div></section>';
  this.element.querySelectorAll<HTMLButtonElement>('[data-belt]').forEach(b=>b.onclick=()=>void this.run(()=>request('/instruments/'+b.dataset.belt+'/conveyor','POST',{speed:devices.find(d=>d.id===b.dataset.belt)!.conveyor!.speed})));
  this.element.querySelectorAll<HTMLButtonElement>('[data-belt-stop]').forEach(b=>b.onclick=()=>void this.run(async()=>{if(this.snapshot?.runtime.cell?.status==='Running')await request('/cell/stop','POST');return request('/instruments/'+b.dataset.beltStop+'/conveyor','POST',{speed:0});}));
  this.element.querySelectorAll<HTMLButtonElement>('[data-grip]').forEach(b=>b.onclick=()=>void this.run(()=>request('/instruments/'+b.dataset.grip+'/gripper','POST',{close:b.dataset.close==='1'})));
  this.element.querySelector<HTMLButtonElement>('#cell-start')!.onclick=()=>void this.run(()=>request('/cell/start','POST'));
  this.element.querySelector<HTMLButtonElement>('#cell-stop')!.onclick=()=>void this.run(()=>request('/cell/stop','POST'));
  this.element.querySelector<HTMLButtonElement>('#cell-reset')!.onclick=()=>void this.run(()=>request('/cell/reset','POST'));
  this.element.querySelector<HTMLButtonElement>('#cell-generate')!.onclick=()=>void this.run(()=>{
    const value=(id:string)=>(this.element.querySelector('#cell-'+id) as HTMLInputElement).value;
    const points=Array.from(this.element.querySelectorAll<HTMLSelectElement>('[data-cell-point]')).map(p=>p.value);
    return request('/cell/generate','POST',{expectedRevision:this.snapshot!.revision,robotId:value('robot'),conveyorId:value('conveyor'),sensorId:value('sensor'),gripperId:value('gripper'),cameraId:value('camera'),pointIds:points,speedPercent:Number(value('speed'))});
  });
  this.drawEditor();
 }
 private drawEditor(){
  const element=this.element.querySelector<HTMLElement>('#cell-editor')!,f=this.draft;if(!f){element.textContent='尚未生成流程。可导入随包的 workcell-pick-place.scene.json。';return;}
  const option=(options:[string,string][],value:string|null)=>options.map(([id,name])=>'<option value="'+id+'" '+(id===value?'selected':'')+'>'+escape(name)+'</option>').join('');
  const dev=this.snapshot!.project.devices.map(d=>[d.id,d.name] as [string,string]),points=this.snapshot!.project.teachPoints.map(p=>[p.id,p.name] as [string,string]);
  element.innerHTML='<table class="cell-step-table"><thead><tr><th>步骤名称</th><th>动作</th><th>设备 / 示教点</th><th>值</th><th>超时 s</th><th></th></tr></thead><tbody>'+f.steps.map((step,i)=>'<tr data-step="'+i+'"><td><input data-config data-field="name" value="'+escape(step.name)+'"></td><td><select data-config data-field="action">'+option(Object.entries(actions),step.action)+'</select></td><td><select data-config data-field="'+(step.action==='MoveTeach'?'pointId':'deviceId')+'">'+option(step.action==='MoveTeach'?points:dev,step.action==='MoveTeach'?step.pointId:step.deviceId)+'</select></td><td><input data-config data-field="value" type="number" step="any" value="'+step.value+'"></td><td><input data-config data-field="timeout" type="number" min=".1" max="300" value="'+step.timeout+'"></td><td><button data-config class="btn outline" data-delete-step="'+i+'">删除</button></td></tr>').join('')+'</tbody></table><button id="cell-add-delay" data-config class="btn outline">添加延时步骤</button><button id="cell-save-sequence" data-config class="btn primary">应用动作表</button>';
  element.querySelectorAll<HTMLInputElement|HTMLSelectElement>('[data-field]').forEach(input=>input.onchange=()=>{
    const index=Number(input.closest('[data-step]')?.getAttribute('data-step')),step=f.steps[index],field=input.dataset.field!;
    if(field==='value'||field==='timeout')step[field]=Number(input.value);else if(field==='action'){step.action=input.value;step.deviceId=step.action==='Grip'?f.gripperId:step.action==='Conveyor'?f.conveyorId:step.action==='WaitSensor'?f.sensorId:step.action==='WaitCapture'?f.cameraId:f.robotId;this.drawEditor();}else (step as unknown as Record<string,unknown>)[field]=input.value;
  });
  element.querySelectorAll<HTMLButtonElement>('[data-delete-step]').forEach(b=>b.onclick=()=>{f.steps.splice(Number(b.dataset.deleteStep),1);this.drawEditor();});
  element.querySelector<HTMLButtonElement>('#cell-add-delay')!.onclick=()=>{f.steps.push({name:'等待 1 秒',action:'Delay',deviceId:null,pointId:null,value:1,timeout:10});this.drawEditor();};
  element.querySelector<HTMLButtonElement>('#cell-save-sequence')!.onclick=()=>void this.run(()=>request('/cell/sequence','PUT',{expectedRevision:this.snapshot!.revision,sequence:f}));
 }
}
