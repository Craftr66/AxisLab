using System.Diagnostics;
using AxisLab.Core;

namespace AxisLab.Host;

public sealed class MotionClock(ProjectStore store, CommunicationService bridge,CaptureService captures) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = clock.Elapsed.TotalSeconds;
                store.AdvanceMotions(now - last);store.AdvanceIndustrial(now-last);
                foreach(var trigger in store.PendingTriggers())
                    try{var capture=captures.Trigger(trigger.CameraId);store.UpdateTrigger(trigger.Sequence,capture.CaptureId,null,capture.Status);}
                    catch(DomainException ex){store.UpdateTrigger(trigger.Sequence,null,null,"Failed",ex.Message);}
                foreach(var camera in store.Get().Project.Devices.Where(d=>d.TypeId=="camera.pinhole"))
                    foreach(var trigger in store.TriggerEvents(camera.Id).Where(t=>t.Status is "Queued" or "Rendering"))
                        try{var capture=captures.Get(trigger.CaptureId!.Value);store.UpdateTrigger(trigger.Sequence,capture.CaptureId,capture.FrameId,capture.Status,capture.Message);}
                        catch(DomainException ex){store.UpdateTrigger(trigger.Sequence,trigger.CaptureId,null,"Failed",ex.Message);}
                bridge.Tick();
                last = clock.Elapsed.TotalSeconds;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { store.StopAllRobots(); }
    }
}
