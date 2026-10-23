using System.Xml.Linq;

namespace AxisLab.VirtualCamera;

// Original minimal feature map for AxisLab's experimental camera transport.
static class Genicam
{
    public static byte[] Create()
    {
        XNamespace ns="http://www.genicam.org/GenApi/Version_1_0";
        XElement E(string n,object v)=>new(ns+n,v);
        XElement Node(string type,string name,params object[] values)=>new(ns+type,new XAttribute("Name",name),new XAttribute("NameSpace","Standard"),values);
        XElement Register(string name,uint address,string access="RW")=>Node("IntReg",name,E("Address","0x"+address.ToString("X")),E("Length",4),E("AccessMode",access),E("pPort","Device"),E("Sign","Unsigned"),E("Endianess","BigEndian"));
        XElement Integer(string name,string reg,int min,int max)=>Node("Integer",name,E("pValue",reg),E("Min",min),E("Max",max),E("Inc",1));
        XElement Enum(string name,string reg,params (string name,int value)[] entries)=>Node("Enumeration",name,entries.Select(x=>new XElement(ns+"EnumEntry",new XAttribute("Name",x.name),E("Value",x.value))),E("pValue",reg));
        var root=new XElement(ns+"RegisterDescription",new XAttribute("ModelName","VirtualCamera"),new XAttribute("VendorName","AxisLab"),new XAttribute("StandardNameSpace","None"),new XAttribute("SchemaMajorVersion",1),new XAttribute("SchemaMinorVersion",0),new XAttribute("SchemaSubMinorVersion",1),new XAttribute("MajorVersion",1),new XAttribute("MinorVersion",0),new XAttribute("SubMinorVersion",0),new XAttribute("ProductGuid","81946986-1584-44EB-A834-122C85DC617D"),new XAttribute("VersionGuid","16A05567-EB80-4B90-BE98-1CF58F992157"));
        root.Add(Node("Category","Root",E("pFeature","DeviceControl"),E("pFeature","ImageFormatControl"),E("pFeature","AcquisitionControl"),E("pFeature","TransportLayerControl")));
        root.Add(Node("Category","DeviceControl",new[]{"DeviceVendorName","DeviceModelName","DeviceVersion","DeviceID","DeviceUserID"}.Select(x=>E("pFeature",x))));
        foreach(var s in new[]{("DeviceVendorName",0x48,32),("DeviceModelName",0x68,32),("DeviceVersion",0x88,32),("DeviceID",0xd8,16),("DeviceUserID",0xe8,16)}) root.Add(Node("StringReg",s.Item1,E("Address",s.Item2),E("Length",s.Item3),E("AccessMode","RO"),E("pPort","Device")));
        root.Add(Node("Category","ImageFormatControl",new[]{"SensorWidth","SensorHeight","Width","Height","OffsetX","OffsetY","PixelFormat"}.Select(x=>E("pFeature",x))));
        root.Add(Register("WidthReg",0x10000,"RO"),Register("HeightReg",0x10004,"RO"),Register("PixelReg",0x10008,"RO"));
        root.Add(Integer("Width","WidthReg",1,8192),Integer("Height","HeightReg",1,8192),Integer("SensorWidth","WidthReg",1,8192),Integer("SensorHeight","HeightReg",1,8192));
        root.Add(Node("Integer","OffsetX",E("Value",0)),Node("Integer","OffsetY",E("Value",0)),Enum("PixelFormat","PixelReg",("Mono8",0x01080001)));
        root.Add(Node("Category","AcquisitionControl",new[]{"AcquisitionMode","AcquisitionStart","AcquisitionStop","TriggerSelector","TriggerMode","TriggerSource","TriggerSoftware"}.Select(x=>E("pFeature",x))));
        root.Add(Register("AcquireReg",0x10010),Register("ModeReg",0x10014),Register("TriggerModeReg",0x10018),Register("TriggerReg",0x1001c),Register("TriggerSourceReg",0x10020));
        root.Add(Enum("AcquisitionMode","ModeReg",("Continuous",2)),Enum("TriggerMode","TriggerModeReg",("Off",0),("On",1)),Enum("TriggerSource","TriggerSourceReg",("Software",7),("Line0",0)),Node("Enumeration","TriggerSelector",new XElement(ns+"EnumEntry",new XAttribute("Name","FrameStart"),E("Value",0)),E("Value",0)));
        root.Add(Node("Command","AcquisitionStart",E("pValue","AcquireReg"),E("CommandValue",1)),Node("Command","AcquisitionStop",E("pValue","AcquireReg"),E("CommandValue",0)),Node("Command","TriggerSoftware",E("pValue","TriggerReg"),E("CommandValue",1)));
        root.Add(Node("Category","TransportLayerControl",new[]{"PayloadSize","GevSCPSPacketSize","GevSCPD"}.Select(x=>E("pFeature",x))));
        root.Add(Node("IntSwissKnife","PayloadSize",new XElement(ns+"pVariable",new XAttribute("Name","W"),"Width"),new XElement(ns+"pVariable",new XAttribute("Name","H"),"Height"),E("Formula","W * H")));
        root.Add(Register("PacketSizeReg",0xd04),Register("PacketDelayReg",0xd08),Integer("GevSCPSPacketSize","PacketSizeReg",576,9000),Integer("GevSCPD","PacketDelayReg",0,1000000));
        root.Add(Node("Integer","TLParamsLocked",E("Visibility","Invisible"),E("Value",0),E("Min",0),E("Max",1)),Node("Port","Device"));
        return System.Text.Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0","utf-8",null),root).ToString());
    }
}
