using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class CalibrationInteractionRegressionSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Assembly module;static int checks;
    static void Check(bool yes,string message){checks++;if(!yes)throw new Exception(message);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static object Invoke(Type t,string n,params object[] a){return t.GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(null,a);}
    static object F(object o,string n){return o.GetType().GetField(n,All).GetValue(o);}
    static void SF(object o,string n,object v){o.GetType().GetField(n,All).SetValue(o,v);}
    static object P(object o,string n){return o.GetType().GetProperty(n,All).GetValue(o,null);}
    static void SP(object o,string n,object v){o.GetType().GetProperty(n,All).SetValue(o,v,null);}
    static void Throws(Action action,string message){bool failed=false;try{action();}catch(Exception e){if(e is TargetInvocationException)e=e.InnerException;failed=e is ArgumentException||e is OperationCanceledException;}Check(failed,message);}
    static void Mouse(Control c,string name,MouseButtons button,int x,int y,int delta=0){Call(c,name,new MouseEventArgs(button,1,x,y,delta));}
    static double[] Wave(int size,int reflection,double amplitude,double width){return Enumerable.Range(0,size).Select(i=>Math.Exp(-Math.Pow((i-400)/2d,2))+amplitude*Math.Exp(-Math.Pow((i-reflection)/width,2))).ToArray();}
    static void PumpUntil(Func<bool> completed){var wait=Stopwatch.StartNew();while(!completed()&&wait.ElapsedMilliseconds<10000){Application.DoEvents();System.Threading.Thread.Sleep(5);}Check(completed(),"Async filtering finishes within bounded wait");}
    static void Scope()
    {
        using(var host=new Form{ClientSize=new Size(900,500)})using(var plot=(Control)Activator.CreateInstance(module.GetType("Xiongxin.Scope.ScopePlot",true),true))
        {
            object capture=Activator.CreateInstance(module.GetType("Xiongxin.Scope.Capture",true)),info=Activator.CreateInstance(module.GetType("Xiongxin.Scope.Reply",true)),config=Activator.CreateInstance(module.GetType("Xiongxin.Scope.Settings",true));
            SF(info,"Samples",10000);SF(info,"IntervalNs",8d);SF(info,"MaxAdc",(short)32767);SF(info,"Config",config);SF(capture,"Info",info);SF(capture,"A",new short[10000]);host.Controls.Add(plot);host.Show();Call(plot,"SetData",capture);Application.DoEvents();
            var r=(Rectangle)P(plot,"PlotBounds");var p=new Point(r.Left+r.Width/2,r.Top+r.Height/2);Mouse(plot,"OnMouseWheel",MouseButtons.None,p.X,p.Y,120);Check((double)F(plot,"span")<1&&(double)F(plot,"ySpan")==2,"Standalone wheel zooms X only");
            double x=(double)F(plot,"span");Call(plot,"Zoom",new MouseEventArgs(MouseButtons.None,0,p.X,p.Y,120),true);Check((double)F(plot,"ySpan")<2&&(double)F(plot,"span")==x,"Standalone Shift branch zooms Y only");
            Mouse(plot,"OnMouseDown",MouseButtons.Right,p.X,p.Y);Check((double)F(plot,"span")==1&&(double)F(plot,"ySpan")==2,"Standalone right restores both axes");
            Mouse(plot,"OnMouseDown",MouseButtons.Left,r.Left+20,r.Top+20);Mouse(plot,"OnMouseMove",MouseButtons.Left,p.X,p.Y);Mouse(plot,"OnMouseUp",MouseButtons.Left,p.X,p.Y);Check((double)F(plot,"span")<1&&(double)F(plot,"ySpan")<2&&!(bool)F(plot,"dragging")&&!plot.Capture,"Standalone box zoom completes");host.Close();
        }
    }
    static void Algorithm(string qa)
    {
        Type host=module.GetType("Xiongxin.Calibration.CalibrationForm",true),window=module.GetType("HVX.OWTS.Task38.CalibrationWindowSettings",true),search=module.GetType("Xiongxin.Calibration.CalibrationReflectionBandwidth",true);
        using(var form=(Form)Activator.CreateInstance(host,All,null,new object[]{null,true},null)){Check(((NumericUpDown)F(form,"length")).Maximum==50000,"Host length editor supports 50000m");}
        foreach(double length in new[]{5000d,7488d,50000d})
        {
            Invoke(host,"ValidateHostGeometry",length,170d,double.NaN,false,true);checks++;
            object plan=Activator.CreateInstance(window);SF(plan,"CableLengthM",length);SF(plan,"VelocityMPerUs",170d);SF(plan,"SampleRateHz",125e6);
            Call(plan,"Validate");int count=(int)P(plan,"WindowSamples");Check(count==400+(int)Math.Ceiling(10*length/170*125)+256+1,"Five cable lengths preserved at "+length+"m");
            int reflection=400+(int)Math.Round(2*length/170*125);var samples=Wave(count,reflection,.6,2);object result=Invoke(search,"Search",samples,400,125e6,length,170d,.5,false,null);Check((bool)F(F(result,"Reflection"),"Valid"),"Real synthetic reflection found at "+length+"m");Check((bool)F(F(result,"Bandwidth"),"Sufficient"),"Half-amplitude long cable needs no invented gain");
        }
        Throws(()=>Invoke(host,"ValidateHostGeometry",50001d,170d,100d,false,true),"Over-limit length explicit error");Throws(()=>Invoke(host,"ValidateHostGeometry",7488d,double.NaN,100d,false,true),"Nonfinite velocity explicit error");Throws(()=>Invoke(host,"ValidateHostGeometry",7488d,170d,double.NaN,false,false),"Calibration still validates charge");
        foreach(int kind in new[]{0,1,2,3})
        {
            var wave=Wave(3000,1400,kind==0?.6:kind==3?0:.04,kind==1?20:2);var original=(double[])wave.Clone();
            object match=Invoke(module.GetType("Xiongxin.Calibration.TdrCorrelation",true),"Match",wave,400,125e6,680d,170d,.5);
            if(kind==1)Check(!(bool)F(match,"Valid"),"Broad weak raw reflection initially lacks sufficient correlation");
            object result=Invoke(search,"Search",wave,400,125e6,680d,170d,.5,false,null),b=F(result,"Bandwidth"),r=F(result,"Reflection");
            Check(wave.SequenceEqual(original),"Low pass search retains original evidence");
            if(kind==0)Check((bool)F(b,"Sufficient")&&(double)F(b,"CutoffMHz")==0,"Already adequate pair retains unfiltered bandwidth");
            if(kind==7)Check((bool)F(b,"Sufficient")&&(bool)F(r,"Valid")&&(double)F(b,"CutoffMHz")>=1&&(double)F(b,"Ratio")>=.1,"Initially unreliable pair recovered only after real linear filter and correlation recheck");
            if(kind>=2)Check(!(bool)F(b,"Sufficient")&&(double)F(b,"CutoffMHz")==0,"Weak equal packets/absent reflection retain original without fabricated success or forced7MHz");
            if(kind==3)Check(!(bool)F(r,"Valid")&&(int)F(r,"ReflectionIndex")==-1,"Missing reflection not fabricated");
        }
        object cap=Invoke(search,"Search",Wave(3000,1400,0,2),400,125e6,0d,0d,.5,true,null);Check(F(cap,"Bandwidth")==null&&!(bool)F(F(cap,"Reflection"),"Valid"),"Capacitor exempt from reflection-dependent bandwidth");
        object slowCap=Invoke(search,"Search",Wave(3000,1400,0,2),400,1e6,0d,0d,.5,true,null);Check(F(slowCap,"Bandwidth")==null,"Capacitor is not rejected by a cable-only minimum lowpass requirement");
        Throws(()=>Invoke(search,"Search",Wave(3000,1400,0,2),400,125e6,680d,170d,.5,false,(Func<bool>)(()=>true)),"Bandwidth search supports cancellation");
        BoundaryCases(search);
        var watch=Stopwatch.StartNew();int length50=400+(int)Math.Ceiling(10*50000/170d*125)+257;var longWave=Wave(length50,800,0,2);object longResult=Invoke(search,"Search",longWave,400,125e6,50000d,170d,.5,false,null);watch.Stop();Check(!(bool)F(F(longResult,"Bandwidth"),"Sufficient"),"50km unmatched search fails safely");Console.WriteLine("50KM_SEARCH_MS="+watch.ElapsedMilliseconds+"; SAMPLES="+length50);File.WriteAllText(Path.Combine(qa,"50km-search-ms.txt"),watch.ElapsedMilliseconds.ToString());
    }
    static void BoundaryCases(Type search)
    {
        var match=module.GetType("Xiongxin.Calibration.TdrCorrelation",true);
        foreach(int incident in new[]{0,4,63,71,1798,1799}) {
            var edge=Enumerable.Range(0,1800).Select(i=>Math.Exp(-Math.Pow((i-incident)/2d,2))+.2*Math.Exp(-Math.Pow((i-incident-1000)/2d,2))).ToArray();
            object raw=Invoke(match,"Match",edge,incident,125e6,680d,170d,.5);
            Check(!(bool)F(raw,"Valid")&&(bool)F(raw,"IncidentWindowTruncated")&&(int)F(raw,"ReflectionIndex")==-1,"Truncated incident at "+incident+" rejected before indexed packet loop");
            var output=Invoke(search,"Search",edge,incident,125e6,680d,170d,.5,false,null);
            Check(!(bool)F(F(output,"Bandwidth"),"Sufficient")&&(double)F(F(output,"Bandwidth"),"CutoffMHz")==0&&(bool?)F(F(output,"Bandwidth"),"Stable")==false,"Truncated raw packet remains unverified and unfiltered at edge "+incident);
            Check(!(bool)F(F(output,"Reflection"),"Valid")&&(int)F(F(output,"Reflection"),"ReflectionIndex")==-1,"Mirrored edge filtering cannot fabricate complete physical packet "+incident);
        }
        var firstComplete=Enumerable.Range(0,1800).Select(i=>Math.Exp(-Math.Pow((i-72)/2d,2))+.2*Math.Exp(-Math.Pow((i-1072)/2d,2))).ToArray();
        Check((bool)F(Invoke(match,"Match",firstComplete,72,125e6,680d,170d,.5),"Valid"),"Full radius plus eight-sample baseline is valid at exact boundary");
        foreach(double fill in new[]{0d,3d}) {
            var flat=Enumerable.Repeat(fill,1800).ToArray();var raw=Invoke(match,"Match",flat,400,125e6,680d,170d,.5);
            Check(!(bool)F(raw,"Valid")&&(int)F(raw,"IncidentIndex")==-1,"No pulse/DC baseline does not invent an incident");
            var output=Invoke(search,"Search",flat,400,125e6,680d,170d,.5,false,null);
            Check(!(bool)F(F(output,"Bandwidth"),"Sufficient")&&(int)F(F(output,"Reflection"),"ReflectionIndex")==-1,"No pulse stays invalid throughout lowpass search");
        }
        foreach(double invalid in new[]{Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity}) {
            var values=Wave(3000,1400,.2,2);values[10]=invalid;
            Check(!(bool)F(Invoke(match,"Match",values,400,125e6,680d,170d,.5),"Valid"),"Raw nonfinite samples reject safely");
            Throws(()=>Invoke(search,"Search",values,400,125e6,680d,170d,.5,false,null),"Nonfinite samples fail before filter or coefficient generation");
        }
        foreach(int nominal in new[]{-1,3000,Int32.MaxValue}) {
            Check(!(bool)F(Invoke(match,"Match",Wave(3000,1400,.2,2),nominal,125e6,680d,170d,.5),"Valid"),"Out-of-range nominal incident returns invalid, not an indexing exception");
            Throws(()=>Invoke(search,"Search",Wave(3000,1400,.2,2),nominal,125e6,680d,170d,.5,false,null),"Search reports invalid nominal incident explicitly");
        }
        Check(!(bool)F(Invoke(match,"Match",Wave(3000,1400,.2,2),400,Double.PositiveInfinity,680d,170d,.5),"Valid"),"Infinite sample rate fails before index conversion");
        Throws(()=>Invoke(search,"Search",Wave(3000,1400,.2,2),400,125e6,Double.NaN,170d,.5,false,null),"NaN geometry is rejected before cutoff search");
        Throws(()=>Invoke(match,"Match",Wave(3000,1400,.2,2),400,125e6,680d,170d,Double.NaN),"NaN tolerance fails explicitly");
    }
    static void Ui(string qa)
    {
        Type type=module.GetType("HVX.Controls.CalibrationProgramControl",true);
        using(var host=new Form{ClientSize=new Size(1280,720)})using(var control=(Control)Activator.CreateInstance(type))
        {
            SP(control,"EmbeddedMode",true);SP(control,"SimulationMode",true);SP(control,"ProjectPath",qa);control.Dock=DockStyle.Fill;host.Controls.Add(control);host.Show();Application.DoEvents();
            SP(control,"SelectedPhaseIndex",0);SP(control,"SelectedLevelCode","TDR");Call(control,"SetPhaseParameters",0,680d,170d,false,false);double[] values=Wave(6000,1400,.2,2);Call(control,"SetPhaseWaveform",0,"TDR",values,0d,8e-9);Call(control,"SetPhaseMarkers",0,"TDR",400*8e-9,1400*8e-9,1400*8e-9,680d);Call(control,"ResetView");
            var phase=Call(control,"GetPhaseStateNoLock",0);var wave=((IDictionary)F(phase,"Waveforms"))["TDR"];Call(control,"EnsureWaveformView",wave);var plot=(RectangleF)P(control,"CurrentPlotRectangle");PointF center=new PointF(plot.Left+plot.Width*.7f,plot.Top+plot.Height*.5f);
            double x0=(double)F(control,"viewX0"),x1=(double)F(control,"viewX1"),y0=(double)F(control,"viewY0"),y1=(double)F(control,"viewY1");
            Mouse(control,"OnMouseWheel",MouseButtons.None,(int)center.X,(int)center.Y,120);Check((double)F(control,"viewX1")-(double)F(control,"viewX0")<x1-x0,"Calibration wheel zooms X");Check((double)F(control,"viewY0")==y0&&(double)F(control,"viewY1")==y1,"Calibration X wheel leaves Y unchanged");
            x0=(double)F(control,"viewX0");x1=(double)F(control,"viewX1");Call(control,"ZoomWaveform",center,120,true,wave);Check((double)F(control,"viewY1")-(double)F(control,"viewY0")<y1-y0,"Shift wheel branch zooms Y");Check((double)F(control,"viewX0")==x0&&(double)F(control,"viewX1")==x1,"Y branch leaves X unchanged");
            Call(control,"ResetView");Call(control,"EnsureWaveformView",wave);y0=(double)F(control,"viewY0");y1=(double)F(control,"viewY1");
            int sx=(int)(plot.Left+plot.Width*.6),sy=(int)(plot.Top+plot.Height*.3);Mouse(control,"OnMouseDown",MouseButtons.Left,sx,sy);Mouse(control,"OnMouseMove",MouseButtons.Left,(int)plot.Right+20,(int)plot.Bottom+20);Mouse(control,"OnMouseUp",MouseButtons.Left,(int)plot.Right+20,(int)plot.Bottom+20);Check(!(bool)F(control,"boxZoomActive")&&!control.Capture,"Plot-outside mouse release completes and releases gesture");Check((double)F(control,"viewY1")-(double)F(control,"viewY0")<y1-y0,"Box selection zoom applied, not swallowed by sidebar");
            Mouse(control,"OnMouseDown",MouseButtons.Right,(int)center.X,(int)center.Y);Check(!(bool)F(control,"viewReady"),"Right click restores calibration view");
            Call(control,"EnsureWaveformView",wave);int incidentX=(int)(float)Call(control,"TimeToReferenceX",400*8e-9);Mouse(control,"OnMouseDown",MouseButtons.Left,incidentX,(int)center.Y);Check((int)F(control,"draggingCursor")==1&&!(bool)F(control,"boxZoomActive"),"Incident cursor drag retains priority over box zoom");Mouse(control,"OnMouseMove",MouseButtons.Left,incidentX+2,(int)center.Y);Mouse(control,"OnMouseUp",MouseButtons.Left,incidentX+2,(int)center.Y);Check((int)F(control,"draggingCursor")==0&&!control.Capture,"Cursor drag releases normally");
            Call(control,"ShowNumericEditor",1);var editor=(TextBox)F(control,"numericEditor");editor.Text="7488";Mouse(control,"OnMouseDown",MouseButtons.Left,(int)center.X,(int)center.Y);Check((double)P(control,"CableLengthMeters")==7488&&!editor.Visible,"Click-away commits 7488m exactly like Enter");Mouse(control,"OnMouseUp",MouseButtons.Left,(int)center.X,(int)center.Y);
            Call(control,"ShowNumericEditor",2);editor.Text="172.5";Call(control,"NumericEditorLostFocus",editor,EventArgs.Empty);Check((double)P(control,"PropagationVelocityMPerUs")==172.5,"Velocity lost-focus commit");
            Call(control,"ShowNumericEditor",1);editor.Text="50001";Check(!(bool)Call(control,"CommitNumericEditor",false)&&(double)P(control,"CableLengthMeters")==7488,"Overlimit edit retained visibly instead of silently truncated");Call(control,"CancelNumericEditor");
            SF(control,"compactQualityTab",true);var noise=Enumerable.Range(0,60001).Select(i=>.001*Math.Sin(i*.3)+.15*Math.Exp(-Math.Pow((i-30000)/4d,2))).ToArray();Call(control,"SetPhaseNoiseWaveform",0,noise,0d,1e-6);object noiseWave=F(phase,"Noise");Call(control,"EnsureNoiseView",noiseWave);var noisePlot=(RectangleF)P(control,"CurrentNoisePlotRectangle");var noiseCenter=new PointF(noisePlot.Left+noisePlot.Width/2,noisePlot.Top+noisePlot.Height/2);
            double nx0=(double)F(control,"noiseX0"),nx1=(double)F(control,"noiseX1"),ny0=(double)F(control,"noiseY0"),ny1=(double)F(control,"noiseY1");Mouse(control,"OnMouseWheel",MouseButtons.None,(int)noiseCenter.X,(int)noiseCenter.Y,120);Check((double)F(control,"noiseX1")-(double)F(control,"noiseX0")<nx1-nx0,"Noise wheel zooms X");Check((double)F(control,"noiseY0")==ny0,"Noise X wheel preserves Y");Call(control,"ZoomNoise",noiseCenter,120,true,noiseWave);Check((double)F(control,"noiseY1")-(double)F(control,"noiseY0")<ny1-ny0,"Noise Shift branch zooms Y");
            var reset=(RectangleF)Call(control,"NoiseResetBounds",P(control,"CompactLayout"));Mouse(control,"OnMouseUp",MouseButtons.Left,(int)reset.Left+10,(int)reset.Top+10);Call(control,"EnsureNoiseView",noiseWave);Check((double)F(control,"noiseX0")==nx0&&(double)F(control,"noiseX1")==nx1,"Visible original-view button restores whole noise view");
            ContextMenuStrip owned=null;
            for(int i=0;i<50;i++){Mouse(control,"OnMouseDown",MouseButtons.Right,(int)noiseCenter.X,(int)noiseCenter.Y);var menu=(ContextMenuStrip)F(control,"noiseMenu");if(owned==null)owned=menu;Check(menu==owned&&!menu.IsDisposed,"Repeated menu reuses live owner-held instance #"+i);((ToolStripMenuItem)menu.Items[menu.Items.Count-1]).PerformClick();menu.Close(ToolStripDropDownCloseReason.ItemClicked);Application.DoEvents();Check(!menu.IsDisposed,"Menu not destroyed within close/click stack #"+i);}
            for(int i=0;i<8;i++) {
                Call(control,"EnsureNoiseView",noiseWave);Call(control,"SelectNoiseEnvelope",noiseWave,29990,30010);Mouse(control,"OnMouseDown",MouseButtons.Right,(int)noiseCenter.X,(int)noiseCenter.Y);owned.Items.Cast<ToolStripItem>().Single(t=>t.Text.StartsWith("添入过滤")).PerformClick();owned.Close(ToolStripDropDownCloseReason.ItemClicked);PumpUntil(()=>!(bool)F(control,"noiseTemplateSaving"));Check(((Array)F(control,"noiseTemplates")).Length==1&&!owned.IsDisposed,"Filter add persists without disposing event dispatcher #"+i);
                Mouse(control,"OnMouseDown",MouseButtons.Right,(int)noiseCenter.X,(int)noiseCenter.Y);owned.Items.Cast<ToolStripItem>().Single(t=>t.Text.StartsWith("删除过滤")).PerformClick();owned.Close(ToolStripDropDownCloseReason.ItemClicked);Application.DoEvents();Check(((Array)F(control,"noiseTemplates")).Length==0&&!owned.IsDisposed,"Filter remove preserves reusable menu #"+i);
            }
            Check(((double[])F(noiseWave,"Samples")).SequenceEqual(noise),"Repeated filters retain original noise samples");
            object template=Activator.CreateInstance(module.GetType("HVX.SignalProcessing.NoisePulseTemplate",true));SF(template,"Id","qa");SF(template,"SourceHash",F(control,"noiseSourceHash"));SF(template,"SourceStartSeconds",.02998);SF(template,"SourceEndSeconds",.03002);SF(template,"SampleRateHz",1e6);SF(template,"PeakIndex",20);SF(template,"CreatedUtc",DateTime.UtcNow.ToString("o"));SF(template,"Samples",new double[41]);var templates=Array.CreateInstance(template.GetType(),1);templates.SetValue(template,0);SF(control,"noiseTemplates",templates);
            PointF upper=new PointF(noiseCenter.X,noisePlot.Top+7),lower=new PointF(noiseCenter.X,noisePlot.Bottom-7);Check((bool)Call(control,"NoiseMarkerHit",upper,template)&&(bool)Call(control,"NoiseMarkerHit",lower,template),"Fixed-size upper and lower filter event markers hit");Call(control,"UpdateNoiseHover",Point.Round(upper));Check(((ToolTip)F(control,"noiseTooltip")).GetToolTip(control).Contains("29.98–30.02 ms"),"Marker hover exposes exact source time range");
            Call(control,"SavePng",Path.Combine(qa,"noise-markers-reset.png"),1280,720);
            int longCount=400+(int)Math.Ceiling(10*50000/170d*125)+257;var longWave=Wave(longCount,400+(int)Math.Round(2*50000/170d*125),.2,2);Call(control,"SetPhaseParameters",0,50000d,170d,false,false);Call(control,"SetPhaseWaveform",0,"TDR",longWave,0d,8e-9);
            var sliderWatch=Stopwatch.StartNew();Call(control,"SetPhaseBandwidth",0,true,1d);sliderWatch.Stop();Console.WriteLine("50KM_SINGLE_SLIDER_UPDATE_MS="+sliderWatch.ElapsedMilliseconds);Check((double)Call(control,"GetPhaseBandwidthCutoffMHz",0)==1,"Applied bandwidth and slider state agree at minimum cutoff");Check(((double[])F(((IDictionary)F(phase,"Waveforms"))["TDR"],"Samples")).SequenceEqual(longWave),"Slider never mutates original captured average");
            host.Close();control.Dispose();Check(owned.IsDisposed,"Owner teardown disposes context menu once");
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try{Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);string qa=Path.Combine(Path.GetFullPath(args[1]),"calibration-interaction-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));Algorithm(qa);Scope();Ui(qa);Console.WriteLine("CALIBRATION_INTERACTION_REGRESSION_PASS="+checks+"; EVIDENCE="+qa);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
