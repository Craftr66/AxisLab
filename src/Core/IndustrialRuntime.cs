namespace AxisLab.Core;

public sealed partial class ProjectStore
{
    sealed class Instrument
    {
        public double Speed;
        public bool Input,Closed;
        public Guid? Part;
        public uint Edges;
        public Guid? Owner;
        public long Heartbeat;
        public uint Counter;
    }
    readonly Dictionary<Guid,Instrument> instruments=[];
    readonly Dictionary<Guid,RigidTransform> partWorld=[];
    readonly Dictionary<Guid,(Guid Gripper,RigidTransform Relative)> held=[];
    readonly List<CameraTriggerEvent> triggerEvents=[];
    long triggerSequence;
    Guid flowOwner=Guid.NewGuid();
    uint flowHeartbeat;
    string flowStatus="Idle",flowMessage="尚未启动工作单元";
    string? flowError;
    int flowIndex;
    double flowElapsed,stepElapsed;
    bool stepStarted;
    uint flowSensorBaseline;
    long flowTrigger;
    Guid? flowCommand;
    static bool Pickable(Device d)=>d.TypeId is "material.workpiece" or "vision.orientedPart";
    Instrument InstrumentAt(Guid id)=>instruments.TryGetValue(id,out var value)?value:instruments[id]=new();
    void ReconcileIndustrial(bool reset)
    {
        if(!reset){flowStatus="Idle";flowIndex=0;flowError=null;flowMessage="工作单元待机";}
        if(reset){instruments.Clear();partWorld.Clear();held.Clear();triggerEvents.Clear();flowStatus="Idle";flowMessage="工作单元待机";flowIndex=0;flowElapsed=0;flowError=null;}
        var ids=project.Devices.Select(d=>d.Id).ToHashSet();
        foreach(var id in instruments.Keys.Where(id=>!ids.Contains(id)).ToArray())instruments.Remove(id);
        foreach(var id in partWorld.Keys.Where(id=>!ids.Contains(id)).ToArray())partWorld.Remove(id);
        foreach(var id in held.Keys.Where(id=>!ids.Contains(id)||!ids.Contains(held[id].Gripper)).ToArray())held.Remove(id);
        foreach(var s in instruments.Values)if(s.Part.HasValue&&!ids.Contains(s.Part.Value))s.Part=null;
    }
    void EnsureIndustrialIdle()
    {
        if(flowStatus=="Running"||instruments.Values.Any(s=>s.Speed!=0))throw new DomainException("CellActive","工作单元或传送带正在运行，请先停止。",409);
    }
    static void ValidateIndustrial(ProjectDocument doc)
    {
        var byId=doc.Devices.ToDictionary(d=>d.Id);
        bool Type(Guid? id,string type)=>id is null||byId.TryGetValue(id.Value,out var d)&&d.TypeId==type;
        foreach(var d in doc.Devices)
        {
            if(d.Conveyor is not null&&(d.TypeId!="mechanism.conveyor"||!double.IsFinite(d.Conveyor.Speed)||d.Conveyor.Speed is <=0 or >1000))throw new DomainException("InvalidInstrument","传送带速度须大于 0 且不超过 1000 mm/s。");
            if(d.Gripper is not null&&(d.TypeId!="tool.gripper"||!double.IsFinite(d.Gripper.GraspRange)||d.Gripper.GraspRange is <=0 or >100))throw new DomainException("InvalidInstrument","夹爪捕获范围须大于 0 且不超过 100 mm。");
            if(d.Photoelectric is not null&&(d.TypeId!="sensor.photoelectric"||!Type(d.Photoelectric.CameraId,"camera.pinhole")||!Type(d.Photoelectric.StopConveyorId,"mechanism.conveyor")))throw new DomainException("InvalidInstrument","光电绑定的相机或传送带无效。");
        }
        var f=doc.CellSequence;if(f is null)return;
        if(f.Id==Guid.Empty||string.IsNullOrWhiteSpace(f.Name)||f.Name.Length>80||!byId.TryGetValue(f.RobotId,out var robot)||robot.Robot is null||
            !Type(f.ConveyorId,"mechanism.conveyor")||!Type(f.SensorId,"sensor.photoelectric")||!Type(f.GripperId,"tool.gripper")||!Type(f.CameraId,"camera.pinhole")||
            f.Steps is null||f.Steps.Length is <1 or >64||!double.IsFinite(f.SpeedPercent)||f.SpeedPercent is <=0 or >100)throw new DomainException("InvalidCellSequence","工作单元设备、步骤或速度无效。");
        var grip=byId[f.GripperId];if(grip.ParentId!=robot.Id||grip.AttachmentFrameId!="tool")throw new DomainException("InvalidCellSequence","夹爪必须挂载到本流程机器人的 TCP。");
        foreach(var step in f.Steps)
        {
            if(step is null||string.IsNullOrWhiteSpace(step.Name)||!double.IsFinite(step.Value)||!double.IsFinite(step.Timeout)||step.Timeout is <=0 or >300)throw new DomainException("InvalidCellSequence","步骤名称、数值或超时无效。");
            var valid=step.Action switch{
                "Conveyor"=>step.DeviceId==f.ConveyorId&&step.Value is >=0 and <=1000,
                "WaitSensor"=>step.DeviceId==f.SensorId,
                "WaitCapture"=>step.DeviceId==f.CameraId,
                "Grip"=>step.DeviceId==f.GripperId&&step.Value is 0 or 1,
                "MoveTeach"=>doc.TeachPoints!.Any(p=>p.Id==step.PointId&&p.RobotId==f.RobotId),
                "Delay"=>step.Value is >=0 and <=60,
                _=>false};
            if(!valid)throw new DomainException("InvalidCellSequence","步骤动作、设备或示教点不匹配。");
        }
    }
    Device PersistPart(Device d)
    {
        if(!Pickable(d)||!partWorld.ContainsKey(d.Id)&&!held.ContainsKey(d.Id))return d;
        var runtime=BuildRuntime();var world=new RigidTransform(runtime.Devices.Single(x=>x.DeviceId==d.Id).World.Matrix);
        var parent=d.ParentId.HasValue?new RigidTransform(runtime.Devices.Single(x=>x.DeviceId==d.ParentId).World.Matrix):RigidTransform.Identity;
        return d with{Transform=parent.Inverse().Then(world).Frame().Pose};
    }
    IndustrialState[] InstrumentSnapshot()=>project.Devices.Where(d=>d.Conveyor is not null||d.Gripper is not null||d.Photoelectric is not null).Select(d=>{
        var s=InstrumentAt(d.Id);return new IndustrialState(d.Id,s.Speed!=0,s.Input,s.Closed,s.Part,s.Edges,s.Speed);
    }).ToArray();
    CellRunState CellSnapshot()=>new(flowStatus,flowStatus=="Running"?flowIndex+1:flowIndex,project.CellSequence?.Steps.Length??0,flowMessage,flowError,flowElapsed,held.Keys.Select(id=>(Guid?)id).FirstOrDefault());
    void CheckInstrumentOwner(Guid? owner)
    {
        if(flowStatus=="Running"&&owner!=flowOwner)throw new DomainException("CellActive","工作单元持有设备控制权，请先停止流程。",409);
    }
    public RuntimeSnapshot SetConveyor(Guid id,double speed,Guid? owner=null)
    {
        lock(gate){
            CheckInstrumentOwner(owner);if(Find(id).Conveyor is null)throw new DomainException("InvalidInstrument","此设备不是传送带。");
            if(!double.IsFinite(speed)||speed is <0 or >1000)throw new DomainException("InvalidInstrument","速度须为 0—1000 mm/s，0 表示停止。");
            var state=InstrumentAt(id);
            if(speed>0&&state.Owner.HasValue&&state.Owner!=owner)throw new DomainException("ControlBusy","传送带由其他控制者持有，请先停止。",409);
            state.Speed=speed;state.Owner=speed==0?null:owner;state.Heartbeat=controlClock.GetTimestamp();return BuildRuntime();
        }
    }
    public RuntimeSnapshot SetGripper(Guid id,bool close,Guid? owner=null)
    {
        lock(gate){
            CheckInstrumentOwner(owner);var d=Find(id);if(d.Gripper is null)throw new DomainException("InvalidInstrument","此设备不是夹爪。");
            var s=InstrumentAt(id);var runtime=BuildRuntime();
            if(!close){
                if(s.Part is Guid part){partWorld[part]=new(runtime.Devices.Single(x=>x.DeviceId==part).World.Matrix);held.Remove(part);}
                s.Part=null;s.Closed=false;return BuildRuntime();
            }
            if(d.ParentId is null||Find(d.ParentId.Value).Robot is null||d.AttachmentFrameId!="tool")throw new DomainException("InvalidInstrument","先将夹爪挂载到机器人的 TCP。");
            s.Closed=true;if(s.Part.HasValue)return BuildRuntime();
            var world=new RigidTransform(runtime.Devices.Single(x=>x.DeviceId==id).World.Matrix);
            var excluded=new HashSet<Guid>{id};for(var p=d.ParentId;p.HasValue;p=Find(p.Value).ParentId)excluded.Add(p.Value);
            var candidate=project.Devices.Where(p=>Pickable(p)&&p.Visible&&!held.ContainsKey(p.Id)&&!excluded.Contains(p.Id)).Select(p=>new{part=p,world=new RigidTransform(runtime.Devices.Single(x=>x.DeviceId==p.Id).World.Matrix)})
                .Select(p=>new{p.part,p.world,distance=(V3.Position(p.world)-V3.Position(world)).Length}).Where(p=>p.distance<=d.Gripper.GraspRange).OrderBy(p=>p.distance).ThenBy(p=>p.part.DeviceNumber).FirstOrDefault();
            if(candidate is not null){held[candidate.part.Id]=(id,world.Inverse().Then(candidate.world));partWorld.Remove(candidate.part.Id);s.Part=candidate.part.Id;}
            return BuildRuntime();
        }
    }
    public RuntimeSnapshot ResetCell()
    {
        lock(gate){EnsureIdle();instruments.Clear();partWorld.Clear();held.Clear();triggerEvents.Clear();flowStatus="Idle";flowIndex=0;flowElapsed=0;flowError=null;flowMessage="物料已恢复到工程保存位置；机器人姿态保持。";return BuildRuntime();}
    }
    public void StopCell(string message="工作单元已停止")
    {
        lock(gate){
            foreach(var s in instruments.Values){s.Speed=0;s.Owner=null;}
            if(flowStatus=="Running"){flowStatus="Cancelled";flowMessage=message;var robot=project.CellSequence?.RobotId;if(robot.HasValue&&motions.TryGetValue(robot.Value,out var m)){if(m.Status=="Running"){m.Status="Cancelled";revision++;dirty=true;}controls.Remove(robot.Value);}}
        }
    }
    public CameraTriggerEvent[] TriggerEvents(Guid camera,long after=0)
    {
        lock(gate){if(Find(camera).TypeId!="camera.pinhole")throw new DomainException("NotCamera","此设备不是相机。");return triggerEvents.Where(e=>e.CameraId==camera&&e.Sequence>after&&e.SessionId==sessionId).ToArray();}
    }
    public void UpdateTrigger(long sequence,Guid? capture,Guid? frame,string status,string? error=null)
    {
        lock(gate){var i=triggerEvents.FindIndex(e=>e.Sequence==sequence);if(i>=0)triggerEvents[i]=triggerEvents[i] with{CaptureId=capture,FrameId=frame,Status=status,Error=error};}
    }
    public CameraTriggerEvent[] PendingTriggers(){lock(gate)return triggerEvents.Where(e=>e.Status=="Pending"&&e.SessionId==sessionId).ToArray();}
    public void AdvanceIndustrial(double seconds)
    {
        if(!double.IsFinite(seconds)||seconds<0)throw new ArgumentOutOfRangeException(nameof(seconds));
        lock(gate){
            while(seconds>1e-9){
                var dt=Math.Min(.01,seconds);seconds-=dt;var runtime=BuildRuntime();
                foreach(var belt in project.Devices.Where(d=>d.Conveyor is not null&&InstrumentAt(d.Id).Speed>0)){
                    var basis=new RigidTransform(runtime.Devices.Single(x=>x.DeviceId==belt.Id).World.Matrix);
                    foreach(var part in project.Devices.Where(Pickable).Where(d=>!held.ContainsKey(d.Id)&&d.Visible)){
                        var world=new RigidTransform(runtime.Devices.Single(x=>x.DeviceId==part.Id).World.Matrix);var local=basis.Inverse().Then(world).Frame().Pose;
                        var half=part.Parameters.SizeZ/2;var top=belt.Parameters.SizeZ/2;
                        if(Math.Abs(local.Y)>(belt.Parameters.SizeY-part.Parameters.SizeY)/2||Math.Abs(local.Z-(top+half))>3||Math.Abs(local.X)>(belt.Parameters.SizeX-part.Parameters.SizeX)/2+1)continue;
                        local=local with{X=Math.Min((belt.Parameters.SizeX-part.Parameters.SizeX)/2,local.X+InstrumentAt(belt.Id).Speed*dt)};
                        partWorld[part.Id]=basis.Then(RigidTransform.FromPose(local));
                    }
                }
                runtime=BuildRuntime();
                foreach(var sensor in project.Devices.Where(d=>d.Photoelectric is not null)){
                    var s=InstrumentAt(sensor.Id);var inv=new RigidTransform(runtime.Devices.Single(x=>x.DeviceId==sensor.Id).World.Matrix).Inverse();
                    var found=project.Devices.Where(Pickable).Where(d=>d.Visible).FirstOrDefault(part=>{
                        var local=V3.Transform(inv,V3.Position(runtime.Devices.Single(x=>x.DeviceId==part.Id).World.Matrix));var p=part.Parameters;var box=sensor.Parameters;
                        return Math.Abs(local.X)<=(box.SizeX+p.SizeX)/2&&Math.Abs(local.Y)<=(box.SizeY+p.SizeY)/2&&Math.Abs(local.Z)<=(box.SizeZ+p.SizeZ)/2;
                    });
                    var input=sensor.Photoelectric!.Enabled&&found is not null;
                    if(input&&!s.Input){
                        s.Edges++;if(s.Edges==0)s.Edges++;
                        if(sensor.Photoelectric.StopConveyorId is Guid belt){InstrumentAt(belt).Speed=0;InstrumentAt(belt).Owner=null;}
                        if(sensor.Photoelectric.CameraId is Guid camera){triggerEvents.Add(new(++triggerSequence,sensor.Id,camera,sessionId));while(triggerEvents.Count>128)triggerEvents.RemoveAt(0);}
                    }
                    s.Input=input;s.Part=found?.Id;
                }
                AdvanceCell(dt);
            }
        }
    }
    public WorkspaceSnapshot GeneratePickPlace(Guid robot,Guid conveyor,Guid sensor,Guid gripper,Guid camera,Guid[] points,double speed,long expected)
    {
        lock(gate){
            Check(expected);EnsureIdle();if(points is null||points.Length!=6)throw new DomainException("InvalidCellSequence","请选择六个示教点：取料接近、取料、抬升、放料接近、放料、退回。");
            var steps=new List<CellStep>{
                new("打开夹爪","Grip",gripper,Value:0),new("启动传送带","Conveyor",conveyor,Value:Find(conveyor).Conveyor?.Speed??80),
                new("等待光电上升沿","WaitSensor",sensor,Timeout:60),new("停带确认","Conveyor",conveyor,Value:0),
                new("等待光电触发图像完成","WaitCapture",camera,Timeout:15)};
            string[] names=["取料接近","到达取料点","抬升工件","放料接近","到达放料点","退回安全位"];
            for(int i=0;i<6;i++){steps.Add(new(names[i],"MoveTeach",robot,points[i]));if(i==1)steps.Add(new("闭合夹爪并确认工件","Grip",gripper,Value:1));if(i==4)steps.Add(new("打开夹爪并放置","Grip",gripper,Value:0));}
            var f=new CellSequence(Guid.NewGuid(),"光电停带 · 示教取放",robot,conveyor,sensor,gripper,camera,steps.ToArray(),speed);
            var devices=project.Devices.Select(d=>d.Id==sensor?d with{Photoelectric=new(camera,conveyor)}:d).ToArray();
            return Commit(project with{Devices=devices,CellSequence=f});
        }
    }
    public WorkspaceSnapshot StartCell()
    {
        lock(gate){
            EnsureIdle();var f=project.CellSequence??throw new DomainException("InvalidCellSequence","请先生成示教取放流程。");ValidateIndustrial(project);
            var binding=Find(f.SensorId).Photoelectric!;
            if(binding.CameraId!=f.CameraId||binding.StopConveyorId!=f.ConveyorId)throw new DomainException("InvalidCellSequence","光电停带/相机绑定与流程不一致，请重新生成流程。");
            if(InstrumentAt(f.GripperId).Part.HasValue)throw new DomainException("InvalidCellSequence","夹爪仍持有工件，请先手动恢复或复位物料，再重新运行。");
            if(InstrumentAt(f.SensorId).Input)throw new DomainException("InvalidCellSequence","光电当前已遮挡；请先复位物料或移出检测区。");
            var signature=Signature(f.RobotId);var start=motions[f.RobotId].Joints;
            foreach(var step in f.Steps.Where(s=>s.Action=="MoveTeach")){
                var p=project.TeachPoints!.Single(p=>p.Id==step.PointId);if(p.DependencySignature!=signature)throw new DomainException("StaleTeachPoint","流程包含失效示教点，请重新校验。");
                PlanJoints(f.RobotId,start,p.Joints,f.SpeedPercent,p.Id);start=p.Joints;
            }
            ClaimRobot(f.RobotId,flowOwner);flowStatus="Running";flowIndex=0;flowElapsed=0;stepElapsed=0;stepStarted=false;flowCommand=null;flowError=null;flowSensorBaseline=InstrumentAt(f.SensorId).Edges;flowTrigger=triggerSequence;flowMessage="准备执行："+f.Steps[0].Name;return Snapshot();
        }
    }
    public WorkspaceSnapshot SetCellSequence(CellSequence sequence,long expected)
    {lock(gate){Check(expected);return Commit(project with{CellSequence=sequence});}}
    void AdvanceCell(double seconds)
    {
        if(flowStatus!="Running"||project.CellSequence is not CellSequence f)return;
        flowElapsed+=seconds;stepElapsed+=seconds;
        try{
            if(!controls.TryGetValue(f.RobotId,out var control)||control.Owner!=flowOwner)throw new DomainException(motions[f.RobotId].ErrorCode??"NoControl",motions[f.RobotId].Message??"流程机器人控制权已撤销。");
            HeartbeatOwner(flowOwner,++flowHeartbeat);
            var step=f.Steps[flowIndex];flowMessage=step.Name;
            if(stepElapsed>step.Timeout)throw new DomainException("CellTimeout","步骤超时："+step.Name);
            bool done=false;
            switch(step.Action){
                case "Conveyor":SetConveyor(step.DeviceId!.Value,step.Value,flowOwner);done=true;break;
                case "Grip":SetGripper(step.DeviceId!.Value,step.Value==1,flowOwner);if(step.Value==1&&!InstrumentAt(step.DeviceId.Value).Part.HasValue)throw new DomainException("GripEmpty","夹爪闭合但范围内没有工件，请校核取料示教点。");done=true;break;
                case "WaitSensor":done=InstrumentAt(f.SensorId).Input&&InstrumentAt(f.SensorId).Edges>flowSensorBaseline;break;
                case "WaitCapture":
                    var e=triggerEvents.LastOrDefault(e=>e.Sequence>flowTrigger&&e.SensorId==f.SensorId&&e.CameraId==f.CameraId);
                    if(e?.Status=="Failed")throw new DomainException("RenderFailed",e.Error??"光电触发图像失败。");
                    done=e?.Status=="Completed";break;
                case "Delay":done=stepElapsed>=step.Value;break;
                case "MoveTeach":
                    if(!stepStarted){var move=PlayTeachPoints(f.RobotId,[step.PointId!.Value],"MoveJ",f.SpeedPercent,revision,flowOwner);flowCommand=move.Runtime.Robots.Single(r=>r.DeviceId==f.RobotId).CommandId;stepStarted=true;}
                    var motion=motions[f.RobotId];
                    if(motion.CommandId!=flowCommand||motion.Status is "Cancelled" or "Failed")throw new DomainException(motion.ErrorCode??"CellCancelled",motion.Message??"示教动作被中断。");
                    done=motion.Status=="Completed";break;
            }
            if(done){flowIndex++;stepElapsed=0;stepStarted=false;if(flowIndex==f.Steps.Length){flowStatus="Completed";flowMessage=InstrumentAt(f.GripperId).Part.HasValue?"流程已完成，夹爪仍持有工件。":"流程已完成，夹爪已释放。";controls.Remove(f.RobotId);foreach(var s in instruments.Values){s.Speed=0;s.Owner=null;}}}
        }catch(DomainException ex){StopCell(ex.Message);flowStatus="Failed";flowError=ex.Code;flowMessage=ex.Message;}
    }
}
