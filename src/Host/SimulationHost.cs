using AxisLab.Core;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace AxisLab.Host;

public sealed record SimulationHostOptions(string ContentRootPath, string DataDirectory, string Url, bool Quiet = false);

/// <summary>The same simulation host can run in a terminal or inside the desktop application's lifetime.</summary>
public static class SimulationHost
{
    public static WebApplication Build(string[] args, SimulationHostOptions? options = null)
    {
        var builder = options is null ? WebApplication.CreateBuilder(args) : WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args, ContentRootPath = options.ContentRootPath,
            ApplicationName = typeof(SimulationHost).Assembly.GetName().Name
        });
        builder.WebHost.UseUrls(options?.Url ?? Environment.GetEnvironmentVariable("AXISLAB_URL") ?? "http://127.0.0.1:5080");
        if (options?.Quiet == true) builder.Logging.ClearProviders();
        builder.Services.Configure<JsonOptions>(o => { o.SerializerOptions.UnmappedMemberHandling =
            System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; o.SerializerOptions.MaxDepth=2048; });
        var dataPath = options?.DataDirectory ?? Environment.GetEnvironmentVariable("AXISLAB_DATA") ??
            Path.Combine(builder.Environment.ContentRootPath, "../../data/projects");
        builder.Services.AddSingleton(new ProjectStore(dataPath));
        builder.Services.AddHostedService<MotionClock>();
        builder.Services.AddSingleton(sp => new CaptureService(sp.GetRequiredService<ProjectStore>()));
        builder.Services.AddSingleton<CommunicationService>();
        builder.Services.AddSingleton<VirtualCameraService>();
        builder.Services.AddHostedService(sp=>sp.GetRequiredService<VirtualCameraService>());
        builder.Services.AddHostedService<ModbusServer>();
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
        var app = builder.Build();
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var address = app.Urls.FirstOrDefault();
            if (address is not null) app.Services.GetRequiredService<CommunicationService>().SetHttpPort(new Uri(address).Port);
        });
        app.Use(async (context, next) =>
        {
            try { await next(); }
            catch (DomainException ex)
            { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message }); }
            catch (BadHttpRequestException)
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = "InvalidRequest", message = "请求格式错误，请检查 JSON 和字段类型。" }); }
            catch (IOException ex)
            {
                app.Logger.LogError(ex, "Project file operation failed");
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { code = "StorageError", message = "工程文件读写失败，请检查目录权限与磁盘空间。" });
            }
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/api/health", () => new { status = "ok", version = "2.1.0", mode = "workcell" });
        app.MapGet("/api/v1/communication", (CommunicationService bridge) => bridge.Status());
        app.MapGet("/api/v1/communication/map", () => ModbusProtocol.Describe());
        app.MapGet("/api/v1/communication/frames/{sessionToken}/{frameToken:long}", (string sessionToken, long frameToken, CommunicationService bridge) =>
        {
            if (frameToken is < 1 or > uint.MaxValue) throw new DomainException("FrameNotFound", "无效帧令牌。", 404);
            return bridge.Frame(sessionToken, (uint)frameToken);
        });
        app.MapGet("/api/v1/communication/clients/{clientId:long}/commands/{sequence:long}", (long clientId, long sequence, CommunicationService bridge) =>
        {
            if (clientId is < 1 or > uint.MaxValue || sequence is < 1 or > uint.MaxValue) throw new DomainException("CommandNotFound", "无效命令索引。", 404);
            return bridge.Command((uint)clientId, (uint)sequence);
        });
        app.MapGet("/api/v1/device-types", () => Catalog.Types);
        app.MapGet("/api/v1/examples/workcell",()=>{
            var paths=new[]{Path.Combine(AppContext.BaseDirectory,"Samples","workcell-pick-place.scene.json"),Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath,"../../samples/workcell-pick-place.scene.json"))};
            var file=paths.FirstOrDefault(File.Exists)??throw new DomainException("ProjectNotFound","发布包中缺少取放演示工程。",404);
            return System.Text.Json.JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(file),Catalog.Json);
        });
        app.MapGet("/api/v1/project", (ProjectStore store) => store.Get());
        app.MapGet("/api/v1/devices", (ProjectStore store) => store.Get().Project.Devices);
        app.MapGet("/api/v1/projects", (ProjectStore store) => store.ListFiles());
        app.MapGet("/api/v1/runtime", (ProjectStore store) => store.GetRuntime());
        app.MapPost("/api/v1/instruments/{id:guid}/conveyor",(Guid id,ConveyorMutation request,ProjectStore store)=>store.SetConveyor(id,request.Speed));
        app.MapPost("/api/v1/instruments/{id:guid}/gripper",(Guid id,GripperMutation request,ProjectStore store)=>store.SetGripper(id,request.Close));
        app.MapPost("/api/v1/cell/generate",(CellMutation request,ProjectStore store)=>store.GeneratePickPlace(request.RobotId,request.ConveyorId,request.SensorId,request.GripperId,request.CameraId,request.PointIds,request.SpeedPercent,request.ExpectedRevision));
        app.MapPut("/api/v1/cell/sequence",(SequenceMutation request,ProjectStore store)=>store.SetCellSequence(request.Sequence,request.ExpectedRevision));
        app.MapPost("/api/v1/cell/start",(ProjectStore store)=>store.StartCell());
        app.MapPost("/api/v1/cell/stop",(ProjectStore store)=>{store.StopCell();return store.GetRuntime();});
        app.MapPost("/api/v1/cell/reset",(ProjectStore store)=>store.ResetCell());
        app.MapGet("/api/v1/cameras/{id:guid}/triggers",(Guid id,long? after,ProjectStore store)=>store.TriggerEvents(id,after??0));
        app.MapGet("/api/v1/virtual-camera",(VirtualCameraService service)=>service.Status());
        app.MapPost("/api/v1/virtual-camera/start",async(VirtualCameraMutation request,HttpContext ctx,VirtualCameraService service)=>await service.Start(request.CameraId,request.ListenAddress,$"http://127.0.0.1:{ctx.Connection.LocalPort}",ctx.RequestAborted));
        app.MapPost("/api/v1/virtual-camera/stop",(VirtualCameraService service)=>service.Stop());
        app.MapGet("/api/v1/robot-model", () => new { modelId = Generic6R.ModelId, joints = Generic6R.Definitions });
        app.MapGet("/api/v1/robot-models", () => new[] {
            new { modelId = Generic6R.ModelId, joints = RobotModels.Definitions(Generic6R.ModelId) },
            new { modelId = RobotModels.ThreeAxis, joints = RobotModels.Definitions(RobotModels.ThreeAxis) } });
        app.MapPost("/api/v1/renderers", (CaptureService captures) => captures.Register());
        static string Token(HttpContext ctx) => ctx.Request.Headers["X-AxisLab-Renderer-Token"].ToString();
        app.MapPost("/api/v1/renderers/{id:guid}/heartbeat", (Guid id, HttpContext ctx, CaptureService captures) =>
        { captures.Heartbeat(id, Token(ctx)); return Results.Ok(new { status = "ok" }); });
        app.MapGet("/api/v1/renderers/{id:guid}/captures", (Guid id, HttpContext ctx, CaptureService captures) =>
        { var work = captures.TakeWork(id, Token(ctx)); return work is null ? Results.NoContent() : Results.Ok(work); });
        app.MapPost("/api/v1/renderers/{id:guid}/captures/{captureId:guid}/fail", (Guid id, Guid captureId,
            RenderFailure request, HttpContext ctx, CaptureService captures) => captures.RenderFailed(id, Token(ctx), captureId, request.Message));
        app.MapPut("/api/v1/renderers/{id:guid}/captures/{captureId:guid}/image", async (Guid id, Guid captureId,
            HttpContext ctx, CaptureService captures) =>
        {
            var limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is not null && !limit.IsReadOnly) limit.MaxRequestBodySize = PngValidation.MaximumBytes;
            if (!string.Equals(ctx.Request.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
                throw new DomainException("InvalidContentType", "请上传 image/png 二进制图像。", 415);
            if (ctx.Request.ContentLength > PngValidation.MaximumBytes) throw new DomainException("ImageTooLarge", "图像超过 8 MiB。", 413);
            using var memory = new MemoryStream();
            var buffer = new byte[65536]; int count;
            while ((count = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                if (memory.Length + count > PngValidation.MaximumBytes) throw new DomainException("ImageTooLarge", "图像超过 8 MiB。", 413);
                memory.Write(buffer, 0, count);
            }
            return captures.Complete(id, Token(ctx), captureId, memory.ToArray());
        });
        app.MapPost("/api/v1/cameras/{id:guid}/captures", (Guid id, CaptureService captures) =>
        { var state = captures.Trigger(id); return Results.Accepted($"/api/v1/captures/{state.CaptureId}", state); });
        app.MapGet("/api/v1/captures/{id:guid}", (Guid id, CaptureService captures) => captures.Get(id));
        app.MapGet("/api/v1/cameras/{id:guid}/frames/{frameId:guid}", (Guid id, Guid frameId, CaptureService captures) => captures.Metadata(id, frameId));
        app.MapGet("/api/v1/cameras/{id:guid}/frames/{frameId:guid}/image", (Guid id, Guid frameId, CaptureService captures) =>
        { var image = captures.Image(id, frameId); return Results.File(image.Bytes, "image/png", entityTag: new EntityTagHeaderValue('"' + image.Metadata.Sha256 + '"')); });
        app.MapPost("/api/v1/robots/{id:guid}/joints", (Guid id, JointMutation request, ProjectStore store) =>
            store.MoveJoints(id, request.Joints, request.SpeedPercent, request.ExpectedRevision));
        app.MapPost("/api/v1/robots/{id:guid}/stop", (Guid id, ProjectStore store) => store.StopRobot(id));
        app.MapPost("/api/v1/robots/{id:guid}/preview", (Guid id, CartesianRequest request, ProjectStore store) => store.PreviewCartesian(id, request));
        app.MapPost("/api/v1/robots/{id:guid}/cartesian", (Guid id, CartesianMutation request, ProjectStore store) => store.MoveCartesian(id, request.Motion, request.ExpectedRevision));
        app.MapPost("/api/v1/robots/{id:guid}/teach-points", (Guid id, TeachMutation request, ProjectStore store) => store.RecordTeachPoint(id, request.Name, request.Frame, request.ExpectedRevision));
        app.MapPut("/api/v1/teach-points/{id:guid}", (Guid id, EditTeachMutation request, ProjectStore store) => store.EditTeachPoint(id, request.Name, request.Action, request.ExpectedRevision));
        app.MapPost("/api/v1/robots/{id:guid}/play", (Guid id, PlaybackMutation request, ProjectStore store) => store.PlayTeachPoints(id, request.PointIds, request.Kind, request.SpeedPercent, request.ExpectedRevision));
        app.MapPost("/api/v1/project/new", (NamedMutation request, ProjectStore store) => store.New(request.ExpectedRevision, request.Name));
        app.MapPost("/api/v1/project/save", (NamedMutation request, ProjectStore store) => store.Save(request.Name, request.ExpectedRevision));
        app.MapPost("/api/v1/project/open", (OpenMutation request, ProjectStore store) => store.Open(request.FileId, request.ExpectedRevision));
        app.MapPost("/api/v1/project/import", (ImportMutation request, ProjectStore store) => store.Import(request.Project, request.ExpectedRevision));
        app.MapPost("/api/v1/devices", (AddMutation request, ProjectStore store) => store.Add(request.TypeId, request.ExpectedRevision));
        app.MapPut("/api/v1/devices/{id:guid}", (Guid id, UpdateMutation request, ProjectStore store) => store.Update(id, request.Device, request.ExpectedRevision));
        app.MapPost("/api/v1/devices/{id:guid}/duplicate", (Guid id, RevisionMutation request, ProjectStore store) => store.Duplicate(id, request.ExpectedRevision));
        app.MapDelete("/api/v1/devices/{id:guid}", (Guid id, long expectedRevision, bool? cascade, ProjectStore store) => store.Delete(id, expectedRevision, cascade ?? false));
        app.MapFallback("/api/{**path}", () => Results.Json(new { code = "Unsupported", message = "此接口尚未实现；当前支持场景、关节运动与相机抓图。" }, statusCode: 404));
        app.MapFallbackToFile("index.html");
        return app;
    }

    private record RevisionMutation(long ExpectedRevision);
    private record ConveyorMutation(double Speed);
    private record GripperMutation(bool Close);
    private record VirtualCameraMutation(Guid CameraId,string ListenAddress);
    private record SequenceMutation(long ExpectedRevision,CellSequence Sequence);
    private record CellMutation(long ExpectedRevision,Guid RobotId,Guid ConveyorId,Guid SensorId,Guid GripperId,Guid CameraId,Guid[] PointIds,double SpeedPercent=35);
    private record JointMutation(long ExpectedRevision, double[] Joints, double SpeedPercent = 25);
    private record CartesianMutation(long ExpectedRevision, CartesianRequest Motion);
    private record TeachMutation(long ExpectedRevision, string Name, string Frame = "base");
    private record EditTeachMutation(long ExpectedRevision, string Name, string Action = "rename");
    private record PlaybackMutation(long ExpectedRevision, Guid[] PointIds, string Kind = "MoveJ", double SpeedPercent = 25);
    private record RenderFailure(string Message);
    private record NamedMutation(long ExpectedRevision, string Name);
    private record OpenMutation(long ExpectedRevision, string FileId);
    private record ImportMutation(long ExpectedRevision, ProjectDocument Project);
    private record AddMutation(long ExpectedRevision, string TypeId);
    private record UpdateMutation(long ExpectedRevision, Device Device);
}
