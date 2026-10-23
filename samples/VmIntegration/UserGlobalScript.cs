using System;
using VM.Core;
using VM.GlobalScript.Methods;
using AxisLab.VmDirectLink;
// VM 4.3 Global Script template. Add AxisLab.VmDirectLink.dll as a VM script reference.
// Configure the procedure outputs described in Docs/VM直接采图与WPF结果通信.md first.
public class UserGlobalScript : UserGlobalMethods, IScriptMethods
{
    const string SolutionPath=@"C:\YourProject\Positioning.sol";
    const string Flow="定位流程", ImageSource="图像源1", Calibration="calibration-001";
    ResultPublisher publisher;
    public int Init()
    {
        int code=InitSDK();if(code!=0)return code;
        if(publisher!=null)publisher.Dispose();
        publisher=new ResultPublisher(SolutionPath,Flow,ImageSource,Calibration,7100,"target");
        // Use "sample" while collecting PixelU/PixelV and exposure poses.
        return 0;
    }
    public int Process()
    {
        ((VmProcedure)VmSolution.Instance[Flow]).Run("",true);
        return 0;
    }
}
