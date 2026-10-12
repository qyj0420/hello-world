using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

// Only disposable fixtures beneath the supplied QA directory are modified.
// No preferences, device coordinator or real project is used.
internal static class CalibrationJointReviewSafetySmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static Type store;static int checks;static bool probe;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static object Call(object target,string name,params object[] args){return target.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target,args);}
    static string Hash(byte[] bytes){using(var algorithm=SHA256.Create())return Convert.ToBase64String(algorithm.ComputeHash(bytes));}
    static string Fixture(string root,string name)
    {
        string path=Path.Combine(root,name+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path,"ITEM.in"),"[ITEM]\r\n电缆芯数=单相\r\nA相电缆长度=180\r\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(path,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\tXLPE\t\r\n1\t接头 1\t校准识别\t80m\r\n2\t本体\tXLPE\t\r\n1\t终端\t终端\t180m\r\n",new UTF8Encoding(false));
        return path;
    }
    static void Review(string root,string expectedHash)
    {
        object[] args=probe?new object[]{root,new[]{"A"},new double[0],new[]{80d}}:new object[]{root,new[]{"A"},new double[0],new[]{80d},expectedHash};
        store.GetMethods(All).Single(m=>m.Name=="ReviewJoints"&&m.GetParameters().Length==args.Length).Invoke(null,args);
    }
    static bool Rejected(string root,string hash)
    {
        try{Review(root,hash);return false;}
        catch(TargetInvocationException e){if(e.InnerException is IOException)return true;throw;}
    }
    static void ReviewConflict(string root)
    {
        string fixture=Fixture(root,"stale-review"),cable=Path.Combine(fixture,"CABLE.in");
        byte[] before=File.ReadAllBytes(cable);string expected=Hash(before);
        File.WriteAllText(cable,File.ReadAllText(cable).Replace("接头 1","外部维护接头"),new UTF8Encoding(false));
        byte[] external=File.ReadAllBytes(cable);bool rejected=Rejected(fixture,expected);
        if(probe) {
            Check(!rejected&&!File.ReadAllBytes(cable).SequenceEqual(external)&&!File.ReadAllText(cable).Contains("外部维护接头"),"baseline stale review deletes externally updated generated joint");
            Console.WriteLine("CONFIRMED_BASELINE_STALE_REVIEW_REMOVES_EXTERNAL_JOINT=1");return;
        }
        Check(rejected,"stale modal snapshot rejected before a write");
        Check(File.ReadAllBytes(cable).SequenceEqual(external),"external CABLE.in preserved byte-for-byte");
        Check(Directory.GetFiles(fixture).Length==2,"stale rejection produces no backup, temporary file or lock");
        fixture=Fixture(root,"unchanged-review");cable=Path.Combine(fixture,"CABLE.in");
        Review(fixture,Hash(File.ReadAllBytes(cable)));Check(!File.ReadAllText(cable).Contains("校准识别"),"unchanged snapshot still saves review");
        fixture=Fixture(root,"late-review");cable=Path.Combine(fixture,"CABLE.in");expected=Hash(File.ReadAllBytes(cable));
        var hook=store.GetField("BeforePublishForTests",All);byte[] late=null;
        hook.SetValue(null,(Action<int,string>)((index,path)=>{File.WriteAllText(path,File.ReadAllText(path).Replace("接头 1","发布前外部接头"),new UTF8Encoding(false));late=File.ReadAllBytes(path);}));
        try{Check(Rejected(fixture,expected),"external change after review read is rejected by existing commit guard");}
        finally{hook.SetValue(null,null);}
        Check(late!=null&&File.ReadAllBytes(cable).SequenceEqual(late),"late external bytes remain intact");
    }
    static void IconOwnership()
    {
        var pageType=module.GetType("HVX.Controls.CalibrationProgramControl",true);Icon supplied=null;
        using(var host=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-3000,-3000)})
        using(var page=(Control)Activator.CreateInstance(pageType))using(var timer=new Timer{Interval=20})
        {
            pageType.GetProperty("SimulationMode",All).SetValue(page,true,null);host.Controls.Add(page);host.Show();
            DateTime limit=DateTime.UtcNow.AddSeconds(5);
            timer.Tick+=delegate {
                var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name=="CableJointDialog");
                if(dialog!=null){supplied=dialog.Icon;timer.Stop();dialog.DialogResult=DialogResult.Cancel;dialog.Close();}
                else if(DateTime.UtcNow>limit){timer.Stop();throw new Exception("synthetic shared joint dialog did not open");}
            };
            timer.Start();Call(page,"OpenSharedJointDialog",-1,"5nC",10d,"");timer.Stop();
            Check(supplied!=null,"caller loaded a real branded icon");
            bool disposed=false;try{var handle=supplied.Handle;disposed=handle==IntPtr.Zero;}catch(ObjectDisposedException){disposed=true;}
            if(probe){Check(!disposed,"baseline caller icon survives dialog close without disposal");supplied.Dispose();Console.WriteLine("CONFIRMED_BASELINE_CALLER_ICON_NOT_DISPOSED=1");}
            else Check(disposed,"caller-owned icon disposed after the shared dialog returns");
        }
        using(var plain=new Form()){plain.Show();plain.Close();Check(true,"caller cleanup does not dispose shared default icon");}
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            if(args.Length<2||args.Length>3)throw new ArgumentException("Pass isolated build root, QA root and optional --probe-baseline.");
            probe=args.Length==3&&args[2]=="--probe-baseline";string root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));store=module.GetType("HVX.Controls.CalibrationProjectStore",true);
            ReviewConflict(root);IconOwnership();Console.WriteLine((probe?"CALIBRATION_JOINT_BASELINE_CONFIRMED=":"CALIBRATION_JOINT_REVIEW_SAFETY_PASS=")+checks+"; HARDWARE_COMMANDS=0; REAL_PROJECT_WRITES=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
