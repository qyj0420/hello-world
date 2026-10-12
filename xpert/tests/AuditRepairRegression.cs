using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.ProjectCables;
using HVX.Controls;
static class AuditRepairRegression
{
 static int checks;
 const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
 static void Check(bool value,string label){if(!value)throw new Exception(label);checks++;}
 static void Refuses(Action work,string label){bool rejected=false;try{work();}catch(IOException){rejected=true;}Check(rejected,label);}
 [STAThread] static int Main(string[] args){try{
  string root=Path.Combine(Path.GetFullPath(args[0]),"regression-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  string item=Path.Combine(root,"ITEM.in"),cable=Path.Combine(root,"CABLE.in");
  File.WriteAllText(item,"电缆芯数 = 三相单芯\r\n",new UTF8Encoding(false));
  File.WriteAllText(cable,"0\tA\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t400m\r\n",new UTF8Encoding(false));
  byte[] original=File.ReadAllBytes(cable);string ch=CableJointStore.Hash(original),ih=CableJointStore.Hash(File.ReadAllBytes(item));
  File.WriteAllText(item,"电缆芯数 = 单相\r\n",new UTF8Encoding(false));
  Refuses(()=>CalibrationProjectStore.ReviewJoints(root,new[]{"A"},new[]{80d},new double[0],ch,ih),"A01 stale ITEM rejected");
  Check(original.SequenceEqual(File.ReadAllBytes(cable)),"A01 no cable mutation on refusal");
  bool concurrentBlocked=false;
  CalibrationProjectStore.BeforePublishForTests=(i,p)=>{try{File.AppendAllText(item,"changed");}catch(IOException){concurrentBlocked=true;}};
  try{CalibrationProjectStore.ReviewJoints(root,new[]{"A"},new[]{80d},new double[0],ch,CableJointStore.Hash(File.ReadAllBytes(item)));}
  finally{CalibrationProjectStore.BeforePublishForTests=null;}
  Check(concurrentBlocked&&File.ReadAllText(cable).Contains("80m"),"A01 ITEM lease protects publish; valid cable commits");
  string big=Path.Combine(root,"big.ini");using(var s=new FileStream(big,FileMode.CreateNew,FileAccess.Write))s.SetLength(32*1024*1024);
  GC.Collect();long start=GC.GetTotalMemory(false);bool rejected=false;
  try{JointMaterialCatalog.Read(big);}catch(InvalidDataException){rejected=true;}
  Check(rejected&&GC.GetTotalMemory(false)-start<4*1024*1024,"A02 bounded read rejects without 32MiB allocation");
  string material=Path.Combine(root,"materials.ini");File.WriteAllText(material,"[Materials]\nitem=XLPE|170\n");
  var catalog=JointMaterialCatalog.Read(material);catalog.Entries.Add(new JointMaterialEntry{Name="PVC",Velocity=150});catalog.Save();
  Check(JointMaterialCatalog.Read(material).Entries.Count==2,"A02 valid dictionary round trip");
  using(var s=new FileStream(material,FileMode.Open,FileAccess.Write))s.SetLength(32*1024*1024);
  rejected=false;try{catalog.Save();}catch(InvalidDataException){rejected=true;}
  Check(rejected&&new FileInfo(material).Length==32*1024*1024,"A02 oversized replacement preserved/refused");
  var known=PressureWaveformView.PrepareData(new double[101],1000,new double[2001],2000,CancellationToken.None);
  Check(known.TimeKnown&&Math.Abs(known.FullDuration-.1)<1e-12,"A03 common shorter duration; independent rates");
  Check(PressureWaveformView.VisibleLastIndex(.10025,2000,2001)==200,"A03 no sample after fractional endpoint");
  Check(PressureWaveformView.VisibleLastIndex(.1,1000,101)==100,"A03 exact last sample retained");
  foreach(double amplitude in new[]{0d,1e-5,.5,5,6,40}) {
   var data=PressureWaveformView.PrepareData(new[]{amplitude,-amplitude},1000,new[]{0d,0d},1000,CancellationToken.None);
   Check(data.VoltageAbs>=5&&data.VoltageAbs>=amplitude,"A04 voltage axis floor/no clipping "+amplitude);
  }
  var unknown=PressureWaveformView.PrepareData(new double[101],double.NaN,new double[51],2000,CancellationToken.None);
  Check(!unknown.TimeKnown&&unknown.VoltageRate==0&&unknown.DischargeRate==2000&&unknown.FullDuration==100,"A05 unknown remains explicit sample coordinate");
  var single=PressureWaveformView.PrepareData(null,0,new double[101],1000,CancellationToken.None);
  Check(single.TimeKnown&&Math.Abs(single.FullDuration-.1)<1e-12,"single valid channel retains timebase");
  foreach(double extent in new[]{.5,1,9.75,100,1e9})Check(PressureWaveformView.HorizontalTicks(extent).All(t=>t==Math.Floor(t)&&t<=extent),"integer ticks no endpoint expansion");
  using(var view=new PressureWaveformView()) {
   view.SetData(new double[101],1000,new double[2001],2000);
   Check((double)typeof(PressureWaveformView).GetField("cachedFullDuration",F).GetValue(view)==known.FullDuration,"sync/async display extent parity");
   view.InstallPreparedData(unknown);
   Check(!(bool)typeof(PressureWaveformView).GetField("cachedTimeKnown",F).GetValue(view),"unknown axis flag installed");
   view.Size=new System.Drawing.Size(900,400);using(var b=new System.Drawing.Bitmap(900,400))view.DrawToBitmap(b,view.ClientRectangle);
   view.InstallPreparedData(known);using(var b=new System.Drawing.Bitmap(900,400))view.DrawToBitmap(b,view.ClientRectangle);
   Check(true,"known/unknown waveform paint completes");
  }
  using(var icon=new System.Drawing.Icon(args[1]))using(var dialog=new CableJointDialog(CableJointStore.CreatePreview("A",400,"XLPE"),80,"A",0,icon)) {
   dialog.ConfigureMaterialCatalog(Path.Combine(root,"new-catalog.ini"));
   var tcs=new TaskCompletionSource<bool>();dialog.ConfigureCommitAsync((p,o,c)=>tcs.Task);
   typeof(CableJointDialog).GetMethod("SubmitPrepared",F).Invoke(dialog,null);
   var message=(TextBox)typeof(CableJointDialog).GetField("message",F).GetValue(dialog);
   var busy=(ProgressBar)typeof(CableJointDialog).GetField("saveBusy",F).GetValue(dialog);
   Check(dialog.IsCommitPending&&message.ReadOnly&&message.Enabled,"A06 read-only message remains copyable");
   Check(busy.MarqueeAnimationSpeed>0,"busy indicator active");
   tcs.SetCanceled();var end=DateTime.UtcNow.AddSeconds(3);while(dialog.IsCommitPending&&DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(1);}
   Check(!dialog.IsCommitPending&&busy.MarqueeAnimationSpeed==0,"cancel restores busy state");
  }
  Console.WriteLine("AUDIT_REPAIR_PASS="+checks+"; QA="+root);return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
