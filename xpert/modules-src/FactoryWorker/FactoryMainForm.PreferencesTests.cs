using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        internal static string TestParameterPersistence()
        {
            string root=Path.Combine(Path.GetTempPath(),"Xiongxin-R28-Preferences-"+Guid.NewGuid().ToString("N"));
            string profiles=Path.Combine(root,"profiles");
            var store=new FactoryParameterStore(profiles);int checks=0;
            Action<bool,string> verify=(ok,reason)=>{if(!ok)throw new Exception("Parameter persistence: "+reason);++checks;};
            FactoryParameterProfile expected;
            using(var form=new FactoryMainForm(Path.Combine(root,"labels1"),profiles))
            {
                form.smokeMode=true;
                verify(form.mode.SelectedIndex==0 && !File.Exists(store.PathFor(0)),"fresh startup not auto saving or switching to hardware");
                foreach(var number in form.values.Values)
                {
                    decimal step=1;for(int i=0;i<number.DecimalPlaces;i++)step/=10;
                    number.Value=number.Value+step<=number.Maximum?number.Value+step:number.Value-step;
                }
                form.resolution.SelectedIndex=1;form.range.SelectedIndex=8;form.coupling.SelectedIndex=1;
                form.knownCharge.SelectedIndex=12;form.calibrationFrequency.SelectedIndex=1;
                form.bandwidth.Checked=true;form.referenceCapacitor.Checked=false;form.automaticRange.Checked=false;
                form.repeatPrompts.Checked=false;
                form.oscillationCircuit.Checked=true;
                form.capturePosition.Value=17;
                form.rawPd.ChargeRulerMaximumPc=12345.678M;form.rawPd.BoxZoomEnabled=false;
                form.continuous.Checked=true;form.repeatCount.Value=6;form.tfThresholdPc.Value=123.456M;
                form.acceptanceLoad.Value=203.125M;form.acceptanceMaximum.Value=29;form.acceptanceKnownCharge.Value=200;
                form.voltageReferenceId.Text="标准分压器 V-01";form.pdReferenceId.Text="标准校准器 PD-02";
                form.artLibrary.Text="C:\\saved ART runtime";form.picoLibrary.Text="C:\\saved Pico runtime";
                expected=form.SnapshotParameters();
                Thread.Sleep(550);Application.DoEvents();
                verify(File.Exists(store.PathFor(0)),"debounced change autosaves without start or save button");
                foreach(var value in store.Read(0).Numbers)
                    verify(expected.Numbers.Find(n=>n.Name==value.Name).Value==value.Value,"every numeric value autosaved: "+value.Name);
            }
            using(var form=new FactoryMainForm(Path.Combine(root,"labels2"),profiles))
            {
                form.smokeMode=true;
                foreach(var value in expected.Numbers)verify(form.values[value.Name].Value==value.Value,"new form restores "+value.Name);
                verify(form.resolution.SelectedIndex==1 && form.range.SelectedIndex==8 && form.coupling.SelectedIndex==1 &&
                    form.calibrationFrequency.SelectedIndex==1 && form.knownCharge.SelectedIndex==12,"all selectors restored");
                verify(form.bandwidth.Checked && !form.referenceCapacitor.Checked && !form.automaticRange.Checked && form.oscillationCircuit.Checked,"configuration toggles restored");
                verify(!form.repeatPrompts.Checked,"optional repeat prompts restore the last saved setting");
                verify(form.capturePosition.Value==17,"capture position restored");
                verify(form.rawPd.ChargeRulerMaximumPc==12345.678M && !form.rawPd.BoxZoomEnabled,"location ruler and interaction mode restored");
                verify(form.remainingCount.Text=="剩余 6 次","fresh sequence displays restored planned count, not a running queue");
                verify(form.acceptanceLoad.Value==203.125M && form.acceptanceMaximum.Value==29 && form.acceptanceKnownCharge.Value==200 && form.voltageReferenceId.Text=="标准分压器 V-01" && form.pdReferenceId.Text=="标准校准器 PD-02","acceptance configuration and reference IDs restored, not measured evidence");
                verify(form.artLibrary.Parent==null && form.picoLibrary.Parent==null && form.artLibrary.Text==expected.ArtLibrary && form.picoLibrary.Text==expected.PicoLibrary,"hidden libraries retained without controls");
                verify(!form.wiring.Checked && !form.calibrationConnected.Checked && form.devices==null && form.targetKv.Value==5 && form.continuous.Checked && form.repeatCount.Value==6 && form.tfThresholdPc.Value==123.456M && !form.busy,"configuration restored, not permission, connection, target or running queue");
                form.mode.SelectedIndex=1;
                verify(form.repeatPrompts.Checked,"fresh hardware mode does not inherit suppressed simulation prompts");
                verify(form.values["divider"].Value==11000 && form.values["pressure"].Value==6000 && form.values["pd"].Value==2100 && !form.oscillationCircuit.Checked,"first hardware profile independent of simulation");
                form.values["divider"].Value=12000;form.values["pressure"].Value=6100;
                form.pinnedArtSerial="1023417009";form.pinnedPicoSerial="13000/0059";
                form.oscillationCircuit.Checked=true;form.wiring.Checked=form.calibrationConnected.Checked=true;
                form.SaveParameterPreferences();
                var calibration=form.ReadSettings();calibration.Input.ArtSerial=form.pinnedArtSerial;calibration.Input.PicoSerial=form.pinnedPicoSerial;
                form.applyingCalibrationValue=true;form.values["pd"].Value=2421.279M;form.applyingCalibrationValue=false;
                calibration.Input.PdCalibrationPcPerV=2421.279;form.appliedCalibrationProfile=calibration;form.calibrationOrigin.Text="实测校准 测试来源";form.SaveParameterPreferences();
                verify(File.Exists(store.PathFor(1)) && File.Exists(store.PathFor(0)),"profiles separate");
                string xml=File.ReadAllText(store.PathFor(1));
                verify(!xml.Contains("Confirmed") && !xml.Contains("Emergency") && !xml.Contains("Sequence") && !xml.Contains("TargetKv"),"no output/confirmation state serialized");
                form.mode.SelectedIndex=0;
                verify(!form.repeatPrompts.Checked,"return to simulation restores its independent prompt preference");
                verify(form.values["pressure"].Value==expected.Numbers.Find(n=>n.Name=="pressure").Value,"simulation profile restored on mode return");
            }
            using(var form=new FactoryMainForm(Path.Combine(root,"labels3"),profiles))
            {
                form.smokeMode=true;form.mode.SelectedIndex=1;
                verify(form.values["divider"].Value==12000 && form.values["pressure"].Value==6100,"hardware values survive new form");
                verify(form.values["pd"].Value==2421.279M && form.appliedCalibrationProfile!=null && form.storedCalibration.Text.Contains("2421.279") && form.calibrationOrigin.Text.Contains("测试来源"),"coefficient and provenance survive restart");
                var match=form.appliedCalibrationProfile.Clone();form.RequireAppliedCalibrationProfile(match);match.Input.PicoSerial="other";
                bool mismatch=false;try{form.RequireAppliedCalibrationProfile(match);}catch(InvalidOperationException){mismatch=true;}verify(mismatch,"restored calibration rejects different device");
                form.values["pd"].Value=2450;verify(form.appliedCalibrationProfile==null,"manual coefficient edit removes certified provenance");
                form.SaveParameterPreferences();
                verify(form.pinnedArtSerial=="1023417009" && form.pinnedPicoSerial=="13000/0059" && form.artIdentity.Text.Contains("待匹配") &&
                    form.ReadSettings().Input.ArtSerial=="" && form.ReadSettings().Input.PicoSerial=="","remembered serials not declared connected");
                verify(!form.wiring.Checked && !form.calibrationConnected.Checked && form.oscillationCircuit.Checked,"saved circuit configuration not a saved per-round acknowledgement");
                var found=new DiscoveryResult();found.ArtDevices.Add(new ArtDeviceIdentity {Name="Dev1",Model="USB3135",Serial="1023417009"});found.PicoSerials.Add("13000/0059");
                form.ApplyDevices(found);
                verify(form.ReadSettings().Input.ArtSerial=="1023417009" && form.ReadSettings().Input.PicoSerial=="13000/0059" && form.oscillationCircuit.Checked,"synthetic exact identity match preserves configured circuit");
                found.PicoSerials.Clear();found.PicoSerials.Add("99999/9999");form.ApplyDevices(found);
                verify(form.ReadSettings().Input.PicoSerial=="" && form.oscillationCircuit.Checked && !form.wiring.Checked,"missing saved card not replaced, configuration retained without permission");
            }
            string legacyProfiles=Path.Combine(root,"legacy-long-duration");
            var legacyStore=new FactoryParameterStore(legacyProfiles);
            using(var form=new FactoryMainForm(Path.Combine(root,"legacy-labels1"),legacyProfiles))
            {
                form.smokeMode=true;form.values["duration"].Value=100;form.values["pulsecount"].Value=73;
                form.SaveParameterPreferences();
            }
            using(var form=new FactoryMainForm(Path.Combine(root,"legacy-labels2"),legacyProfiles))
            {
                form.smokeMode=true;
                verify(form.values["duration"].Value==100,"legacy long duration stays visible instead of silently clamping to 60 ms");
                verify(form.values["pulsecount"].Value==73 && form.values["pulsecount"].Parent==null,
                    "legacy target count is preserved as metadata, without a misleading time-segment input");
                string unchanged=File.ReadAllText(legacyStore.PathFor(0));bool durationRejected=false;
                try{form.ReadSettings().ValidateBeforeOperatorConfirmation(FactoryRunKind.Calibration);}
                catch(ArgumentException error){durationRejected=error.Message.Contains("60 ms");}
                verify(durationRejected,"legacy long calibration profile requires an explicit valid duration before a new run");
                verify(File.ReadAllText(legacyStore.PathFor(0))==unchanged && form.values["duration"].Value==100,
                    "rejected new-run validation does not rewrite the historical duration or stored profile");
            }
            var bad=store.Read(0);bad.Numbers.Find(v=>v.Name=="pressure").Value=-10;store.Save(0,bad);
            using(var form=new FactoryMainForm(Path.Combine(root,"labels4"),profiles))
            {
                form.smokeMode=true;form.mode.SelectedIndex=0;
                verify(form.values["pressure"].Value==6000 && form.values["divider"].Value==11000 && form.parameterPersistence.Text.Contains("未载入"),"out-of-range snapshot rejected as a whole");
            }
            verify(File.Exists(store.PathFor(0)+".bak"),"atomic replacement retains previous file");
            File.WriteAllText(store.PathFor(0),"<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///never-read'>]><FactoryParameterProfile>&e;</FactoryParameterProfile>");
            using(var form=new FactoryMainForm(Path.Combine(root,"labels5"),profiles))
            {
                form.smokeMode=true;form.mode.SelectedIndex=0;
                verify(form.parameterPersistence.Text.Contains("未载入") && !form.busy,"DTD/corrupt file does not authorize or crash startup");
            }
            TestDeviceBoundParameters(Path.Combine(root,"device-profiles"),verify);
            TestDeferredParameterSave(Path.Combine(root,"editing-profiles"),verify);
            TestLegacyRepeatPrompts(Path.Combine(root,"legacy-prompt-profiles"),verify);
            return "R28 parameter persistence: "+checks+" checks PASS; isolated UI/XML fixtures; HardwareAccessed=False; "+root;
        }

        private static void TestDeferredParameterSave(string profiles,Action<bool,string> verify)
        {
            var store=new FactoryParameterStore(profiles);
            using(var form=new FactoryMainForm(Path.Combine(profiles,"labels"),profiles))
            {
                form.smokeMode=true;form.ShowInTaskbar=false;form.Opacity=0;form.Show();
                form.tabs.SelectedTab=form.parametersPage;Application.DoEvents();
                form.SaveParameterPreferences();
                string before=File.ReadAllText(store.PathFor(0));DateTime written=File.GetLastWriteTimeUtc(store.PathFor(0));
                var editor=form.values["divider"];editor.Focus();
                TextBox editBox=null;foreach(Control control in editor.Controls)if(control is TextBox)editBox=(TextBox)control;
                verify(editBox!=null && editor.ContainsFocus,"deferred-save fixture has a focused numeric editor");
                int changed=0;editor.ValueChanged+=(s,e)=>++changed;
                // Committed A schedules the save; typing B must stay untouched
                // even after that already-scheduled debounce timer fires.
                form.values["current"].Value=9;
                editBox.Text="12345.6";string pending=editor.Text,label=form.parameterPersistence.Text;
                Thread.Sleep(550);Application.DoEvents();
                verify(editor.Text==pending && changed==0,"A save timer does not commit/reformat unfinished B text");
                verify(form.parameterSaveDeferredForEdit && !form.parameterSaveTimer.Enabled && form.parameterPersistence.Text==label,"save parks until edit completion without polling or label churn");
                verify(File.ReadAllText(store.PathFor(0))==before && File.GetLastWriteTimeUtc(store.PathFor(0))==written,"deferred timer never rewrites the saved profile");
                form.start.Focus();form.Validate();
                verify(!editor.ContainsFocus && form.parameterSaveTimer.Enabled,"leaving/validating the editor reschedules the pending save");
                Thread.Sleep(550);Application.DoEvents();
                var saved=store.Read(0);
                verify(saved.Numbers.Find(v=>v.Name=="current").Value==9 && saved.Numbers.Find(v=>v.Name=="divider").Value==12345.6M,"after leaving both committed A and final B are saved");
                verify(!form.parameterSaveDeferredForEdit && !form.parameterSaveTimer.Enabled,"completed save has no endlessly deferred/armed timer");
                editor.Focus();editBox.Text="13000.9";
                verify(form.SaveParameterPreferences(),"explicit save remains available while editing");
                verify(store.Read(0).Numbers.Find(v=>v.Name=="divider").Value==13000.9M && !form.parameterSaveTimer.Enabled,"explicit snapshot commits the final value once without scheduling another autosave");
                editBox.Text="14000.8";form.Close();
                verify(store.Read(0).Numbers.Find(v=>v.Name=="divider").Value==14000.8M,"closing the form still flushes the final edited configuration");
            }
        }

        private static void TestDeviceBoundParameters(string profiles,Action<bool,string> verify)
        {
            var store=new FactoryParameterStore(profiles);
            var found=new DiscoveryResult();
            found.ArtDevices.Add(new ArtDeviceIdentity {Name="Dev1",Model="USB3135",Serial="100001"});
            found.ArtDevices.Add(new ArtDeviceIdentity {Name="Dev2",Model="USB3135",Serial="100002"});
            found.PicoSerials.Add("PICO/A");found.PicoSerials.Add("PICO/B");
            FactoryParameterProfile profileA;
            using(var form=new FactoryMainForm(Path.Combine(profiles,"labels-a"),profiles))
            {
                form.smokeMode=true;form.mode.SelectedIndex=1;form.ApplyDevices(found);
                form.artChoice.SelectedIndex=0;form.picoChoice.SelectedIndex=0;
                verify(form.pinnedArtSerial=="100001" && form.pinnedPicoSerial=="PICO/A","selecting both serials binds profile A");
                form.values["divider"].Value=12000;form.values["pressure"].Value=6100;
                form.values["inductance"].Value=.812345M;form.values["resistance"].Value=32.123M;
                form.values["pd"].Value=2345.678M;form.continuous.Checked=true;form.repeatCount.Value=9;
                form.repeatPrompts.Checked=false;
                form.values["locationsavecycles"].Value=3;
                form.rawPd.ChargeRulerMaximumPc=3456;form.rawPd.BoxZoomEnabled=false;
                form.acceptanceLoad.Value=205;form.voltageReferenceId.Text="V-A";form.pdReferenceId.Text="PD-A";
                var calibration=form.ReadSettings();form.appliedCalibrationProfile=calibration;form.calibrationOrigin.Text="A calibration";
                profileA=form.SnapshotParameters();
                form.targetKv.Value=15;form.wiring.Checked=form.calibrationConnected.Checked=true;
                // No explicit save: changing identities must flush a pending edit
                // to A before any new B defaults are applied.
                form.artChoice.SelectedIndex=1;form.picoChoice.SelectedIndex=1;
                verify(form.pinnedArtSerial=="100002" && form.pinnedPicoSerial=="PICO/B","selecting B binds different profile");
                verify(form.values["divider"].Value==11000 && form.values["pressure"].Value==6000 && form.values["inductance"].Value==.7M && form.values["resistance"].Value==22 && form.appliedCalibrationProfile==null,"unseen B does not inherit A conversions, circuit or calibration");
                verify(form.rawPd.ChargeRulerMaximumPc==0 && form.rawPd.BoxZoomEnabled,"new device uses default display settings");
                verify(form.repeatPrompts.Checked,"unseen device B defaults to showing repeat prompts");
                verify(form.values["locationsavecycles"].Value==1,"new device defaults to one location cycle");
                verify(!form.wiring.Checked && !form.calibrationConnected.Checked && form.targetKv.Value==15,"device switch doesn't restore permission or set target");
                form.values["divider"].Value=15000;form.values["pressure"].Value=7000;form.values["inductance"].Value=.91M;form.values["resistance"].Value=41;
                form.values["pd"].Value=3456;form.repeatCount.Value=3;form.acceptanceLoad.Value=180;form.voltageReferenceId.Text="V-B";
                form.values["locationsavecycles"].Value=100;
                form.artChoice.SelectedIndex=0;form.picoChoice.SelectedIndex=0;
                foreach(var item in profileA.Numbers)verify(form.values[item.Name].Value==item.Value,"return A restores numeric parameter "+item.Name);
                verify(form.repeatCount.Value==9 && form.continuous.Checked && form.acceptanceLoad.Value==205 && form.voltageReferenceId.Text=="V-A" && form.pdReferenceId.Text=="PD-A","return A restores its own other settings");
                verify(!form.repeatPrompts.Checked && !store.ReadForDevice(1,"100001","PICO/A").RepeatPrompts,"return A restores and saves only its suppressed prompt preference");
                verify(form.rawPd.ChargeRulerMaximumPc==3456 && !form.rawPd.BoxZoomEnabled,"return A restores its own ruler and drag preference");
                verify(form.appliedCalibrationProfile!=null && form.calibrationOrigin.Text=="A calibration" && form.values["pd"].Value==2345.678M,"return A restores its coefficient/provenance");
                form.SaveParameterPreferences();
                verify(store.ReadForDevice(1,"100002","PICO/B").Numbers.Find(n=>n.Name=="divider").Value==15000,"B saved independently");
                string before=File.ReadAllText(store.PathForDevice(1,"100001","PICO/A"));
                form.mode.SelectedIndex=0;form.values["divider"].Value=9999;form.SaveParameterPreferences();
                verify(File.ReadAllText(store.PathForDevice(1,"100001","PICO/A"))==before,"simulation cannot overwrite hardware A");
                form.mode.SelectedIndex=1;
            }
            using(var form=new FactoryMainForm(Path.Combine(profiles,"labels-b"),profiles))
            {
                form.smokeMode=true;
                verify(form.mode.SelectedIndex==1 && form.pinnedArtSerial=="100001" && form.pinnedPicoSerial=="PICO/A" && form.values["divider"].Value==12000 && form.targetKv.Value==5 && form.devices==null && !form.busy,"restart restores last device/profile, not target or hardware state");
                verify(!form.repeatPrompts.Checked,"device A prompt suppression survives reopening");
                form.ApplyDevices(found);form.artChoice.SelectedIndex=1;form.picoChoice.SelectedIndex=1;
                verify(form.repeatPrompts.Checked && store.ReadForDevice(1,"100002","PICO/B").RepeatPrompts,"device B prompt preference remains independent after reopening");
                verify(form.values["locationsavecycles"].Value==100 && form.ReadSettings().LocationSaveCycleCount==100,"device B restores N into actual capture settings");
                verify(form.values["divider"].Value==15000 && form.values["pressure"].Value==7000 && form.values["inductance"].Value==.91M && form.values["resistance"].Value==41 && form.values["pd"].Value==3456 && form.repeatCount.Value==3 && form.voltageReferenceId.Text=="V-B","B settings survive closing and reopening");
                var missing=new DiscoveryResult();form.ApplyDevices(missing);form.SaveParameterPreferences();
                verify(form.pinnedArtSerial=="100002" && form.pinnedPicoSerial=="PICO/B" && form.values["divider"].Value==15000 && form.ReadSettings().Input.ArtSerial=="","unplugged cards retain bound values but are not connected");
                form.ApplyDevices(found);
                verify(form.values["divider"].Value==15000 && form.ReadSettings().Input.PicoSerial=="PICO/B","re-enumerating same pair doesn't reset parameters");
                var older=store.ReadForDevice(1,"100001","PICO/A");older.Numbers.RemoveAll(n=>n.Name=="inductance");store.Save(1,older);
                form.artChoice.SelectedIndex=0;form.picoChoice.SelectedIndex=0;
                verify(form.values["inductance"].Value==.7M && form.values["resistance"].Value==32.123M,"missing old-profile fields use defaults, never the preceding device value");
                form.artChoice.SelectedIndex=1;form.picoChoice.SelectedIndex=1;
                // A bad target profile must not leave B controls under A's identity.
                var bad=store.ReadForDevice(1,"100001","PICO/A");bad.Numbers.Find(n=>n.Name=="pressure").Value=-1;store.Save(1,bad);
                form.artChoice.SelectedIndex=0; // intermediate ART-A + PICO/B has its own defaults
                decimal before=form.values["pressure"].Value;form.picoChoice.SelectedIndex=0;
                verify(form.picoChoice.SelectedItem as string=="PICO/B" && form.pinnedPicoSerial=="PICO/B" && form.values["pressure"].Value==before,"corrupt selected profile rejected before identity/values mutation");
            }
            verify(store.PathForDevice(1,"a/b","c")!=store.PathForDevice(1,"a","b/c") && Path.GetFileName(store.PathForDevice(1,"../a","b")).Length==68,"identity paths cannot collide through separators or traverse directories");
            verify(store.ReadForDevice(1,"unknown","unknown")==null,"unknown pair never falls back to last-used device");
            var mismatch=profileA.Clone();mismatch.ArtSerial="different";
            bool rejected=false;try{store.Save(1,mismatch);}catch(InvalidDataException){rejected=true;}
            verify(rejected,"calibration cannot be saved against a different serial");
            var invalidRuler=profileA.Clone();invalidRuler.LocationChargeMaximumPc=-1;
            rejected=false;try{store.Save(1,invalidRuler);}catch(InvalidDataException){rejected=true;}
            verify(rejected,"negative saved location ruler cannot be persisted");
        }

        private static void TestLegacyRepeatPrompts(string profiles,Action<bool,string> verify)
        {
            var store=new FactoryParameterStore(profiles);
            using(var form=new FactoryMainForm(Path.Combine(profiles,"new-labels"),profiles))
            {
                form.smokeMode=true;
                verify(form.repeatPrompts.Checked && new FactoryParameterProfile().RepeatPrompts,"repeat prompts default to enabled");
                form.repeatPrompts.Checked=false;
                Thread.Sleep(550);Application.DoEvents();
                verify(!store.Read(0).RepeatPrompts,"changing only repeat-prompt checkbox triggers autosave");
            }
            string legacy=File.ReadAllText(store.PathFor(0)).Replace("<RepeatPrompts>false</RepeatPrompts>","");
            verify(!legacy.Contains("<RepeatPrompts>"),"legacy XML fixture omits the new preference field");
            File.WriteAllText(store.PathFor(0),legacy);
            verify(store.Read(0).RepeatPrompts,"old XML without the preference defaults to enabled");
            using(var form=new FactoryMainForm(Path.Combine(profiles,"legacy-labels"),profiles))
            {
                form.smokeMode=true;
                verify(form.repeatPrompts.Checked,"old XML restores enabled prompt checkbox without a saved permission");
            }
        }
    }
}
