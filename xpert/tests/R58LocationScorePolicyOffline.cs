using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using HVX.LocationScoring;
using HVX.PdReview;
using HVX.UnifiedSpectrum;
using Xiongxin.OWTS.Desktop;

// Pure managed regression. Product entry, algorithm worker, SDK and device are never invoked.
internal static class R58LocationScorePolicyOffline
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static Assembly analysis;
    static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    static object Get(object value,string name){Type t=value as Type??value.GetType();var p=t.GetProperty(name,All);return p!=null?p.GetValue(value is Type?null:value,null):t.GetField(name,All).GetValue(value is Type?null:value);}
    static void Set(object value,string name,object item){var p=value.GetType().GetProperty(name,All);if(p!=null)p.SetValue(value,item,null);else value.GetType().GetField(name,All).SetValue(value,item);}
    static object Call(object value,string name,params object[] args){Type t=value as Type??value.GetType();return t.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value is Type?null:value,args);}
    static void Reject(Action action,string name){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}catch(InvalidDataException){rejected=true;}catch(TargetInvocationException e){rejected=e.InnerException is ArgumentException||e.InnerException is InvalidDataException||e.InnerException is InvalidOperationException;}Check(rejected,name);}
    static void Defaults(AppSettings s,string name){Check(s.LocationSuspiciousMinimumPercent==30&&s.LocationManualAcceptanceMinimumPercent==45&&s.LocationAutomaticAcceptanceMinimumPercent==50,name);}
    static void Policy()
    {
        var policy=LocationScorePolicy.Defaults();
        double[] percents={29.999,30,44.999,45,49.999,50,100};
        int[] classification={0,1,1,1,1,2,2};bool[] manual={false,false,false,true,true,true,true};bool[] automatic={false,false,false,false,false,true,true};
        var matrix=new double[1,36];matrix[0,10]=10;matrix[0,11]=30;matrix[0,12]=.16;matrix[0,31]=0;matrix[0,32]=63;
        for(int i=0;i<percents.Length;i++){
            double score=percents[i]/100;matrix[0,33]=score;
            Check(policy.Classify(score)==classification[i],"classification boundary "+percents[i]);
            Check(policy.CanManuallyAccept(score)==manual[i],"manual boundary "+percents[i]);
            Check(policy.CanAutomaticallyAccept(score)==automatic[i],"automatic boundary "+percents[i]);
            Check(UnifiedSpectrumControl.ClassifySourceRow(matrix,0,30,50)==classification[i],"actual spectrum classification "+percents[i]);
        }
        foreach(double bad in new[]{Double.NaN,Double.NegativeInfinity,Double.PositiveInfinity,-.00001,1.00001})
            Check(policy.Classify(bad)==-1&&!policy.CanManuallyAccept(bad)&&!policy.CanAutomaticallyAccept(bad),"invalid score no acceptance "+bad);
        foreach(var limits in new[]{new[]{31d,30,50},new[]{30d,51,50},new[]{-1d,45,50},new[]{30d,45,101},new[]{Double.NaN,45,50},new[]{30d,Double.PositiveInfinity,50},new[]{30d,45,Double.NaN}})
            Reject(()=>new LocationScorePolicy(limits[0],limits[1],limits[2]),"invalid ordered triple rejected");
        Check(new LocationScorePolicy(0,0,0).Classify(0)==2&&new LocationScorePolicy(100,100,100).CanManuallyAccept(1),"equal end limits valid with empty suspect interval");
        Check(!LocationScorePolicy.Meets(0,Double.Epsilon),"positive underflow threshold does not accept zero");
        matrix[0,33]=1;matrix[0,11]=9;matrix[0,12]=-1;Check(UnifiedSpectrumControl.ClassifySourceRow(matrix,0,30,50)==-1,"negative delay remains unavailable");
        foreach(var limits in new[]{new[]{-1d,50},new[]{30d,101},new[]{60d,50},new[]{Double.NaN,50}})
            Reject(()=>UnifiedSpectrumControl.ClassifySourceRow(matrix,0,limits[0],limits[1]),"public classification rejects invalid thresholds");
    }
    static void Configuration(string qa)
    {
        string path=Path.Combine(qa,"settings.xml");File.WriteAllText(path,"<HVXUnifiedDesktopSettings/>");AppSettings loaded;string message;
        Check(AppSettings.TryLoad(path,out loaded,out message),"old XML missing all fields loads");Defaults(loaded,"old defaults 30/45/50");
        var settings=new AppSettings{DataRoot=qa,LocationSuspiciousMinimumPercent=20,LocationManualAcceptanceMinimumPercent=40,LocationAutomaticAcceptanceMinimumPercent=55};
        settings.Save(path);Check(AppSettings.TryLoad(path,out loaded,out message),"real atomic XML reload");
        Check(loaded.LocationSuspiciousMinimumPercent==20&&loaded.LocationManualAcceptanceMinimumPercent==40&&loaded.LocationAutomaticAcceptanceMinimumPercent==55,"three independent values persisted");
        var clone=loaded.Clone();clone.LocationManualAcceptanceMinimumPercent=41;Check(loaded.LocationManualAcceptanceMinimumPercent==40,"snapshot clone independent");
        foreach(var values in new[]{new[]{30d,29,50},new[]{30d,55,50},new[]{-1d,45,50},new[]{30d,45,101},new[]{Double.NaN,45,50},new[]{30d,Double.NegativeInfinity,50}}){
            var bad=loaded.Clone();bad.LocationSuspiciousMinimumPercent=values[0];bad.LocationManualAcceptanceMinimumPercent=values[1];bad.LocationAutomaticAcceptanceMinimumPercent=values[2];
            string error;Check(!bad.TryValidate(out error)&&error.Contains("评分"),"invalid save validation");byte[] before=File.ReadAllBytes(path);Reject(()=>bad.Save(path),"invalid limits cannot save");Check(before.SequenceEqual(File.ReadAllBytes(path)),"invalid save preserves original output");
        }
        foreach(string bad in new[]{"NaN","Infinity","-1","101","70"}){
            var xml=new XmlDocument();xml.Load(path);xml.DocumentElement.SelectSingleNode("LocationSuspiciousMinimumPercent").InnerText=bad;xml.Save(path);byte[] original=File.ReadAllBytes(path);
            Check(AppSettings.TryLoad(path,out loaded,out message),"bad old limits safely migrated "+bad);Defaults(loaded,"bad old defaults restored "+bad);
            Check(!String.IsNullOrEmpty(loaded.SessionLocationScoreDiagnostic),"bad old limits have diagnostic "+bad);
            Check(original.SequenceEqual(File.ReadAllBytes(path)),"load never rewrites old settings "+bad);
            Check(loaded.DataRoot==qa,"unrelated settings preserved on score-only fallback");settings.Save(path);
        }
        var malformed=new XmlDocument();malformed.Load(path);malformed.DocumentElement.SelectSingleNode("LocationManualAcceptanceMinimumPercent").InnerText="not-a-number";malformed.Save(path);
        Check(!AppSettings.TryLoad(path,out loaded,out message)&&!loaded.Hardware.Enabled&&!String.IsNullOrEmpty(loaded.SessionLocationScoreDiagnostic),"malformed old XML safely fails offline with diagnostic");Defaults(loaded,"malformed old limits use defaults");
        File.WriteAllText(path,"<HVXUnifiedDesktopSettings><LocationSuspiciousMinimumPercent>25</LocationSuspiciousMinimumPercent></HVXUnifiedDesktopSettings>");
        Check(AppSettings.TryLoad(path,out loaded,out message)&&loaded.LocationSuspiciousMinimumPercent==25&&loaded.LocationManualAcceptanceMinimumPercent==45&&loaded.LocationAutomaticAcceptanceMinimumPercent==50,"partially missing old fields retain independent defaults");
    }
    static void SettingsUi(string qa)
    {
        using(var form=new SettingsForm(new AppSettings{DataRoot=qa,LanguageCode="en"})){
            var low=(NumericUpDown)Get(form,"locationSuspiciousMinimum");var manual=(NumericUpDown)Get(form,"locationManualMinimum");var automatic=(NumericUpDown)Get(form,"locationAutomaticMinimum");
            Check(low.Value==30&&manual.Value==45&&automatic.Value==50,"general UI default values");
            Check(low.Maximum==45&&manual.Minimum==30&&manual.Maximum==50&&automatic.Minimum==45,"all linked UI bounds");
            low.Value=35;manual.Value=48;automatic.Value=60;
            Check(low.Maximum==48&&manual.Minimum==35&&manual.Maximum==60&&automatic.Minimum==48,"bounds follow all three edits");
            var captured=(AppSettings)Call(form,"CaptureValues");Check(captured.LocationSuspiciousMinimumPercent==35&&captured.LocationManualAcceptanceMinimumPercent==48&&captured.LocationAutomaticAcceptanceMinimumPercent==60,"general UI captures independent scores");
            Check(!captured.Hardware.Enabled&&String.IsNullOrEmpty(captured.DeviceParametersReviewedHash),"UI edit is not device or physical confirmation");
            var captions=(System.Collections.IDictionary)Get(form,"settingsCaptions");
            foreach(string text in new[]{"定位评分：可疑下限 / %","定位评分：手动接受下限 / %","定位评分：自动接受下限 / %"}){
                Control label=null;foreach(System.Collections.DictionaryEntry entry in captions)if((string)entry.Value==text)label=(Control)entry.Key;
                Check(label!=null&&label.Text.StartsWith("Location score:",StringComparison.Ordinal),"EN translated general score label "+text);
            }
            automatic.Value=48;manual.Value=35;low.Value=35;Check(low.Maximum==35&&manual.Minimum==35&&automatic.Minimum==35,"equal ordered limits possible without inverted bounds");
        }
    }
    static object Request(string project,string loc)
    {
        var request=Activator.CreateInstance(analysis.GetType("HVX.PdReview.ReviewAnalysisRequest",true));Set(request,"ProjectPath",project);Set(request,"LocationFilePath",loc);return request;
    }
    static void ResultGate()
    {
        Type resultType=analysis.GetType("HVX.PdReview.ReviewAnalysisResult",true),gate=analysis.GetType("HVX.PdReview.ReviewLocationScoreAcceptance",true);
        var request=Request("unused","unused");var result=Activator.CreateInstance(resultType);var p=new double[12,36];
        double[] scores={.29999,.30,.44999,.45,.49999,.50,1,Double.NaN,Double.PositiveInfinity,-1,.75,.55};
        for(int i=0;i<scores.Length;i++){p[i,33]=scores[i];p[i,35]=i==10?0:1;}Set(result,"Parameters",p);Call(gate,"Apply",result,request);
        for(int i=0;i<scores.Length;i++)Check(p[i,35]==(i==5||i==6||i==11?1:0),"automatic result gate "+i);
        for(int i=0;i<scores.Length;i++)Check(Double.IsNaN(scores[i])?Double.IsNaN(p[i,33]):BitConverter.DoubleToInt64Bits(scores[i])==BitConverter.DoubleToInt64Bits(p[i,33]),"raw score unchanged "+i);
        Check(p[10,35]==0,"independently unqualified high score never promoted");Check(p[11,35]==1,"qualified composite 55% can pass 50 despite shape NCC floor 60%");
        Set(request,"AutomaticAcceptance",false);Call(gate,"Apply",result,request);Check(p.Cast<double>().Where((x,i)=>i%36==35).All(x=>x==0),"manual-analysis request clears all automatic inclusions");
    }
    static void TransmissionAndManual(string qa)
    {
        Type prefs=analysis.GetType("HVX.PdReview.ReviewAnalysisPreferences",true);prefs.GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"analysis-preferences.json")));
        using(var review=new PdReviewControl())using(var host=new ModuleHost(new AppSettings(),new ApplicationState(),new Diagnostics(qa),Path.Combine(qa,"modules"))){
            review.PromptSavedAnalysisOnProjectLoad=false;review.EnglishVersion=true;
            var settings=new AppSettings{DataRoot=qa,LocationSuspiciousMinimumPercent=31,LocationManualAcceptanceMinimumPercent=46,LocationAutomaticAcceptanceMinimumPercent=51};
            Call(host,"ApplyDataAnalysisSettings",review,"",new[]{true,true,true,true},settings);
            Check(review.LocationSuspiciousMinimumPercent==31&&review.LocationManualAcceptanceMinimumPercent==46&&review.LocationAutomaticAcceptanceMinimumPercent==51,"host atomically transmits all score limits");
            var spectrum=(UnifiedSpectrumControl)Get(review,"spectrum");Check(spectrum.ClassificationLowThresholdPercent==31&&spectrum.ClassificationHighThresholdPercent==51,"actual spectrum receives classification thresholds");
            Reject(()=>review.SetLocationScoreThresholds(60,45,50),"control atomic setter rejects unordered values");Check(review.LocationManualAcceptanceMinimumPercent==46,"failed setter preserves prior policy");
            review.SetLocationScoreThresholds(30,45,50);
            string project=Path.Combine(qa,"project");Directory.CreateDirectory(project);File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度: = \"414\"\r\n");File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t终端\t414m\r\n");
            string loc=Path.Combine(project,"A_13.40kV_1.loc");File.WriteAllText(loc,"LOC regression\nReceived UTC\t2026-10-12T00:00:00Z\nPhase\t0\nTarget peak kV\t6\nCable-length\t414\nVelocity (m/us)\t170\nDelta x [s]\t8E-09\nCharge unit\tpC x 100000\nCapture start seconds\t0\nAcquisition context\t{}\nReserved\tfixture\n"+String.Join("\n",Enumerable.Repeat("100000",16)));
            review.ProjectPath=project;
            var request=Call(review,"CreateFileAnalysisRequest",loc,0L,true,0);Check((double)Get(request,"LocationSuspiciousMinimumPercent")==30&&(double)Get(request,"LocationManualAcceptanceMinimumPercent")==45&&(double)Get(request,"LocationAutomaticAcceptanceMinimumPercent")==50,"UI captures three scalar request fields");
            Set(request,"LocationSuspiciousMinimumPercent",20d);Set(request,"LocationManualAcceptanceMinimumPercent",40d);Set(request,"LocationAutomaticAcceptanceMinimumPercent",55d);
            var engine=analysis.GetType("HVX.PdReview.ReviewAnalysisEngine",true);var snapshot=Call(engine,"Snapshot",request);
            Check((double)Get(snapshot,"LocationSuspiciousMinimumPercent")==20&&(double)Get(snapshot,"LocationManualAcceptanceMinimumPercent")==40&&(double)Get(snapshot,"LocationAutomaticAcceptanceMinimumPercent")==55,"validated analysis snapshot retains independent limits");
            string algorithm=(string)Get(engine,"ExpectedAlgorithmPath");File.WriteAllText(algorithm,"inert test-owned hash input; never loaded or executed");var cache=analysis.GetType("HVX.PdReview.PressureAnalysisCache",true);
            string key=(string)Call(cache,"Key","score-policy-fixture",snapshot);Check(!String.IsNullOrEmpty(key),"pure cache identity available");
            Type history=analysis.GetType("HVX.PdReview.AHistoryEvidence",true);string historySettings=(string)Call(history,"Settings",snapshot);
            foreach(string name in new[]{"LocationSuspiciousMinimumPercent","LocationManualAcceptanceMinimumPercent","LocationAutomaticAcceptanceMinimumPercent"}){double old=(double)Get(snapshot,name);Set(snapshot,name,old+.1);Check((string)Call(cache,"Key","score-policy-fixture",snapshot)!=key,"cache identity changes for "+name);Check((string)Call(history,"Settings",snapshot)!=historySettings,"history settings identity changes for "+name);Set(snapshot,name,old);}
            Set(request,"LocationManualAcceptanceMinimumPercent",99d);Reject(()=>Call(engine,"Snapshot",request),"snapshot rejects unordered thresholds before algorithm");
            double[,] p=new double[1,36],w=new double[1,4096];p[0,0]=1;p[0,2]=13.4;p[0,3]=45;p[0,5]=414;p[0,6]=170;p[0,7]=125e6;p[0,10]=100;p[0,11]=710;p[0,12]=4.88;p[0,13]=200;p[0,15]=p[0,16]=180;p[0,20]=100;p[0,21]=.92;p[0,32]=4095;p[0,33]=.91;
            for(int i=0;i<4096;i++)w[0,i]=180*Math.Exp(-Math.Pow((i-100)/14d,2))+60*Math.Exp(-Math.Pow((i-710)/23d,2));
            Check(review.LoadDataMatrices(p,w),"managed review fixture loaded: "+review.LastError);review.SelectSourceRow(0);
            var working=(double[,])Get(review,"working");
            foreach(double score in new[]{.29999,.30,.44999,.45,.49999,.50,1d}){working[0,33]=score;Check(review.CanAccept==(score>=.45),"actual manual Accept button boundary "+score);}
            working[0,33]=.44999;Check(!review.AcceptCurrent()&&!review.CurrentAccepted&&review.LastError.Contains("45%")&&!review.LastError.Contains("评分"),"manual action rejects low score with English simple reason");
            working[0,33]=.45;Check(review.AcceptCurrent()&&review.CurrentAccepted,"explicit manual acceptance succeeds at 45%");
            Check(((bool[])Get(review,"AcceptanceConfirmedRows"))[0],"manual provenance only from explicit accepted action");
            Set(review,"capacitorProject",true);working[0,26]=-2;working[0,33]=Double.NaN;Check((bool)Get(review,"ManualLocationScoreEligible"),"non-location capacitor row remains exempt from location score policy");
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try{string qa=Path.GetFullPath(args[0]);Directory.CreateDirectory(qa);analysis=typeof(PdReviewControl).Assembly;
            Policy();Configuration(qa);SettingsUi(qa);ResultGate();TransmissionAndManual(qa);
            Console.WriteLine("LOCATION_SCORE_POLICY_PASS="+checks);Console.WriteLine("HardwareAccessed=False;SDKCalled=False;ProductStarted=False;AlgorithmWorkerStarted=False;PhysicalAcceptance=False");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
