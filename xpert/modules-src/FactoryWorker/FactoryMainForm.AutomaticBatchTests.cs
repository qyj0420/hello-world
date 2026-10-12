using System;
using System.IO;
using System.Threading.Tasks;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        internal static string TestAutomaticBatchRouting()
        { return FactoryOfflineAsyncTest.Run(CheckAutomaticBatchRouting); }

        private static Task<string> CheckAutomaticBatchRouting()
        {
            int checks=0;
            Action<bool,string> check=(ok,message)=>{if(!ok)throw new InvalidOperationException("Main batch routing: "+message);++checks;};
            string root=Path.Combine(Path.GetTempPath(),"Xiongxin-MainBatchRoute-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using(var form=new FactoryMainForm(Path.Combine(root,"labels")))
            {
                // A test-label root disables the connection monitor at construction;
                // smoke mode also prevents scans during the synthetic mode changes.
                form.smokeMode=true;form.ShowInTaskbar=false;form.Opacity=0;
                form.values["rate"].Value=.125M;form.values["duration"].Value=20;
                foreach(int mode in new[]{0,1})
                {
                    form.mode.SelectedIndex=mode;
                    if(mode==1)
                    {
                        var discovered=new DiscoveryResult();
                        discovered.ArtDevices.Add(new ArtDeviceIdentity{Name="Dev1",Model="USB3135",Serial="123456789"});
                        discovered.PicoSerials.Add("BATCH-UI-PICO");
                        form.pinnedArtSerial="123456789";form.pinnedPicoSerial="BATCH-UI-PICO";
                        form.ApplyDevices(discovered);
                    }
                    foreach(bool continuous in new[]{false,true})
                    foreach(decimal voltage in new[]{0M,5M})
                    foreach(FactoryRunKind kind in new[]{FactoryRunKind.Pressure,FactoryRunKind.Calibration})
                    {
                        form.continuous.Checked=continuous;form.targetKv.Value=voltage;
                        var settings=form.ReadStartSettings(kind);
                        check(settings.AutomaticBatch==(continuous && voltage>0 && kind==FactoryRunKind.Pressure),
                            "actual preparation routes mode="+mode+",continuous="+continuous+",voltage="+voltage+",kind="+kind);
                        check(settings.BatchPermit==null && !settings.BatchStartConfirmed && !settings.BatchContinuation,
                            "reading controls is not explicit start authorization");
                    }
                }
                form.continuous.Checked=true;form.repeatCount.Value=5;form.targetKv.Value=5;
                form.oscillationCircuit.Checked=true;
                var pending=form.ReadStartSettings(FactoryRunKind.Pressure);
                pending.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
                check(!pending.Simulation && pending.AutomaticBatch && pending.Input.ArtSerial=="123456789" &&
                    pending.Input.PicoSerial=="BATCH-UI-PICO","real main UI preserves exact synthetic card identities into batch request");
                check(FactoryStartConfirmation.Accept(pending,FactoryRunKind.Pressure,false)==null,"cancelled start creates no batch request");
                var accepted=FactoryStartConfirmation.Accept(pending,FactoryRunKind.Pressure,true);
                check(accepted.AutomaticBatch && accepted.BatchStartConfirmed && !accepted.BatchContinuation && accepted.BatchPermit==null,
                    "only actual start acceptance creates the initial batch declaration, not native permit");
                check(accepted.IndependentDischargeConfirmed && accepted.FeedbackWiringConfirmed,
                    "first accepted start retains explicit initial confirmations");
                check(!pending.BatchStartConfirmed && !form.ReadStartSettings(FactoryRunKind.Pressure).BatchStartConfirmed,
                    "acceptance does not persist into fresh UI reads");
                string prompt=FactoryPressureStartPrompt.Text(pending,5,false);
                check(prompt.Contains("5") && prompt.Contains("自动") && !prompt.Contains("逐轮现场确认"),
                    "actual main start prompt describes the five-round automatic batch");
                form.repeatPrompts.Checked=false;
                var hidden=form.ReadStartSettings(FactoryRunKind.Pressure);
                form.repeatPrompts.Checked=true;
                var visible=form.ReadStartSettings(FactoryRunKind.Pressure);
                check(hidden.AutomaticBatch && visible.AutomaticBatch && !hidden.BatchStartConfirmed && !visible.BatchStartConfirmed,
                    "ordinary notice visibility neither enables nor blocks automatic batch authorization");
                check(!form.ReadSettings().AutomaticBatch && !form.busy && !form.scanning,
                    "stored parameter snapshot remains non-running; no worker or scan started");
                check(!form.pulseTip.GetToolTip(form.repeatPrompts).Contains("仍须在主界面"),
                    "tooltip no longer advertises the removed inline confirmation behavior");
            }
            return Task.FromResult("Main batch routing: "+checks+" actual-control/preparation checks PASS; no Run/worker/SDK; HardwareAccessed=False");
        }
    }
}
