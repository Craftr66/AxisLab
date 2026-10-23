using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace AxisLab.WpfDemo;
public partial class MainWindow
{
    CancellationTokenSource? _vmCts;
    bool _visionBusy;
    void InitializeVision() => SampleDirectory.Text = Path.Combine(Environment.GetEnvironmentVariable("AXISLAB_WPF_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AxisLab","WpfDemo"),"samples");
    void ClearVisionResult() => ClearDirect();
    void ResetVision(bool resetBinding) { ClearDirect(); if(resetBinding) { _binding=null; _sampleOrigin=null; _sampleIndex=0; } }
    void UpdateVisionUi()
    {
        var busy=_visionBusy||_isBusy||_isMoving||_isCapturing;
        SpeedSlider.IsEnabled=!busy; JointPanel.IsEnabled=!busy;
        DirectPreview.IsEnabled=!busy&&_directTarget is not null;
        DirectMove.IsEnabled=DirectPreview.IsEnabled&&_directPreview is not null;
        DirectSettings.IsEnabled=!busy; SamplingPanel.IsEnabled=!busy;
        if(busy) {
            foreach(var b in new Button[]{AutoDetectButton,ConnectButton,DisconnectButton,RefreshButton,ImportButton,OpenProjectButton,AcquireButton,MoveButton,ZeroButton,ReleaseButton,CaptureButton}) b.IsEnabled=false;
            DeviceList.IsEnabled=false; ProjectCombo.IsEnabled=false;
            StopButton.IsEnabled=_connection?.IsModbusConnected==true;
        }
    }
    async void VisionWork(Func<CancellationToken,Task> action)
    {
        if(_visionBusy||_isBusy||_isMoving||_isCapturing)return;
        _visionBusy=true;var cts=_vmCts=new CancellationTokenSource();UpdateUiState();
        try {await action(cts.Token);}
        catch(Exception e) {ClearDirect();DirectStatus.Text="操作失败："+e.Message;Log(DirectStatus.Text);SetStatus(DirectStatus.Text);}
        finally {_visionBusy=false;_vmCts=null;cts.Dispose();UpdateUiState();}
    }
    Task CloseVision(){_vmCts?.Cancel();ResetVision(true);return Task.CompletedTask;}
}
