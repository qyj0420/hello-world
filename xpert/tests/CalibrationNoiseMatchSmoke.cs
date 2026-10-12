using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class CalibrationNoiseMatchSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object F(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static void SF(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void P(object o,string n,object v){o.GetType().GetProperty(n,B).SetValue(o,v,null);}
    static void Check(bool v,string why){checks++;if(!v)throw new Exception(why);}
    [STAThread]static int Main(string[] args)
    {
        try {
            Directory.CreateDirectory(args[1]);Application.EnableVisualStyles();
            var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            var type=asm.GetType("HVX.Controls.CalibrationProgramControl",true);var templateType=asm.GetType("HVX.SignalProcessing.NoisePulseTemplate",true);
            double[] raw=Enumerable.Range(0,20000).Select(i=>Math.Exp(-Math.Pow((i-1000)/8d,2))+.7*Math.Exp(-Math.Pow((i-4000)/8d,2))-.9*Math.Exp(-Math.Pow((i-7000)/8d,2))+.8*Math.Exp(-Math.Pow((i-10000)/20d,2))).ToArray();
            var filter=asm.GetType("HVX.SignalProcessing.PdLinearHighPass",true);
            double[] filtered=(double[])filter.GetMethod("Apply").Invoke(null,new object[]{raw,125e6});
            var template=Activator.CreateInstance(templateType);SF(template,"Id","offline-template");SF(template,"CreatedUtc",DateTime.UtcNow.ToString("o"));SF(template,"SampleRateHz",125e6);
            SF(template,"PeakIndex",40);SF(template,"Samples",filtered.Skip(960).Take(81).ToArray());SF(template,"FilterId",filter.GetField("FilterId",B).GetValue(null));
            var templates=Array.CreateInstance(templateType,1);templates.SetValue(template,0);
            var matcher=type.GetMethod("FindNoiseMatches",B);
            var watch=System.Diagnostics.Stopwatch.StartNew();
            var matches=((IEnumerable)matcher.Invoke(null,new object[]{raw,filtered,templates,125e6,0,raw.Length-1,1d,CancellationToken.None})).Cast<object>().ToArray();watch.Stop();
            Check(matches.Length==3,"same-window matching marks three similar pulses, not different shape");
            Check(matches.Select(m=>(int)F(m,"Peak")).SequenceEqual(new[]{1000,4000,7000}),"matching uses waveform sample coordinates, including opposite polarity");
            var matchType=asm.GetType("HVX.SignalProcessing.NoisePulseTemplates",true);
            foreach(var m in matches)Check((bool)matchType.GetMethod("Matches").Invoke(null,new object[]{templates,filtered,F(m,"Peak"),125e6,1d}),"visible match uses acquisition exclusion criterion");
            var zoom=((IEnumerable)matcher.Invoke(null,new object[]{raw,filtered,templates,125e6,3000,8000,1d,CancellationToken.None})).Cast<object>().ToArray();
            Check(zoom.Length==2,"zoom returns only peaks within current view");
            var full=Activator.CreateInstance(templateType);SF(full,"Samples",filtered);SF(full,"PeakIndex",1000);SF(full,"SampleRateHz",125e6);SF(full,"FilterId",F(template,"FilterId"));
            Check((double)matchType.GetMethod("Similarity").Invoke(null,new object[]{full,filtered,1000,125e6})>.999999,"exact last sample is a valid comparison boundary");
            var cancellation=new CancellationTokenSource();cancellation.Cancel();bool cancelled=false;
            try{matcher.Invoke(null,new object[]{raw,filtered,templates,125e6,0,raw.Length-1,1d,cancellation.Token});}catch(TargetInvocationException e){cancelled=e.InnerException is OperationCanceledException;}
            Check(cancelled,"background matching can cancel on view/context change");cancellation.Dispose();
            using(var host=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-5000,-5000),ClientSize=new Size(1500,950)})
            using(var c=(Control)Activator.CreateInstance(type)) {
                c.Dock=DockStyle.Fill;host.Controls.Add(c);P(c,"EmbeddedMode",true);host.Show();
                Call(c,"SetPhaseNoiseWaveform",-1,raw,0d,8e-9);SF(c,"compactQualityTab",true);SF(c,"noiseTemplates",templates);
                Call(c,"SavePng",Path.Combine(args[1],"matching-start.png"),1500,950);
                Check((bool)F(c,"noiseMatchBusy")&&((System.Windows.Forms.Timer)F(c,"noiseBusyTimer")).Enabled,"asynchronous matching starts animated busy feedback");
                var before=(double[])raw.Clone();var wait=System.Diagnostics.Stopwatch.StartNew();
                do{Application.DoEvents();Thread.Sleep(10);}while((F(c,"noiseMatchWave")==null||(bool)F(c,"noiseMatchBusy"))&&wait.ElapsedMilliseconds<15000);
                Check(!((bool)F(c,"noiseMatchBusy"))&&((Array)F(c,"noiseMatchResults")).Length==3,"visible control publishes asynchronous match overlay: "+F(c,"noiseMatchStatus")+"; matches="+((Array)F(c,"noiseMatchResults")).Length);
                Check(raw.SequenceEqual(before),"highlighting never edits raw samples");
                Check(!((System.Windows.Forms.Timer)F(c,"noiseBusyTimer")).Enabled,"busy animation stops on completion");
                Call(c,"SavePng",Path.Combine(args[1],"matching-interference.png"),1500,950);
                SF(c,"noiseTemplates",Array.CreateInstance(templateType,0));Call(c,"SavePng",Path.Combine(args[1],"matching-cleared.png"),1500,950);Application.DoEvents();
                Check(((Array)F(c,"noiseMatchResults")).Length==0,"removing templates clears stale highlights");
                SF(c,"noiseTemplateSaving",true);Call(c,"UpdateNoiseBusyFeedback");
                Check(((System.Windows.Forms.Timer)F(c,"noiseBusyTimer")).Enabled,"first-template preparation has busy feedback even with zero saved templates");
                Call(c,"SavePng",Path.Combine(args[1],"preparing-first-template.png"),1500,950);
                SF(c,"noiseTemplateSaving",false);Call(c,"UpdateNoiseBusyFeedback");
                Check(!((System.Windows.Forms.Timer)F(c,"noiseBusyTimer")).Enabled,"preparation success/failure clears busy feedback");
                host.Close();
                Check(F(c,"noiseBusyTimer")==null,"dispose releases animation timer");
            }
            Console.WriteLine("CALIBRATION_NOISE_MATCH_PASS="+checks+"; MATCH_MS="+watch.ElapsedMilliseconds+"; HARDWARE_COMMANDS=0");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
