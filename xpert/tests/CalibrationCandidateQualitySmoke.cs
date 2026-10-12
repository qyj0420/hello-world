using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class CalibrationCandidateQualitySmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Assembly module; static Type type; static int checks;
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static object Static(string name,params object[] args){return type.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(null,args);}
    static object Get(object o,string name){return o.GetType().GetProperty(name,All).GetValue(o,null);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name,All).SetValue(o,value,null);}
    static object F(object o,string name){return o.GetType().GetField(name,All).GetValue(o);}
    static void SF(object o,string name,object value){o.GetType().GetField(name,All).SetValue(o,value);}
    static void Check(bool ok,string reason){checks++;if(!ok)throw new Exception(reason);}
    static double[] Samples(int reflected,double ratio=.3){return Enumerable.Range(0,2048).Select(i=>.00001*Math.Sin(i*.73)+.15*Math.Exp(-Math.Pow((i-400)/8d,2))+(reflected>0?.15*ratio*Math.Exp(-Math.Pow((i-reflected)/8d,2)):0)).ToArray();}
    static object Capture(double[] samples,bool capacitor=false)
    {
        object result=Activator.CreateInstance(module.GetType("Xiongxin.Calibration.CalibrationCaptureResult",true),true);
        var windows=Enumerable.Range(0,8).Select(i=>(double[])samples.Clone()).ToArray();
        object q=module.GetType("HVX.OWTS.Task38.CalibrationIncidentQuality",true).GetMethod("Analyze").Invoke(null,
            new object[]{windows,200,336,465,100d,.2,false,"offline",null});
        SF(result,"Average",samples);SF(result,"Windows",windows);SF(result,"SampleRateHz",100e6);
        SF(result,"Assessment",q);SF(result,"CapacitiveLoad",capacitor);SF(result,"RangeIndex",4);SF(result,"RawPaths",new string[0]);
        object r=Activator.CreateInstance(module.GetType("Xiongxin.Calibration.TdrCorrelationResult",true),true);
        SF(r,"Valid",false);SF(r,"IncidentIndex",400);SF(r,"ReflectionIndex",-1);SF(r,"Message","离线回放：未通过可靠反射判据。");SF(result,"Reflection",r);
        return result;
    }
    static object Phase(Control c){return Call(c,"GetPhaseStateNoLock",-1);}
    static object Analysis(Control c){return ((IDictionary)F(Phase(c),"Analyses"))["100pC"];}
    static IDictionary Candidates(Control c){return (IDictionary)F(Phase(c),"ReflectionCandidates");}
    static void Wave(Control c,double[] samples){Call(c,"SetPhaseWaveform",-1,"100pC",samples,0d,1e-8);}
    static object BandSearch(double[] wave,bool capacitor=false)
    {
        return module.GetType("Xiongxin.Calibration.CalibrationReflectionBandwidth",true).GetMethod("Search",All).Invoke(null,
            new object[]{wave,400,100e6,180d,180d,.15,capacitor,null});
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            if(args.Length!=2)throw new ArgumentException("Pass build root and isolated QA directory.");
            string output=Path.Combine(Path.GetFullPath(args[1]),"candidate-quality-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
            module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            type=module.GetType("HVX.Controls.CalibrationProgramControl",true);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(Control c=(Control)Activator.CreateInstance(type)) {
                type.GetField("ShowReflectionSelectionNoticeForTests",All).SetValue(c,(Action<string>)(message=>{}));
                c.Size=new Size(1280,720);Set(c,"EmbeddedMode",true);Set(c,"SelectedLevelCode","100pC");
                Call(c,"SetPhaseParameters",-1,180d,180d,false,false);
                Check(!(bool)Get(c,"ManualBandwidthPanelVisible"),"Manual bandwidth starts collapsed");
                double[] strong=Samples(600),copy=(double[])strong.Clone();Wave(c,strong);
                Check((string)Call(c,"GetAnalysisStatusCode",-1,"100pC")=="Ok","Strong real pair remains valid");
                Check(Candidates(c).Count==0,"Reliable pair has no candidate annotation");
                Check((double)F(Analysis(c),"DisplayReflectionScore")>=80,"Strong pair has high evidence score");
                Call(c,"SetPhaseBandwidth",-1,true,5d);
                string signature=(string)Call(c,"LevelSignature",-1,"100pC");long sequence=(long)Get(c,"BandwidthChangedSequence");
                object filtered=((IDictionary)F(Phase(c),"FilteredWaveforms"))["100pC"];
                foreach(bool expanded in new[]{true,false,true,false}) {
                    Set(c,"ManualBandwidthPanelVisible",expanded);
                    Check((bool)Get(c,"BandwidthEnhancement"),"Panel never disables filter");
                    Check((double)Get(c,"BandwidthCutoffMHz")==5,"Panel never changes cutoff");
                    Check((string)Call(c,"LevelSignature",-1,"100pC")==signature,"Panel leaves archive signature unchanged");
                    Check(Object.ReferenceEquals(filtered,((IDictionary)F(Phase(c),"FilteredWaveforms"))["100pC"]),"Panel does not rebuild waveform");
                }
                Check(sequence==(long)Get(c,"BandwidthChangedSequence"),"Panel does not raise filter change");
                Call(c,"HandleCompactMouseUp",new MouseEventArgs(MouseButtons.Left,1,181,230,0));
                Check((bool)Get(c,"ManualBandwidthPanelVisible")&&(bool)Get(c,"BandwidthEnhancement"),"Real compact switch expands panel without changing enabled filter");
                Call(c,"HandleCompactMouseUp",new MouseEventArgs(MouseButtons.Left,1,181,230,0));
                Check(!(bool)Get(c,"ManualBandwidthPanelVisible")&&(bool)Get(c,"BandwidthEnhancement"),"Real compact switch collapses panel without removing filter");
                Check(strong.SequenceEqual(copy),"Raw strong waveform unchanged");
                Call(c,"SetPhaseBandwidth",-1,false,60d);
                double[] candidate=Samples(660);Wave(c,candidate);object capture=Capture(candidate);Call(c,"ReceiveLiveCapture",-1,"100pC",capture);
                Check(Candidates(c).Contains("100pC"),"Actual local peak inside cable propagation interval shown as candidate");
                object evidence=Candidates(c)["100pC"];
                Check((int)F(evidence,"Index")==660,"Candidate anchors real sampled peak, not predicted position600");
                Check((double)F(evidence,"Score")>=0&&(double)F(evidence,"Score")<80,"Candidate confidence never masquerades as confirmed");
                Check((int)Get(Analysis(c),"ReflectionIndex")==-1,"Candidate not copied into official reflection");
                Check(!((IDictionary)F(Phase(c),"Markers")).Contains("100pC"),"Candidate not copied to marker pair");
                Check(!(bool)Call(c,"SaveLiveCalibration",-1,"100pC"),"Candidate cannot save formal coefficient");
                Check((long)Get(c,"CalibrationSavedSequence")==0,"Candidate cannot auto-save");
                Call(c,"SetPhaseBandwidth",-1,true,40d);
                Check((string)Call(c,"GetAnalysisStatusCode",-1,"100pC")!="Ok","Manual filter preserves rejected live correlation gate");
                Check(!(bool)Call(c,"SaveLiveCalibration",-1,"100pC"),"Filtered rejected candidate cannot save");
                Call(c,"SavePng",Path.Combine(output,"candidate.png"),1280,720);
                string text=(string)F(c,"measurementInformationText");
                foreach(string token in new[]{"校准系数","时间差","采用波速","衰减系数","校准质量分","反射可信分","不代表测量准确率或合格结论","反射候选","不用于保存"})Check(text.Contains(token),"Evidence details include "+token);
                double score=(double)Static("IncidentQualityEngineeringScore",capture);Check(score>90&&score<=100,"Complete repeat/SNR evidence gives bounded quality score");
                SF(capture,"Windows",null);Check(Double.IsNaN((double)Static("IncidentQualityEngineeringScore",capture)),"Legacy absent repeat evidence stays unassessed");
                object insufficient=BandSearch(Samples(600,.02));
                Check(!(bool)F(F(insufficient,"Bandwidth"),"Sufficient")&&(bool)F(F(insufficient,"Bandwidth"),"DisplayFallback")&&(double)F(F(insufficient,"Bandwidth"),"CutoffMHz")==3,"Weak actual pair uses manual 3MHz fallback without claiming half-amplitude success");
                SF(capture,"Bandwidth",F(insufficient,"Bandwidth"));SF(capture,"Reflection",F(insufficient,"Reflection"));
                Set(c,"ManualBandwidthPanelVisible",false);
                Check(((string)Call(c,"ApplyAutomaticBandwidthOutcome",Phase(c),capture)).Length<80,"Stable sub-target display has concise review feedback");
                Check((bool)Get(c,"ManualBandwidthPanelVisible"),"Sub-target fallback opens manual controls");
                object absent=BandSearch(Samples(-1));
                Check(!(bool)F(F(absent,"Reflection"),"Valid")&&(double)F(F(absent,"Bandwidth"),"CutoffMHz")==3&&(bool?)F(F(absent,"Bandwidth"),"Stable")==false,"Manual 3MHz fallback does not invent a reliable pair");
                object sufficient=BandSearch(Samples(600,.6));
                Check((bool)F(F(sufficient,"Bandwidth"),"Sufficient")&&(double)F(F(sufficient,"Bandwidth"),"CutoffMHz")==0,"Already suitable pair needs no extra lowpass");
                object capacitorSearch=BandSearch(strong,true);Check(F(capacitorSearch,"Bandwidth")==null,"Capacitor does not demand reflection bandwidth search");
                // Exercise the completed-capture route, not just the panel helper.
                // The requested 3MHz fallback may visibly broaden these pulses,
                // but first-arrival timing and HP-only charge evidence stay raw.
                Set(c,"UseFirstArrivalTiming",true);Call(c,"SetPhaseBandwidth",-1,false,60d);
                var weakLive=Samples(600,.2);var weakCopy=(double[])weakLive.Clone();Wave(c,weakLive);
                int rawI=(int)Call(c,"GetIncidentFirstArrivalIndex",-1,"100pC"),rawR=(int)Call(c,"GetReflectionFirstArrivalIndex",-1,"100pC");
                var weakSearch=BandSearch(weakLive);var weakCapture=Capture(weakLive);
                SF(weakCapture,"Bandwidth",F(weakSearch,"Bandwidth"));SF(weakCapture,"Reflection",F(weakSearch,"Reflection"));
                SF(weakCapture,"QualityOverrideForReview",true);
                double rawCoefficient=(double)Get(F(weakCapture,"Assessment"),"CoefficientPcPerV");
                Call(c,"ReceiveLiveCapture",-1,"100pC",weakCapture);
                Check((bool)Get(c,"BandwidthEnhancement")&&(double)Get(c,"BandwidthCutoffMHz")==3&&(bool)Get(c,"ManualBandwidthPanelVisible"),"Completed weak capture applies 3MHz and opens synchronized manual panel");
                Check(rawI>=0&&rawR>rawI&&(int)Call(c,"GetIncidentFirstArrivalIndex",-1,"100pC")==rawI&&(int)Call(c,"GetReflectionFirstArrivalIndex",-1,"100pC")==rawR,"Display fallback preserves raw first-arrival times");
                Check(weakLive.SequenceEqual(weakCopy)&&(double)Get(F(weakCapture,"Assessment"),"CoefficientPcPerV")==rawCoefficient,"Display fallback never alters raw samples or formal HP-only coefficient");
                var strongLive=Samples(600,.6);Wave(c,strongLive);var strongSearch=BandSearch(strongLive);var strongCapture=Capture(strongLive);
                SF(strongCapture,"Bandwidth",F(strongSearch,"Bandwidth"));SF(strongCapture,"Reflection",F(strongSearch,"Reflection"));SF(strongCapture,"QualityOverrideForReview",true);
                Call(c,"ReceiveLiveCapture",-1,"100pC",strongCapture);
                Check(!(bool)Get(c,"ManualBandwidthPanelVisible")&&!(bool)Get(c,"BandwidthEnhancement"),"Next sufficient capture resets filter and hides old manual fallback panel");
                Set(c,"UseFirstArrivalTiming",false);
                foreach(int width in new[]{1024,1280,1920})foreach(float font in new[]{9.5f,18f}) {
                    c.Font=new Font("Microsoft YaHei UI",font);c.Size=new Size(width,720);
                    Call(c,"SavePng",Path.Combine(output,"card-"+width+"-"+font.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"),width,720);
                    RectangleF card=(RectangleF)F(c,"measurementInformationHit");Check(card.Left>=0&&card.Right<=width&&card.Bottom<=720,"Evidence card within page");
                    using(Form detail=(Form)Call(c,"CreateMeasurementInformationDialog")) {
                        detail.Show();Application.DoEvents();var box=(TextBox)detail.Controls.Find("CalibrationMeasurementEvidence",true)[0];
                        Check(box.WordWrap&&box.ScrollBars==ScrollBars.Vertical&&box.ReadOnly,"Large font evidence remains wrapped, scrollable and read-only");
                        Check(detail.ClientRectangle.Contains(box.Bounds),"Details never crop outside dialog");
                        if(width==1280)using(var bitmap=new Bitmap(detail.Width,detail.Height)){detail.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"details-"+font.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"));}
                        detail.Close();
                    }
                }
                Call(c,"SetPhaseBandwidth",-1,false,60d);Wave(c,new double[2048]);
                Check(Candidates(c).Count==0,"Flat trace has no fabricated candidate");
                double[] incidentOnly=Samples(-1);Wave(c,incidentOnly);Call(c,"ReceiveLiveCapture",-1,"100pC",Capture(incidentOnly));Check(Candidates(c).Count==0,"Noise alone is not a reflection candidate");
                double[] outOfCable=Samples(1500);Wave(c,outOfCable);Call(c,"ReceiveLiveCapture",-1,"100pC",Capture(outOfCable));Check(Candidates(c).Count==0,"Unrelated peak outside propagation range not proposed");
                double[] spike=Samples(-1);spike[660]=.05;Wave(c,spike);Call(c,"ReceiveLiveCapture",-1,"100pC",Capture(spike));Check(Candidates(c).Count==0,"Single sample spike rejected");
                SF(c,"capacitiveLoad",true);Wave(c,strong);
                Check(Candidates(c).Count==0&&!((IDictionary)F(Phase(c),"Markers")).Contains("100pC"),"Capacitor produces no candidate or cable pair");
                Call(c,"SavePng",Path.Combine(output,"capacitor.png"),1280,720);
                text=(string)F(c,"measurementInformationText");Check(text.Contains("不适用（电容）")&&!text.Contains("衰减系数"),"Capacitor score says not applicable and omits geometry");
                Call(c,"ClearCalibrationMemory");Check(!(bool)Get(c,"ManualBandwidthPanelVisible")&&Candidates(c).Count==0,"Scope reset removes candidate and panel state");
            }
            Console.WriteLine("CALIBRATION_CANDIDATE_QUALITY_PASS="+checks+"; evidence="+output);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
