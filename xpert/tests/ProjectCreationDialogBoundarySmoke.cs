using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// Uses the actual independently built ProjectCreation module. Synthetic metadata
// is created only under the supplied QA directory. No weather/network/SDK calls,
// settings saves or project commits are dispatched.
internal static class ProjectCreationDialogBoundarySmoke
{
    const BindingFlags Any=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly assembly;
    static string output;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
    static object Call(object target,string name,params object[] args){return target.GetType().GetMethod(name,Any).Invoke(target,args);}
    static object Get(object target,string name){return target.GetType().GetProperty(name,Any).GetValue(target,null);}
    static void Set(object target,string name,object value){target.GetType().GetProperty(name,Any).SetValue(target,value,null);}
    static T Find<T>(Control root,string name)where T:Control{return root.Controls.Find(name,true).Cast<T>().Single();}
    static void Pump(){Application.DoEvents();Application.DoEvents();}
    static string Traditional(string text){return (string)assembly.GetType("Xiongxin.OWTS.Localization.UiLanguage",true).GetMethod("ToTraditional",Any).Invoke(null,new object[]{text});}
    static void Fit(Form form,Rectangle area,Size minimum)
    {assembly.GetType("HVX.Controls.ProjectDialogLayout",true).GetMethod("FitToWorkingArea",Any).Invoke(null,new object[]{form,area,minimum});Pump();}
    static void Contained(Form form,Rectangle area)
    {Check(form.Width<=area.Width-24&&form.Height<=area.Height-24,"window fits synthetic working area: "+form.Name);Check(area.Contains(form.Bounds),"window location contained: "+form.Name);}
    static void Actions(Form form,Control footer,int count)
    {
        Check(form.ClientRectangle.Contains(footer.Bounds),"footer contained: "+form.Name);
        Check(footer.Controls.Count==count,"action count retained: "+form.Name);
        foreach(Control action in footer.Controls)
        {
            Check(footer.ClientRectangle.Contains(action.Bounds),"button contained: "+action.Name+" "+action.Bounds+" in "+footer.ClientRectangle);
            Check(action.Font.SizeInPoints==form.Font.SizeInPoints,"button inherits current font: "+action.Name);
            Check(action.Width>=TextRenderer.MeasureText(action.Text,action.Font).Width+8,"complete action caption: "+action.Text);
            Check(action.Height>=action.Font.Height+8,"action height: "+action.Text);
        }
    }
    static void SaveImage(Form form,string name)
    {using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(output,name+".png"));}}
    static void Weather(int language,float size,Rectangle area)
    {
        using(var form=(Form)Activator.CreateInstance(assembly.GetType("HVX.Controls.WeatherLocationDialog",true),Any,null,new object[]{language,false,double.NaN,double.NaN},CultureInfo.InvariantCulture))
        using(var font=new Font("Microsoft YaHei UI",size))
        {
            form.Show();Pump();form.Font=font;Pump();Fit(form,area,Size.Empty);Contained(form,area);
            Label notice=Find<Label>(form,"WeatherPrivacyNotice");var body=Find<TableLayoutPanel>(form,"WeatherLocationBody");
            int required=TextRenderer.MeasureText(notice.Text,notice.Font,new Size(Math.Max(1,notice.ClientSize.Width),10000),TextFormatFlags.WordBreak).Height;
            Check(notice.Height>=required,"privacy text not cropped at "+language+" / "+size+": "+notice.Height+" vs "+required);
            Check(notice.Text.Contains("api.met.no")&&notice.Text.Contains("IP"),"privacy disclosure remains present");
            Check(body.AutoScroll,"privacy body can scroll");Actions(form,Find<FlowLayoutPanel>(form,"WeatherLocationActions"),2);
            var accept=(Button)form.GetType().GetField("accept",Any).GetValue(form);var consent=(CheckBox)form.GetType().GetField("agreement",Any).GetValue(form);
            body.ScrollControlIntoView(consent);Pump();
            Check(body.RectangleToScreen(body.ClientRectangle).Contains(consent.RectangleToScreen(consent.ClientRectangle)),"weather consent reachable by body scrolling");
            Check(!accept.Enabled,"weather requires explicit consent");
            ((TextBox)form.GetType().GetField("latitude",Any).GetValue(form)).Text="0";
            ((TextBox)form.GetType().GetField("longitude",Any).GetValue(form)).Text="0";consent.Checked=true;
            Check(accept.Enabled&&(double)Get(form,"Latitude")==0&&(double)Get(form,"Longitude")==0,"valid zero manual coordinate remains allowed");
            if(language==18)Check(accept.Text==Traditional("获取天气"),"weather footer uses traditional Chinese");
            if(size==24&&area.Width==760)SaveImage(form,"weather-"+language+"-24-short");
            form.Close();
        }
    }
    static void Modal(Control creation,Form host,string method,string expected,int language,float size,Rectangle area)
    {
        Exception failure=null;bool found=false;
        using(var timer=new Timer{Interval=30})
        {
            timer.Tick+=delegate
            {
                var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name==expected&&f.Owner==host);
                if(dialog==null)return;timer.Stop();found=true;
                try
                {
                    // Change font after Shown, matching the original clipping scenario.
                    using(var font=new Font("Microsoft YaHei UI",size))
                    {
                        dialog.Font=font;Pump();Fit(dialog,area,new Size(460,310));Contained(dialog,area);
                        Check(dialog.Icon!=null,"owned branded modal: "+expected);
                        if(expected=="EnvironmentLimitsDialog")
                        {
                            var body=Find<TableLayoutPanel>(dialog,"EnvironmentLimitsBody");Check(body.AutoScroll,"environment limits body scrollable");
                            Actions(dialog,Find<FlowLayoutPanel>(dialog,"EnvironmentLimitsActions"),2);
                            foreach(Control text in body.Controls)
                            {
                                if(!(text is Label)&&!(text is CheckBox))continue;
                                int width=Math.Max(1,text.Width-(text is CheckBox?Math.Max(24,text.Font.Height+8):0));
                                int required=TextRenderer.MeasureText(text.Text,text.Font,new Size(width,10000),TextFormatFlags.WordBreak).Height;
                                Check(text.Height>=required,"full environment label at "+size+": "+text.Text);
                            }
                            Check(Find<Button>(dialog,"SaveEnvironmentLimits").Text==(language==18?Traditional("保存"):language==0?"保存":"Save"),"limits action language");
                            Control popup=body.GetControlFromPosition(0,4);body.ScrollControlIntoView(popup);Pump();
                            Check(body.RectangleToScreen(body.ClientRectangle).Contains(popup.RectangleToScreen(popup.ClientRectangle)),"out-of-range option reachable by body scrolling");
                        }
                        else
                        {
                            var content=Find<TextBox>(dialog,"InsulationReferenceText");var close=Find<Button>(dialog,"CloseInsulationReference");
                            Check(content.ReadOnly&&content.Multiline&&content.ScrollBars==ScrollBars.Vertical&&content.Text.Length>100,"reference remains complete and scrollable");
                            Check(content.Font.SizeInPoints==size&&close.Font.SizeInPoints==size,"reference body and close inherit current font");
                            Check(dialog.ClientRectangle.Contains(close.Bounds)&&close.Height>=close.Font.Height+8,"reference close reachable");
                            Check(close.Text==(language==18?Traditional("关闭"):language==0?"关闭":"Close"),"reference close language");
                        }
                        if(size==24&&area.Width==760)SaveImage(dialog,expected+"-"+language+"-24-short");
                        dialog.DialogResult=DialogResult.Cancel;dialog.Close();
                    }
                }
                catch(Exception e){failure=e;dialog.DialogResult=DialogResult.Cancel;dialog.Close();}
            };
            timer.Start();Call(creation,method);timer.Stop();
        }
        Check(found,"expected QA-owned modal was shown: "+expected);
        if(failure!=null)throw failure;
    }
    static void Shared(int language,float size,Rectangle area,object snapshot,byte[] original,string cable)
    {
        using(var form=(Form)Activator.CreateInstance(assembly.GetType("HVX.ProjectCables.CableJointDialog",true),Any,null,new object[]{snapshot,100d,"ALL",language,null},CultureInfo.InvariantCulture))
        using(var font=new Font("Microsoft YaHei UI",size))
        {
            form.Show();Pump();form.Font=font;Pump();Call(form,"FitToWorkingArea",area);Pump();Contained(form,area);
            Actions(form,Find<FlowLayoutPanel>(form,"JointActions"),4);
            Check(Find<Panel>(form,"JointEditorViewport").AutoScroll,"joint editor remains scrollable");
            var validation=Find<TextBox>(form,"JointValidation");Check(validation.Height==0,"empty validation does not reserve unused space");
            if(language==18)
            {
                Check(Find<Button>(form,"ConfirmJoint").Text==Traditional("确认添加"),"joint traditional action");
                Check(form.Text==Traditional("电缆接头 · 新增 / 编辑"),"joint traditional title");
            }
            Find<TextBox>(form,"JointName").Text="用户输入 后台 皇后";
            Check(Find<TextBox>(form,"JointName").Text=="用户输入 后台 皇后","language conversion does not alter user input");
            object[] prepareArgs={null};
            Check((bool)form.GetType().GetMethod("TryPrepare",Any).Invoke(form,prepareArgs),"UI preparation succeeds without a commit: "+prepareArgs[0]);
            object plan=Get(form,"Plan");var targets=(Array)plan.GetType().GetField("ResolvedTargets",Any).GetValue(plan);
            foreach(object target in targets)Check((string)target.GetType().GetField("MaterialBefore",Any).GetValue(target)=="XLPE"&&
                (string)target.GetType().GetField("MaterialAfter",Any).GetValue(target)=="XLPE","language changes preserve actual plan material, not merely its display caption");
            string failure=String.Concat(Enumerable.Repeat("Not saved: the selected calibration source changed while validation was pending. Reload the records and review the joint before saving again. ",8))+"D:/QA/原始文件 后台 皇后/source.cal";
            validation.Text=failure;Pump();Actions(form,Find<FlowLayoutPanel>(form,"JointActions"),4);
            Check(validation.ReadOnly&&validation.Multiline&&validation.WordWrap&&validation.ScrollBars==ScrollBars.Vertical&&validation.ShortcutsEnabled,"long validation is a read-only scrollable copyable text area");
            Check(form.ClientRectangle.Contains(validation.Bounds),"error body remains in the working-area-fitted window");
            Check(validation.Text==failure,"long error and source path remain exact, including user text");
            validation.SelectAll();Check(validation.SelectedText==failure,"full failure can be selected for copying without changing the clipboard");
            validation.Focus();validation.SelectionStart=validation.TextLength;validation.SelectionLength=0;validation.ScrollToCaret();Pump();
            Point last=validation.GetPositionFromCharIndex(validation.TextLength-1);
            Check(last.Y>=0&&last.Y<validation.ClientSize.Height,"last failure line is reachable by scrolling at "+size);
            Check(Get(form,"Plan")!=null,"presenting an error does not mutate the prepared plan");
            Type planType=plan.GetType();
            object callback=typeof(ProjectCreationDialogBoundarySmoke).GetMethod("FailedCommit",Any).MakeGenericMethod(planType).Invoke(null,new object[]{failure});
            Call(form,"ConfigureCommitAsync",callback);Find<Button>(form,"ConfirmJoint").PerformClick();Pump();
            Check(!(bool)Get(form,"IsCommitPending")&&Get(form,"Plan")==null&&form.Visible,"faulted async callback stays open with no successful plan");
            Check(validation.Enabled&&validation.Text.EndsWith(failure,StringComparison.Ordinal),"actual failed-callback text is preserved and restored for reading");
            Check(Find<TextBox>(form,"JointName").Enabled&&Find<TextBox>(form,"JointName").Text=="用户输入 后台 皇后","failed callback preserves editable user input");
            validation.SelectAll();Check(validation.SelectedText==validation.Text,"actual failure is fully selectable");
            Actions(form,Find<FlowLayoutPanel>(form,"JointActions"),4);
            if(size==24&&area.Width==760)SaveImage(form,"joint-"+language+"-24-short");
            form.Close();Check(File.ReadAllBytes(cable).SequenceEqual(original),"opening/closing does not write metadata");
        }
    }
    static object FailedCommit<T>(string failure)
    {
        return new Func<T,string,System.Threading.CancellationToken,System.Threading.Tasks.Task>((plan,operation,token)=>
        {
            var done=new System.Threading.Tasks.TaskCompletionSource<bool>();done.SetException(new IOException(failure));return done.Task;
        });
    }
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding=Encoding.UTF8;Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            assembly=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll"));
            output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            string fixture=Path.Combine(output,"synthetic-joint");Directory.CreateDirectory(fixture);
            File.WriteAllText(Path.Combine(fixture,"ITEM.in"),"[ITEM]\r\n电缆芯数: = \"一体三芯\"\r\n",new UTF8Encoding(false));
            string cable=Path.Combine(fixture,"CABLE.in");File.WriteAllText(cable,"0\tALL\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t户内冷缩\t400m\r\n",new UTF8Encoding(false));
            byte[] original=File.ReadAllBytes(cable);object snapshot=assembly.GetType("HVX.ProjectCables.CableJointStore",true).GetMethod("Read",Any).Invoke(null,new object[]{fixture});
            using(var host=new Form{Name="BoundaryQaHost",ClientSize=new Size(1000,700),ShowInTaskbar=false})
            using(var creation=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.ProjectCreationControl",true)))
            {
                Set(creation,"ShowDialogMessages",false);Set(creation,"OnlineWeatherEnabled",false);Set(creation,"AutoRefreshWeatherOnHardwareSample",false);
                creation.Dock=DockStyle.Fill;host.Controls.Add(creation);host.Show();Pump();
                Check(!(bool)Get(creation,"HasValidGnssFix"),"fresh project does not claim valid GPS");
                foreach(string name in new[]{"latitudeValue","longitudeValue","altitudeValue"})Check(((Label)creation.GetType().GetField(name,Any).GetValue(creation)).Text=="—","fresh GPS empty state: "+name);
                Check((bool)Call(creation,"ApplyGnssFix",0d,0d,0d,new DateTime(2026,10,2,0,0,0,DateTimeKind.Utc)),"genuine zero coordinate accepted");
                Check((bool)Get(creation,"HasValidGnssFix")&&(double)Get(creation,"BeidouLatitude")==0&&(double)Get(creation,"BeidouLongitude")==0&&(double)Get(creation,"BeidouAltitude")==0,"genuine zeros remain valid measurements");
                Call(creation,"ReportGnssAcquisitionResult","QA no fix",false);
                foreach(string name in new[]{"latitudeValue","longitudeValue","altitudeValue"})Check(((Label)creation.GetType().GetField(name,Any).GetValue(creation)).Text=="—","invalid GPS empty state: "+name);
                foreach(int language in new[]{0,1,18})
                {
                    Set(creation,"LanguageIndex",language);
                    foreach(float size in new[]{9.5f,14.25f,18f,24f})
                    foreach(Rectangle area in new[]{new Rectangle(10,10,1280,680),new Rectangle(10,10,760,500)})
                    {
                        Weather(language,size,area);
                        Modal(creation,host,"ShowEnvironmentLimits","EnvironmentLimitsDialog",language,size,area);
                        Modal(creation,host,"ShowInsulationReferenceDetails","InsulationReferenceDialog",language,size,area);
                        Shared(language,size,area,snapshot,original,cable);
                        Console.WriteLine("CASE_PASS="+language+"/"+size+"/"+area.Size);
                    }
                }
                host.Close();
            }
            foreach(bool english in new[]{false,true})using(var old=(Form)Activator.CreateInstance(assembly.GetType("HVX.ProjectCables.CableJointDialog",true),Any,null,new object[]{snapshot,100d,"ALL",english,null},CultureInfo.InvariantCulture))
                Check(old.Text==(english?"Cable joints · Add / edit":"电缆接头 · 新增 / 编辑"),"legacy bool constructor retained");
            Console.WriteLine("PROJECT_CREATION_DIALOG_BOUNDARY_PASS="+checks);Console.WriteLine("MODULE_SHA256="+HashFile(assembly.Location));return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);Console.WriteLine("PROJECT_CREATION_DIALOG_BOUNDARY_FAIL="+checks);return 1;}
    }
    static string HashFile(string path){using(var stream=File.OpenRead(path))using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
}
