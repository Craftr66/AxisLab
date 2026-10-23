using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using VM.Core;
using VM.PlatformSDKCS;
using static VM.PlatformSDKCS.ImvsSdkDefine;
namespace AxisLab.VmDirectLink
{
    /// <summary>Load into the VisionMaster process/global script, never into WPF.</summary>
    public sealed class ResultPublisher : IDisposable
    {
        readonly VmProcedure procedure;
        readonly VmModule source;
        readonly string solution,calibration,kind,session=Guid.NewGuid().ToString("N");
        readonly int port;
        readonly object sync=new object();
        int sequence; uint lastExecution; bool disposed;
        public string LastError { get; private set; }
        public ResultPublisher(string solutionPath,string flow,string imageSource,string calibrationId,int resultPort=7100,string resultKind="target")
        {
            solution=Path.GetFullPath(solutionPath);if(!File.Exists(solution))throw new FileNotFoundException(solution);
            if(string.IsNullOrWhiteSpace(calibrationId))throw new ArgumentException("Calibration ID required");
            if(resultKind!="target"&&resultKind!="sample")throw new ArgumentException("Result kind must be target or sample");
            if(resultPort<1024||resultPort>65535)throw new ArgumentOutOfRangeException(nameof(resultPort));
            procedure=VmSolution.Instance[flow] as VmProcedure??throw new ArgumentException("Unknown procedure: "+flow);
            source=procedure[imageSource] as VmModule??throw new ArgumentException("Unknown image source");
            calibration=calibrationId;port=resultPort;kind=resultKind;
            source.EnableResultCallback();procedure.EnableResultCallback();
            procedure.ModuleResultCallBackArrived+=Completed;
        }
        IMVS_MODULE_RESULT_INFO_LIST_EX_Data ReadSource()
        {
            var manager=new ModuleManager();var raw=manager.QueryModuleOneResult(source.ID,false);
            if(!raw.HasValue||raw.Value.nStatus!=1||raw.Value.nErrorCode!=0)throw new InvalidDataException("Image acquisition failed");
            return manager.PreProData(raw.Value,false);
        }
        static int Integer(IMVS_MODULE_RESULT_INFO_LIST_EX_Data data,string field)
        { var v=data.pInfo.Single(p=>p.strParamName==field);if(v.nValueNum!=1||v.pIntValue==null)throw new InvalidDataException(field);return v.pIntValue[0]; }
        double Float(string field)
        {
            var v=procedure.ModuResult.GetOutputFloat(field);
            if(v.nValueNum!=1||v.pFloatVal==null)throw new InvalidDataException("Expected one float: "+field);
            double n=v.pFloatVal[0];if(double.IsNaN(n)||double.IsInfinity(n))throw new InvalidDataException(field);return n;
        }
        bool Flag(string field)
        {
            var v=procedure.ModuResult.GetOutputInt(field);
            if(v.nValueNum!=1||v.pIntVal==null||(v.pIntVal[0]!=0&&v.pIntVal[0]!=1))throw new InvalidDataException("Expected int flag 0/1: "+field);
            return v.pIntVal[0]==1;
        }
        void Completed(object sender,EventArgs args)
        {
            lock(sync) {
                if(disposed)return;
                try {
                    var s=ReadSource();if(s.nExecuteCount==lastExecution)return;lastExecution=s.nExecuteCount;
                    if(Integer(s,"GetCameraFailCount")!=0||Integer(s,"LostPacketCount")!=0)throw new InvalidDataException("Camera failed or lost packets");
                    int block=Integer(s,"FrameNum");if(block<1||block>65535)throw new InvalidDataException("Invalid camera block");
                    bool found=Flag("Found"),ok=kind=="sample"?false:Flag("CalibrationOk");
                    object target=null;double u=0,v=0;
                    if(kind=="sample"&&found){u=Float("PixelU");v=Float("PixelV");}
                    if(kind=="target"&&found&&ok)target=new{x=Float("RobotX"),y=Float("RobotY"),z=Float("RobotZ"),rx=Float("RobotRx"),ry=Float("RobotRy"),rz=Float("RobotRz")};
                    string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(solution))).Replace("-","");
                    Send(new{version=2,kind=kind,publisherSession=session,sequence=++sequence,blockId=block,flow=procedure.Name,source=source.Name,solutionHash=hash,calibrationId=calibration,frame="robot-base",units="mm/deg",found=found,calibrationOk=ok,target=target,u=u,v=v});
                    LastError=null;
                }catch(Exception e){LastError=e.Message;Console.Error.WriteLine("AxisLab result: "+LastError);try{Send(new{version=2,error=LastError});}catch{}}
            }
        }
        void Send(object value)
        {
            var bytes=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value)+"\n");
            using(var client=new TcpClient()){var pending=client.ConnectAsync("127.0.0.1",port);if(!pending.Wait(1000))throw new TimeoutException("WPF listener unavailable");pending.GetAwaiter().GetResult();client.SendTimeout=1000;client.GetStream().Write(bytes,0,bytes.Length);}
        }
        public void Dispose(){lock(sync){disposed=true;procedure.ModuleResultCallBackArrived-=Completed;}}
    }
}
