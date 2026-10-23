using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
namespace AxisLab.WpfDemo;
public partial class MainWindow
{
    DirectResultListener? _directListener;
    Process? _directCamera;
    TransformInfo? _directTarget;
    CapturedFrame? _directFrame;
    DirectResult? _directResult;
    string? _directPreview,_binding,_directPublisher;
    int _directSequence;
    readonly HashSet<Guid> _directConsumed=[];
    string DirectDirectory=>Path.Combine(Environment.GetEnvironmentVariable("AXISLAB_WPF_DATA")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AxisLab","WpfDemo"),"direct-camera");
    static string FileHash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    void InitializeDirect()
    {
        var addresses=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up).SelectMany(n=>n.GetIPProperties().UnicastAddresses.Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&!System.Net.IPAddress.IsLoopback(a.Address)).Select(a=>new{address=a.Address.ToString(),name=n.Name})).OrderByDescending(a=>a.name.Contains("tun",StringComparison.OrdinalIgnoreCase));
        foreach(var a in addresses)DirectNic.Items.Add(a.address);
        if(DirectNic.Items.Count>0)DirectNic.SelectedIndex=0;
        DirectStatePath.Text=DirectDirectory;
    }
    void ClearDirect(){_directResult=null;_directFrame=null;_directTarget=null;_directPreview=null;}
    string BindingKey()=>JsonSerializer.Serialize(new{endpoint=_connection?.BaseUri?.AbsoluteUri,session=_connection?.Project?.SessionId,robot=_selectedRobot?.Id,robotConfig=_selectedRobot?.ConfigurationRevision,camera=_selectedCamera?.Id,cameraConfig=_selectedCamera?.ConfigurationRevision,solution=Path.GetFullPath(DirectSolution.Text),hash=FileHash(DirectSolution.Text),flow=DirectFlow.Text,source=DirectSource.Text,calibration=DirectCalibration.Text});
    string DirectKey()=>BindingKey()+"|"+SpeedSlider.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)+"|"+_directFrame?.FrameId;
    void DirectSolution_Click(object sender,RoutedEventArgs e){var dialog=new OpenFileDialog{Filter="VisionMaster方案|*.sol"};if(dialog.ShowDialog(this)==true){ClearDirect();_binding=null;DirectSolution.Text=dialog.FileName;}}
    void DirectBind_Click(object sender,RoutedEventArgs e)=>VisionWork(ct=>{
        if(_connection is null||_selectedRobot is null||_selectedCamera is null||string.IsNullOrWhiteSpace(DirectCalibration.Text))throw new InvalidOperationException("连接后选择机器人、相机，并填写 VM 标定编号。");
        ClearDirect();_binding=BindingKey();DirectStatus.Text="已绑定当前设备、方案文件与标定编号。请在 VM 确认该标定已独立验证，再运行流程。";Log(DirectStatus.Text);return Task.CompletedTask;
    });
    void DirectStart_Click(object sender,RoutedEventArgs e)=>VisionWork(StartDirectCamera);
    async Task StartDirectCamera(CancellationToken ct)
    {
        ClearDirect();
        if(_connection?.BaseUri is null||_selectedCamera is null)throw new InvalidOperationException("先连接 AxisLab 并选择虚拟相机。");
        if(_directCamera is {HasExited:false})throw new InvalidOperationException("虚拟相机已启动；切换设备或接口前先停止。");
        var executable=Environment.GetEnvironmentVariable("AXISLAB_VIRTUAL_CAMERA")??Path.Combine(AppContext.BaseDirectory,"VirtualCamera","AxisLab.VirtualCamera.exe");
        if(!File.Exists(executable))throw new FileNotFoundException("缺少 VirtualCamera 目录，请使用完整发布包。",executable);
        Directory.CreateDirectory(DirectDirectory);var config=Path.Combine(DirectDirectory,"camera.json");
        DirectStatePath.Text=Path.Combine(DirectDirectory,Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(config,JsonSerializer.Serialize(new{listenAddress=DirectNic.Text,axisLabUrl=_connection.BaseUri.GetLeftPart(UriPartial.Authority),cameraId=_selectedCamera.Id,stateDirectory=DirectStatePath.Text}),ct);
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add("--config");info.ArgumentList.Add(config);
        _directCamera=Process.Start(info)??throw new IOException("虚拟相机启动失败。");
        _directCamera.OutputDataReceived+=(_,a)=>{if(a.Data is not null)Dispatcher.BeginInvoke(new Action(()=>Log(a.Data)));};
        _directCamera.ErrorDataReceived+=(_,a)=>{if(a.Data is not null)Dispatcher.BeginInvoke(new Action(()=>Log("虚拟相机："+a.Data)));};
        _directCamera.BeginOutputReadLine();_directCamera.BeginErrorReadLine();await Task.Delay(800,ct);
        if(_directCamera.HasExited)throw new IOException("启动失败，请检查接口地址与 UDP 3956 占用。");
        DirectStatus.Text="已发布 AxisLabCam（AXISLAB001）。在 VM 相机管理连接，将图像源切换为相机。";Log(DirectStatus.Text);
    }
    void DirectListen_Click(object sender,RoutedEventArgs e)=>VisionWork(ct=>{
        ClearDirect();if(_directListener is not null)throw new InvalidOperationException("结果监听已开启。");
        if(!int.TryParse(DirectPort.Text,out var port)||port is <1024 or >65535)throw new ArgumentException("结果端口须为 1024—65535。");
        _directPublisher=null;_directSequence=0;
        _directListener=new DirectResultListener(port,async result=>await Dispatcher.InvokeAsync(async()=>{
            if(_visionBusy||_isBusy||_isMoving||_isCapturing){ClearDirect();Log("任务运行中，VM 结果已拒绝，请停稳后重拍。");return;}
            _visionBusy=true;try{await AcceptDirect(result,CancellationToken.None);}catch(Exception ex){ClearDirect();DirectStatus.Text="结果已拒绝："+ex.Message;Log(DirectStatus.Text);}finally{_visionBusy=false;UpdateUiState();}
        }).Task.Unwrap(),message=>Dispatcher.BeginInvoke(new Action(()=>{ClearDirect();DirectStatus.Text="结果已拒绝："+message;Log(DirectStatus.Text);UpdateUiState();})));
        DirectStatus.Text=$"正在监听 127.0.0.1:{port}，等待 VM 协议 v2 结果；请在 VM 运行流程。";Log(DirectStatus.Text);return Task.CompletedTask;
    });
    async Task<CapturedFrame> ReadVmFrame(int? block,CancellationToken ct)
    {
        if(_connection is null||_selectedCamera is null)throw new InvalidOperationException("请选择相机。");
        var path=block.HasValue?Path.Combine(DirectStatePath.Text,"blocks",$"{block.Value:D5}.json"):Path.Combine(DirectStatePath.Text,"latest.json");
        JsonElement receipt=default;
        for(int retry=0;retry<20;retry++){try{using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(path,ct));receipt=doc.RootElement.Clone();break;}catch(IOException){await Task.Delay(50,ct);}}
        if(receipt.ValueKind!=JsonValueKind.Object||(block.HasValue&&receipt.GetProperty("blockId").GetInt32()!=block.Value))throw new InvalidDataException("找不到对应图像的曝光记录，请在 VM 重新采图。");
        var meta=receipt.GetProperty("frame");
        if(DateTimeOffset.UtcNow-meta.GetProperty("exposureAt").GetDateTimeOffset()>TimeSpan.FromSeconds(30))throw new InvalidDataException("图像已超过 30 秒，请在 VM 重拍。");
        var runtime=await _connection.Http.GetRuntimeAsync(ct);
        if(_connection.Project?.SessionId!=runtime.SessionId||_connection.Project?.Revision!=runtime.ProjectRevision)throw new InvalidDataException("工程已改变，请刷新设备并重新绑定标定。");
        var devices=await _connection.Http.GetDevicesAsync(ct);
        if(runtime.SessionId!=meta.GetProperty("sessionId").GetGuid()||runtime.ProjectRevision!=meta.GetProperty("sceneRevision").GetInt64()||meta.GetProperty("cameraId").GetGuid()!=_selectedCamera.Id||meta.GetProperty("cameraConfigurationRevision").GetUInt32()!=devices.Single(d=>d.Id==_selectedCamera.Id).ConfigurationRevision)throw new InvalidDataException("场景、会话或相机配置已变化，请刷新后重拍。");
        var frame=await _connection.Http.ReadCapturedFrameAsync(_selectedCamera.Id,meta.GetProperty("frameId").GetGuid(),ct);
        if(frame.Sha256!=meta.GetProperty("sha256").GetString())throw new InvalidDataException("图像摘要不一致。");
        return frame;
    }
    async Task AcceptDirect(DirectResult result,CancellationToken ct)
    {
        ClearDirect();
        if(_connection is null||_selectedRobot is null||_selectedCamera is null||_binding!=BindingKey())throw new InvalidOperationException("先绑定当前设备和 VM 标定，配置变化后需重新绑定。");
        if((_directPublisher is not null&&_directPublisher!=result.PublisherSession)||result.Sequence<=_directSequence)throw new InvalidDataException("发布器重启或结果重复，请重启结果监听。");
        _directPublisher=result.PublisherSession;_directSequence=result.Sequence;
        if(!string.Equals(FileHash(DirectSolution.Text),result.SolutionHash,StringComparison.OrdinalIgnoreCase)||result.Flow!=DirectFlow.Text||result.Source!=DirectSource.Text||result.CalibrationId!=DirectCalibration.Text)throw new InvalidDataException("方案摘要、流程、图像源或标定编号不一致。");
        var frame=await ReadVmFrame(result.BlockId,ct);
        var exposure=frame.Metadata.GetProperty("robots").EnumerateArray().Single(r=>r.GetProperty("deviceId").GetGuid()==_selectedRobot.Id);
        if(exposure.GetProperty("status").GetString()=="Running"||_directConsumed.Contains(frame.FrameId))throw new InvalidDataException("曝光时机器人运动中，或此帧已经执行过。");
        _lastFrame=frame;DisplayFrame(frame);_directFrame=frame;_directResult=result;
        if(result.Kind=="sample"){DirectStatus.Text="已收到 VM 采样结果，可在采样助手中记录像素及曝光姿态。";}
        else {if(!result.Found||!result.CalibrationOk)throw new InvalidDataException("VM 返回未检出或标定无效。");_directTarget=result.Target;DirectStatus.Text=$"VM 基坐标目标：X {result.Target!.X:F3} / Y {result.Target.Y:F3} / Z {result.Target.Z:F3} mm\nRx {result.Target.Rx:F3} / Ry {result.Target.Ry:F3} / Rz {result.Target.Rz:F3}°\n请先预检，再执行一次定位。";}
        Log(DirectStatus.Text);
    }
    async Task ValidateDirect(CancellationToken ct)
    {
        if(_directTarget is null||_directFrame is null||_directResult is null||_connection is null||_selectedRobot is null||_binding!=BindingKey()||_directConsumed.Contains(_directFrame.FrameId))throw new InvalidOperationException("结果或配置失效，请重新运行 VM 流程。");
        var frame=await ReadVmFrame(_directResult.BlockId,ct);
        if(frame.FrameId!=_directFrame.FrameId)throw new InvalidDataException("帧已被替换。");
        var runtime=await _connection.Http.GetRuntimeAsync(ct);var robot=runtime.Robots.Single(r=>r.DeviceId==_selectedRobot.Id);
        var exposure=_directFrame.Metadata.GetProperty("robots").EnumerateArray().Single(r=>r.GetProperty("deviceId").GetGuid()==_selectedRobot.Id);
        if(robot.Status=="Running"||robot.PoseId!=exposure.GetProperty("poseId").GetUInt64())throw new InvalidOperationException("曝光后机器人姿态已变化，请在 VM 重新采图。");
    }
    async Task PreviewDirect(CancellationToken ct)
    { _directPreview=null;await ValidateDirect(ct);await _connection!.Http.PreviewCartesianAsync(_selectedRobot!.Id,_directTarget!,SpeedSlider.Value,_selectedRobot.Axes==3,ct);await ValidateDirect(ct);_directPreview=DirectKey();DirectStatus.Text+="\n目标预检通过。";Log("VM 目标预检通过。"); }
    async Task MoveDirect(CancellationToken ct)
    {
        await ValidateDirect(ct);if(_directPreview!=DirectKey())throw new InvalidOperationException("先预检当前目标。");
        await PreviewDirect(ct);var connection=_connection!;var frame=_directFrame!.FrameId;var target=_directTarget!;bool acquired=connection.RobotSession?.IsHoldingControl!=true,owns=false;
        try {if(acquired)await connection.AcquireRobotControlAsync(ct);await ValidateDirect(ct);owns=true;_directConsumed.Add(frame);_isMoving=true;ApplyRobotState(await connection.RobotSession!.MoveCartesianAsync(target,SpeedSlider.Value,ct));DirectStatus.Text="已通过 Modbus 完成一次定位；请重新采图后再执行。";Log(DirectStatus.Text);}
        catch {if(owns)try{await connection.StopRobotAsync();}catch{}throw;}
        finally{_isMoving=false;ClearDirect();if(acquired)try{ApplyRobotState(await connection.ReleaseRobotControlAsync());}catch{}UpdateUiState();}
    }
    void DirectPreview_Click(object sender,RoutedEventArgs e)=>VisionWork(PreviewDirect);
    void DirectMove_Click(object sender,RoutedEventArgs e)=>VisionWork(MoveDirect);
    void DirectStop_Click(object sender,RoutedEventArgs e)=>VisionWork(async ct=>{await CloseDirect();DirectStatus.Text="本次启动的虚拟相机与结果监听已关闭。";});
    async Task CloseDirect()
    {
        ClearDirect();if(_directListener is not null){await _directListener.DisposeAsync();_directListener=null;}
        if(_directCamera is not null){if(!_directCamera.HasExited)_directCamera.Kill();_directCamera.Dispose();_directCamera=null;}
    }
}
