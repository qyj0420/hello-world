using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Synthetic completed record and quality snapshot only. Never starts a worker.
internal static class PressureNextRangeAutomaticSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static Type type;static string qa;static int checks,prompts;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,F).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,F).SetValue(o,v,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Pump(){Application.DoEvents();Thread.Sleep(2);}
    static bool Await(Task<bool> task){var w=Stopwatch.StartNew();while(!task.IsCompleted&&w.ElapsedMilliseconds<5000)Pump();Check(task.IsCompleted,"bounded asynchronous decision write");return task.GetAwaiter().GetResult();}
    public static int Choose(object review){prompts++;return 0;}
    static void Run(bool auto,string severity,bool remind,bool remindAutomatic){
        using(var form=new Form{ClientSize=new Size(1180,720),ShowInTaskbar=false,Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual})
        using(var c=(Control)Activator.CreateInstance(type)){
            form.Controls.Add(c);c.Dock=DockStyle.Fill;Set(c,"ProjectPath",qa);Set(c,"FrequencyReminderEnabled",false);Set(c,"MaximumPeakVoltageKv",20d);
            Call(c,"SetPressurePlan",new[]{1d},new[]{1},new[]{1d});Call(c,"SetPressureProgramMode","PLAN");Call(c,"SetDischargeCalibration",1000d,"fixture.cal",DateTimeOffset.UtcNow,"A",false);
            Call(c,"SetAutomaticRange",auto);Set(c,"OverRangeReminderEnabled",remind);Set(c,"AutomaticRangeReminderEnabled",remindAutomatic);Set(c,"ActionHandshakeEnabled",true);form.Show();Pump();
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);Call(c,"RequestPressure");Check((bool)Get(c,"ActionPending"),"one synthetic completed pressure mailbox");
            long unchangedRequest=(long)Get(c,"PressureRequestSequence");
            Set(c,"AutomaticRangeReminderEnabled",!remindAutomatic);
            Check((bool)Get(c,"AutomaticRangeReminderEnabled")==!remindAutomatic&&(long)Get(c,"PressureRequestSequence")==unchangedRequest,"presentation preference can be restored during a capture without publishing a pressure request");
            Set(c,"AutomaticRangeReminderEnabled",remindAutomatic);
            Call(c,"CompleteAction",Get(c,"ActionSequence"),true,"");Call(c,"SetPressureState","COMPLETE",1d);
            Field(c,"postProcessStateCode","COMPLETE");Field(c,"dataSaved",true);Field(c,"dataAnalyzed",true);Field(c,"hasAcquisitionResult",true);
            string id=(string)Get(c,"LocationAcquisitionId");int current=(int)Get(c,"SelectedPicoRangeCode"),next=Math.Min(10,current+1);
            var capture=Activator.CreateInstance(module.GetType("HVX.Controls.PressureZeroVoltageResult"),true);
            var review=Activator.CreateInstance(module.GetType("HVX.Controls.PressureRangeReviewSnapshot"));var quality=Activator.CreateInstance(module.GetType("Xiongxin.ZeroBench.LocationRangeResult"));
            bool critical=severity!="ordinary";Field(quality,"WindowValid",true);Field(quality,"Change",true);Field(quality,"RecommendedIndex",next);
            Field(quality,"AtMaximumAndClipped",severity=="clipped");Field(quality,"MaximumRangeInsufficient",severity=="insufficient");
            Field(review,"Schema","HVX.Pressure.RangeReview/R27");Field(review,"Quality",quality);Field(review,"CurrentRangeIndex",current);Field(review,"NextRangeIndex",next);
            Field(review,"CanApply",!critical);Field(review,"NeedsPrompt",true);Field(review,"NextRoundBlocked",severity=="blocked");
            Field(capture,"RangeReview",review);Field(capture,"Quality",quality);Field(capture,"RawSha256","SYNTHETIC-RAW-UNCHANGED");
            string dir=Path.Combine(qa,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);Field(capture,"Directory",dir);
            Field(c,"lastPressureCapture",capture);Field(c,"lastPressureAcquisitionId",id);
            var hook=type.GetField("NextRangeChoiceForTests",F);var p=Expression.Parameter(hook.FieldType.GetGenericArguments()[0]);hook.SetValue(c,Expression.Lambda(hook.FieldType,Expression.Call(typeof(PressureNextRangeAutomaticSmoke).GetMethod("Choose"),Expression.Convert(p,typeof(object))),p).Compile());
            long before=(long)Get(c,"PressureRequestSequence");prompts=0;bool result=Await((Task<bool>)Call(c,"ReviewNextPressureRangeAsync"));
            bool shouldApply=auto&&!critical,shouldPrompt=false;
            Check(prompts==(shouldPrompt?1:0),"prompt policy auto="+auto+" severity="+severity+" reminder="+remind);
            Check((int)Get(c,"SelectedPicoRangeCode")== (shouldApply?next:current),"only valid automatic recommendation updates next range");
            Check(result==!critical,"critical does not continue, ordinary advice never opens modal");
            Check((long)Get(c,"PressureRequestSequence")==before&&!(bool)Get(c,"ActionPending")&&!(bool)Get(c,"HasActiveHardwareSession"),"review never starts acquisition or output");
            Check((int)Field(review,"CurrentRangeIndex")==current&&(int)Field(quality,"RecommendedIndex")==next&&(bool)Field(quality,"Change"),"original quality evidence unchanged");
            var files=Directory.GetFiles(dir,"next-range-decision-*.xml");Check(files.Length==1,"exactly one decision record");string xml=File.ReadAllText(files[0]);
            Check(xml.Contains("<StartsHardware>false</StartsHardware>")&&xml.Contains("<ProvesDischargeSafe>false</ProvesDischargeSafe>"),"no safety or output assertion");
            Check(xml.Contains("<DecisionSource>"+(shouldApply?"AutomaticRange":critical?"Blocked":"NoPrompt")+"</DecisionSource>"),"record distinguishes automation from blocked review");
            Check(xml.Contains("<AutomaticRangeReminderEnabled>"+(remindAutomatic?"true":"false")+"</AutomaticRangeReminderEnabled>"),"record retains actual reminder preference");
            bool again=Await((Task<bool>)Call(c,"ReviewNextPressureRangeAsync"));Check(again==result&&prompts==(shouldPrompt?1:0)&&Directory.GetFiles(dir,"next-range-decision-*.xml").Length==1,"duplicate cannot reprompt, reapply or rewrite decision");
        }
    }
    static void TextTest(){
        var quality=Activator.CreateInstance(module.GetType("Xiongxin.ZeroBench.LocationRangeResult"));Field(quality,"WindowValid",true);Field(quality,"Change",true);Field(quality,"RecommendedIndex",6);Field(quality,"LocationQuality","量程偏小，建议调整");
        var ct=module.GetType("Xiongxin.ZeroBench.RangeDecisionContext");object context=Activator.CreateInstance(ct,new object[]{quality,5,6,0d,0,false,false,false});
        var dt=module.GetType("Xiongxin.ZeroBench.PressureRangeDecisionDialog");
        using(var dialog=(Form)Activator.CreateInstance(dt,F,null,new object[]{context,null},null)){
            var items=Flatten(dialog).ToArray();string text=String.Join("\n",items.Select(v=>v.Text));
            foreach(string internalText in new[]{"P0.7","HV/AO","HV ON","调试工具","此组件","调用方","外层流程"})Check(!text.Contains(internalText),"customer prompt excludes internal phrase: "+internalText);
            // Latest user request: ordinary prompt shows only too small/large;
            // measured diagnostics remain in the record, not a large UI dump.
            Check(items.OfType<TextBox>().Single(v=>v.Name=="RangeDecisionDetails").Text=="量程过小"&&
                !text.Contains("提醒选择将保存")&&!text.Contains("自动调整仅限已开始的本轮")&&
                !text.Contains("保持零电压设置")&&!text.Contains("原始峰值占用"),"brief verdict excludes internal explanation while policy remains enforced");
            Check(items.OfType<Button>().Count()==2&&items.OfType<Button>().Any(b=>b.Text=="忽略并继续试验")&&items.OfType<Button>().Any(b=>b.Text=="调整量程重新试验"),"quality dialog has two explicit actions");
            Check(items.OfType<CheckBox>().Any(b=>b.Text=="不再提示（自动调整）"),"automatic adjustment is explicit opt-in");
            items.OfType<CheckBox>().Single().Checked=true;
            var ignore=items.OfType<Button>().Single(b=>b.Text=="忽略并继续试验");
            typeof(Button).GetMethod("OnClick",F).Invoke(ignore,new object[]{EventArgs.Empty});
            Check((bool)Get(dialog,"SuppressFurtherRangePrompts")&&(string)Get(dialog,"Decision").ToString()=="Ignore","actual Ignore button captures persistent checked reminder choice");
            var record=Get(dialog,"Record");
            Check(!(bool)Field(record,"RequestedRetry")&&!(bool)Field(record,"AutomaticRangeRetriesEnabled"),"saving preference on Ignore does not record an automatic retry intent");
        }
        object pressureContext=Activator.CreateInstance(ct,new object[]{quality,5,6,1000d,0,false,true,false});
        using(var dialog=(Form)Activator.CreateInstance(dt,F,null,new object[]{pressureContext,null},null)){
            string text=String.Join("\n",Flatten(dialog).Select(v=>v.Text));
            Check(!text.Contains("提醒选择将保存")&&!text.Contains("自动调整仅限已开始的本轮"),"real-pressure dialog also excludes removed orange explanation");
        }
    }
    static System.Collections.Generic.IEnumerable<Control> Flatten(Control c){yield return c;foreach(Control child in c.Controls)foreach(Control x in Flatten(child))yield return x;}
    [STAThread]static int Main(string[] args){try{
        string root=Path.GetFullPath(args[0]);qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(root,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        module=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));type=module.GetType("HVX.Controls.PressureTestControl");Application.EnableVisualStyles();
        foreach(bool auto in new[]{true,false})foreach(bool remind in new[]{true,false})foreach(bool remindAutomatic in new[]{true,false})foreach(string severity in new[]{"ordinary","clipped","insufficient","blocked"})Run(auto,severity,remind,remindAutomatic);
        TextTest();Console.WriteLine("PRESSURE_NEXT_RANGE_AUTOMATIC_PASS="+checks+";HardwareAccessed=false;WorkersStarted=0");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
