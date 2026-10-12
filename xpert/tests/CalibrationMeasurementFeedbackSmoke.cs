using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class CalibrationMeasurementFeedbackSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Type type;static Assembly module;static int checks;
    static object Call(object owner,string name,params object[] args){return owner.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(owner,args);}
    static object Get(object owner,string name){return owner.GetType().GetProperty(name,All).GetValue(owner,null);}
    static void Set(object owner,string name,object value){owner.GetType().GetProperty(name,All).SetValue(owner,value,null);}
    static object F(object owner,string name){return owner.GetType().GetField(name,All).GetValue(owner);}
    static void SF(object owner,string name,object value){owner.GetType().GetField(name,All).SetValue(owner,value);}
    static object Static(string name,params object[] args){return type.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(null,args);}
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static double[] Samples(bool reflected){return Enumerable.Range(0,4096).Select(i=>.00001*Math.Sin(i*.73)+.15*Math.Exp(-Math.Pow((i-400)/8d,2))+(reflected?.06*Math.Exp(-Math.Pow((i-600)/8d,2)):0)).ToArray();}
    static object Quality(double[] samples){return module.GetType("HVX.OWTS.Task38.CalibrationIncidentQuality",true).GetMethod("Analyze").Invoke(null,new object[]{new[]{samples,samples,samples},200,336,465,100d,.2,false,"offline",null});}
    static void CheckBandwidth()
    {
        var algorithm=module.GetType("HVX.SignalProcessing.ReflectionBandwidth",true).GetMethod("Select");
        foreach(int kind in new[]{0,1,2}) {
            double ratio=kind==0?.2:.04,width=kind==1?20:2;
            double[] wave=Enumerable.Range(0,2400).Select(i=>Math.Exp(-Math.Pow((i-400)/2d,2))+ratio*Math.Exp(-Math.Pow((i-1400)/width,2))).ToArray();
            double[] original=(double[])wave.Clone();object result=algorithm.Invoke(null,new object[]{wave,125e6,400,1400});
            double cutoff=(double)F(result,"CutoffMHz"),actual=(double)F(result,"Ratio");bool sufficient=(bool)F(result,"Sufficient");
            Check(wave.SequenceEqual(original),"Automatic bandwidth never mutates raw evidence");
            if(kind==0)Check(sufficient&&cutoff==0&&actual>=.1,"Already sufficient reflection leaves bandwidth unchanged");
            if(kind==1)Check(sufficient&&cutoff>=1&&cutoff<=60&&actual>=.1,"Weak broad reflection obtains >=10% through linear low-pass");
            if(kind==2)Check(!sufficient&&cutoff==7&&actual<.1,"Inadequate reflection stops exactly at 7 MHz without claiming success");
        }
    }
    static object Capture(double[] samples,bool capacitor,string[] raw)
    {
        object capture=Activator.CreateInstance(module.GetType("Xiongxin.Calibration.CalibrationCaptureResult",true),true);
        SF(capture,"Average",samples);SF(capture,"Windows",new[]{samples,samples,samples});SF(capture,"SampleRateHz",100e6);
        SF(capture,"Assessment",Quality(samples));SF(capture,"CapacitiveLoad",capacitor);SF(capture,"RangeIndex",4);
        SF(capture,"RawPaths",raw);SF(capture,"DeviceSerial","OFFLINE-FIXTURE");SF(capture,"DeviceModel","synthetic");SF(capture,"AcquisitionMode","Pulse");
        object reflection=Activator.CreateInstance(module.GetType("Xiongxin.Calibration.TdrCorrelationResult",true),true);
        SF(reflection,"Valid",false);SF(reflection,"Message","离线测试：反射相关度未通过。");SF(capture,"Reflection",reflection);return capture;
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            if(args.Length!=2)throw new ArgumentException("Pass build root and isolated QA directory.");
            string output=Path.Combine(Path.GetFullPath(args[1]),"measurement-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
            module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            type=module.GetType("HVX.Controls.CalibrationProgramControl",true);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            CheckBandwidth();
            Check((string)Static("TimeAxisUnit",0d,.059999992)=="ms","60 ms noise uses ms, not huge microsecond labels");
            Check((string)Static("FormatTimeAxisTick",.059999992,"ms")=="60","No floating-point tail on 60 ms label");
            Check((string)Static("TimeAxisUnit",0d,8e-6)=="μs","TDR remains in microseconds");
            Check((string)Static("TimeAxisUnit",0d,800e-9)=="ns","Sub-microsecond waveform uses ns");
            Check((string)Static("TimeAxisUnit",0d,1.2)=="s","Long spans use seconds");
            using(Control control=(Control)Activator.CreateInstance(type)) {
                type.GetField("ShowReflectionSelectionNoticeForTests",All).SetValue(control,(Action<string>)(message=>{}));
                Set(control,"EmbeddedMode",true);control.Size=new Size(1280,720);Set(control,"SelectedLevelCode","100pC");
                // This original hover case explicitly tests peak timing; the
                // current default can be first-arrival timing, tested below.
                Set(control,"UseFirstArrivalTiming",false);
                Call(control,"SetPhaseParameters",-1,180d,180d,false,false);
                double[] wave=Samples(false);Call(control,"SetPhaseWaveform",-1,"100pC",wave,0d,1e-8);
                object capture=Capture(wave,false,new string[0]);Call(control,"ReceiveLiveCapture",-1,"100pC",capture);
                object phase=Call(control,"GetPhaseStateNoLock",-1),analysis=((IDictionary)F(phase,"Analyses"))["100pC"];
                Check((int)Get(analysis,"IncidentIndex")==400,"Validated incident survives missing reflection");
                Check((int)Get(analysis,"ReflectionIndex")==-1,"No invented reflection");
                Check(!((IDictionary)F(phase,"Markers")).Contains("100pC"),"No fabricated paired cable markers");
                Call(control,"SavePng",Path.Combine(output,"incident-awaiting-reflection.png"),1280,720);
                using(var bitmap=new Bitmap(Path.Combine(output,"incident-awaiting-reflection.png"))) {
                    int shadeX=(int)Convert.ToSingle(Call(control,"TimeToReferenceX",5.5e-6));
                    int outsideX=(int)Convert.ToSingle(Call(control,"TimeToReferenceX",6.3e-6));
                    Check(bitmap.GetPixel(shadeX,420).R==bitmap.GetPixel(outsideX,420).R,"No permanent cable-extent shadow obscures the waveform");
                }
                string info=(string)F(control,"measurementInformationText");
                foreach(string text in new[]{"pC/V","入射位置","反射位置","时间差","电缆长度","采用波速","μs⁻¹","dB","反射待确认"})Check(info.Contains(text),"Information contains "+text);
                object raw=((IDictionary)F(phase,"Waveforms"))["100pC"];
                float x=Convert.ToSingle(Call(control,"TimeToReferenceX",5e-6));Call(control,"UpdateHoverMeasurement",new PointF(x,300),phase,raw,null);
                Check((bool)Get(control,"HoverMeasurementAvailable"),"Incident-only cable supports hover distance");
                double peakCursorTime=(double)Call(control,"ReferenceXToTime",x);
                double peakIncidentTime=(double)Get(analysis,"IncidentTimeSeconds");
                Check(Math.Abs(peakIncidentTime-400e-8)<1e-12,"Explicit peak timing uses actual selected incident peak");
                Check(Math.Abs((double)Get(control,"HoverDistanceMeters")-(peakCursorTime-peakIncidentTime)*180e6/2)<1e-6,"Hover uses adopted velocity and selected timing with actual pixel-to-time transform");
                Check(double.IsNaN((double)Get(control,"HoverReflectionDeltaSeconds")),"Unknown reflection delta remains unknown");
                Set(control,"UseFirstArrivalTiming",true);
                analysis=((IDictionary)F(phase,"Analyses"))["100pC"];
                Call(control,"UpdateHoverMeasurement",new PointF(x,300),phase,raw,null);
                double usedIncident=(double)Get(analysis,"IncidentTimeSeconds");
                double cursorTime=(double)Call(control,"ReferenceXToTime",x);
                Check(usedIncident<400e-8,"First-arrival mode uses selected incident packet edge before its peak");
                Check(Math.Abs((double)Get(control,"HoverDistanceMeters")-(cursorTime-usedIncident)*180e6/2)<1e-6,"Hover follows configured first-arrival timing rather than fixed peak");
                Call(control,"ClearHoverMeasurement");
                double[] noise=Enumerable.Range(0,6001).Select(i=>.333*Math.Sin(i*.5)).ToArray();
                Call(control,"SetPhaseNoiseWaveform",-1,noise,0d,1e-5);SF(control,"compactQualityTab",true);
                Call(control,"SavePng",Path.Combine(output,"noise-single-scale-60ms.png"),1280,720);
                Check(Math.Abs((double)F(control,"noiseY1")-.333*1.08)<.001,"Noise scale comes from actual waveform");
                SF(control,"compactQualityTab",false);
                foreach(int h in new[]{600,720,900}) {
                    control.Size=new Size(1280,h);Set(control,"CalibrateAllLevels",true);
                    object ui=Get(control,"CompactLayout");RectangleF list=(RectangleF)Call(control,"CompactLevelListBounds",ui);
                    Check(list.Bottom<((RectangleF)F(ui,"ConfirmJoints")).Top,"All-level list never covers joint or save buttons");
                    Check((int)Call(control,"CompactLevelRows",list)>0,"List has readable rows at "+h);
                    Call(control,"HandleCompactLevelWheel",new MouseEventArgs(MouseButtons.None,0,(int)list.Left+10,(int)list.Top+10,-120));
                    Check((int)F(control,"compactLevelOffset")>0,"All-level scroll reaches later choices");
                    Call(control,"SavePng",Path.Combine(output,"all-levels-"+h+".png"),1280,h);
                }
                Set(control,"SelectedLevelCode","5nC");Check((bool)Get(control,"CalibrateAllLevels"),"Selecting common level does not collapse all-level list");
            }
            string project=Path.Combine(output,"capacitor");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"负载类型 = 电容局放\r\n",Encoding.UTF8);File.WriteAllText(Path.Combine(project,"CABLE.in"),"",Encoding.UTF8);
            string[] rawPaths=Enumerable.Range(0,3).Select(i=>{string p=Path.Combine(project,"raw-"+i+".bin");File.WriteAllBytes(p,new byte[]{1,2,(byte)i});return p;}).ToArray();
            using(Control control=(Control)Activator.CreateInstance(type)) {
                type.GetField("ShowReflectionSelectionNoticeForTests",All).SetValue(control,(Action<string>)(message=>{}));
                Set(control,"EmbeddedMode",true);control.Size=new Size(1280,720);Set(control,"ProjectPath",project);
                Check((bool)Get(control,"IsCapacitiveLoad"),"Explicit capacitor ITEM selects capacitor workflow");
                Check((string)Get(control,"SelectedLevelCode")=="5nC","Capacitor defaults to 5 nC");
                Check(!((string[])Call(control,"GetVisibleLevelCodes")).Contains("TDR"),"Common capacitor levels exclude ranging");
                Set(control,"CalibrateAllLevels",true);Check(!((string[])Call(control,"GetVisibleLevelCodes")).Contains("TDR"),"Complete capacitor list excludes ranging");
                Set(control,"SelectedLevelCode","TDR");Check((string)Get(control,"SelectedLevelCode")=="5nC","Ranging cannot be selected for capacitor");
                Set(control,"SelectedLevelCode","100pC");double[] wave=Samples(false);Call(control,"SetPhaseWaveform",-1,"100pC",wave,0d,1e-8);
                object capture=Capture(wave,true,rawPaths);Check((bool)Get(F(capture,"Assessment"),"SaveRecommended"),"Synthetic incident passes actual incident-quality algorithm");
                Call(control,"ReceiveLiveCapture",-1,"100pC",capture);
                string file=(string)module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true).GetMethod("RecordPath").Invoke(null,new object[]{project,"ALL","100pC"});
                Check(file==Path.Combine(project,"ALL_100pC.cal"),"New capacitor archive uses the actual project-root .cal contract");
                Check(File.Exists(file),"Capacitor incident-only qualified calibration auto-saves");
                object record=module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true).GetMethod("Load").Invoke(null,new object[]{project,file,true});
                Check((string)F(record,"MeasurementKind")=="Capacitor","Explicit archive measurement kind");
                foreach(string field in new[]{"LengthM","VelocityMPerUs","ReflectionSeconds","AttenuationDb","Correlation"})Check((double)F(record,field)==0,"No invented capacitor "+field);
                Check((int)F(record,"ReflectionPeakIndex")==-1,"No capacitor reflection peak");
                Check(Math.Abs((double)F(record,"CoefficientPcPerV")-100/.15)<.1,"Coefficient uses charge divided by incident amplitude");
                Call(control,"SavePng",Path.Combine(output,"capacitor-incident-only.png"),1280,720);
                Check(Math.Abs((double)F(control,"viewX1")-4095e-8)<1e-12,"Capacitor display is not clipped to fake cable geometry");
                string info=(string)F(control,"measurementInformationText");Check(!info.Contains("电缆长度")&&!info.Contains("反射位置"),"Capacitor omits inapplicable geometry and reflection card fields");
                Check((string)Get(control,"CalibrationSaveCaption")=="删除校准","Saved capacitor record exposes withdrawal action");
            }
            using(Control reloaded=(Control)Activator.CreateInstance(type)) {
                Set(reloaded,"ProjectPath",project);Set(reloaded,"SelectedLevelCode","100pC");
                Check((bool)Get(reloaded,"IsCalibrationSavedValid"),"Capacitor record reloads as saved valid");
                object phase=Call(reloaded,"GetPhaseStateNoLock",-1);Check(!((IDictionary)F(phase,"Markers")).Contains("100pC"),"Reload does not invent cable marker pair");
            }
            Console.WriteLine("CALIBRATION_MEASUREMENT_FEEDBACK_PASS="+checks+"; evidence="+output);return 0;
        } catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
