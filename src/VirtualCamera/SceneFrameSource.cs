using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AxisLab.VirtualCamera;

sealed record CameraConfiguration(string ListenAddress,string AxisLabUrl,Guid CameraId,string StateDirectory);
sealed record CameraFrame(byte[] Pixels,JsonElement Metadata);

sealed class SceneFrameSource : IDisposable
{
    readonly HttpClient http;
    readonly Guid camera;
    readonly string directory;
    public int Width {get;private set;}
    public int Height {get;private set;}
    public SceneFrameSource(CameraConfiguration config)
    {
        var url=new Uri(config.AxisLabUrl);
        if(url.Scheme!="http"||!url.IsLoopback)throw new ArgumentException("AxisLabUrl must be a loopback HTTP service.");
        http=new HttpClient {BaseAddress=url,Timeout=TimeSpan.FromSeconds(25)};camera=config.CameraId;directory=Path.GetFullPath(config.StateDirectory);Directory.CreateDirectory(directory);
    }
    public async Task Initialize(CancellationToken ct)
    {
        using var devices=JsonDocument.Parse(await http.GetStringAsync("/api/v1/devices",ct));
        var item=devices.RootElement.EnumerateArray().Single(d=>d.GetProperty("id").GetGuid()==camera);
        if(!item.GetProperty("typeId").GetString()!.StartsWith("camera."))throw new ArgumentException("CameraId must identify an AxisLab camera.");
        var p=item.GetProperty("parameters");Width=p.GetProperty("width").GetInt32();Height=p.GetProperty("height").GetInt32();
    }
    public async Task<CameraFrame> Capture(CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(20));var token=deadline.Token;
        using var response=await http.PostAsync($"/api/v1/cameras/{camera}/captures",null,token);response.EnsureSuccessStatusCode();
        var state=await response.Content.ReadFromJsonAsync<JsonElement>(token);var capture=state.GetProperty("captureId").GetGuid();
        while(true) {
            var status=state.GetProperty("status").GetString();if(status=="Completed")break;if(status=="Failed")throw new IOException(state.ToString());
            await Task.Delay(30,token);state=await http.GetFromJsonAsync<JsonElement>($"/api/v1/captures/{capture}",token);
        }
        var id=state.GetProperty("frameId").GetGuid();var meta=await http.GetFromJsonAsync<JsonElement>($"/api/v1/cameras/{camera}/frames/{id}",token);
        if(meta.GetProperty("width").GetInt32()!=Width||meta.GetProperty("height").GetInt32()!=Height)throw new InvalidOperationException("Camera resolution changed; stop VM acquisition and restart the virtual camera.");
        var png=await http.GetByteArrayAsync($"/api/v1/cameras/{camera}/frames/{id}/image",token);
        if(!Convert.ToHexString(SHA256.HashData(png)).Equals(meta.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Frame SHA-256 mismatch.");
        using var input=new MemoryStream(png);var bitmap=BitmapDecoder.Create(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
        if(bitmap.PixelWidth!=Width||bitmap.PixelHeight!=Height)throw new InvalidDataException("Decoded dimensions mismatch.");
        var gray=new FormatConvertedBitmap(bitmap,PixelFormats.Gray8,null,0);var pixels=new byte[Width*Height];gray.CopyPixels(pixels,Width,0);
        await File.WriteAllBytesAsync(Path.Combine(directory,"latest.png"),png,token);
        return new CameraFrame(pixels,meta);
    }
    public async Task Sent(ushort block,CameraFrame frame,CancellationToken ct)
    {
        var receipt=JsonSerializer.Serialize(new {blockId=block,sentAt=DateTimeOffset.UtcNow,frame=frame.Metadata},new JsonSerializerOptions{WriteIndented=true});
        var blocks=Path.Combine(directory,"blocks");Directory.CreateDirectory(blocks);
        var blockPath=Path.Combine(blocks,$"{block:D5}.json");await File.WriteAllTextAsync(blockPath+".tmp",receipt,ct);File.Move(blockPath+".tmp",blockPath,true);
        foreach(var old in new DirectoryInfo(blocks).GetFiles("*.json").OrderByDescending(f=>f.LastWriteTimeUtc).Skip(64))old.Delete();
        var destination=Path.Combine(directory,"latest.json");var temporary=destination+".tmp";await File.WriteAllTextAsync(temporary,receipt,ct);File.Move(temporary,destination,true);
    }
    public void Dispose()=>http.Dispose();
}
