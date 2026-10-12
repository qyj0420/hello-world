using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class PressureFailureCleanupHostSmoke
{
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    [STAThread] static int Main(string[] args)
    {
        try {
            var worker=Assembly.LoadFrom(Path.Combine(args[0],"modules","PressureRuntime","Xiongxin.Pressure.Runtime.exe"));
            var type=worker.GetType("Xiongxin.FactoryTest.FactoryFailedRoundReleasedException",true);
            var original=new InvalidOperationException("original failed round");
            var proof=(Exception)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{original},null);
            Check(ModuleHost.IsVerifiedFailedRoundRelease(proof,worker),"exact worker-owned proof recognized");
            Check(!ModuleHost.IsVerifiedFailedRoundRelease(proof,typeof(ModuleHost).Assembly),"same exception wrong worker assembly rejected");
            Check(!ModuleHost.IsVerifiedFailedRoundRelease(null,worker)&&!ModuleHost.IsVerifiedFailedRoundRelease(proof,null),"missing evidence rejected");
            Check(!ModuleHost.IsVerifiedFailedRoundRelease(original,worker),"original failure not cleanup proof");
            Check(!ModuleHost.IsVerifiedFailedRoundRelease(new InvalidOperationException("VerifiedOwnedFailedRoundNativeCleanup"),worker),"text cannot release capture block");
            Check(Object.ReferenceEquals(proof.InnerException,original)&&proof.Message==original.Message,"actual failure preserved");
            Check(!ModuleHost.IsVerifiedInputStop(proof,worker)&&!ModuleHost.IsVerifiedPreNativeCancellation(proof,worker),"failed cleanup is not zero input stop or pre-native cancellation");
            Check(ModuleHost.PressureCaptureFailureStatus(proof,worker)=="SessionEndedAfterFailedRelease","failed verified cleanup explains session ending, not a physical disconnect");
            var stopped=(Exception)Activator.CreateInstance(worker.GetType("Xiongxin.FactoryTest.FactoryHealthyRoundStoppedException",true),true);
            Check(stopped is OperationCanceledException&&ModuleHost.IsVerifiedHealthyRoundStop(stopped,worker),"exact bound healthy-stop outcome preserves cancellation semantics");
            Check(!ModuleHost.IsVerifiedHealthyRoundStop(stopped,typeof(ModuleHost).Assembly)&&!ModuleHost.IsVerifiedHealthyRoundStop(proof,worker)&&!ModuleHost.IsVerifiedHealthyRoundStop(new OperationCanceledException("VerifiedOwnedHealthyRoundStop"),worker),"wrong assembly, failed cleanup and arbitrary cancellation text cannot retain session");
            var incomplete=(Exception)Activator.CreateInstance(worker.GetType("Xiongxin.FactoryTest.FactoryResidentRoundIncompleteException",true),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{"original cancel or failure",(int?)1},null);
            Check(ModuleHost.PressureCaptureFailureStatus(incomplete,worker)=="StopReleaseUnconfirmed"&&!ModuleHost.IsVerifiedHealthyRoundStop(incomplete,worker),"alive but unconfirmed resources remain blocked");
            Check(ModuleHost.PressureCaptureFailureStatus(new InvalidOperationException("FactoryFailedRoundReleasedException"),worker)=="PresenceUnknown","text never manufactures a typed session outcome");
            string ended=MainForm.LiveConnectionLossNotice("SessionEndedAfterFailedRelease",true,true),pending=MainForm.LiveConnectionLossNotice("StopReleaseUnconfirmed",true,true);
            Check(ended.Contains("原控制会话已主动结束")&&ended.Contains("需要重新连接")&&ended.Contains("不代表试品已放电")&&!ended.Contains("连接状态未通过"),"released failure notice preserves failed capture and physical-discharge boundary");
            Check(pending.Contains("工作进程仍在运行")&&pending.Contains("尚未核验")&&!pending.Contains("断线")&&!pending.Contains("清理成功"),"unconfirmed Stop is not falsely described as hardware disconnect or release");
            Check(MainForm.LiveConnectionLossNotice("SessionEndedAfterFailedRelease",true,false).Contains("not physical discharge")&&MainForm.LiveConnectionLossNotice("StopReleaseUnconfirmed",true,false).Contains("not yet verified"),"typed notices retain safety semantics in English");
            var bridge=worker.GetType("Xiongxin.FactoryTest.UnifiedPressureBridge",true);
            Check(bridge.GetMethod("CreateConfigurationWithEndResidual").GetParameters().Length==7,"host/runtime end-residual contract exact");
            Check(bridge.GetMethod("CreateConfigurationWithFeedbackSource").GetParameters().Length==8,"host/runtime selected-source contract exact");
            Check(ModuleHost.PressureVoltageFeedbackCode(new AppSettings{HvupVoltageSource=HvupVoltageSource.Divider})=="DIV","actual configured divider reaches bridge code");
            Check(ModuleHost.PressureVoltageFeedbackCode(new AppSettings{HvupVoltageSource=HvupVoltageSource.VMonitor})=="VMON","actual configured source monitor reaches bridge code");
            bool rejected=false;try{ModuleHost.PressureVoltageFeedbackCode(new AppSettings{HvupVoltageSource=(HvupVoltageSource)999});}catch(InvalidDataException){rejected=true;}
            Check(rejected,"invalid source cannot silently default to divider");
            var guard=typeof(FactoryPressureSession).GetField("monitorRestartSuppressed",BindingFlags.Instance|BindingFlags.NonPublic);
            Check(guard!=null&&guard.FieldType==typeof(bool),"failure monitor restart guard persists beyond finally");
            // Allocate without constructor, with no device/bridge. Verify the
            // suppression is checked before any control or worker interaction.
            var session=(FactoryPressureSession)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(FactoryPressureSession));
            guard.SetValue(session,true);
            // StartMonitor reads control first; supply a harmless UI-only control.
            using(var page=new Control()){
                typeof(FactoryPressureSession).GetField("control",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(session,page);
                session.StartMonitor();Check(true,"suppressed StartMonitor performs no bridge/native work");
            }
            Console.WriteLine("PRESSURE_FAILURE_CLEANUP_HOST_PASS="+checks+"; HARDWARE_COMMANDS=0; WORKER_ENTRY_CALLED=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
