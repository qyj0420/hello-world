using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class InsulationGuidanceSmoke
{
    static int checks;
    static Type rules;
    static readonly BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static void Check(bool condition,string message) {checks++;if(!condition)throw new InvalidOperationException(message);}
    static object Call(object value,string method,params object[] args) {return value.GetType().GetMethod(method,Any).Invoke(value,args);}
    static object Rule(string method,params object[] args) {return rules.GetMethod(method,Any).Invoke(null,args);}
    static string Kind(string raw) {object reading=Rule("Parse",raw);return reading.GetType().GetField("Kind",Any).GetValue(reading).ToString();}
    static string Advice(string a,string b,bool comparable,bool english) {return (string)Rule("PairAdvice","A",a,b,comparable,english);}
    static void Set(object target,string property,object value) {target.GetType().GetProperty(property).SetValue(target,value,null);}
    static object Get(object target,string property) {return target.GetType().GetProperty(property).GetValue(target,null);}
    [STAThread]
    static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();
            string qa=Path.GetFullPath(args.Length>1?args[1]:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"InsulationGuidanceSmoke"));
            Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll"));
            rules=assembly.GetType("HVX.Controls.InsulationReferenceRules",true);
            foreach(string s in new[]{""," ","未测","未测量","Not measured","N/A","—","-"})Check(Kind(s)=="Missing","missing: "+s);
            foreach(string s in new[]{"0","1","1e3","200 MΩ","200 MΩ","100兆欧"})Check(Kind(s)=="Exact","exact: "+s);
            foreach(string s in new[]{">1000",">= 1000 MΩ","≥1000"})Check(Kind(s)=="LowerBound","range limit: "+s);
            foreach(string s in new[]{"-1","NaN","Infinity","∞","abc","1,000","1 GΩ","1e999",">-1",">",">>100","<100"})Check(Kind(s)=="Invalid","invalid: "+s);
            Check(Advice("100","200",true,true).Contains("increased 100%"),"numeric increase");
            Check(Advice("200","100",true,false).Contains("减少 50%"),"numeric decrease");
            Check(Advice("100","100",true,true).Contains("unchanged"),"same values");
            Check(!Advice("100","200",false,true).Contains("100%"),"conditions unchecked prevents trend");
            Check(!Advice(">100","200",true,true).Contains("100%"),"lower bound not exact");
            Check(Advice("0","200",true,true).Contains("Zero recorded"),"zero does not divide by zero");
            Check(Advice("","200",true,true).Contains("incomplete"),"missing record");
            Check(Advice("-1","200",true,true).Contains("Check the entry"),"invalid advice");
            Check(Advice("100","200",true,true).Contains("not an insulation verdict"),"not a pass/fail assessment");
            using(var module=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.ProjectCreationControl",true)))
            using(var host=new Form {AutoScaleMode=AutoScaleMode.None,ShowInTaskbar=false,ClientSize=new Size(1050,150)})
            {
                Set(module,"ShowDialogMessages",false);Set(module,"AutoRefreshWeatherOnHardwareSample",false);Set(module,"OnlineWeatherEnabled",false);Set(module,"SettingsPath",Path.Combine(qa,"qa.ini"));
                Set(module,"LanguageIndex",0);Check((string)Get(module,"InsulationReferenceProfile")=="DLT596-2021","Chinese default");
                Set(module,"LanguageIndex",1);Check((string)Get(module,"InsulationReferenceProfile")=="IEEE400-2023","international default before explicit selection");
                Set(module,"InsulationReferenceProfile","DLT596-2021");
                Set(module,"LanguageIndex",1);Check((string)Get(module,"InsulationReferenceProfile")=="DLT596-2021","saved selection is not changed by language");
                bool refused=false;try {Set(module,"InsulationReferenceProfile","UNKNOWN");}catch(TargetInvocationException e){refused=e.InnerException is ArgumentException;}
                Check(refused,"unknown reference safely rejected");
                Check((string)Get(module,"InsulationReferenceProfile")=="DLT596-2021","unknown reference preserves previous selection");
                Set(module,"InsulationConditionsComparable",true);Check((bool)Get(module,"InsulationConditionsComparable"),"comparable state retained");
                Control guidance=(Control)Call(module,"BuildInsulationGuidance");guidance.Dock=DockStyle.Fill;host.Controls.Add(guidance);host.Show();Application.DoEvents();
                foreach(int language in new[]{0,1})
                {
                    Set(module,"LanguageIndex",language);Call(module,"RefreshInsulationGuidance");Application.DoEvents();
                    string detail=(string)Call(module,"InsulationReferenceDetails");
                    Check(detail.Contains("https://"),"official source present");
                    Check(language==0?detail.Contains("不计算吸收比"):detail.Contains("No absorption ratio"),"independent readings clarified");
                    using(Bitmap bitmap=new Bitmap(guidance.Width,guidance.Height)){guidance.DrawToBitmap(bitmap,guidance.ClientRectangle);bitmap.Save(Path.Combine(qa,language==0?"guidance-zh.png":"guidance-en.png"));}
                    var combo=(Control)module.GetType().GetField("insulationReferenceCombo",Any).GetValue(module);
                    Check(guidance.RectangleToScreen(guidance.ClientRectangle).Contains(combo.RectangleToScreen(combo.ClientRectangle)),"selector inside view");
                }
                host.Close();
            }
            Console.WriteLine("INSULATION_GUIDANCE_PASS="+checks);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
