using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
namespace AxisLab.WpfDemo;
public sealed record DirectResult(int Version,string Kind,string PublisherSession,int Sequence,int BlockId,string Flow,string Source,string SolutionHash,string CalibrationId,string Frame,string Units,bool Found,bool CalibrationOk,TransformInfo? Target,double U=0,double V=0)
{
    public static DirectResult Parse(string line)
    {
        if(Encoding.UTF8.GetByteCount(line)>8192)throw new InvalidDataException("VM 结果超过 8KiB。");
        using var doc=JsonDocument.Parse(line);if(doc.RootElement.TryGetProperty("error",out var error))throw new InvalidDataException("VM 流程失败："+error.GetString());
        var r=JsonSerializer.Deserialize<DirectResult>(line,JsonDefaults.Web)??throw new InvalidDataException("空结果。");
        if(r.Version!=2||r.Kind is not ("target" or "sample")||!Guid.TryParseExact(r.PublisherSession,"N",out _)||r.Sequence<=0||r.BlockId is <1 or >65535||
           r.SolutionHash?.Length!=64||!r.SolutionHash.All(Uri.IsHexDigit)||string.IsNullOrWhiteSpace(r.CalibrationId)||string.IsNullOrWhiteSpace(r.Flow)||string.IsNullOrWhiteSpace(r.Source)||r.Frame!="robot-base"||r.Units!="mm/deg"||
           !double.IsFinite(r.U)||!double.IsFinite(r.V)||
           (r.Kind=="target"&&r.Found&&r.CalibrationOk&&(r.Target is null||new[]{r.Target.X,r.Target.Y,r.Target.Z,r.Target.Rx,r.Target.Ry,r.Target.Rz}.Any(n=>!double.IsFinite(n)))))
            throw new InvalidDataException("VM 协议 v2 格式或坐标无效。");
        if(r.Kind=="sample"&&r.Found)
            foreach(var name in new[]{"u","v"})if(!doc.RootElement.TryGetProperty(name,out var value)||value.ValueKind!=JsonValueKind.Number)throw new InvalidDataException("缺少采样像素："+name);
        // Require all six pose fields; missing JSON values must never silently become zero.
        if(r.Kind=="target"&&r.Found&&r.CalibrationOk) {
            var pose=doc.RootElement.GetProperty("target");
            foreach(var name in new[]{"x","y","z","rx","ry","rz"})if(!pose.TryGetProperty(name,out var v)||v.ValueKind!=JsonValueKind.Number)throw new InvalidDataException("缺少完整 VM 目标："+name);
        }
        return r;
    }
}
sealed class DirectResultListener:IAsyncDisposable
{
    readonly TcpListener listener;readonly CancellationTokenSource stop=new();readonly Task running;
    public int Port{get;}
    public DirectResultListener(int port,Func<DirectResult,Task> receive,Action<string> error) {
        listener=new TcpListener(IPAddress.Loopback,port);listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;running=Run(receive,error);
    }
    async Task Run(Func<DirectResult,Task> receive,Action<string> error)
    {
        while(!stop.IsCancellationRequested) {
            try {
                using var client=await listener.AcceptTcpClientAsync(stop.Token);
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(2000);
                var bytes=new byte[8193];int count=0;
                while(count<bytes.Length){int got=await client.GetStream().ReadAsync(bytes.AsMemory(count,1),deadline.Token);if(got==0)break;if(bytes[count++]==(byte)'\n')break;}
                if(count==0||count>8192||bytes[count-1]!=(byte)'\n')throw new InvalidDataException("VM 结果须以换行结尾且不超过 8KiB。");
                await receive(DirectResult.Parse(new UTF8Encoding(false,true).GetString(bytes,0,count)));
            }catch(Exception e) when(e is IOException or InvalidDataException or SocketException or JsonException or OperationCanceledException or ArgumentException or InvalidOperationException or KeyNotFoundException) {if(!stop.IsCancellationRequested)error(e.Message);}
        }
    }
    public async ValueTask DisposeAsync(){stop.Cancel();listener.Stop();try{await running;}catch(OperationCanceledException){}stop.Dispose();}
}
