using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Pure real-control/reflection tests. No product entry, device calls, user-settings
// save, stored credentials, or successful-password substitute is used.
static class MaintenanceAdvancedAccessPublishedSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static Assembly host;
    static Type H(string name){return host.GetType("Xiongxin.OWTS.Desktop."+name,true);}
    static object Make(Type type,params object[] values){return Activator.CreateInstance(type,All & ~BindingFlags.Static,null,values,null);}
    static object Call(object obj,string name,params object[] values){Type t=obj as Type??obj.GetType();return t.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==values.Length).Invoke(obj is Type?null:obj,values);}
    static object Get(object obj,string name){return obj.GetType().GetProperty(name,All).GetValue(obj,null);}
    static void Set(object obj,string name,object value){obj.GetType().GetProperty(name,All).SetValue(obj,value,null);}
    static object Field(object obj,string name){return obj.GetType().GetField(name,All).GetValue(obj);}
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static bool Enabled(Type type){return (bool)type.GetField("Enabled",All).GetRawConstantValue();}
    static void Pump(Form f){f.PerformLayout();Application.DoEvents();}
    static void Ui(string root,string fixture)
    {
        Check(Enabled(H("SettingsAccessControl")),"Actual host was compiled with maintenance guard");
        foreach(string language in new[]{"zh-CN","en","zh-TW"})
        {
            var settings=Make(H("AppSettings"));Set(settings,"LanguageCode",language);Set(settings,"DividerRatio",12500d);
            using(var form=(Form)Make(H("SettingsForm"),settings))
            {
                Set(form,"AuditEnabled",false);
                form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Pump(form);
                Check((bool)Call(form,"IsSectionAuthorized",Enum.Parse(H("SettingsSection"),"General")),"Routine settings remain password-free");
                Check(Field(form,"dataRoot") is TextBox&&Field(form,"reportOutputPath") is TextBox,"Routine editors remain available");
                foreach(string name in new[]{"Device","Expert","Oem","SystemAdmin","PlusData"})
                {
                    var section=Enum.Parse(H("SettingsSection"),name);
                    Check(!(bool)Call(H("SettingsAccessControl"),"Verify",section,null),"Empty credential denied");
                    string wrong=Guid.NewGuid().ToString("N");
                    Check(!(bool)Call(H("SettingsAccessControl"),"Verify",section,wrong),"Incorrect credential denied");
                    Call(form,"SelectSection",section);Call(form,"OpenSection",section);Pump(form);
                    Check(!(bool)Call(form,"IsSectionAuthorized",section),"Selected/direct OpenSection does not authorize");
                    Check(form.Controls.Find("UnlockSettings"+name,true).Length==1,"Locked page has explicit authorization entry");
                    string field=name=="Device"?"dividerRatio":name=="Expert"?"minus3Attenuation":name=="Oem"?"softwareModel":name=="SystemAdmin"?"hardwareEnabled":"minus3AlgorithmIndex";
                    Check(Field(form,field)==null,"No protected editor before authorization");
                    Check(!(bool)Call(form,"TryUnlockSection",section,wrong),"Wrong direct unlock denied");
                    Check(!(bool)Call(form,"TryUnlockSection",section,null),"Canceled/absent credential denied");
                    Check(Field(form,field)==null&&! (bool)Call(form,"IsSectionAuthorized",section),"Rejected attempt leaves page locked");
                    Check(form.DialogResult==DialogResult.None,"Navigation/rejection does not save settings");
                    var captured=Call(form,"CaptureValues");
                    Check((double)Get(captured,"DividerRatio")==12500d,"Capture preserves unavailable device values");
                    using(var prompt=(Form)Call(form,"CreateAuthorizationDialog",section))
                    {
                        var input=(TextBox)prompt.Controls.Find("AuthorizationPassword",true).Single();
                        Check(input.UseSystemPasswordChar&&input.MaxLength>0,"Password input is masked/bounded");
                        Check(prompt.CancelButton!=null&&prompt.DialogResult==DialogResult.None,"Prompt construction is not an authorization");
                        Check(input.Text.Length==0,"Prompt never pre-populates stored credentials");
                        if(language=="en")Check(!prompt.Text.Any(c=>c>='\u4e00'&&c<='\u9fff'),"English authorization caption");
                        if(language=="zh-TW")Check(!prompt.Text.Contains("关键")&&!prompt.Text.Contains("维护"),"Traditional authorization caption");
                    }
                }
                // Model only the post-verification window grant, without bypassing
                // the actual verifier or claiming a correct human password test.
                var grants=Field(form,"maintenanceGrants");var device=Enum.Parse(H("SettingsSection"),"Device");
                Call(grants,"Add",device);Check((bool)Call(form,"IsSectionAuthorized",device),"Authorization state is local to current window");
                var clone=Call(settings,"Clone");
                using(var next=(Form)Make(H("SettingsForm"),clone))
                {Set(next,"AuditEnabled",false);Check(!(bool)Call(next,"IsSectionAuthorized",device),"Settings clone/reopened window carries no grant");}
                form.Dispose();Check(!(bool)Call(form,"IsSectionAuthorized",device),"Disposed window cannot use an old grant");
            }
        }
        object[] promptArgs={null,Enum.Parse(H("SettingsSection"),"Expert"),"en",null};
        TextBox canceledInput=null;bool seen=false;
        using(var timer=new Timer{Interval=30})
        {
            timer.Tick+=(s,e)=>{var p=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name=="MaintenanceAuthorizationDialog");if(p==null)return;timer.Stop();canceledInput=(TextBox)p.Controls.Find("AuthorizationPassword",true).Single();canceledInput.Text=Guid.NewGuid().ToString("N");seen=true;p.DialogResult=DialogResult.Cancel;p.Close();};
            timer.Start();bool read=(bool)H("MaintenanceAuthorizationPrompt").GetMethod("TryRead",All).Invoke(null,promptArgs);
            Check(seen&&!read&&promptArgs[3]==null,"Actual modal cancellation denies and returns no credential");
            Check(canceledInput.Text.Length==0,"Canceled input is cleared before disposal");
        }
        var properties=H("AppSettings").GetProperties(All).Where(p=>p.CanRead&&p.CanWrite).Select(p=>p.Name).ToArray();
        Check(!properties.Any(p=>p.IndexOf("Password",StringComparison.OrdinalIgnoreCase)>=0||p.IndexOf("MaintenanceGrant",StringComparison.OrdinalIgnoreCase)>=0),"Legacy settings schema has no password/grant persistence");
    }
    static void Device(string fixture)
    {
        Check(!(bool)Call(H("SettingsAccessControl"),"RequiresDeviceAuthorization",true),"First configuration remains password-free");
        Check((bool)Call(H("SettingsAccessControl"),"RequiresDeviceAuthorization",false),"Configured device editing requires authorization");
        var settings=Make(H("AppSettings"));Set(settings,"DataRoot",fixture);var hardware=Get(settings,"Hardware");
        Set(hardware,"Enabled",true);Set(hardware,"SelectedHubMac","001122334455");Set(hardware,"ControllerSerial","0000000001");Set(hardware,"AcquisitionSerial","QA000/0001");Set(hardware,"ControllerLocation","QA-ART");Set(hardware,"AcquisitionLocation","QA-PICO");
        var actual=Make(H("HardwareStatusSnapshot"));foreach(var name in new[]{"HardwareEnabled","DriverInstalled","Connected","IdentityValid","FromLiveIntegrationProcess"})Set(actual,name,true);
        Set(actual,"StatusCode","Ready");Set(actual,"HubMac","001122334455");Set(actual,"ControllerSerial","0000000001");Set(actual,"AcquisitionSerial","QA000/0001");Set(actual,"CheckedUtc",DateTime.UtcNow);
        var state=Make(H("ApplicationState"));Call(state,"SetHardware",actual);
        using(var form=(Form)Make(H("MainForm"),settings,state,true))
        {
            form.GetType().GetField("DeviceParameterSettingsPathForTests",All).SetValue(form,Path.Combine(fixture,"never-written.xml"));
            // A null editor cancels, and never saves a QA/user settings file.
            var delegateType=form.GetType().GetField("DeviceParameterEditorForTests",All).FieldType;
            var counter=typeof(DeviceCounter<>).MakeGenericType(H("AppSettings"));var instance=Activator.CreateInstance(counter);
            var editor=Delegate.CreateDelegate(delegateType,instance,counter.GetMethod("Edit"));
            form.GetType().GetField("DeviceParameterEditorForTests",All).SetValue(form,editor);
            Check(!((System.Threading.Tasks.Task<bool>)Call(form,"EditDeviceParameters")).GetAwaiter().GetResult(),"First configuration can be canceled without save");
            Check((int)counter.GetField("Calls").GetValue(instance)==1,"First configuration reaches editor without password");
            Call(H("DeviceParameterProfiles"),"Confirm",settings,actual);
            Check(!((System.Threading.Tasks.Task<bool>)Call(form,"EditDeviceParameters")).GetAwaiter().GetResult(),"Configured direct edit without authorization denied");
            Check((int)counter.GetField("Calls").GetValue(instance)==1,"Denied configured edit never reaches editor");
            Check(!File.Exists(Path.Combine(fixture,"never-written.xml")),"Canceled/denied device entry writes no settings");
        }
    }
    public sealed class DeviceCounter<T>{public int Calls;public T Edit(T draft,string pair,bool first){Calls++;return default(T);}}
    static void Analysis(string root,string fixture)
    {
        var analysis=Assembly.LoadFrom(Path.Combine(root,"modules/DataAnalysis/HVX.PdReview.V1.0.dll"));
        var security=analysis.GetType("HVX.PdReview.ReviewAdvancedSettingsSecurity",true);Check(Enabled(security),"Actual analysis module maintenance guard matches host");
        Check(!(bool)Call(security,"VerifyPassword",(object)null)&&!(bool)Call(security,"VerifyPassword",Guid.NewGuid().ToString("N")),"Analysis empty/incorrect credential denied");
        analysis.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(fixture,"never-preferences.json")));
        var reviewType=analysis.GetType("HVX.PdReview.PdReviewControl",true);var dialogType=analysis.GetType("HVX.PdReview.ReviewAnalysisSettingsDialog",true);
        using(var review=(Control)Make(reviewType))
        {
            Set(review,"EnableAiFeatures",false);
            foreach(int lang in new[]{0,1,18})foreach(bool managed in new[]{false,true})
            {
                Set(review,"LanguageIndex",lang);Set(review,"ProtectedAnalysisSettingsManaged",managed);
                using(var dialog=(Form)Make(dialogType,review))
                {
                    Check(!(bool)Get(dialog,"AdvancedUnlocked"),"Analysis advanced settings initially locked");
                    Check(!((Control)Field(dialog,"algorithm")).Enabled,"Algorithm editor is not authorized by construction");
                    Call(dialog,"LayoutAdvancedControls");Call(dialog,"LayoutAdvancedControls");
                    Check(!(bool)Get(dialog,"AdvancedUnlocked"),"Layout cannot grant analysis authorization");
                    Check(!(bool)Call(dialog,"TryUnlockAdvanced",Guid.NewGuid().ToString("N")),"Direct analysis unlock with wrong credential denied");
                    Check(!(bool)Call(dialog,"TryUnlockAdvanced",(object)null),"Canceled analysis unlock denied");
                    Check(((Control)Field(dialog,"threshold")).Enabled,"Routine analysis threshold remains editable");
                    if(!managed)
                    {
                        var input=(TextBox)Field(dialog,"password");Check(input!=null&&input.UseSystemPasswordChar,"Standalone analysis has masked original advanced lock");
                        input.Text=Guid.NewGuid().ToString("N");Call(dialog,"UnlockAdvanced");Check(input.Text.Length==0&&!(bool)Get(dialog,"AdvancedUnlocked"),"Wrong analysis input is cleared without grant");
                    }
                    else Check(Field(dialog,"password")==null&&!((Control)Field(dialog,"advancedGroup")).Visible,"Host-owned advanced settings remain unavailable here");
                    dialog.Dispose();
                }
            }
            Check(!File.Exists(Path.Combine(fixture,"never-preferences.json")),"Opening/rejecting/canceling analysis settings writes no preferences");
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            var root=Path.GetFullPath(args[0]);var fixture=Path.GetFullPath(args[1]);if(Directory.Exists(fixture))throw new IOException("Use a fresh QA fixture.");Directory.CreateDirectory(fixture);
            host=Assembly.LoadFrom(args.Length>2?Path.GetFullPath(args[2]):Path.Combine(root,"Xiongxin.OWTS.Desktop.exe"));
            Ui(root,fixture);Device(fixture);Analysis(root,fixture);
            Console.WriteLine("MAINTENANCE_ADVANCED_ACCESS_PASS="+checks+"; CancellationDenied=True; InvalidPasswordDenied=True; DirectEntryGuarded=True; RoutineEntryPasswordFree=True; HardwareAccessed=False; ProductExecuted=False; UserSettingsWritten=False; CredentialMaterialIncluded=False; KnownCorrectCredentialEntered=False");return 0;
        } catch(Exception error) {Console.Error.WriteLine(error);return 1;}
    }
}
