using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HVX.Controls;
using HVX.SignalProcessing;
using Xiongxin.Calibration;

internal static class CalibrationReflectionBandwidthSmoke
{
    static int checks;
    static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    static double[] Wave(double ratio,double incidentWidth=8,double reflectionWidth=8,int incident=400,int reflection=1000,int count=2200)
    {return Enumerable.Range(0,count).Select(i=>Math.Exp(-Math.Pow((i-incident)/incidentWidth,2))+ratio*Math.Exp(-Math.Pow((i-reflection)/reflectionWidth,2))).ToArray();}
    static CalibrationBandwidthSearchResult Search(double[] samples,double rate=100e6,Func<bool> cancelled=null)
    {return CalibrationReflectionBandwidth.Search(samples,400,rate,300d*100e6/rate,100d,.15,false,cancelled);}
    static void CheckResult(double[] original,CalibrationBandwidthSearchResult result,bool sufficient,bool? stable,string name)
    {
        Check(result.Bandwidth.Sufficient==sufficient,name+" actual target status");
        Check(result.Bandwidth.Stable==stable,name+" separately evidenced stability");
        Check(result.Bandwidth.TargetRatio==.5,name+" calibration-only half amplitude target");
        Check(result.Bandwidth.Waveform.Length==original.Length,name+" sample count retained");
        Check(!sufficient||result.Reflection.Valid&&result.Bandwidth.Ratio>=.5,name+" target never fabricated");
        var expected=result.Bandwidth.CutoffMHz>0?PdLinearLowPass.Apply(original,1/100e6,result.Bandwidth.CutoffMHz):original;
        Check(expected.SequenceEqual(result.Bandwidth.Waveform),name+" original-source FIR only; no gain/cascade/axis change");
    }
    static void DirectReject(double[] original,double[] display,string expected,string name,int displayIncident=400,int displayReflection=1000)
    {
        var r=CalibrationDisplayBandwidth.Assess(original,display,400,1000,displayIncident,displayReflection,5);
        Check(r.Stable==false&&!r.Sufficient,name+" not accepted");
        Check(r.StabilityReason==expected,name+" diagnostic reason "+r.StabilityReason);
    }
    static void Captions()
    {
        string[] chinese={"已自动调整显示，请核对波形。","请核对波形。","请核对波形，必要时手动调整显示滤波。"};
        string[] english={"Display adjusted automatically. Please review the waveform.","Please review the waveform.","Please review the waveform and adjust the display filter manually if needed."};
        string[] traditional={"已自動調整顯示，請核對波形。","請核對波形。","請核對波形，必要時手動調整顯示濾波。"};
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        using(var control=new CalibrationProgramControl())foreach(string code in Xiongxin.OWTS.Localization.UiLanguage.Codes) {
            control.SetLanguageCode(code);
            for(int i=0;i<english.Length;i++) {
                var actual=(string)typeof(CalibrationProgramControl).GetMethod("CalibrationText",flags).Invoke(control,new object[]{chinese[i],english[i]});
                Check(!String.IsNullOrWhiteSpace(actual),code+" display caption retained");
                string catalog;Check(Xiongxin.OWTS.Localization.UiLanguage.TryTranslate(control.LanguageIndex,english[i],out catalog),code+" exact display caption available");
                Check(actual==catalog,code+" UI uses the generated catalog");
                Check(code=="en"||code=="en-GB"||actual!=english[i],code+" caption translated");
                if(code=="zh-TW")Check(actual==traditional[i],"traditional display caption exact");
            }
        }
        Check(Xiongxin.OWTS.Localization.UiLanguage.Codes.Length==40,"No previously selectable language removed");
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            Directory.CreateDirectory(args[0]);
            Captions();
            Check(new ReflectionBandwidthResult().Stable==null,"Historical stability is unknown, not false/true");
            Check(new ReflectionBandwidthResult().TargetRatio==0,"Historical target remains unevaluated");
            Check(PdLinearLowPass.FilterId=="PD-LP-HAMMING129-v1","Existing FIR contract unchanged");
            foreach(double ratio in new[]{.5,.6,1d}) {
                var wave=Wave(ratio);var before=(double[])wave.Clone();var result=Search(wave);
                CheckResult(wave,result,true,true,"strong "+ratio);Check(result.Bandwidth.CutoffMHz==0,"Already half amplitude needs no display filter");
                Check(wave.SequenceEqual(before),"Original strong waveform immutable");
            }
            foreach(double ratio in new[]{.02,.2,.49}) {
                var wave=Wave(ratio);var before=(double[])wave.Clone();var result=Search(wave);
                CheckResult(wave,result,false,false,"weak "+ratio);Check(result.Reflection.Valid,"Actual weak reflected pair retained");
                Check(result.Bandwidth.CutoffMHz==3&&result.Bandwidth.DisplayFallback,"Sub-target 3 MHz is explicitly manual display fallback");
                Check(result.Reflection.IncidentIndex==400&&result.Reflection.ReflectionIndex==1000,"Fallback retains raw timing packet anchors");
                Check(wave.SequenceEqual(before),"Original weak waveform immutable");
            }
            // A modest real differential bandwidth benefit can meet 0.5 while
            // preserving both packets. Independent assessment is authoritative.
            var near=Wave(.48,8,10);var nearResult=Search(near);
            Check(nearResult.Bandwidth.Stable==true,"Near-target candidate remains stable");
            Check(!nearResult.Bandwidth.Sufficient||nearResult.Bandwidth.Ratio>=.5,"Near-target search reports actual achieved ratio only");
            Console.WriteLine("NEAR_TARGET_CUTOFF_MHZ="+nearResult.Bandwidth.CutoffMHz+";RATIO="+nearResult.Bandwidth.Ratio+";SUFFICIENT="+nearResult.Bandwidth.Sufficient);
            var noisy=Wave(.3);for(int i=0;i<noisy.Length;i++)noisy[i]+=.001*Math.Sin(i*1.7);
            Check(Search(noisy).Bandwidth.DisplayFallback&&!Search(noisy).Bandwidth.Sufficient,"Noisy sub-target display remains manual review, not automatic success");
            var missing=Search(Wave(0));Check(!missing.Reflection.Valid&&missing.Reflection.ReflectionIndex==-1,"Absent reflection not fabricated");
            Check(missing.Bandwidth.Stable==false&&!missing.Bandwidth.Sufficient&&missing.Bandwidth.CutoffMHz==3&&missing.Bandwidth.DisplayFallback,"Missing pair may show manual fallback but never becomes verified");
            var clipped=Wave(.7);for(int i=397;i<=403;i++)clipped[i]=clipped[397];
            var clippedResult=Search(clipped);Check(clippedResult.Bandwidth.Stable==false&&!clippedResult.Bandwidth.Sufficient,"Flat-topped raw packet cannot become verified stable by filtering");
            var spike=new double[2200];spike[400]=1;spike[1000]=.7;Check(Search(spike).Bandwidth.Stable==false,"Unresolved one-sample spikes not stable packets");
            var source=Wave(.3);var moved=new double[source.Length];Array.Copy(source,0,moved,8,source.Length-8);
            DirectReject(source,moved,"PeakMovedOrPacketChanged","Eight-sample packet timing movement",408,1008);
            var reversed=source.Select(v=>-v).ToArray();DirectReject(source,reversed,"PolarityChanged","Polarity inversion");
            var distorted=PdLinearLowPass.Apply(source,1/100e6,1);
            var distortion=CalibrationDisplayBandwidth.Assess(source,distorted,400,1000,400,1000,1);
            Check(distortion.Stable==false&&!distortion.Sufficient,"Heavy broadening rejected rather than achieving target cosmetically");
            Check(distortion.StabilityReason=="PacketWidthChanged"||distortion.StabilityReason=="LeadingEdgeMoved"||distortion.StabilityReason=="PacketShapeChanged","Distortion reason explicit");
            var mismatch=Wave(.7,8,80);Check(Search(mismatch).Bandwidth.Stable==false,"Uncorrelated raw packet shapes not manufactured into stable pair");
            foreach(int invalidIndex in new[]{-1,Int32.MaxValue}) {
                var invalidPacket=CalibrationDisplayBandwidth.Assess(source,source,400,1000,400,invalidIndex,0);
                Check(invalidPacket.Stable==false&&!invalidPacket.Sufficient,"Invalid display anchor rejected before indexed access");
            }
            foreach(int edge in new[]{0,4,49}) {
                var wave=Wave(.7,8,8,edge,edge+600);var result=CalibrationReflectionBandwidth.Search(wave,edge,100e6,300,100,.15,false,null);
                Check(!result.Reflection.Valid&&result.Bandwidth.Stable==false&&result.Bandwidth.CutoffMHz==0,"Truncated raw packet remains unverified "+edge);
            }
            foreach(double rate in new[]{2e6,6e6,10e6,14e6,20e6,125e6}) {
                var result=Search(Wave(.2),rate);Check(result.Bandwidth.CutoffMHz==0||result.Bandwidth.CutoffMHz<=rate*.5/1e6,"Nyquist bounded "+rate);
                Check(!result.Bandwidth.Sufficient,"Low-rate weak same-shape packet truthfully remains weak "+rate);
                Check(rate>6e6?result.Bandwidth.DisplayFallback&&result.Bandwidth.CutoffMHz==3:!result.Bandwidth.DisplayFallback&&result.Bandwidth.CutoffMHz==0,"Unsupported 3 MHz never falsely applied "+rate);
            }
            bool cancelled=false;try{Search(Wave(.7),100e6,()=>true);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"Cancellation honored even before early target success");
            int trials=0;Search(Wave(.2),100e6,()=>{trials++;return false;});Check(trials<=12,"Calibration display search bounded to ten original-source candidates plus fallback cancellation");
            var invalid=Wave(.7);invalid[100]=Double.NaN;bool rejected=false;try{Search(invalid);}catch(ArgumentException){rejected=true;}Check(rejected,"Nonfinite signal rejected before search");
            var cap=CalibrationReflectionBandwidth.Search(Wave(0),400,1e6,0,0,.15,true,null);Check(cap.Bandwidth==null,"Capacitor bypass remains unchanged");
            var oldWeak=ReflectionBandwidth.Select(Wave(.03,4,4),125e6,400,1000);
            Check(!oldWeak.Sufficient&&oldWeak.CutoffMHz==7&&oldWeak.Stable==null,"Existing non-calibration 10%/7MHz strategy preserved");
            var oldGood=ReflectionBandwidth.Select(Wave(.2),125e6,400,1000);
            Check(oldGood.Sufficient&&oldGood.CutoffMHz==0&&oldGood.TargetRatio==0,"Existing analysis 10% success unchanged");
            string form=File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),"modules-src","CalibrationStandalone","CalibrationForm.cs"));
            Check(System.Text.RegularExpressions.Regex.IsMatch(form,@"var\s+qualityWindows\s*=\s*extracted\.Windows\s*;"),"Charge quality still receives HP-only windows, never display-lowpass windows");
            Check(form.Contains("CalibrationDisplayBandwidth.EngineeringPolicyId")&&form.Contains("PhysicalAcceptance=false"),"Detailed display engineering evidence retained only in review diagnostics");
            using(var control=new CalibrationProgramControl()) {
                var type=typeof(CalibrationProgramControl);var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var phase=type.GetMethod("GetPhaseStateNoLock",flags).Invoke(control,new object[]{-1});
                var field=phase.GetType().GetField("ManualBandwidthPanelVisible",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
                field.SetValue(phase,false);
                var method=type.GetMethod("ApplyAutomaticBandwidthOutcome",flags);
                var stable=Search(Wave(.2));var capture=new CalibrationCaptureResult{Bandwidth=stable.Bandwidth,Reflection=stable.Reflection};
                var text=(string)method.Invoke(control,new object[]{phase,capture});
                Check((bool)field.GetValue(phase),"Sub-target fallback opens manual panel");
                Check(text.Length>0&&text.Length<80&&!text.Contains("MHz")&&!text.Contains("10%"),"Ordinary feedback stays short and excludes internal policy");
                capture.Bandwidth=missing.Bandwidth;capture.Reflection=missing.Reflection;method.Invoke(control,new object[]{phase,capture});
                Check((bool)field.GetValue(phase),"Unverified actual pair still offers manual review");
                capture.Bandwidth=nearResult.Bandwidth;capture.Reflection=nearResult.Reflection;method.Invoke(control,new object[]{phase,capture});
                Check(!(bool)field.GetValue(phase),"A subsequent sufficient capture hides the prior manual panel");
            }
            Console.WriteLine("PASS CalibrationReflectionBandwidthSmoke checks="+checks+";SDK=0;HARDWARE=0;PRODUCT=0;PHYSICAL_ACCEPTANCE=0");return 0;
        } catch(Exception e){Console.Error.WriteLine("FAIL check="+checks+" "+e);return 1;}
    }
}
