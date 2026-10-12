using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    // 雄新电力 · 雄新 OWTS: optional FactoryPress hardware stack for the 加压测试 page
    // (DeviceSdkLease, LiveEmergencyMonitor, SessionFault, StatusCopy,
    // OwtsServices.RunFactoryAlignedPress, Task6RangeAdvisor). Loaded by reflection
    // so the host keeps compiling without the bridge; the legacy PressureRuntime
    // stays the default path.
    internal static class FactoryPressOwtsHost
    {
        internal const string EnableEnvVar="XIONGXIN_OWTS_FACTORY_ALIGNED";
        internal const string FakeLiveEnvVar="XIONGXIN_OWTS_FACTORY_ALIGNED_FAKE";
        internal const string EnableMarkerFile="factory-press-backend.enabled";
        internal const string ModuleFolder="FactoryPressOwts";
        internal const string BridgeFile="Xiongxin.OWTS.FactoryPressBridge.dll";
        const string DialogType="Xiongxin.OWTS.FactoryPressBridge.XpertFactoryPressDialog";

        internal static bool IsRequested()
        {
            string value=Environment.GetEnvironmentVariable(EnableEnvVar);
            if(String.Equals(value,"0",StringComparison.Ordinal)||String.Equals(value,"false",StringComparison.OrdinalIgnoreCase))return false;
            if(String.Equals(value,"1",StringComparison.Ordinal)||String.Equals(value,"true",StringComparison.OrdinalIgnoreCase))return true;
            return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,EnableMarkerFile));
        }

        internal static bool FakeLiveRequested
        {get{return String.Equals(Environment.GetEnvironmentVariable(FakeLiveEnvVar),"1",StringComparison.Ordinal);}}

        internal static string BridgePath(string modulesRoot)
        {return Path.Combine(modulesRoot??String.Empty,ModuleFolder,BridgeFile);}

        // Requested but missing must fail closed: the operator asked for this
        // stack, so silently pressing through the other one is not acceptable.
        internal static string Run(IWin32Window owner,string modulesRoot,string xpertArtSerial,string picoSerial,
            double pressureRatio,double dividerRatio,double maximumKv,int rangeIndex,string roundsRoot,Action<string,string> diagnostics)
        {
            string path=BridgePath(modulesRoot);
            if(!File.Exists(path))throw new FileNotFoundException("已启用出厂加压硬件栈，但缺少组件："+path+"。请修复安装，或删除 "+EnableMarkerFile+" 后使用默认加压。",path);
            var assembly=Assembly.LoadFrom(path);
            var type=assembly.GetType(DialogType,true);
            var show=type.GetMethod("ShowForXpert",BindingFlags.Public|BindingFlags.Static);
            if(show==null)throw new InvalidDataException("出厂加压组件版本不匹配，请修复安装。");
            try{
                return (string)show.Invoke(null,new object[]{owner,xpertArtSerial,picoSerial,pressureRatio,dividerRatio,
                    maximumKv,rangeIndex,roundsRoot,FakeLiveRequested,diagnostics});
            }catch(TargetInvocationException error){
                if(error.InnerException!=null)throw new InvalidOperationException(error.InnerException.Message,error.InnerException);
                throw;
            }
        }
    }
}
