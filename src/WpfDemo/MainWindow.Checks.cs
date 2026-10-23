using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace AxisLab.WpfDemo;
public partial class MainWindow
{
    // Integration fixture: wire envelopes emulate VM output, while HTTP/Modbus/PNG/motion are real.
    public async Task RunDirectChecks(string configPath,string reportPath)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(configPath));var c=doc.RootElement;var checks=new List<string>();
        void Check(string name,bool value){if(!value)throw new InvalidOperationException(name+"\n"+LogBox.Text);checks.Add(name);Log("PASS "+name);}
        async Task Wait(Func<bool> predicate){var end=DateTime.UtcNow.AddSeconds(30);while(!predicate()){if(DateTime.UtcNow>end)throw new TimeoutException(LogBox.Text);await Task.Delay(30);}}
        async Task<bool> Reject(Func<Task> action){try{await action();return false;}catch(Exception e) when(e is InvalidOperationException or InvalidDataException){return true;}}
        bool Bad(string json){try{DirectResult.Parse(json);return false;}catch(Exception e)when(e is InvalidDataException or JsonException){return true;}}
        async Task Wire(object body){using var socket=new TcpClient();await socket.ConnectAsync("127.0.0.1",_directListener!.Port);await socket.GetStream().WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body,new JsonSerializerOptions(JsonDefaults.Web){WriteIndented=false})+"\n"));}
        AddressBox.Text=c.GetProperty("url").GetString()!;ConnectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>!_isBusy&&_connection?.IsModbusConnected==true);
        DeviceList.SelectedItem=_devices.First(d=>d.Axes==6);await Wait(()=>!_isBusy&&_selectedRobot is not null);
        DeviceList.SelectedItem=_devices.First(d=>d.IsCamera);await Wait(()=>!_isBusy&&_selectedCamera is not null);SpeedSlider.Value=70;
        var folder=Path.GetDirectoryName(configPath)!;DirectSolution.Text=Path.Combine(folder,"fixture.sol");File.WriteAllText(DirectSolution.Text,"integration fixture; no VM algorithm is claimed");
        DirectStatePath.Text=Path.Combine(folder,"frames");Directory.CreateDirectory(Path.Combine(DirectStatePath.Text,"blocks"));
        DirectFlow.Text="Fixture";DirectSource.Text="ImageSource";DirectCalibration.Text="test-calibration";_binding=BindingKey();
        DirectPort.Text=c.GetProperty("port").GetInt32().ToString();DirectListen_Click(this,new RoutedEventArgs());await Wait(()=>!_visionBusy&&_directListener is not null);
        Check("three clear workflow tabs and no SDK mode",VisionTabs.Items.Count==3&&!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name=="VM.Core"));
        // Only the fixture triggers acquisition here. Product Capture_Click reads an already acquired VM frame.
        var frame=await _connection!.CaptureAsync(_selectedCamera!);
        var receipt=JsonSerializer.Serialize(new{blockId=1,frame=frame.Metadata},JsonDefaults.Web);
        File.WriteAllText(Path.Combine(DirectStatePath.Text,"blocks","00001.json"),receipt);File.WriteAllText(Path.Combine(DirectStatePath.Text,"latest.json"),receipt);
        var exposure=(await _connection.Http.GetRuntimeAsync()).Robots.Single(r=>r.DeviceId==_selectedRobot!.Id).TcpBase.Pose;
        var target=new TransformInfo{X=exposure.X-5,Y=exposure.Y+3,Z=exposure.Z,Rx=exposure.Rx,Ry=exposure.Ry,Rz=exposure.Rz};
        var result=new DirectResult(2,"sample",Guid.NewGuid().ToString("N"),1,1,"Fixture","ImageSource",FileHash(DirectSolution.Text),"test-calibration","robot-base","mm/deg",true,false,null,100,100);
        await Wire(result);await Wait(()=>_directResult?.Sequence==1);
        Check("v2 sample resolves exact exposure and displays verified PNG",_directFrame!.FrameId==frame.FrameId&&CameraImage.Source is BitmapSource&&_directTarget is null);
        SampleDirectory.Text=Path.Combine(folder,"samples");await RecordSample(CancellationToken.None);
        var observation=File.ReadAllText(Path.Combine(SampleDirectory.Text,frame.FrameId+".observation.json"));
        Check("sampling export contains pixels and exposure matrices without camera truth",observation.Contains("flangeBase")&&observation.Contains("matrix")&&!observation.Contains("cameraWorld")&&!observation.Contains("intrinsics"));
        Check("duplicate sample is rejected",await Reject(()=>RecordSample(CancellationToken.None)));
        result=result with{Kind="target",Sequence=2,CalibrationOk=true,Target=target};await Wire(result);await Wait(()=>_directResult?.Sequence==2);
        Check("WPF accepts VM base pose unchanged",JsonSerializer.Serialize(_directTarget)==JsonSerializer.Serialize(target));
        Check("motion without preflight is rejected",await Reject(()=>MoveDirect(CancellationToken.None)));
        await PreviewDirect(CancellationToken.None);SpeedSlider.Value=60;
        Check("changed speed requires another preflight",await Reject(()=>MoveDirect(CancellationToken.None)));SpeedSlider.Value=70;
        Check("duplicate VM sequence clears old motion target",await Reject(()=>AcceptDirect(result,CancellationToken.None))&&_directTarget is null);
        result=result with{Sequence=3};await AcceptDirect(result,CancellationToken.None);
        Check("malformed version, coordinates and missing full pose are rejected",Bad("{}")&&Bad(JsonSerializer.Serialize(result with{Version=1},JsonDefaults.Web))&&Bad(JsonSerializer.Serialize(result with{BlockId=0},JsonDefaults.Web))&&Bad(MissingRx()));
        string MissingRx(){var json=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(result,JsonDefaults.Web))!;json["target"]!.AsObject().Remove("rx");return json.ToJsonString();}
        await Wire(new{version=2,error="fixture failure"});await Wait(()=>DirectStatus.Text.Contains("fixture failure"));
        Check("failed VM result clears old target and leaves listener alive",_directTarget is null&&_directListener is not null);
        result=result with{Sequence=4};await Wire(result);await Wait(()=>_directResult?.Sequence==4);
        await PreviewDirect(CancellationToken.None);await MoveDirect(CancellationToken.None);
        var actual=(await _connection.Http.GetRuntimeAsync()).Robots.Single(r=>r.DeviceId==_selectedRobot!.Id).TcpBase.Pose;
        Check("real Modbus motion reaches received base coordinates",Math.Abs(actual.X-target.X)<.5&&Math.Abs(actual.Y-target.Y)<.5&&Math.Abs(actual.Z-target.Z)<.5);
        Check("frame is consumed and owned control released",_directTarget is null&&_directConsumed.Contains(frame.FrameId)&&_connection.RobotSession?.IsHoldingControl!=true);
        Check("consumed frame cannot be replayed",await Reject(()=>AcceptDirect(result with{Sequence=5},CancellationToken.None)));
        VisionTabs.SelectedIndex=2;UpdateLayout();var shot=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32);shot.Render(this);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(shot));using(var output=File.Create(Path.Combine(folder,"wpf-direct.png")))encoder.Save(output);
        File.WriteAllText(reportPath,JsonSerializer.Serialize(new{success=true,fixture="VM v2 protocol fixture; real simulator motion and images",checks,target,actual},JsonDefaults.Web));
    }
}
