import {escape} from './icons';
import type {Device} from './types';
interface State{running:boolean;cameraId:string|null;listenAddress:string|null;stateDirectory:string|null;addresses:string[];messages:string[]}
export class DirectCameraPanel{
 private devices:Device[]=[];private selected:string|null=null;private state?:State;private busy=false;
 constructor(private element:HTMLElement,private notify:(m:string,error?:boolean)=>void){void this.refresh();setInterval(()=>void this.refresh(false),2500);}
 setDevices(devices:Device[],selected:string|null){this.devices=devices.filter(d=>d.typeId==='camera.pinhole');if(this.devices.some(d=>d.id===selected))this.selected=selected;if(!this.devices.some(d=>d.id===this.selected))this.selected=this.devices[0]?.id??null;this.draw();}
 private async refresh(redraw=true){try{const r=await fetch('/api/v1/virtual-camera');if(!r.ok)return;this.state=await r.json();if(redraw||!this.element.querySelector('#direct-camera-state'))this.draw();else this.showState();}catch{}}
 private showState(){const status=this.element.querySelector('#direct-camera-state');if(status)status.textContent=this.state?.running?'已发布 AxisLabCam · '+this.state.listenAddress:'虚拟相机未发布';const log=this.element.querySelector('#direct-camera-log');if(log)log.textContent=(this.state?.stateDirectory??'')+'\n'+(this.state?.messages??[]).slice(-3).join('\n');}
 private draw(){
  const previous=this.element.querySelector<HTMLSelectElement>('#direct-camera-ip')?.value;
  this.element.innerHTML='<h3>直接连接 VisionMaster</h3><div class="cell-selects"><label>相机<select id="direct-camera-id">'+this.devices.map(d=>'<option value="'+d.id+'" '+(d.id===this.selected?'selected':'')+'>'+escape(d.name)+'</option>').join('')+'</select></label><label>本机接口<select id="direct-camera-ip">'+(this.state?.addresses??[]).map(ip=>'<option '+(ip===(previous??this.state?.listenAddress)?'selected':'')+'>'+escape(ip)+'</option>').join('')+'</select></label></div><div class="cell-actions"><button id="publish-camera" class="btn primary" '+(this.busy?'disabled':'')+'>发布虚拟 GigE 相机</button><button id="stop-published-camera" class="btn outline">停止本实例</button></div><p id="direct-camera-state"></p><p class="properties-note">在 VM 相机管理选择 AxisLabCam / AXISLAB001。手动拍照用 Software；光电触发用 TriggerMode=On、TriggerSource=Line0，VM 先等待采集，再启动取放流程。此链路不需要 WPF。</p><pre id="direct-camera-log"></pre>';
  const act=async(start:boolean)=>{if(this.busy)return;this.busy=true;try{const body=start?{cameraId:this.element.querySelector<HTMLSelectElement>('#direct-camera-id')!.value,listenAddress:this.element.querySelector<HTMLSelectElement>('#direct-camera-ip')!.value}:undefined;const r=await fetch('/api/v1/virtual-camera/'+(start?'start':'stop'),{method:'POST',headers:body?{'Content-Type':'application/json'}:{},body:body?JSON.stringify(body):undefined});const data=await r.json();if(!r.ok)throw new Error(data.message);this.state=data;this.notify(start?'虚拟相机已发布，可直接在 VM 连接。':'本实例虚拟相机已停止。');}catch(e){this.notify(e instanceof Error?e.message:'操作失败',true);}finally{this.busy=false;await this.refresh();}};
  this.element.querySelector<HTMLButtonElement>('#publish-camera')!.onclick=()=>void act(true);
  this.element.querySelector<HTMLButtonElement>('#stop-published-camera')!.onclick=()=>void act(false);this.showState();
 }
}
