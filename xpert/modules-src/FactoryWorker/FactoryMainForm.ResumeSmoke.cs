using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        // Only reachable from the explicit offline UI smoke. The guard and
        // unchanged simulation mode prohibit discovery or real workers.
        private async Task SmokeResumeStart(string directory)
        {
            if (!smokeMode || mode.SelectedIndex != 0) throw new Exception("Resume smoke requires simulation");
            Directory.CreateDirectory(directory);
            decimal oldRate=values["rate"].Value, oldTarget=targetKv.Value;
            int oldRange=range.SelectedIndex;
            try
            {
                values["rate"].Value=1;targetKv.Value=1;continuous.Checked=true;repeatCount.Value=3;automaticRange.Checked=false;
                var first=ReadSettings();
                sequenceProgress=new FactorySequenceProgress(first,3);
                int syntheticRuns=0;
                try
                {
                    await FactoryPressureSequence.Run(first,3,s=>{
                        ++syntheticRuns;
                        if(s.SequenceRound==2){Progress("EMERGENCY_ACTIVE","UI fixture: emergency");throw new InvalidOperationException("UI fixture emergency");}
                        return Task.FromResult(FactoryPressureSequenceTests.Capture(s));
                    },null,(i,n,s)=>range.SelectedIndex=s.Input.RangeIndex,CancellationToken.None,null,sequenceProgress);
                }
                catch(InvalidOperationException ex){if(ex.Message!="UI fixture emergency")throw;}
                sequenceProgress.RememberStoppedUi(ReadSettings());
                if(syntheticRuns!=2 || sequenceProgress.Completed!=1 || !sequenceProgress.CanResume)
                    throw new Exception("Resume UI checkpoint fixture failed");
                var checkpoint=sequenceProgress;string id=checkpoint.Id;
                // Cancel is tested through the actual Run/ShowDialog path.
                int dialogs=await SmokeChooseResume(FactorySequenceChoice.Cancel,Path.Combine(directory,"cancel-dialog.png"));
                if(dialogs!=1 || sequenceProgress!=checkpoint || sequenceProgress.Completed!=1 || sequenceProgress.Attempt!=1 || busy)
                    throw new Exception("Cancel changed checkpoint or dispatched a worker");
                dialogs=await SmokeChooseResume(FactorySequenceChoice.Continue,Path.Combine(directory,"continue-dialog.png"));
                if(dialogs!=1 || sequenceProgress!=checkpoint || checkpoint.Id!=id || checkpoint.Completed!=3 || checkpoint.Attempt!=2 ||
                    checkpoint.CanResume || busy || stageSettings.SequenceRound!=3 || stageSettings.SequenceAttempt!=2 ||
                    !diagnostics.Text.Contains("已完成 3 / 3，剩余 0 次"))
                    throw new Exception("Actual Start/continue did not execute exactly two remaining simulation workers: "+diagnostics.Text);
                SaveImage(Path.Combine(directory,"resumed-result.png"));
                File.WriteAllText(Path.Combine(directory,"resume-start.txt"),
                    "ACTUAL_START_CANCEL_NO_WORKER=PASS\nACTUAL_START_CONTINUE_REMAINING_TWO_WORKERS=PASS\n"+
                    "RESUME_DIALOGS_PER_CLICK=1\nCOMPLETED=3/3\nATTEMPT=2\nSEQUENCE_ID="+id+"\nHARDWARE_ACCESSED=False\n");
                // Exercise the real UI Start path with five separate simulation
                // child processes and a short record, not only fake-clock tasks.
                decimal oldDuration=values["duration"].Value;
                try
                {
                    sequenceProgress=null;values["duration"].Value=20;repeatCount.Value=5;range.SelectedIndex=10;
                    smokeMode=false;await Run(FactoryRunKind.Pressure);smokeMode=true;
                    if(sequenceProgress==null || sequenceProgress.Completed!=5 || sequenceProgress.Remaining!=0 ||
                        stageSettings.SequenceRound!=5 || stageSettings.Input.RangeIndex>=10 ||
                        !diagnostics.Text.Contains("已完成 5 / 5，剩余 0 次") || !diagnostics.Text.Contains("量程决定（未弹窗）"))
                        throw new Exception("R29 actual Start five simulation workers / ranges failed: "+diagnostics.Text);
                    SaveImage(Path.Combine(directory,"five-complete.png"));
                    File.WriteAllText(Path.Combine(directory,"five-workers.txt"),"FIVE_UI_START_WORKERS=PASS\nRECORD_MS=20\nCOMPLETED=5\nREMAINING=0\nFINAL_RANGE_INDEX="+stageSettings.Input.RangeIndex+"\nHARDWARE_ACCESSED=False\n"+diagnostics.Text);
                }
                finally {smokeMode=true;values["duration"].Value=oldDuration;}
            }
            finally
            {
                smokeMode=true;sequenceProgress=null;busy=false;values["rate"].Value=oldRate;targetKv.Value=oldTarget;
                range.SelectedIndex=oldRange;continuous.Checked=false;repeatCount.Value=1;automaticRange.Checked=true;
                SetBusy(false);ResetEmergency();
            }
        }

        private async Task<int> SmokeChooseResume(FactorySequenceChoice choice,string imagePath)
        {
            int dialogs=0;
            using(var timer=new System.Windows.Forms.Timer { Interval=50 })
            {
                timer.Tick+=(sender,args)=>{
                    foreach(Form owned in OwnedForms)
                    {
                        var dialog=owned as FactorySequenceResumeDialog;
                        if(dialog==null)continue;
                        ++dialogs;
                        using(var bitmap=new Bitmap(dialog.Width,dialog.Height))
                        {dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,dialog.Size));bitmap.Save(imagePath);}
                        if(choice==FactorySequenceChoice.Continue)dialog.ContinueButton.PerformClick();
                        else if(choice==FactorySequenceChoice.Restart)dialog.RestartButton.PerformClick();
                        else dialog.CancelChoice.PerformClick();
                        break;
                    }
                };
                smokeMode=false;timer.Start();
                try { await Run(FactoryRunKind.Pressure); }
                finally { timer.Stop();smokeMode=true; }
            }
            return dialogs;
        }
    }
}
