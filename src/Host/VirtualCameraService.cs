using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using AxisLab.Core;

namespace AxisLab.Host;

public sealed class VirtualCameraService(ProjectStore store,IHostEnvironment environment):IHostedService,IDisposable
{
    readonly object gate=new();
    Process? process;
    Guid? cameraId,session;
    long configuration;
    string? directory,address;
    readonly Queue<string> messages=new();
    CancellationTokenSource? stop;
    bool disposed;
    Task? watch;
    static string[] Addresses()=>NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up)
        .OrderByDescending(n=>n.Name.Contains("tun",StringComparison.OrdinalIgnoreCase)).SelectMany(n=>n.GetIPProperties().UnicastAddresses)
        .Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a.Address)).Select(a=>a.Address.ToString()).Distinct().ToArray();
    public object Status(){lock(gate)return new{running=process is{HasExited:false},cameraId,listenAddress=address,stateDirectory=directory,addresses=Addresses(),messages=messages.ToArray()};}
    void Log(string? value){if(value is null)return;lock(gate){messages.Enqueue(value);while(messages.Count>30)messages.Dequeue();}}
    public async Task<object> Start(Guid camera,string ip,string url,CancellationToken ct)
    {
        var snapshot=store.Get();var device=snapshot.Project.Devices.SingleOrDefault(d=>d.Id==camera&&d.TypeId=="camera.pinhole")??throw new DomainException("NotCamera","请选择虚拟相机。");
        if(!Addresses().Contains(ip))throw new DomainException("InvalidInstrument","请选择本机有效 IPv4 地址。");
        lock(gate){if(process is{HasExited:false})throw new DomainException("CameraBusy","已有本程序启动的虚拟相机，请先停止。",409);}
        var candidates=new[]{
            Environment.GetEnvironmentVariable("AXISLAB_VIRTUAL_CAMERA"),
            Path.Combine(AppContext.BaseDirectory,"AxisLab.VirtualCamera.exe"),
            Path.Combine(AppContext.BaseDirectory,"VirtualCamera","AxisLab.VirtualCamera.exe"),
            Path.GetFullPath(Path.Combine(environment.ContentRootPath,"../VirtualCamera/bin/Release/net10.0-windows/AxisLab.VirtualCamera.exe"))
        };
        var executable=candidates.FirstOrDefault(p=>p is not null&&File.Exists(p))??throw new DomainException("AdapterMissing","缺少虚拟相机组件，请使用完整 AxisLab 发布包。",503);
        var data=Environment.GetEnvironmentVariable("AXISLAB_DESKTOP_HOME")??Environment.GetEnvironmentVariable("AXISLAB_DATA")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AxisLab");
        var output=Path.Combine(data,"VirtualCamera",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        var config=Path.Combine(output,"camera.json");
        var port=int.TryParse(Environment.GetEnvironmentVariable("AXISLAB_CAMERA_CONTROL_PORT"),out var configured)?configured:3956;
        if(port is <1 or >65535)throw new DomainException("InvalidInstrument","相机控制端口须为 1—65535。");
        await File.WriteAllTextAsync(config,JsonSerializer.Serialize(new{listenAddress=ip,axisLabUrl=url,cameraId=camera,stateDirectory=output,controlPort=port}),ct);
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add("--config");info.ArgumentList.Add(config);
        lock(gate){
            if(process is{HasExited:false})throw new DomainException("CameraBusy","已有本程序启动的虚拟相机，请先停止。",409);
            process?.Dispose();
            messages.Clear();cameraId=camera;session=snapshot.SessionId;configuration=device.ConfigurationRevision;directory=output;address=ip;
            process=Process.Start(info)??throw new DomainException("AdapterMissing","虚拟相机启动失败。",503);
            process.OutputDataReceived+=(_,e)=>Log(e.Data);process.ErrorDataReceived+=(_,e)=>Log(e.Data);process.BeginOutputReadLine();process.BeginErrorReadLine();
        }
        await Task.Delay(800,ct);
        lock(gate){if(process is null||process.HasExited)throw new DomainException("AdapterUnavailable","虚拟相机未启动：请检查 UDP 3956 是否已有相机占用。日志："+string.Join(" ",messages.TakeLast(3)),503);}
        return Status();
    }
    public object Stop()
    {
        lock(gate){
            if(process is not null){if(!process.HasExited)process.Kill();process.Dispose();process=null;}
            cameraId=null;session=null;
            return Status();
        }
    }
    public Task StartAsync(CancellationToken ct)
    {
        stop=CancellationTokenSource.CreateLinkedTokenSource(ct);watch=Watch(stop.Token);return Task.CompletedTask;
    }
    async Task Watch(CancellationToken ct)
    {
        try{while(!ct.IsCancellationRequested){await Task.Delay(500,ct);Guid? id,s;long config;lock(gate){id=cameraId;s=session;config=configuration;}
            if(id.HasValue){var snapshot=store.Get();if(snapshot.SessionId!=s||!snapshot.Project.Devices.Any(d=>d.Id==id&&d.ConfigurationRevision==config)){Stop();Log("工程、相机或配置已变化，虚拟相机已停止；请重新启动。");}}
        }}catch(OperationCanceledException)when(ct.IsCancellationRequested){}
    }
    public async Task StopAsync(CancellationToken ct){stop?.Cancel();Stop();if(watch is not null)await watch;}
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;stop?.Cancel();Stop();stop?.Dispose();stop=null;}}
}
