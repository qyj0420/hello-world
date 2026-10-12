using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        internal static int TestConnectionDisplay(string root)
        {
            int n=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Connection UI: "+why);++n;};
            using(var f=new FactoryMainForm(Path.Combine(root,"link-ui-labels")))
            {
                f.smokeMode=true;f.mode.SelectedIndex=1;
                f.pinnedArtSerial="1023417009";f.pinnedPicoSerial="13000/0059";f.parameterArtName="Dev1";
                var watch=f.ConnectionWatchSettings();
                check(watch.Input.ArtSerial==f.pinnedArtSerial && f.ReadSettings().Input.ArtSerial=="","remembered identity only permits readonly discovery");
                f.connectionJournal=new FactoryConnectionJournal(root,f.pinnedArtSerial,f.pinnedPicoSerial);
                f.connectionIdentity=f.pinnedArtSerial+"|"+f.pinnedPicoSerial;
                f.ObserveConnection("LINK_ART_NAME","Dev2",false);
                f.ObserveConnection("LINK_ART_HIGH",FactoryConnectionWatch.Stamp("fake"),false);
                f.ObserveConnection("LINK_PICO_ONLINE",FactoryConnectionWatch.Stamp("fake"),false);
                check(f.emergency.Text.Contains("在线") && f.emergency.Text.Contains("已识别") && f.emergency.BackColor!=Color.MistyRose,"idle live display");
                check(f.start.Enabled,"fresh matching HIGH permits the UI start path only");
                check(f.ReadSettings().Input.ArtDevice=="Dev2" && f.ReadSettings().Input.PicoSerial==f.pinnedPicoSerial,"exact discovered devices selectable without manual scan: "+f.ReadSettings().Input.ArtDevice+" / '"+f.ReadSettings().Input.PicoSerial+"' / selected '"+f.picoChoice.SelectedItem+"' / pinned '"+f.pinnedPicoSerial+"'");
                f.ObserveConnection("LINK_ART_LOW",FactoryConnectionWatch.Stamp("fake"),false);
                check(f.emergency.Text.Contains("P0.4 低") && f.connectionJournal.Incidents==0,"valid low is not a communication fault or proof of a physical button press");
                check(!f.start.Enabled,"pressed emergency disables nonzero start");
                f.targetKv.Value=0;f.RefreshStartAvailability();
                check(f.start.Enabled,"zero read-only capture is not blocked by emergency low");
                f.targetKv.Value=5;f.RefreshStartAvailability();
                check(!f.start.Enabled,"changing back to nonzero restores emergency gate");
                f.ObserveConnection("LINK_ART_ERROR",FactoryConnectionWatch.Stamp("read failed"),false);
                check(f.emergency.Text.Contains("读取异常") && f.connectionJournal.Incidents==1,"idle disconnect visible and logged");
                f.ObserveConnection("LINK_ART_HIGH",FactoryConnectionWatch.Stamp("fake"),false);
                check(f.emergency.Text.Contains("恢复后待操作") && !f.busy,"reconnection never starts a test");
                check(f.start.Enabled,"new high permits another explicit click without auto-start");
                f.connectionIdentity="different|device";f.RefreshStartAvailability();
                check(!f.start.Enabled,"old device high cannot enable a new identity");
                f.connectionIdentity=f.pinnedArtSerial+"|"+f.pinnedPicoSerial;
                f.artSeen=DateTime.UtcNow.AddSeconds(-5);f.RenderConnection();
                check(f.emergency.Text.Contains("状态过期") && f.emergency.BackColor==Color.Gainsboro,"stale heartbeat not green");
                check(!f.start.Enabled,"stale high disables nonzero start");
                f.ObserveConnection("LINK_ART_HIGH",FactoryConnectionWatch.Stamp("fresh again"),false);
                f.connectionTransition=true;f.RefreshStartAvailability();check(!f.start.Enabled,"connection handoff cannot enable start");
                f.connectionTransition=false;f.busy=true;f.RefreshStartAvailability();check(!f.start.Enabled,"active operation cannot enable start");
                f.busy=false;f.closeAfterStop=true;f.RefreshStartAvailability();check(!f.start.Enabled,"close/stop drain cannot enable start");
                f.closeAfterStop=false;f.RefreshStartAvailability();check(f.start.Enabled,"released operation returns to explicit-click readiness");
                f.workerConnectionOwned=true;f.cancellation=new CancellationTokenSource();
                f.ObserveConnection("LINK_PICO_ERROR",FactoryConnectionWatch.Stamp("fake USB failure"),true);
                f.ObserveConnection("LINK_PICO_ONLINE",FactoryConnectionWatch.Stamp("late good read"),true);
                check(f.cancellation.IsCancellationRequested && f.emergency.Text.Contains("中断锁定") && f.emergency.BackColor==Color.MistyRose,"active reconnect cannot undo stop or lamp latch");
                f.LogConnectionResult("测试关断结果：仅模拟，不访问设备");
                check(f.connectionHistory.Text.Contains("测试关断结果") && f.connectionHistory.Text.Contains("13000/0059"),"summary displays outcomes and serial");
                f.ShowInTaskbar=false;f.Opacity=0;f.Show();System.Windows.Forms.Application.DoEvents();
                f.SaveImage(Path.Combine(root,"connection-fault-ui.png"));
                f.tabs.SelectedTab=f.connectionPage;System.Windows.Forms.Application.DoEvents();
                f.SaveImage(Path.Combine(root,"connection-log-ui.png"));f.tabs.SelectedIndex=0;
                f.workerConnectionOwned=false;f.cancellation.Dispose();f.cancellation=null;
                f.ObserveConnection("LINK_ART_HIGH",FactoryConnectionWatch.Stamp("fake recovered"),false);
                f.ObserveConnection("LINK_PICO_ONLINE",FactoryConnectionWatch.Stamp("fake recovered"),false);
                f.SaveImage(Path.Combine(root,"connection-live-ui.png"));
                check(f.emergency.Bottom<=f.emergency.Parent.ClientSize.Height && f.emergency.ClientSize.Height>=3*f.emergency.Font.Height,"all three live-status lines fit toolbar");f.Hide();
                f.activeArtLinkFault=f.activePicoLinkFault=false;f.workerConnectionOwned=true;
                f.ObserveConnection("LINK_ART_CLOSED",FactoryConnectionWatch.Stamp("released"),true);
                f.artSeen=DateTime.UtcNow.AddSeconds(-5);f.RenderConnection();
                check(f.artLink=="CLOSED" && !f.activeArtLinkFault,"normal owner release is not stale disconnect");
                f.workerConnectionOwned=false;
            }
            n+=TestConnectionDoesNotCommitParameters(Path.Combine(root,"heartbeat-parameters"));
            return n;
        }

        private static int TestConnectionDoesNotCommitParameters(string root)
        {
            int n=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Connection parameter isolation: "+why);++n;};
            string profileRoot=Path.Combine(root,"profiles");
            using(var f=new FactoryMainForm(Path.Combine(root,"labels"),profileRoot))
            {
                f.smokeMode=true;f.mode.SelectedIndex=1;
                f.pinnedArtSerial="1023417009";f.pinnedPicoSerial="13000/0059";f.parameterArtName="Dev1";
                f.connectionJournal=new FactoryConnectionJournal(Path.Combine(root,"journal"),f.pinnedArtSerial,f.pinnedPicoSerial);
                f.connectionIdentity=f.pinnedArtSerial+"|"+f.pinnedPicoSerial;
                f.ObserveConnection("LINK_ART_NAME","Dev1",false);
                f.ObserveConnection("LINK_ART_HIGH",FactoryConnectionWatch.Stamp("isolated test"),false);
                f.ObserveConnection("LINK_PICO_ONLINE",FactoryConnectionWatch.Stamp("isolated test"),false);
                f.SaveParameterPreferences();
                var files=new Dictionary<string,string>();var writes=new Dictionary<string,DateTime>();
                foreach(string path in Directory.GetFiles(profileRoot,"*.xml",SearchOption.AllDirectories))
                {files[path]=File.ReadAllText(path);writes[path]=File.GetLastWriteTimeUtc(path);}
                check(files.Count>0,"isolated saved profile fixture exists");
                f.ShowInTaskbar=false;f.Opacity=0;f.Show();f.tabs.SelectedTab=f.parametersPage;Application.DoEvents();
                var editor=f.values["divider"];editor.Focus();editor.Select(0,editor.Text.Length);
                Panel scroll=null;foreach(Control control in f.parametersPage.Controls)if(control is Panel)scroll=(Panel)control;
                check(scroll!=null,"parameter scroll container found");
                scroll.ScrollControlIntoView(editor);Point scrollBefore=scroll.AutoScrollPosition;
                bool focusBefore=editor.ContainsFocus;int valueChanges=0,historyChanges=0;
                editor.ValueChanged+=(s,e)=>++valueChanges;
                f.connectionHistory.TextChanged+=(s,e)=>++historyChanges;
                // Set the edit box, as keyboard input does. NumericUpDown.Text's
                // programmatic setter itself validates and cannot model typing.
                TextBox editBox=null;foreach(Control control in editor.Controls)if(control is TextBox)editBox=(TextBox)control;
                check(editBox!=null,"numeric text editor found");
                editBox.Text="12345.6";string pending=editor.Text;
                string persistence=f.parameterPersistence.Text;
                string journal=File.ReadAllText(f.connectionJournal.FilePath);
                for(int i=0;i<10;i++)
                {
                    var snapshot=f.ConnectionWatchSettings();
                    check(snapshot.Input.ArtSerial==f.pinnedArtSerial && snapshot.Input.PicoSerial==f.pinnedPicoSerial,"read-only snapshot retains exact serials");
                    f.ObserveConnection("LINK_ART_HIGH",FactoryConnectionWatch.Stamp("healthy heartbeat"),false);
                    f.ObserveConnection("LINK_PICO_ONLINE",FactoryConnectionWatch.Stamp("healthy heartbeat"),false);
                    f.RenderConnection();
                }
                check(editor.Text==pending && valueChanges==0,"heartbeats do not commit or reformat unfinished numeric edits");
                check(editor.ContainsFocus==focusBefore && scroll.AutoScrollPosition==scrollBefore && f.tabs.SelectedTab==f.parametersPage,"heartbeat preserves focus, scroll and selected page");
                check(historyChanges==0 && f.connectionJournal.Lines.Count==0 && File.ReadAllText(f.connectionJournal.FilePath)==journal,"healthy heartbeat neither rebuilds history nor appends logs");
                check(f.parameterPersistence.Text==persistence && !f.parameterSaveTimer.Enabled,"heartbeats do not schedule parameter persistence");
                foreach(var item in files)
                    check(File.ReadAllText(item.Key)==item.Value && File.GetLastWriteTimeUtc(item.Key)==writes[item.Key],"heartbeat does not rewrite profile "+Path.GetFileName(item.Key));
                // Prove the fixture is an uncommitted edit: the normal explicit
                // settings read commits it, whereas the heartbeat path did not.
                f.ReadSettings();check(valueChanges>0,"ordinary settings read commits the pending edit only when explicitly requested");
                f.parameterSaveTimer.Stop();f.Hide();
            }
            return n;
        }
    }
}
