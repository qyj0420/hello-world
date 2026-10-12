using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class CalibrationPeakFeedbackSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static object F(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void P(object o,string n,object v){o.GetType().GetProperty(n,B).SetValue(o,v,null);}
    static void Check(bool v,string why){checks++;if(!v)throw new Exception(why);}
    static double G(int i,double at,double width){return Math.Exp(-Math.Pow((i-at)/width,2));}
    [STAThread]static int Main(string[] args)
    {
        try {
            Directory.CreateDirectory(args[1]);Application.EnableVisualStyles();
            var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.CalibrationProgramControl",true))) {
                P(c,"SimulationMode",true);P(c,"EmbeddedMode",true);P(c,"UseFirstArrivalTiming",true);P(c,"SelectedLevelCode","5nC");c.Size=new Size(1500,950);c.CreateControl();
                foreach(double sign in new[]{1d,-1d}) {
                    double[] values=Enumerable.Range(0,4096).Select(i=>G(i,400,8)+.4*G(i,1400,8)+sign*.25*G(i,1700,8)).ToArray();
                    var before=(double[])values.Clone();Call(c,"SetPhaseParameters",-1,680d,170d,false,false);
                    Call(c,"SetPhaseWaveform",-1,"5nC",values,0d,8e-9);
                    var phase=Call(c,"GetPhaseStateNoLock",-1);var wave=((IDictionary)F(phase,"Waveforms"))["5nC"];
                    var old=Call(c,"BuildManualResult",wave,phase,400,1400,null);Call(c,"StoreAnalysisNoLock",phase,"5nC",wave,old);
                    Call(c,"SetPhaseMarkers",-1,"5nC",380*8e-9,1380*8e-9,1400*8e-9,680d);
                    Call(c,"StoreManualCursorAnalysis",-1,"5nC",1684,false);
                    var a=((IDictionary)F(phase,"Analyses"))["5nC"];var m=((IDictionary)F(phase,"Markers"))["5nC"];
                    // Result fields are public properties in the shared analyzer.
                    Func<string,object> read=n=>a.GetType().GetProperty(n,B).GetValue(a,null);
                    // First-arrival time stays at the selected sample; decay
                    // independently uses that packet's physical peak.
                    double selectedMagnitude=Math.Abs(values[1700]);
                    Check((int)read("IncidentIndex")==400,"reflection drag retains incident amplitude evidence");
                    Check((int)read("ReflectionIndex")==1700,"first-arrival reflection has independent physical peak");
                    Check(Math.Abs((double)read("IncidentMagnitude")-1)<1e-9&&Math.Abs((double)read("ReflectionMagnitude")-selectedMagnitude)<1e-9,"manual decay uses pulse peaks, not onset amplitude");
                    Check((int)read("IncidentFirstArrivalIndex")==380&&(int)read("ReflectionFirstArrivalIndex")==1684,"manual timing is not replaced by automatic first arrival");
                    Check((int)read("ReflectionPolarity")==(int)sign&&Math.Abs((double)read("ReflectionValue")-values[1700])<1e-9,"physical peak preserves reflection polarity and value");
                    var times=(double[])read("DecayCurveTimeSeconds");var magnitudes=(double[])read("DecayCurveMagnitude");
                    Check(Math.Abs(times.First()-400*8e-9)<1e-12&&Math.Abs(times.Last()-1700*8e-9)<1e-12,"manual decay ends at the physical peak, independent of onset time");
                    Check(Math.Abs(magnitudes.First()-1)<1e-9&&Math.Abs(magnitudes.Last()-selectedMagnitude)<1e-9,"manual decay reaches the selected sample magnitude");
                    Check(Math.Abs((double)F(m,"StartTimeSeconds")-380*8e-9)<1e-12&&Math.Abs((double)F(m,"DistanceMarkerSeconds")-1684*8e-9)<1e-12,"manual onset times remain exactly selected");
                    Check(Math.Abs((double)F(m,"DistanceMeters")-(1684-380)*.68)<1e-8,"length uses onset separation independently");
                    Check(values.SequenceEqual(before),"raw waveform preserved");
                    Call(c,"RebuildLevelDerivedNoLock",phase,"5nC");
                    a=((IDictionary)F(phase,"Analyses"))["5nC"];m=((IDictionary)F(phase,"Markers"))["5nC"];
                    Check((int)read("IncidentIndex")==400&&(int)read("ReflectionIndex")==1700,"derived refresh preserves separate manual time and peak evidence");
                    Check((int)read("IncidentFirstArrivalIndex")==380&&(int)read("ReflectionFirstArrivalIndex")==1684&&Math.Abs((double)F(m,"DistanceMarkerSeconds")-1684*8e-9)<1e-12,"derived refresh cannot overwrite manual reflection timing");
                    Check(values.SequenceEqual(before),"refresh preserves the raw waveform");
                    foreach(int arrival in new[]{1676,1680,1688,1690}) {
                        Call(c,"StoreManualCursorAnalysis",-1,"5nC",arrival,false);
                        a=((IDictionary)F(phase,"Analyses"))["5nC"];m=((IDictionary)F(phase,"Markers"))["5nC"];
                        Check((int)read("ReflectionIndex")==1700&&Math.Abs((double)read("ReflectionMagnitude")-selectedMagnitude)<1e-9,"moving the onset on the same rising pulse does not change its decay amplitude");
                        Check((int)read("ReflectionFirstArrivalIndex")==arrival&&Math.Abs((double)F(m,"DistanceMarkerSeconds")-arrival*8e-9)<1e-12,"each manually selected onset still controls timing exactly");
                    }
                    Call(c,"SavePng",Path.Combine(args[1],sign>0?"peak-decay-positive.png":"peak-decay-inverted.png"),1500,950);
                }
            }
            using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.CalibrationProgramControl",true))) {
                P(c,"SimulationMode",true);P(c,"UseFirstArrivalTiming",true);P(c,"SelectedLevelCode","5nC");
                var values=Enumerable.Range(0,4096).Select(i=>G(i,400,8)+.25*G(i,1700,8)+2*G(i,1740,8)).ToArray();
                Call(c,"SetPhaseParameters",-1,884d,170d,false,false);Call(c,"SetPhaseWaveform",-1,"5nC",values,0d,8e-9);
                var phase=Call(c,"GetPhaseStateNoLock",-1);var wave=((IDictionary)F(phase,"Waveforms"))["5nC"];
                var original=Call(c,"BuildManualResult",wave,phase,400,1700,null);Call(c,"StoreAnalysisNoLock",phase,"5nC",wave,original);
                Call(c,"SetPhaseMarkers",-1,"5nC",380*8e-9,1680*8e-9,1700*8e-9,884d);Call(c,"StoreManualCursorAnalysis",-1,"5nC",1680,false);
                var a=((IDictionary)F(phase,"Analyses"))["5nC"];
                Check((int)a.GetType().GetProperty("ReflectionIndex",B).GetValue(a,null)==1700,"a later larger ringing packet does not replace the chosen first-arrival pulse");
                P(c,"UseFirstArrivalTiming",false);Call(c,"StoreManualCursorAnalysis",-1,"5nC",1694,false);
                a=((IDictionary)F(phase,"Analyses"))["5nC"];
                Check((int)a.GetType().GetProperty("ReflectionIndex",B).GetValue(a,null)==1694,"peak timing mode retains the operator's exact selected sample");
            }
            var correlation=asm.GetType("Xiongxin.Calibration.TdrCorrelation",true);
            var samples=Enumerable.Range(0,2048).Select(i=>.0001*Math.Sin(i)+G(i,400,4)+.4*G(i,625,4)+.25*G(i,653,5)).ToArray();
            Func<string,double[],object> match=(name,s)=>correlation.GetMethod(name,B).Invoke(null,new object[]{s,400,125e6,172.04,170d,.15});
            var legacy=match("Match",samples);var terminal=match("MatchTerminal",samples);
            Check((bool)F(legacy,"Valid")&&(int)F(legacy,"ReflectionIndex")==653,"TDR rough-length prior prefers the credible real endpoint over stronger early ringing");
            Check((bool)F(terminal,"Valid")&&(int)F(terminal,"ReflectionIndex")==653,"known-length calibration prefers credible endpoint packet");
            Check(Math.Abs((double)F(terminal,"DistanceM")-172.04)<1e-9,"reported distance still derives from actual sample separation");
            var empty=Enumerable.Range(0,2048).Select(i=>.0001*Math.Sin(i)+G(i,400,4)).ToArray();
            Check(!(bool)F(match("MatchTerminal",empty),"Valid"),"known endpoint never fabricates a missing reflection");
            var weak=Enumerable.Range(0,2048).Select(i=>.0001*Math.Sin(i)+G(i,400,4)+.03*G(i,653,4)).ToArray();
            Check(!(bool)F(match("MatchTerminal",weak),"Valid"),"terminal prior does not override weak reflection gate");
            Console.WriteLine("CALIBRATION_PEAK_FEEDBACK_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
