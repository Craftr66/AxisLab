using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
namespace AxisLab.WpfDemo;
public partial class MainWindow
{
    PoseInfo? _sampleOrigin;
    string? _sampleBinding;
    int _sampleIndex;
    readonly HashSet<Guid> _sampleFrames=[];
    void SampleOrigin_Click(object sender,RoutedEventArgs e)=>VisionWork(async ct=>{
        if(_connection is null||_selectedRobot is null)throw new InvalidOperationException("先选择机器人。");
        var state=(await _connection.Http.GetRuntimeAsync(ct)).Robots.Single(r=>r.DeviceId==_selectedRobot.Id);
        if(state.Status=="Running")throw new InvalidOperationException("请先停稳机器人。");
        _sampleOrigin=state.TcpBase.Pose;_sampleIndex=0;_sampleBinding=BindingKey();SampleStatus.Text="已设置中心；下一步移动到网格点 1/9。";
    });
    void SampleNext_Click(object sender,RoutedEventArgs e)=>VisionWork(async ct=>{
        if(_sampleOrigin is null||_connection is null||_selectedRobot is null||_sampleBinding!=BindingKey())throw new InvalidOperationException("请先设置采样中心，且保持设备与方案不变。");
        if(_sampleIndex>=9)throw new InvalidOperationException("九点移动已完成；可记录独立验证点，或新建采样批次。");
        if(!double.TryParse(SampleSpacing.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var spacing)||!double.IsFinite(spacing)||spacing<=0||spacing>100)throw new ArgumentException("间距须大于 0 且不超过 100 mm。");
        var p=_sampleOrigin;var target=new TransformInfo{X=p.X+(_sampleIndex%3-1)*spacing,Y=p.Y+(_sampleIndex/3-1)*spacing,Z=p.Z,Rx=p.Rx,Ry=p.Ry,Rz=p.Rz};
        ClearDirect();await _connection.Http.PreviewCartesianAsync(_selectedRobot.Id,target,SpeedSlider.Value,_selectedRobot.Axes==3,ct);
        var acquired=_connection.RobotSession?.IsHoldingControl!=true;var owns=false;
        try {if(acquired)await _connection.AcquireRobotControlAsync(ct);owns=true;_isMoving=true;ApplyRobotState(await _connection.RobotSession!.MoveCartesianAsync(target,SpeedSlider.Value,ct));_sampleIndex++;SampleStatus.Text=$"已到网格点 {_sampleIndex}/9。请在 VM 运行采样流程，收到结果后记录。";}
        catch {if(owns)try{await _connection.StopRobotAsync();}catch{}throw;}
        finally {_isMoving=false;if(acquired)try{await _connection.ReleaseRobotControlAsync();}catch{}}
    });
    void SampleRecord_Click(object sender,RoutedEventArgs e)=>VisionWork(RecordSample);
    async Task RecordSample(CancellationToken ct)
    {
        if(_connection is null||_selectedRobot is null||_directFrame is null||_directResult?.Kind!="sample"||!_directResult.Found||_binding!=BindingKey())throw new InvalidOperationException("请先在 VM 运行采样流程，等待有效 sample 结果；绑定设备后无需有效标定即可采样。");
        var frame=await ReadVmFrame(_directResult.BlockId,ct);
        if(!_sampleFrames.Add(frame.FrameId))throw new InvalidOperationException("此帧已记录，请在 VM 重新采图。");
        try {
            var robot=frame.Metadata.GetProperty("robots").EnumerateArray().Single(r=>r.GetProperty("deviceId").GetGuid()==_selectedRobot.Id).Deserialize<RobotRuntime>(JsonDefaults.Web)!;
            if(robot.Status=="Running")throw new InvalidOperationException("曝光时机器人未停稳。");
            if(_directResult.U<0||_directResult.U>=frame.Width||_directResult.V<0||_directResult.V>=frame.Height)throw new InvalidDataException("VM 采样像素越界。");
            var folder=Path.GetFullPath(SampleDirectory.Text);Directory.CreateDirectory(folder);
            var role=(SampleRole.SelectedItem as ComboBoxItem)?.Content?.ToString()??"calibration";
            var data=new{schema=1,role,frameId=frame.FrameId,cameraId=frame.CameraId,robotId=_selectedRobot.Id,width=frame.Width,height=frame.Height,sha256=frame.Sha256,sessionId=frame.Metadata.GetProperty("sessionId"),sceneRevision=frame.Metadata.GetProperty("sceneRevision"),exposureAt=frame.Metadata.GetProperty("exposureAt"),flangeBase=robot.FlangeBase,tcpBase=robot.TcpBase,joints=robot.Joints,poseId=robot.PoseId,vm=_directResult};
            await File.WriteAllBytesAsync(Path.Combine(folder,frame.FrameId+".png"),frame.Png,ct);
            await File.WriteAllTextAsync(Path.Combine(folder,frame.FrameId+".observation.json"),JsonSerializer.Serialize(data,JsonDefaults.Web),ct);
            var csv=Path.Combine(folder,"samples.csv");
            if(!File.Exists(csv))await File.WriteAllTextAsync(csv,"frameId,role,u,v,x,y,z,rx,ry,rz\n",Encoding.UTF8,ct);
            var p=robot.TcpBase.Pose;
            var values=new[]{_directResult.U,_directResult.V,p.X,p.Y,p.Z,p.Rx,p.Ry,p.Rz}.Select(n=>n.ToString("G17",CultureInfo.InvariantCulture));
            await File.AppendAllTextAsync(csv,frame.FrameId+","+role+","+string.Join(",",values)+"\n",Encoding.UTF8,ct);
            SampleStatus.Text=$"已记录 {role} 样本 {_sampleFrames.Count}，输出：{folder}。CSV 的 TCP XY 仅在靶标原点与 TCP 重合时可直接配对；手眼求解使用完整姿态。";Log(SampleStatus.Text);
        } catch {_sampleFrames.Remove(frame.FrameId);throw;}
    }
    void SampleReset_Click(object sender,RoutedEventArgs e)
    {
        _sampleOrigin=null;_sampleIndex=0;_sampleBinding=null;_sampleFrames.Clear();
        SampleDirectory.Text=Path.Combine(Path.GetDirectoryName(SampleDirectory.Text)??DirectDirectory,"samples-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        SampleStatus.Text="已新建批次，请设置中心；原导出文件保留。";
    }
}
