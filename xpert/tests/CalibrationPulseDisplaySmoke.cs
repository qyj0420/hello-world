using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using System.Xml;

// Managed reflection only. No discovery, driver/open/arm/start, worker Main, or
// writes to source captures. Generated images and proof stay in the QA root.
static class CalibrationPulseDisplaySmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Assembly cal,browser; static Type control; static int checks; static string qa;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){var p=o.GetType().GetProperty(n,B);if(p!=null)p.SetValue(o,v,null);else o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static object Static(string t,string n,params object[] a){return cal.GetType(t,true).GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(null,a);}
    static object New(string t){return Activator.CreateInstance(cal.GetType(t,true),true);}
    static void Check(bool yes,string why){checks++;if(!yes)throw new Exception(why);}
    static void Near(double a,double b,string why){Check(Math.Abs(a-b)<1e-12,why);}
    static string Hash(string p){using(var h=SHA256.Create())using(var s=File.OpenRead(p))return BitConverter.ToString(h.ComputeHash(s)).Replace("-","");}
    static double[] Window(double start,double end,double dx,double length,double velocity,double incident,double reflection){return(double[])control.GetMethod("CalculateDisplayWindow",B).Invoke(null,new object[]{start,end,dx,length,velocity,incident,reflection});}
    static void Windows()
    {
        foreach(double incident in new[]{3.136e-6,50e-6})foreach(double travel in new[]{.1e-6,2.064e-6,10e-6}) {
            var w=Window(0,100e-6,8e-9,travel*1e6*160/2,160,incident,incident+travel);
            if(w[3]==0){Near((incident-w[0])/(w[1]-w[0]),.1,"10 percent true pre-arrival");Near(travel/(w[1]-w[0]),3d/7,"calibration span 3/7");Check(w[1]>incident+2*travel,"second return inside real view");}
            Check(w[0]>=0&&w[1]<=100e-6,"no padding or range invention");
        }
        var shortTail=Window(0,6e-6,8e-9,165,160,3.136e-6,5.2e-6);Check(shortTail[3]==3,"missing second return diagnosed");Near(shortTail[1],6e-6,"true short capture end");
        var pre=Window(3e-6,15e-6,8e-9,165,160,3.136e-6,5.2e-6);Check(pre[3]==5,"missing pre-trigger diagnosed");Near(pre[0],3e-6,"true short capture start");
        foreach(double invalid in new[]{0d,-1d,double.NaN,double.PositiveInfinity}){Check(Window(0,15e-6,8e-9,invalid,160,3e-6,5e-6)[3]==1,"invalid geometry not fabricated");}
        Check(Window(0,15e-6,8e-9,165,160,3e-6,14e-6)[3]==2,"inconsistent marker is explicit");
    }
    static int First(double[] v,int first,int limit,int width,double baseline=0,double sigma=.001){return(int)control.GetMethod("FirstResolvedLobe",B).Invoke(null,new object[]{v,first,limit,width,baseline,sigma});}
    static int Connected(double[] v,int anchor,int sign){return(int)control.GetMethod("ConnectedDisplayLobe",B).Invoke(null,new object[]{v,anchor,12,0d,.001,sign});}
    static void Lobes()
    {
        double[] v=new double[90];v[10]=.01;v[11]=.1;v[12]=.3;v[13]=.1;v[14]=.01;v[25]=-1;v[26]=-2;v[27]=-1;
        Check(First(v,8,80,40)==12,"first positive lobe, not larger negative trough");Check(Connected(v,12,1)==12,"connected signed display lobe");
        var negative=v.Select(x=>-x).ToArray();Check(First(negative,8,80,40)==12,"real negative first lobe preserved");Check(Connected(negative,12,-1)==12,"negative display is not abs positive");Check(Connected(negative,12,1)<0,"filtered opposite polarity cannot silently switch packet");
        var spike=(double[])v.Clone();spike[9]=.5;Check(First(spike,8,80,40)<0,"unresolved early spike cannot fall back to later lobe");
        var malformed=(double[])v.Clone();malformed[11]=double.NaN;Check(First(malformed,8,80,40)<0,"NaN rejected");
        Check(Connected(Enumerable.Repeat(.2,90).ToArray(),40,1)<0,"unbounded lobe not invented");Check(First(new double[90],8,80,40)<0,"quiet data not fit");
    }
    static void SignedDisplay()
    {
        foreach(double sign in new[]{1d,-1d})foreach(double reflection in new[]{.4,-.4,2d})
        using(var c=(Control)Activator.CreateInstance(control)) {
            Set(c,"SimulationMode",true);Set(c,"UseFirstArrivalTiming",true);Set(c,"SelectedLevelCode","5nC");
            double[] y=Enumerable.Range(0,1000).Select(n=>sign*(Math.Abs(n-200)<20?Math.Exp(-Math.Pow((n-200)/7d,2)):0)+sign*reflection*(Math.Abs(n-500)<20?Math.Exp(-Math.Pow((n-500)/7d,2)):0)).ToArray();var original=(double[])y.Clone();
            Call(c,"SetPhaseParameters",-1,192d,160d,false,false);Call(c,"SetPhaseWaveform",-1,"5nC",y,0d,8e-9);
            var phase=Call(c,"GetPhaseStateNoLock",-1);var wave=((IDictionary)Get(phase,"Waveforms"))["5nC"];var a=Call(c,"BuildManualResult",wave,phase,200,500,null);
            Call(c,"StoreAnalysisNoLock",phase,"5nC",wave,a);Call(c,"SetPhaseMarkers",-1,"5nC",180*8e-9,480*8e-9,500*8e-9,192d);
            double[] anchors=(double[])Call(c,"GetDisplayDecayAnchors",-1,"5nC");
            if(reflection==.4){Check(anchors.Length==4,"resolved signed pair displays");Check(Math.Sign(anchors[1])==(int)sign&&Math.Sign(anchors[3])==(int)sign,"negative physical pair remains negative");}
            else Check(anchors.Length==0,"opposite polarity or growing pair is not falsely fit");
            Check(y.SequenceEqual(original),"synthetic display never rewrites samples");Check((int)Get(a,"IncidentIndex")==200&&(int)Get(a,"ReflectionIndex")==500,"original analysis indices unchanged");
            var marker=((IDictionary)Get(phase,"Markers"))["5nC"];Near((double)Get(marker,"StartTimeSeconds"),180*8e-9,"display retains manually selected timing");Near((double)Get(marker,"DistanceMarkerSeconds"),480*8e-9,"display retains manual reflection timing");
        }
    }
    static void BrowserTdrDisplay()
    {
        Type host=browser.GetType("HVX.Controls.ProjectDataBrowserControl",true),record=browser.GetType("HVX.Controls.BrowserRecord",true);
        object r=Activator.CreateInstance(record);var metadata=(IDictionary)Get(r,"Metadata");var policy=host.GetMethod("ShouldShowCalibrationDecay",B);
        Check((bool)policy.Invoke(null,new[]{r}),"unknown archive keeps legacy decay display");Set(r,"Name","ALL_TDR");Check((bool)policy.Invoke(null,new[]{r}),"a filename is not TDR identity");
        metadata["记录类型"]="TDR测距；采用波速，不是电量校准";Check(!(bool)policy.Invoke(null,new[]{r}),"parsed TDR hides decay");metadata.Clear();metadata["校准系数 pC/V"]="1000";Check((bool)policy.Invoke(null,new[]{r}),"charge record retains decay");
        using(var c=(Control)Activator.CreateInstance(browser.GetType("CalibrationWaveform.CalibrationWaveformControl",true))) {
            c.Size=new Size(1000,600);Call(c,"SetWaveformXY",Enumerable.Range(0,1001).Select(n=>n*.008).ToArray(),Enumerable.Range(0,1001).Select(n=>Math.Exp(-Math.Pow((n-200)/7d,2))+.4*Math.Exp(-Math.Pow((n-500)/7d,2))).ToArray());Call(c,"SetCableParameters",160d,192d);Call(c,"SetCursorPositions",1.45,3.85);Call(c,"FitPulseView");
            double i=(double)Call(c,"GetIncidentCursorX"),rtime=(double)Call(c,"GetReflectionCursorX");Check((bool)Get(c,"ShowDecayCurve"),"charge display default on");
            using(var before=new Bitmap(1000,600))using(var after=new Bitmap(1000,600)) {
                c.DrawToBitmap(before,c.ClientRectangle);Set(c,"ShowDecayCurve",false);c.DrawToBitmap(after,c.ClientRectangle);int pixels=0;
                for(int y=0;y<600;y++)for(int x=0;x<1000;x++)if(before.GetPixel(x,y)!=after.GetPixel(x,y))pixels++;
                Check(pixels>50,"real renderer suppresses only decay pixels");Check(c.Size==new Size(1000,600),"TDR display flag never changes geometry");
            }
            Near((double)Call(c,"GetIncidentCursorX"),i,"TDR display flag preserves incident time");Near((double)Call(c,"GetReflectionCursorX"),rtime,"TDR display flag preserves reflection time");
        }
    }
    static double[] ReadArchive(string p,out XmlDocument meta)
    {
        using(var r=new BinaryReader(File.OpenRead(p))) {
            string magic=Encoding.ASCII.GetString(r.ReadBytes(8));Check(magic.StartsWith("HVCLIVE"),"real archive magic");int count=r.ReadInt32();byte[] expected=r.ReadBytes(32),payload=r.ReadBytes(count);
            using(var h=SHA256.Create())Check(h.ComputeHash(payload).SequenceEqual(expected),"real archive payload hash");
            using(var inner=new BinaryReader(new MemoryStream(payload))){meta=new XmlDocument{XmlResolver=null};meta.LoadXml(Encoding.UTF8.GetString(inner.ReadBytes(inner.ReadInt32())));int n=inner.ReadInt32();var y=new double[n];for(int i=0;i<n;i++)y[i]=inner.ReadDouble();Check(inner.BaseStream.Position==inner.BaseStream.Length,"complete archive payload");return y;}
        }
    }
    static void Actual()
    {
        string project=@"D:\OWTSData\软件测试-软件\2026-10-07_13-23-28";
        string saved=Path.Combine(project,"ALL_5nC.cal"),folder=Path.Combine(project,"Calibration","Acquisition","20261010_065747_317077");
        Check(File.Exists(saved)&&Directory.Exists(folder),"reported actual inputs exist");
        string[] files=Directory.GetFiles(folder,"capture_*.xxs.capture.log").OrderBy(x=>x).ToArray();Check(files.Length==5,"reported earlier capture has five retained acquisition logs");
        string[] hashes=files.Concat(new[]{saved}).Select(Hash).ToArray();
        Console.WriteLine("EARLIER_065747_RAW_XXS_AVAILABLE="+Directory.GetFiles(folder,"capture_*.xxs").Length+"; earlier screenshot exact raw replay not claimed; retained logs readonly.");
        XmlDocument metadata;double[] savedY=ReadArchive(saved,out metadata);Check(savedY.Length==1953,"60 MHz archive 1953 original samples");
        var savedPair=New("Xiongxin.Calibration.TdrCorrelationResult");Set(savedPair,"Valid",true);Set(savedPair,"IncidentIndex",400);Set(savedPair,"ReflectionIndex",658);Set(savedPair,"Correlation",1d);Set(savedPair,"Message","Read-only recorded original pair");
        Replay(savedY,savedPair,159.79941860465118,"saved-070139");
        Check(files.Concat(new[]{saved}).Select(Hash).SequenceEqual(hashes),"all source waveform/archive bytes unchanged");File.WriteAllLines(Path.Combine(qa,"actual-input-sha256.txt"),files.Concat(new[]{saved}).Select((p,i)=>p+" "+hashes[i]));
    }
    static void Replay(double[] samples,object pair,double velocity,string tag)
    {
        using(var c=(Control)Activator.CreateInstance(control)) {
            Set(c,"EmbeddedMode",true);Set(c,"UseFirstArrivalTiming",true);Set(c,"SelectedLevelCode","5nC");c.Size=new Size(1400,900);
            Call(c,"SetPhaseParameters",-1,164.913,velocity,false,false);Call(c,"SetPhaseWaveform",-1,"5nC",samples,0d,8e-9);
            object phase=Call(c,"GetPhaseStateNoLock",-1),wave=((IDictionary)Get(phase,"Waveforms"))["5nC"],capture=New("Xiongxin.Calibration.CalibrationCaptureResult"),q=New("HVX.OWTS.Task38.CalibrationIncidentAssessment");
            Set(q,"IncidentPeakIndex",400);Set(q,"CoefficientPcPerV",2297.367);Set(capture,"Reflection",pair);Set(capture,"Assessment",q);Set(capture,"Average",samples);
            ((IDictionary)Get(c,"liveCaptures"))["-1:5nC"]=capture;((IDictionary)Get(c,"liveWaveSources"))["-1:5nC"]=wave;
            double i0=double.NaN,r0=double.NaN;string dataHash=String.Join(",",samples.Select(x=>x.ToString("R")));
            foreach(double cutoff in new[]{60d,2.3d,60d}) {
                Call(c,"SetPhaseBandwidth",-1,true,cutoff);var marker=((IDictionary)Get(phase,"Markers"))["5nC"];double i=(double)Get(marker,"StartTimeSeconds"),r=(double)Get(marker,"DistanceMarkerSeconds");
                if(double.IsNaN(i0)){i0=i;r0=r;}else{Near(i,i0,"slider preserves original incident first arrival");Near(r,r0,"slider preserves original reflection first arrival");}
                double[] anchors=(double[])Call(c,"GetDisplayDecayAnchors",-1,"5nC");string status=(string)Get(c,"DisplayDecayStatus");
                var analysis=((IDictionary)Get(phase,"Analyses"))["5nC"];double baseline=(double)Get(analysis,"Baseline"),sigma=(double)Get(analysis,"NoiseSigma");int first=(int)Math.Round(i/8e-9),second=(int)Math.Round(r/8e-9),width=Math.Max(12,Math.Min(96,(second-first)/6));
                int rawI=First(samples,first,second,width,baseline,sigma),rawR=First(samples,second,samples.Length,width,baseline,sigma);var filtered=(double[])Call(c,"GetFilteredWaveform",-1,"5nC");
                Console.WriteLine("DISPLAY_DIAGNOSTIC rawI="+rawI+" rawR="+rawR+" baseline="+baseline+" sigma="+sigma+" width="+width+" first="+first+" second="+second);
                var legacyMatch=Static("Xiongxin.Calibration.TdrCorrelation","MatchTerminal",filtered,400,125e6,164.913,velocity,.15);int legacyI=(int)Get(legacyMatch,"IncidentIndex"),legacyR=(int)Get(legacyMatch,"ReflectionIndex");
                Console.WriteLine("OLD_ABSOLUTE_LP_MATCH valid="+Get(legacyMatch,"Valid")+" I="+legacyI+" R="+legacyR+" values="+filtered[legacyI]+","+filtered[legacyR]);
                Console.WriteLine(tag+" cutoff="+cutoff+" incidentUs="+i*1e6+" reflectionUs="+r*1e6+" distanceM="+Get(marker,"DistanceMeters")+" decay="+status+" anchors="+String.Join(",",anchors.Select(x=>x.ToString("R"))));
                Check(anchors.Length==4,"actual first-lobe display decay reliably paired at "+cutoff);Check(anchors[1]>0&&anchors[3]>0,"actual positive first lobes, not later negative troughs");
                // Symmetric low-pass can shift its display-copy maximum slightly
                // before the raw onset. That is displayed truthfully, not used as travel time.
                Check(Math.Abs(anchors[0]-rawI*8e-9)<=width*8e-9&&Math.Abs(anchors[2]-rawR*8e-9)<=width*8e-9,"display maxima remain on bounded original packet lobes");Check(anchors[3]<anchors[1],"true attenuation, no fabricated growing fit");
                Call(c,"ResetView");Call(c,"SavePng",Path.Combine(qa,tag+"-"+cutoff.ToString("0.0")+"MHz.png"),1400,900);
                Check((string)Get(c,"DisplayWindowStatus")=="Ready","actual second return crop supported");double x0=(double)Get(c,"viewX0"),x1=(double)Get(c,"viewX1");Near((i-x0)/(x1-x0),.1,"rendered true pre-arrival ratio");Near((r-i)/(x1-x0),3d/7,"rendered main interval ratio");
                Check((double)Get(q,"CoefficientPcPerV")==2297.367,"coefficient evidence unchanged");Check(String.Join(",",((double[])Get(wave,"Samples")).Select(x=>x.ToString("R")))==dataHash,"raw sample values unchanged");
            }
            using(var b=(Control)Activator.CreateInstance(browser.GetType("CalibrationWaveform.CalibrationWaveformControl",true))) {
                Call(b,"SetWaveformXY",Enumerable.Range(0,samples.Length).Select(n=>n*.008).ToArray(),samples);Call(b,"SetCableParameters",velocity,164.913);Call(b,"SetCursorPositions",i0*1e6,r0*1e6);Call(b,"FitPulseView");
                Check((string)Get(b,"PulseViewStatus")=="Ready","browser crop shares true window contract");Near(((i0*1e6)-(double)Get(b,"viewXMin"))/((double)Get(b,"viewXMax")-(double)Get(b,"viewXMin")),.1,"browser pre-arrival 10 percent");Near((double)Call(b,"GetIncidentCursorX"),i0*1e6,"browser cursor is not moved");
                Call(b,"SavePng",Path.Combine(qa,tag+"-browser.png"),1400,800);
            }
            Check(((double[])Call(c,"GetDisplayDecayAnchors",-1,"TDR")).Length==0,"TDR never draws decay");
        }
    }
    [STAThread]static int Main(string[] args)
    {try{if(args.Length!=2)throw new ArgumentException("Build root and QA root required.");qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);Application.EnableVisualStyles();cal=Assembly.LoadFrom(Path.Combine(args[0],"modules/CalibrationTest/Xiongxin.OWTS.CalibrationTest.dll"));browser=Assembly.LoadFrom(Path.Combine(args[0],"modules/DataBrowser/Xiongxin.OWTS.DataBrowser.dll"));control=cal.GetType("HVX.Controls.CalibrationProgramControl",true);Windows();Lobes();SignedDisplay();BrowserTdrDisplay();Actual();Console.WriteLine("CALIBRATION_PULSE_DISPLAY_PASS="+checks+"; HARDWARE_COMMANDS=0; ORIGINAL_PROJECT_WRITES=0");return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
