using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Xiongxin.Scope
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--worker")
            {
                int parent;
                if (!Int32.TryParse(args[1], out parent) || parent <= 0) throw new ArgumentException("采集进程参数无效。");
                Worker.Run(parent);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if ((args.Length == 2 || args.Length == 3) && args[0] == "--offline-smoke")
            {
                string folder=Path.GetFullPath(args[1]); Directory.CreateDirectory(folder);
                try
                {
                    var config=new Settings {Simulation=true,DurationMs=1,RateMS=25,TimeoutMs=3000};
                    var client=new WorkerClient();
                    try
                    {
                        var opened=client.Send(new Command{Op="OPEN",Config=config},5000).GetAwaiter().GetResult();
                        if(!opened.Ok || !opened.Connected)throw new IOException("Simulation open failed: "+opened.Message);
                        string path=Path.Combine(folder,"simulation.xxs");
                        var reply=client.Send(new Command{Op="CAPTURE",Config=config,DataPath=path},7000).GetAwaiter().GetResult();
                        if(!reply.Ok || Wire.Load(path).A.Length!=reply.Samples)throw new IOException("Simulation capture or archive failed.");
                        client.Close().GetAwaiter().GetResult();
                    }
                    catch { client.RetireUnresponsive().GetAwaiter().GetResult(); throw; }
                    string projectNote="";
                    if(args.Length==3)
                    {
                        var defaults=Xiongxin.Calibration.ProjectDefaults.Load(args[2]);
                        projectNote="Project length="+defaults.LengthM+"; velocity="+defaults.VelocityMPerUs+"; material="+defaults.Material+"; source="+defaults.VelocitySource+"\r\n";
                    }
                    var window=new HVX.OWTS.Task38.CalibrationWindowSettings {SampleRateHz=125000000,CableLengthM=180,VelocityMPerUs=170};
                    if(window.PostPeakSamples<1300)throw new InvalidOperationException("5L extraction window is too short.");
                    var pulse=new double[3000];for(int j=-10;j<=10;j++){pulse[400+j]=Math.Exp(-j*j/25.0);pulse[665+j]=-.6*Math.Exp(-j*j/25.0);}
                    var match=Xiongxin.Calibration.TdrCorrelation.Match(pulse,400,125000000,180,170);
                    if(!match.Valid || Math.Abs(match.DistanceM-180)>5)throw new InvalidOperationException("TDR 50%-150% correlation result failed.");
                    using(var form=new Xiongxin.Calibration.CalibrationForm(args.Length==3?args[2]:null))
                    {
                        form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual;
                        form.Location=new Point(-3000,-3000); form.Show(); Application.DoEvents();
                        using(var image=new Bitmap(form.Width,form.Height))
                        {form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(folder,"window.png"));}
                        form.Close();
                    }
                    File.WriteAllText(Path.Combine(folder,"result.txt"),"PASS: simulated Pico worker, archive read, UI render, 5L extraction window. ART native driver not loaded.\r\n"+projectNote);
                }
                catch(Exception ex) {File.WriteAllText(Path.Combine(folder,"result.txt"),"FAIL: "+ex);Environment.ExitCode=1;}
                return;
            }
            bool integrated=args.Length==3 && args[0]=="--project" && args[2]=="--integrated";
            if(args.Length!=0 && !(args.Length==2 && args[0]=="--project") && !integrated)
                throw new ArgumentException("命令行仅支持 --project 项目目录 [--integrated]。");
            Xiongxin.OWTS.Ui.DialogPlacement.Install();
            Application.Run(new Xiongxin.Calibration.CalibrationForm(args.Length>=2?args[1]:null,integrated));
        }
    }
}
