using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class WithstandSimulationSmoke
{
    static int checks;
    static void Check(bool value,string label){checks++;if(!value)throw new InvalidOperationException(label);}
    static IEnumerable<Control> All(Control c){foreach(Control child in c.Controls){yield return child;foreach(var item in All(child))yield return item;}}
    static void Pump(int milliseconds=20){var watch=Stopwatch.StartNew();do{Application.DoEvents();}while(watch.ElapsedMilliseconds<milliseconds);}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            string output=Path.GetFullPath(args.Length>1?args[1]:args[0]);Directory.CreateDirectory(output);
            foreach(var pair in new[]{Tuple.Create(15d,.5d),Tuple.Create(30d,1d),Tuple.Create(45d,2d),Tuple.Create(60d,3d),Tuple.Create(90d,4.5d),Tuple.Create(29.99,1d),Tuple.Create(60.01,3d)})
                Check(WithstandSimulation.PrecheckVoltageForLimit(pair.Item1)==pair.Item2,"precheck mapping "+pair.Item1);
            foreach(double invalid in new[]{0d,-1d,Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity,.001d})Check(!WithstandSimulation.PrecheckVoltageForLimit(invalid).HasValue,"invalid/unrepresentable precheck rejected");
            for(int i=1;i<1000;i++){double limit=i*.13;double? value=WithstandSimulation.PrecheckVoltageForLimit(limit);Check(!value.HasValue||(value>0&&value<=limit),"precheck positive and within rating");}
            var model=new WithstandSimulation();model.SetContext("project1","A",20,60,30);
            Check(!model.Start()&&model.ErrorCode=="mode_unavailable","no capabilities blocks start");
            model.ConfigureCapabilities(true,false);Check(model.AvailableModes.SequenceEqual(new[]{WithstandMode.CosineVlf,WithstandMode.DirectCurrent,WithstandMode.Sheath}),"cosine capability mapping");
            model.ConfigureCapabilities(false,true);Check(model.AvailableModes.Length==4&&model.AvailableModes.Contains(WithstandMode.SineVlf),"combined capability mapping");
            model.SetMode(WithstandMode.CosineVlf);Check(model.Start()&&model.Stage==WithstandStage.InitialDac,"first phase requires DAC");
            Check(model.PrecheckVoltageKv==1&&model.TargetKv==20,"low precheck independent of withstand target");
            model.Advance(5);Check(model.Stage==WithstandStage.InitialDac&&!model.InitialDacCompleted,"elapsed time alone does not validate capacitance");
            Check(!model.CompleteInitialDac(false,.2)&&model.Stage==WithstandStage.Failed&&!model.InitialDacCompleted,"failed DAC blocks withstand and mark");
            foreach(double? invalid in new double?[]{null,0,-1,Double.NaN,Double.PositiveInfinity})
            {Check(model.Start(),"precheck retry starts");Check(!model.CompleteInitialDac(true,invalid)&&!model.InitialDacCompleted,"invalid capacitance does not complete precheck");}
            Check(model.Start(),"precheck start before stop");model.Stop();Check(!model.InitialDacCompleted&&model.Stage==WithstandStage.Stopped,"cancel precheck not marked complete");
            Check(!model.CompleteInitialDac(true,.2),"late completion after cancel ignored");
            int events=0;model.InitialDacCompletedSimulation+=(s,e)=>{Check(e.IsSimulation&&e.ProjectId=="project1"&&e.Phase=="A"&&e.VoltageKv==1,"simulation event identity and low voltage");events++;};
            model.Start();Check(model.CompleteInitialDac(true,.2)&&model.Stage==WithstandStage.Running&&model.InitialDacCompleted,"valid precheck starts withstand");
            Check(events==1,"one precheck event");model.Advance(60);Check(model.Stage==WithstandStage.Completed,"duration completion");
            model.Start();Check(model.Stage==WithstandStage.Running&&events==1,"same project phase skips repeated precheck");model.Stop();
            model.SetContext("project1","B",20,60,30);model.Start();Check(model.Stage==WithstandStage.InitialDac&&!model.InitialDacCompleted,"new phase needs precheck");model.Stop();
            model.SetContext("project2","A",20,60,30);model.Start();Check(model.Stage==WithstandStage.InitialDac&&!model.InitialDacCompleted,"new project needs precheck");model.Stop();
            model.SetContext("project1","A",20,60,30);Check(model.InitialDacCompleted,"original project phase retains simulation mark");
            var separate=new WithstandSimulation();separate.ConfigureCapabilities(true,false);separate.SetContext("project1","A",20,60,30);Check(!separate.InitialDacCompleted,"simulation marks cannot populate a separate/live session");
            separate.SetContext("project1","A",31,60,30);Check(!separate.Start()&&separate.ErrorCode=="invalid_target","overvoltage target rejected");
            separate.SetContext("project1","A",10,60,45);Check(separate.PrecheckVoltageKv==2&&separate.PrecheckVoltageIsEstimated,"unknown rating estimate identified");
            separate.ExplicitPrecheckKv=46;Check(!separate.Start()&&separate.ErrorCode=="precheck_voltage_required","invalid explicit precheck blocks instead of fallback");
            separate.ExplicitPrecheckKv=null;separate.SetMode(WithstandMode.DirectCurrent);separate.Start();Check(separate.Stage==WithstandStage.Running&&!separate.InitialDacCompleted,"DC demonstration does not mark VLF DAC complete");separate.Stop();
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            foreach(int language in Enumerable.Range(0,19))foreach(var size in new[]{new Size(1120,416),new Size(1256,620),new Size(1850,990)})
            using(var form=new Form{AutoScaleMode=AutoScaleMode.None,ClientSize=size,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var control=new WithstandTestControl())
            {
                control.LanguageIndex=language;control.ConfigureCapabilities(false,true);control.SetContext("synthetic-project","A",13.4,60,30);form.Controls.Add(control);control.Dock=DockStyle.Fill;form.Show();Pump();
                Check(control.SimulationOnly&&!control.SimulationInitialDacSucceeded,"UI is explicitly isolated simulation");
                Check(control.RequestStart()&&control.StateCode=="InitialDac","UI starts precheck only");Pump(250);
                foreach(var child in All(control).Where(c=>c.Visible&&(c.Name.StartsWith("Withstand")||c is NumericUpDown)))
                    Check(child.Parent.ClientRectangle.Contains(child.Bounds),"visible control fits: "+child.Name+" "+child.Bounds+" in "+child.Parent.ClientSize);
                foreach(var button in All(control).OfType<Button>().Where(b=>b.Visible))Check(button.Width>=TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.NoPadding).Width+10,"full button label "+button.Name);
                Check(!control.HorizontalScroll.Visible&&!control.VerticalScroll.Visible,"no workspace scrollbars");
                using(var bmp=new Bitmap(control.Width,control.Height)){control.DrawToBitmap(bmp,new Rectangle(Point.Empty,control.Size));bmp.Save(Path.Combine(output,"withstand-"+language+"-"+size.Width+".png"));}
                var trends=All(control).OfType<Button>().Single(c=>c.Name=="WithstandTrendTab");if(trends.Visible){trends.PerformClick();Pump();using(var bmp=new Bitmap(control.Width,control.Height)){control.DrawToBitmap(bmp,new Rectangle(Point.Empty,control.Size));bmp.Save(Path.Combine(output,"withstand-"+language+"-"+size.Width+"-trends.png"));}}
                control.RequestStop();Check(!control.IsRunning&&!control.SimulationInitialDacSucceeded,"stop UI precheck leaves completion unmarked");form.Close();
            }
            Console.WriteLine("WITHSTAND_SIMULATION_PASS="+checks);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
