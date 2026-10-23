using AxisLab.Core;
using System.Text.Json;
try {
var root=Path.GetFullPath(Path.Combine("artifacts","workcell-checks",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
var checks=0;
void Check(bool valid,string label){if(!valid)throw new Exception(label);Console.WriteLine("PASS: "+label);checks++;}
void Reject(Action action,string code){try{action();throw new Exception("Expected "+code);}catch(DomainException e){Check(e.Code==code,code);}}
var original=JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText("samples/oriented-positioning.scene.json"),Catalog.Json)!;
var robot=original.Devices.Single(d=>d.Robot is not null) with {Id=Guid.NewGuid()};
robot=robot with{Robot=robot.Robot! with{JointPositions=[0,-15,30,0,75,0]}};
var pick=RobotModels.Forward(robot.Robot!,robot.Robot!.JointPositions).Tcp.Pose;
var belt=new Device(Guid.NewGuid(),"mechanism.conveyor","进料传送带",2,null,new(pick.X-30,pick.Y,pick.Z-45),new(SizeX:280,SizeY:160,SizeZ:50),Conveyor:new(90));
var camera=new Device(Guid.NewGuid(),"camera.pinhole","顶部定位相机",3,null,new(pick.X,pick.Y,850,180),new());
var sensor=new Device(Guid.NewGuid(),"sensor.photoelectric","到位光电",4,belt.Id,new(52,0,45),new(SizeX:4,SizeY:180,SizeZ:20),Photoelectric:new(camera.Id,belt.Id));
var gripper=new Device(Guid.NewGuid(),"tool.gripper","TCP 平行夹爪",5,robot.Id,new(0,0,0,0,90),new(SizeX:80,SizeY:60,SizeZ:60),AttachmentFrameId:"tool",Gripper:new(15));
var part=new Device(Guid.NewGuid(),"material.workpiece","待抓取工件",6,belt.Id,new(-110,0,45),new(SizeX:40,SizeY:40,SizeZ:40));
var station=new Device(Guid.NewGuid(),"geometry.box","放料工位",7,null,new(pick.X-40,pick.Y+230,(pick.Z-20)/2),new(SizeX:130,SizeY:130,SizeZ:pick.Z-20));
var store=new ProjectStore(root);store.Import(new(8,Guid.NewGuid(),"AxisLab 光电停带示教取放",8,[robot,belt,camera,sensor,gripper,part,station],[]),store.Get().Revision);
var poses=new[]{pick with{Z=pick.Z+80},pick,pick with{Z=pick.Z+100},pick with{X=pick.X-40,Y=pick.Y+230,Z=pick.Z+100},pick with{X=pick.X-40,Y=pick.Y+230},pick with{X=pick.X-40,Y=pick.Y+230,Z=pick.Z+100}};
string[] names=["01 取料接近","02 取料","03 抬升","04 放料接近","05 放料","06 退回"];
var points=new List<Guid>();
for(int i=0;i<poses.Length;i++){
 store.MoveCartesian(robot.Id,new(poses[i],SpeedPercent:60),store.Get().Revision);
 for(int n=0;store.GetRuntime().Robots[0].Status=="Running"&&n<10000;n++)store.AdvanceMotions(.02);
 Check(store.GetRuntime().Robots[0].Status=="Completed","reachable taught point "+(i+1));
 var state=store.RecordTeachPoint(robot.Id,names[i],"base",store.Get().Revision);points.Add(state.Project.TeachPoints!.Last().Id);
}
store.GeneratePickPlace(robot.Id,belt.Id,sensor.Id,gripper.Id,camera.Id,points.ToArray(),50,store.Get().Revision);
var fixture=store.Get().Project;
if(args.Contains("--generate")){File.WriteAllText("samples/workcell-pick-place.scene.json",JsonSerializer.Serialize(fixture,Catalog.Json));Console.WriteLine("Generated samples/workcell-pick-place.scene.json");}
Check(fixture.CellSequence!.Steps.Any(s=>s.Action=="WaitSensor")&&fixture.CellSequence.Steps.Count(s=>s.Action=="MoveTeach")==6,"generated flow includes I/O and six real taught points");
var saved=store.Save("AxisLab 光电停带示教取放",store.Get().Revision);var reopened=new ProjectStore(root);reopened.Open(saved.Project.Id.ToString("D"),reopened.Get().Revision);
Check(reopened.Get().Project.CellSequence!.Id==fixture.CellSequence.Id,"flow and instrument bindings persist without WPF");
store.StartCell();Reject(()=>store.SetConveyor(belt.Id,0),"CellActive");Reject(()=>store.Update(sensor.Id,sensor,store.Get().Revision),"CellActive");
var sawHeld=false;long lastTrigger=0;
for(int n=0;n<20000&&store.GetRuntime().Cell!.Status=="Running";n++){
 store.AdvanceMotions(.02);store.AdvanceIndustrial(.02);
 foreach(var e in store.PendingTriggers()){lastTrigger=e.Sequence;store.UpdateTrigger(e.Sequence,Guid.NewGuid(),Guid.NewGuid(),"Completed");}
 var runtime=store.GetRuntime();sawHeld|=runtime.Instruments!.Single(s=>s.DeviceId==gripper.Id).PartId==part.Id;
}
var result=store.GetRuntime();
Check(result.Cell!.Status=="Completed","internal taught pick/place reaches completion: "+result.Cell.Message);
Check(sawHeld&&!result.Instruments!.Single(s=>s.DeviceId==gripper.Id).PartId.HasValue,"workpiece is attached while gripping and released at station");
var placed=result.Devices.Single(d=>d.DeviceId==part.Id).World.Pose;
Check(Math.Abs(placed.X-poses[4].X)<2&&Math.Abs(placed.Y-poses[4].Y)<2&&Math.Abs(placed.Z-poses[4].Z)<2,"actual runtime workpiece reaches station without teleport at pickup");
Check(lastTrigger>0&&store.TriggerEvents(camera.Id).Length==1&&result.Instruments!.Single(s=>s.DeviceId==sensor.Id).RisingEdges==1,"one rising edge triggers one exposure and stops belt");
Check(result.Robots[0].ControlOwnerId is null&&result.Instruments!.All(s=>!s.Running),"successful cycle releases control and stops belt");
store.ResetCell();store.StartCell();store.AdvanceIndustrial(.02);store.StopCell();Check(store.GetRuntime().Cell!.Status=="Cancelled"&&store.GetRuntime().Instruments!.All(s=>!s.Running),"stop cancels waiting flow and stops devices");
store.ResetCell();store.SetGripper(gripper.Id,true);Check(store.GetRuntime().Instruments!.Single(s=>s.DeviceId==gripper.Id).PartId is null,"empty close never invents or teleports a workpiece");store.SetGripper(gripper.Id,false);
store.StartCell();for(int n=0;n<3000&&store.PendingTriggers().Length==0;n++){store.AdvanceMotions(.02);store.AdvanceIndustrial(.02);}var failed=store.PendingTriggers().Single();store.UpdateTrigger(failed.Sequence,null,null,"Failed","test-render-failure");store.AdvanceIndustrial(.1);
Check(store.GetRuntime().Cell!.Status=="Failed"&&store.GetRuntime().Instruments!.All(s=>!s.Running),"failed camera trigger stops the cycle with an explicit failure");
File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{success=true,checks,placed},Catalog.Json));Console.WriteLine($"{checks} workcell checks passed. Artifacts: {root}");

} catch(Exception ex){Console.Error.WriteLine(ex.ToString());Environment.ExitCode=1;}
