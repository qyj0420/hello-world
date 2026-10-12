using System;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        internal async Task SmokeLocationCycles(string output)
        {
            smokeMode=true;
            if(mode.SelectedIndex!=0 || parameterStore!=null)throw new Exception("Smoke must use isolated simulation configuration");
            values["locationsavecycles"].Value=100;values["duration"].Value=20;
            automaticRange.Checked=false;range.SelectedIndex=10;continuous.Checked=true;repeatCount.Value=5;
            var s=ReadSettings();s.Simulation=true;s.Input.ArtSerial="SIM-ART";s.Input.PicoSerial="SIM-PICO";
            s.Input.RequestedPicoRateHz=1000000;s.Input.OscillationCircuitConnected=true;s.Input.PreTriggerPercent=0;
            sequenceProgress=new FactorySequenceProgress(s,5);
            var seen=new List<int>();var rounds=new List<FactoryResult>();
            SetBusy(true);
            int done=await FactoryPressureSequence.Run(s,5,async next=>{
                if(!next.Simulation)throw new Exception("No hardware in smoke test");
                seen.Add(next.Input.RangeIndex);
                var r=await RunWithRangeReview(next,FactoryRunKind.Pressure,CancellationToken.None);
                rounds.Add(r);return r;
            },null,(i,n,next)=>{range.SelectedIndex=next.Input.RangeIndex;RefreshRemainingCount();},CancellationToken.None,
                (r,q)=>Task.FromResult(ReviewCapturedRange(r,q,true)),sequenceProgress,RefreshRemainingCount,Progress);
            SetBusy(false);RefreshRemainingCount();
            if(done!=5 || sequenceProgress.Remaining!=0 || seen[1]>=seen[0] || rounds.Any(r=>!r.Metrics.RangeQuality.WindowShortened))
                throw new Exception("Five finite-length workers or next range failed");
            foreach(var r in rounds)
                if(!File.Exists(Path.Combine(r.DirectoryPath,"location-ncycles.bin")) ||
                    Directory.GetFiles(r.DirectoryPath,"next-range-decision-*.xml").Length!=1)
                    throw new Exception("Worker archive / silent decision missing");
            SaveImage(Path.Combine(output,"five-rounds.png"));
            tabs.SelectedTab=parametersPage;
            var scroll=parametersPage.Controls.OfType<Panel>().First(x=>x.AutoScroll);
            scroll.ScrollControlIntoView(values["locationsavecycles"]);await Task.Delay(100);
            if(!values["locationsavecycles"].Visible || ReadSettings().LocationSaveCycleCount!=100)
                throw new Exception("Location cycle parameter missing from UI or actual settings");
            SaveImage(Path.Combine(output,"location-cycle-parameter.png"));
            var first=rounds[0];var quality=FactoryRangeReview.EvaluateCapturedData(first);
            using(var dialog=new FactoryNextRangeDialog(first,quality,first.Capture.Settings.RangeIndex,Icon))
            {
                dialog.Show(this);await Task.Delay(100);
                using(var b=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(b,new Rectangle(Point.Empty,dialog.Size));b.Save(Path.Combine(output,"range-reminder.png"));}
                if(!dialog.ApplyButton.Enabled)throw new Exception("Valid range dialog disabled");
                dialog.ApplyButton.PerformClick();if(!dialog.ApplyNext)throw new Exception("Range dialog action lost");
            }
            File.WriteAllText(Path.Combine(output,"result.txt"),"PASS; HardwareAccessed=False; SimulationWorkers=5; completed=5; remaining=0; N=100; finite20ms; actual range indexes="+
                String.Join(",",seen)+"\r\n"+String.Join("\r\n",rounds.Select(r=>r.DirectoryPath)));
        }
    }
}
