using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Net.Http;

namespace AxisLab.VirtualCamera;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        using var stop=new CancellationTokenSource();
        Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
        try {
            if(args.Length is 2 or 3&&args[0]=="--test-pattern") {await new GigECamera(IPAddress.Parse(args[1]),320,240,null,args.Length==3?int.Parse(args[2]):3956).Run(stop.Token);return 0;}
            if(args.Length!=2||args[0]!="--config")throw new ArgumentException("Usage: AxisLab.VirtualCamera --config camera.json");
            var config=JsonSerializer.Deserialize<CameraConfiguration>(await File.ReadAllTextAsync(args[1]),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new ArgumentException("Missing configuration");
            using var source=new SceneFrameSource(config);await source.Initialize(stop.Token);
            await new GigECamera(IPAddress.Parse(config.ListenAddress),source.Width,source.Height,source,config.ControlPort).Run(stop.Token);return 0;
        }
        catch(OperationCanceledException){return 0;}
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}

sealed class GigECamera
{
    readonly byte[] memory=new byte[0x40000];
    readonly IPAddress address;
    readonly int width,height;
    readonly int controlPort;
    readonly SceneFrameSource? source;
    readonly object sync=new();
    readonly SemaphoreSlim triggers=new(0,1);
    IPEndPoint? controller;
    DateTime lastControl;
    long acquisitionGeneration;
    ushort frameId;
    readonly Dictionary<string,(DateTime at,byte[] ack)> triggerReplies=new();
    uint Read(uint a)=>BinaryPrimitives.ReadUInt32BigEndian(memory.AsSpan((int)a,4));
    void Set(uint a,uint v)=>BinaryPrimitives.WriteUInt32BigEndian(memory.AsSpan((int)a,4),v);
    void String(uint a,int len,string value)=>Encoding.ASCII.GetBytes(value.AsSpan(0,Math.Min(value.Length,len-1)),memory.AsSpan((int)a,len));
    static uint Ip(IPAddress ip)=>BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
    public GigECamera(IPAddress ip,int w,int h,SceneFrameSource? source,int controlPort=3956)
    {
        address=ip;width=w;height=h;this.source=source;this.controlPort=controlPort;
        Set(0,0x00020000);Set(4,0x80000000);Set(8,0x02ab);Set(12,0x11223344);Set(0x10,1);Set(0x14,1);Set(0x24,Ip(ip));Set(0x34,0xffffff00);
        String(0x48,32,"AxisLab");String(0x68,32,"VirtualCamera");String(0x88,32,"2.1.0");String(0xa8,48,"Experimental AxisLab scene camera");String(0xd8,16,"AXISLAB001");String(0xe8,16,"AxisLabCam");
        var xml=Genicam.Create();xml.CopyTo(memory,0x20000);String(0x200,512,$"Local:AxisLab.xml;20000;{xml.Length:X}");
        Set(0x600,1);Set(0x904,1);Set(0x934,0xc0000000);Set(0x938,5000);Set(0x940,1000000000);Set(0xd04,1500);
        Set(0x10000,(uint)w);Set(0x10004,(uint)h);Set(0x10008,0x01080001);Set(0x10014,2);Set(0x10020,7);
    }
    public async Task Run(CancellationToken ct)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);ct=lifetime.Token;
        using var control=new UdpClient(new IPEndPoint(IPAddress.Any,controlPort));
        using var stream=new UdpClient(new IPEndPoint(address,0));
        Console.WriteLine($"EXPERIMENTAL {(source==null?"test-pattern":"AxisLab scene")} camera {address}:{controlPort} {width}x{height}");
        var streaming=Stream(stream,ct);
        try {while(!ct.IsCancellationRequested) {
            var request=await control.ReceiveAsync(ct);var p=request.Buffer;
            if(!request.RemoteEndPoint.Address.Equals(address))continue;
            if(p.Length<8||p[0]!=0x42)continue;
            ushort cmd=BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2)),id=BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6));
            int length=BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4));if(length!=p.Length-8)continue;
            bool softwareTrigger=cmd==0x82&&length==8&&BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(8))==0x1001c;
            string duplicateKey=request.RemoteEndPoint+"/"+id+"/"+Convert.ToHexString(p);
            if(softwareTrigger&&triggerReplies.TryGetValue(duplicateKey,out var previous)&&(DateTime.UtcNow-previous.at).TotalSeconds<2){await control.SendAsync(previous.ack,request.RemoteEndPoint,ct);continue;}
            byte[] payload=[];ushort status=0;
            lock(sync) {
                if(controller!=null&&(DateTime.UtcNow-lastControl).TotalMilliseconds>Read(0x938)) {controller=null;Set(0xa00,0);Set(0x10010,0);}
                bool owner=controller==null||controller.Equals(request.RemoteEndPoint);
                if(controller?.Equals(request.RemoteEndPoint)==true)lastControl=DateTime.UtcNow;
                try {switch(cmd) {
                    case 2:payload=memory[..0xf8];Console.WriteLine($"Discovery {request.RemoteEndPoint}");break;
                    case 0x80:
                        if(length==0||length%4!=0)throw new ArgumentException();
                        payload=new byte[length];for(int i=0;i<length;i+=4) {uint a=BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(8+i));if(a>memory.Length-4)throw new ArgumentException();memory.AsSpan((int)a,4).CopyTo(payload.AsSpan(i));}
                        break;
                    case 0x82:
                        if(!owner){status=0x8006;break;}if(length==0||length%8!=0)throw new ArgumentException();
                        for(int i=0;i<length;i+=8) {uint a=BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(8+i)),v=BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12+i));if(a!=0xa00&&a!=0x938&&a!=0xd00&&a!=0xd04&&a!=0xd08&&a!=0xd18&&a!=0x10010&&a!=0x10014&&a!=0x10018&&a!=0x1001c&&a!=0x10020)throw new ArgumentException();
                            if((a==0xd18&&v!=0&&v!=Ip(address))||(a==0x938&&(v<1000||v>60000))||(a==0x10018&&v>1)||(a==0x10020&&v is not (0 or 7)))throw new ArgumentException();
                            if(a==0xa00) {controller=v==0?null:request.RemoteEndPoint;lastControl=DateTime.UtcNow;}
                            if(a==0x10010||a==0xa00||a==0xd18||a==0xd00||a==0x10018||a==0x10020){acquisitionGeneration++;while(triggers.Wait(0)){};}
                            if(a==0x1001c&&Read(0x10020)!=7)throw new ArgumentException();
                            Set(a,v);if(a==0xd04)Set(a,v&0x7fffffff);
                            if(a==0x1001c&&v!=0&&triggers.CurrentCount==0)triggers.Release();
                        }payload=new byte[4];BinaryPrimitives.WriteUInt32BigEndian(payload,(uint)(length/8));break;
                    case 0x84:
                        if(length!=8)throw new ArgumentException();uint start=BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(8));int count=BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(14));if(count>4096||start+count>memory.Length)throw new ArgumentException();payload=new byte[4+count];BinaryPrimitives.WriteUInt32BigEndian(payload,start);memory.AsSpan((int)start,count).CopyTo(payload.AsSpan(4));break;
                    default:status=0x8001;Console.WriteLine($"Unsupported {cmd:X4}");break;
                }} catch(ArgumentException){status=0x8002;}
            }
            var ack=new byte[8+payload.Length];BinaryPrimitives.WriteUInt16BigEndian(ack,status);BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(2),(ushort)(cmd+1));BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(4),(ushort)payload.Length);BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(6),id);payload.CopyTo(ack,8);
            if(softwareTrigger){foreach(var old in triggerReplies.Where(x=>(DateTime.UtcNow-x.Value.at).TotalSeconds>=2).Select(x=>x.Key).ToArray())triggerReplies.Remove(old);if(triggerReplies.Count<128)triggerReplies[duplicateKey]=(DateTime.UtcNow,ack);}
            await control.SendAsync(ack,request.RemoteEndPoint,ct);
        }} finally { lifetime.Cancel();try{await streaming;}catch(OperationCanceledException){} }
    }
    async Task Stream(UdpClient socket,CancellationToken ct)
    {
        var pixels=new byte[width*height];for(int y=0;y<height;y++)for(int x=0;x<width;x++)pixels[y*width+x]=(byte)((x+y)%256);
        long armedGeneration=-1;
        while(!ct.IsCancellationRequested) {
            IPEndPoint? target=null;int size=1500;bool trigger=false;bool line=false;long generation;
            lock(sync) {
                if(controller!=null&&(DateTime.UtcNow-lastControl).TotalMilliseconds>Read(0x938)){controller=null;Set(0xa00,0);Set(0x10010,0);acquisitionGeneration++;}
                generation=acquisitionGeneration;
                if(controller!=null&&Read(0x10010)!=0&&Read(0xd00)!=0&&Read(0xd18)!=0) {target=new IPEndPoint(new IPAddress(BitConverter.GetBytes(Read(0xd18)).Reverse().ToArray()),(int)(Read(0xd00)&0xffff));size=Math.Clamp((int)(Read(0xd04)&0xffff),576,9000);trigger=Read(0x10018)!=0;}}
            if(target==null){await Task.Delay(20,ct);continue;}
            lock(sync)line=Read(0x10020)==0;
            if(trigger&&!line&&!await triggers.WaitAsync(100,ct))continue;
            CameraFrame? sceneFrame=null;
            if(source!=null) {
                try {if(trigger&&line&&armedGeneration!=generation){await source.Arm(ct);armedGeneration=generation;}sceneFrame=trigger&&line?await source.Triggered(ct):await source.Capture(ct);if(sceneFrame is null){await Task.Delay(30,ct);continue;}pixels=sceneFrame.Pixels;}
                catch(Exception e) when(e is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException) {if(ct.IsCancellationRequested)throw;Console.Error.WriteLine("CAPTURE FAILED: "+e.Message);await Task.Delay(500,ct);continue;}
            }
            lock(sync)if(generation!=acquisitionGeneration)continue;
            frameId++;if(frameId==0)frameId++;
            byte[] Packet(int type,int id,int bytes){var p=new byte[8+bytes];BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2),frameId);BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4),(uint)((type<<24)|id));return p;}
            var leader=Packet(1,0,36);BinaryPrimitives.WriteUInt16BigEndian(leader.AsSpan(10),1);BinaryPrimitives.WriteUInt64BigEndian(leader.AsSpan(12),(ulong)DateTime.UtcNow.Ticks*100);BinaryPrimitives.WriteUInt32BigEndian(leader.AsSpan(20),0x01080001);BinaryPrimitives.WriteUInt32BigEndian(leader.AsSpan(24),(uint)width);BinaryPrimitives.WriteUInt32BigEndian(leader.AsSpan(28),(uint)height);
            await socket.SendAsync(leader,target,ct);int packet=1;
            for(int pos=0;pos<pixels.Length;pos+=size-36) {int take=Math.Min(size-36,pixels.Length-pos);var p=Packet(3,packet++,take);pixels.AsSpan(pos,take).CopyTo(p.AsSpan(8));await socket.SendAsync(p,target,ct);if(packet%16==0)await Task.Delay(1,ct);}
            var trailer=Packet(2,packet,8);BinaryPrimitives.WriteUInt32BigEndian(trailer.AsSpan(8),1);BinaryPrimitives.WriteUInt32BigEndian(trailer.AsSpan(12),(uint)height);await socket.SendAsync(trailer,target,ct);
            if(sceneFrame!=null)await source!.Sent(frameId,sceneFrame,ct);
            Console.WriteLine($"Sent frame {frameId} to {target}");await Task.Delay(200,ct);
        }
    }
}
