using System;
using System.Collections;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class DeviceSetupStatusSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Type statusType,settingsType,snapshotType,formType;
    static int checks;static float dpi;
    static object Get(object owner,string name){return owner.GetType().GetProperty(name,All).GetValue(owner,null);}
    static void Set(object owner,string name,object value){owner.GetType().GetProperty(name,All).SetValue(owner,value,null);}
    static object Field(object owner,string name){return owner.GetType().GetField(name,All).GetValue(owner);}
    static object Call(object owner,string name,params object[] args)
    {Type type=owner as Type??owner.GetType();return type.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(owner is Type?null:owner,args);}
    static void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
    static bool Saved(object settings){return (bool)Call(statusType,"ParametersSaved",settings);}
    static bool Matches(object settings,object snapshot){return (bool)Call(statusType,"IdentityMatches",settings,snapshot);}
    static object Snapshot(string pico="AB123/4567",string art="1234567890",string mac="1CBCEC5A9C5C")
    {
        object snapshot=Activator.CreateInstance(snapshotType,true);
        foreach(string name in new[]{"HardwareEnabled","DriverInstalled","Connected","IdentityValid"})Set(snapshot,name,true);
        Set(snapshot,"StatusCode","Ready");Set(snapshot,"HubMac",mac);Set(snapshot,"ControllerSerial",pico);Set(snapshot,"AcquisitionSerial",art);Set(snapshot,"AssetMatchStatus","Unknown");
        Set(snapshot,"CheckedUtc",DateTime.UtcNow);return snapshot;
    }
    static object Settings()
    {
        object settings=Activator.CreateInstance(settingsType,true);object hardware=Get(settings,"Hardware");
        Set(hardware,"Enabled",true);Set(hardware,"SelectedHubMac","1CBCEC5A9C5C");
        Set(hardware,"ControllerLocation","H01");Set(hardware,"AcquisitionLocation","S01");return settings;
    }
    static void Reject(Action action,string name){try{action();}catch(TargetInvocationException ex){Check(ex.InnerException is ArgumentException,name);return;}throw new InvalidOperationException(name);}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            if(args.Length!=2)throw new ArgumentException("Pass release root and QA output directory.");
            string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Assembly app=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"Xiongxin.OWTS.Desktop.exe"));
            statusType=app.GetType("Xiongxin.OWTS.Desktop.DeviceSetupStatus",true);settingsType=app.GetType("Xiongxin.OWTS.Desktop.AppSettings",true);
            snapshotType=app.GetType("Xiongxin.OWTS.Desktop.HardwareStatusSnapshot",true);formType=app.GetType("Xiongxin.OWTS.Desktop.DeviceSetupForm",true);
            object settings=Settings(),actual=Snapshot();
            Check(!Saved(settings),"Defaults are not silently confirmed");
            Check(!Matches(settings,actual),"Unreviewed device cannot match parameters");
            string first=(string)Call(statusType,"ParameterHash",settings);
            CultureInfo prior=Thread.CurrentThread.CurrentCulture;Thread.CurrentThread.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
            Check(first==(string)Call(statusType,"ParameterHash",settings),"Fingerprint culture invariant");Thread.CurrentThread.CurrentCulture=prior;
            foreach(string property in new[]{"DividerRatio","PressureRatio","PdSampleRateHz","PdCalibrationCorrectionFactor","InductanceHenry","InductorDcResistanceOhm","MaximumVoltageKv","OscillatingPdCaptureDurationMs"})
            {object copy=Call(settings,"Clone");Set(copy,property,(double)Get(copy,property)+.25);Check(first!=(string)Call(statusType,"ParameterHash",copy),"Fingerprint covers "+property);}
            foreach(string property in new[]{"SupportsCosineVlf","SupportsCombinedVlf"})
            {object copy=Call(settings,"Clone");Set(copy,property,!(bool)Get(copy,property));Check(first!=(string)Call(statusType,"ParameterHash",copy),"Fingerprint covers "+property);}
            object bits=Call(settings,"Clone");Set(bits,"PdSampleBits",14);Check(first!=(string)Call(statusType,"ParameterHash",bits),"Fingerprint covers sample bits");
            object source=Call(settings,"Clone");Set(source,"HvupVoltageSource",Enum.ToObject(Get(source,"HvupVoltageSource").GetType(),1));Check(first!=(string)Call(statusType,"ParameterHash",source),"Fingerprint covers HVUP source");
            foreach(string property in new[]{"SelectedHubMac","ControllerLocation","AcquisitionLocation","ControllerSerial","AcquisitionSerial","ExcludedAcquisitionSerial"})
            {object copy=Call(settings,"Clone");Set(Get(copy,"Hardware"),property,(string)Get(Get(copy,"Hardware"),property)+"A");Check(first!=(string)Call(statusType,"ParameterHash",copy),"Fingerprint covers hardware field "+property);}
            object appearance=Call(settings,"Clone");Set(appearance,"SoftwareModel","Other branding");Set(appearance,"LanguageCode","en");Check(first==(string)Call(statusType,"ParameterHash",appearance),"Display preferences do not invalidate device review");
            object collisionA=Call(settings,"Clone"),collisionB=Call(settings,"Clone");
            Set(Get(collisionA,"Hardware"),"ControllerLocation","A|B");Set(Get(collisionA,"Hardware"),"AcquisitionLocation","C");
            Set(Get(collisionB,"Hardware"),"ControllerLocation","A");Set(Get(collisionB,"Hardware"),"AcquisitionLocation","B|C");
            Check((string)Call(statusType,"ParameterHash",collisionA)!=(string)Call(statusType,"ParameterHash",collisionB),"Fingerprint strings cannot collide through separators");
            Call(statusType,"ConfirmParameters",settings,actual);Check(Saved(settings)&&Matches(settings,actual),"Confirmed valid parameters match full actual identity");
            Check(Matches(settings,Snapshot("ab123／4567","1234567890","1c:bc:ec:5a:9c:5c")),"Identity normalization handles case, serial slash and MAC punctuation");
            Check(Matches(settings,Snapshot("1234567890","AB123/4567")),"Pico and ART discovered at swapped connector roles remain same identity");
            foreach(object missing in new[]{Snapshot("","1234567890"),Snapshot("H11","S51"),Snapshot("DS600","1234567890"),Snapshot("AB123/4567",""),Snapshot("AB123/4567","1234567890","")})
            {
                object copy=Call(settings,"Clone");Call(statusType,"ConfirmParameters",copy,missing);
                Check(Saved(copy),"Parameters can be saved while actual identity incomplete");Check(!Matches(copy,missing),"Incomplete serial or MAC never binds");
                Check(String.IsNullOrEmpty((string)Get(copy,"DeviceParametersReviewedIdentity")),"No port or note substituted for actual identity");
            }
            object changed=Snapshot("AB124/4567");Check((bool)Call(statusType,"IdentityChanged",settings,changed)&&!Matches(settings,changed),"Changed actual device requires new review");
            object otherHub=Snapshot("AB123/4567","1234567890","AABBCCDDEEFF"),copyHub=Call(settings,"Clone");Call(statusType,"ConfirmParameters",copyHub,otherHub);
            Check(Saved(copyHub)&&!Matches(copyHub,otherHub)&&String.IsNullOrEmpty((string)Get(copyHub,"DeviceParametersReviewedIdentity")),"Different actual hub can never bind the candidate selected hub");
            object disconnected=Snapshot();Set(disconnected,"Connected",false);Check(!Matches(settings,disconnected),"Disconnected snapshot never confirms current association");
            object offline=Call(settings,"Clone");Set(Get(offline,"Hardware"),"Enabled",false);Check(Saved(offline)&&!Matches(offline,actual),"Offline mode retains parameters but not live confirmation");
            object modified=Call(settings,"Clone");Set(modified,"DividerRatio",13d);Check(!Saved(modified),"Changing reviewed parameters requires re-review");
            object future=Call(settings,"Clone");Set(future,"DeviceParametersReviewedUtc",DateTime.UtcNow.AddDays(1).ToString("o"));Check(!Saved(future),"Future review timestamp rejected");
            object invalid=Call(settings,"Clone");Set(invalid,"DividerRatio",double.NaN);string previousHash=(string)Get(invalid,"DeviceParametersReviewedHash");
            Reject(()=>Call(statusType,"ConfirmParameters",invalid,actual),"Invalid numeric input rejected before confirmation");Check(previousHash==(string)Get(invalid,"DeviceParametersReviewedHash"),"Rejected confirmation leaves previous metadata unchanged");
            string savedFile=Path.Combine(output,"device-setup-settings-"+Guid.NewGuid().ToString("N")+".xml");
            Call(settings,"Save",savedFile);object reloaded=Call(settingsType,"Load",savedFile);Check(Saved(reloaded)&&Matches(reloaded,actual),"Confirmation survives real QA-only save/reload");
            foreach(bool chinese in new[]{true,false})
            {
                IList steps=(IList)Call(statusType,"Steps",settings,actual,true,false,chinese);
                Check(steps.Count==9,"Exactly nine steps");
                for(int i=0;i<9;i++)Check(((string)Field(steps[i],"Name")).StartsWith((i+1)+".")&&!String.IsNullOrWhiteSpace((string)Field(steps[i],"Detail")),"Numbered step has evidence and next action");
                Check(!(bool)Field(steps[4],"Complete")&&(bool)Field(steps[5],"Complete")&&(bool)Field(steps[6],"Complete"),"Unknown factory record does not erase parameter save or manual verified association");
                Check(!(bool)Field(steps[8],"Complete"),"Setup never grants live HV permission");
                IList checking=(IList)Call(statusType,"Steps",settings,actual,true,true,chinese);
                foreach(int i in new[]{2,3,4,6})Check(!(bool)Field(checking[i],"Complete"),"Connection recheck does not present stale live completion");
                IList offlineSteps=(IList)Call(statusType,"Steps",offline,actual,true,false,chinese);
                foreach(int i in new[]{0,1,2,3,4,6})Check(!(bool)Field(offlineSteps[i],"Complete"),"Offline settings suppress stale Ready completion");
                Set(reloaded,"LanguageCode",chinese?"zh-CN":"en");
                using(Form form=(Form)Activator.CreateInstance(formType,All,null,new object[]{reloaded,actual,true,false},CultureInfo.InvariantCulture))
                {
                    form.Show();Application.DoEvents();using(Graphics g=form.CreateGraphics())dpi=g.DpiX;
                    Check(!form.Text.Contains("XENSION")&&form.Text.Contains("HV DAC Xpert"),"Customer-facing title uses product series");Check(form.Icon!=null,"Own setup window has brand icon");
                    Label intro=(Label)form.Controls.Find("SetupIntroduction",true)[0];
                    Check(intro.Height>=TextRenderer.MeasureText(intro.Text,intro.Font,new Size(intro.Width,int.MaxValue),TextFormatFlags.WordBreak).Height,"Full introduction fits at actual DPI");
                    DataGridView grid=(DataGridView)form.Controls.Find("DeviceSetupSteps",true)[0];Check(grid.Rows.Count==9&&grid.ReadOnly,"Status table is read-only and complete");Check(grid.ScrollBars==ScrollBars.Vertical,"No horizontal scrollbar");
                    foreach(Control section in new Control[]{intro,grid,form.Controls.Find("SetupActions",true)[0]})
                    {Check(section.Parent.ClientRectangle.Contains(section.Bounds),"Table section stays inside its parent: "+section.Name);Rectangle inForm=form.RectangleToClient(section.Parent.RectangleToScreen(section.Bounds));Check(form.ClientRectangle.Contains(inForm),"Table section visible in form: "+section.Name);}
                    foreach(string action in new[]{"CloseSetup","ReviewParameters","RecheckConnection"})
                    {Control button=form.Controls.Find(action,true)[0];Check(button.Parent.ClientRectangle.Contains(button.Bounds),"Action fits: "+action);Check(button.Width>=TextRenderer.MeasureText(button.Text,button.Font).Width+12,"Action caption complete: "+action);}
                    Rectangle area=Screen.FromControl(form).WorkingArea;Check(area.Contains(form.Bounds),"Window fits actual working area");
                    using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(output,"device-setup-"+(chinese?"zh":"en")+"-"+dpi.ToString("0",CultureInfo.InvariantCulture)+"dpi.png"));}
                    form.Close();
                }
            }
            Console.WriteLine("DEVICE_SETUP_STATUS_PASS="+checks+"; DPI="+dpi+"; HARDWARE_COMMANDS=0");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
