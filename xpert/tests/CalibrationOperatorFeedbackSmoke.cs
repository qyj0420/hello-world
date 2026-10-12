using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class CalibrationOperatorFeedbackSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks, failures;
    static object Call(object o,string n,params object[] a) { return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a); }
    static object F(object o,string n) { return o.GetType().GetField(n,B).GetValue(o); }
    static void SF(object o,string n,object v) { o.GetType().GetField(n,B).SetValue(o,v); }
    static void Set(object o,string n,object v) { o.GetType().GetProperty(n,B).SetValue(o,v,null); }
    static object Get(object o,string n) { return o.GetType().GetProperty(n,B).GetValue(o,null); }
    static void Check(bool ok,string why) { checks++; if(!ok)failures++; Console.WriteLine((ok?"PASS ":"FAIL ")+why); }
    [STAThread] static int Main(string[] args)
    {
        try {
            string root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
            var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
            var type=asm.GetType("HVX.Controls.CalibrationProgramControl",true);
            Application.EnableVisualStyles();
            using(var c=(Control)Activator.CreateInstance(type)) {
                Set(c,"EmbeddedMode",true);c.Size=new Size(1500,950);c.CreateControl();
                double[] samples=Enumerable.Range(0,4096).Select(i=>Math.Exp(-Math.Pow((i-400)/8d,2))+.4*Math.Exp(-Math.Pow((i-1400)/8d,2))-.3*Math.Exp(-Math.Pow((i-2000)/8d,2))).ToArray();
                foreach(string level in new[]{"TDR","5nC","100nC"}) {
                    Set(c,"SelectedLevelCode",level);Call(c,"SetPhaseParameters",-1,680d,170d,false,true);
                    Call(c,"SetPhaseWaveform",-1,level,samples,0d,8e-9);
                    Call(c,"SetPhaseMarkers",-1,level,400*8e-9,1400*8e-9,1400*8e-9,680d);
                    Call(c,"SavePng",Path.Combine(root,level+".png"),1500,950);
                    var phase=Call(c,"GetPhaseStateNoLock",-1);var wave=((IDictionary)F(phase,"Waveforms"))[level];
                    var markers=((IDictionary)F(phase,"Markers"))[level];
                    var plot=(RectangleF)Get(c,"CurrentPlotRectangle");
                    float x=Convert.ToSingle(Call(c,"TimeToReferenceX",1400*8e-9));
                    Check((int)Call(c,"HitMarker",new PointF(x-64,plot.Top+155),markers)==2,level+" reflection badge is draggable");
                    SF(c,"draggingCursor",2);SF(c,"dragOriginalIncidentTime",400*8e-9);SF(c,"dragOriginalReflectionTime",1400*8e-9);
                    float target=Convert.ToSingle(Call(c,"TimeToReferenceX",2000*8e-9));
                    Call(c,"PreviewDraggedCursor",new PointF(target,plot.Top+220));
                    markers=((IDictionary)F(phase,"Markers"))[level];
                    Check(Math.Abs(Convert.ToDouble(F(markers,"DistanceMeters"))-1088)<.1,level+" live preview distance follows dragged reflection");
                    Call(c,"FinishCursorDrag");SF(c,"draggingCursor",0);
                    markers=((IDictionary)F(phase,"Markers"))[level];
                    Check(Math.Abs(Convert.ToDouble(F(markers,"DistanceMarkerSeconds"))-2000*8e-9)<1e-10,level+" manual reflection outside old geometry persists");
                    Check(samples.SequenceEqual((double[])F(wave,"Samples")),level+" raw samples unchanged");
                }
                Set(c,"CalibrateAllLevels",false);Set(c,"SelectedLevelCode","TDR");
                object ui=Get(c,"CompactLayout");var area=(RectangleF)Call(c,"CompactLevelListBounds",ui);
                Check((bool)Call(c,"HandleCompactLevelClick",new PointF(area.Left+20,area.Top+2+28+10),ui),"common levels directly clickable without all-level mode");
                Check((string)Get(c,"SelectedLevelCode")=="100nC","common level click selects correct code");
                var resolve=type.GetMethod("ResolveHardwareLevelCodes",B);
                Check(resolve!=null,"batch request has explicit level resolver, not a numeric ALL_LEVELS charge");
                if(resolve!=null) {
                    string[] values=(string[])resolve.Invoke(c,new object[]{"ALL_LEVELS",true});
                    Check(values.Length==15&&!values.Contains("TDR")&&values.Contains("5nC")&&values.Contains("50pC"),"all-level batch validates actual charge levels");
                    foreach(string code in new[]{"5 nC","50 pC","TDR"})Check(((string[])resolve.Invoke(c,new object[]{code,false})).Length==1,"single displayed level resolves: "+code);
                    bool rejected=false;try{resolve.Invoke(c,new object[]{"ALL_LEVELS",false});}catch(TargetInvocationException){rejected=true;}Check(rejected,"batch identifier cannot masquerade as a single charge");
                }
                Set(c,"SelectedLevelCode","TDR");
                Check((string)Call(c,"GetLevelSaveState",-1,"TDR")=="Unsaved","acquired level is not labelled saved");
                ((IDictionary)F(c,"savedLevelSignatures"))["-1:TDR"]=Call(c,"LevelSignature",-1,"TDR");
                Check((string)Call(c,"GetLevelSaveState",-1,"TDR")=="Saved","saved status uses current level signature");
                Check((string)Call(c,"GetLevelSaveState",0,"TDR")=="Empty","saved state is phase specific");
                Call(c,"SetPhaseMarkers",-1,"TDR",400*8e-9,1500*8e-9,1400*8e-9,748d);
                Check((string)Call(c,"GetLevelSaveState",-1,"TDR")=="Changed","cursor change requires resaving");
                Set(c,"UseFirstArrivalTiming",true);
                Call(c,"SetPhaseMarkers",-1,"TDR",400*8e-9,1400*8e-9,1400*8e-9,680d);
                SF(c,"draggingCursor",2);SF(c,"dragOriginalIncidentTime",400*8e-9);SF(c,"dragOriginalReflectionTime",1400*8e-9);
                Call(c,"PreviewDraggedCursor",new PointF(Convert.ToSingle(Call(c,"TimeToReferenceX",2000*8e-9)),400));
                Call(c,"FinishCursorDrag");SF(c,"draggingCursor",0);
                var current=Call(c,"GetPhaseStateNoLock",-1);var finalMarker=((IDictionary)F(current,"Markers"))["TDR"];
                Check(Math.Abs((double)F(finalMarker,"DistanceMarkerSeconds")-2000*8e-9)<1e-10,"first-arrival mode also permits an explicitly chosen new reflection");
                SF(c,"draggingCursor",2);SF(c,"dragOriginalIncidentTime",400*8e-9);SF(c,"dragOriginalReflectionTime",2000*8e-9);
                Call(c,"PreviewDraggedCursor",new PointF(Convert.ToSingle(Call(c,"TimeToReferenceX",2400*8e-9)),400));Call(c,"ClearCursorSelection");
                finalMarker=((IDictionary)F(current,"Markers"))["TDR"];
                Check(Math.Abs((double)F(finalMarker,"DistanceMeters")-1088)<1e-5,"Escape restores the original measured distance");
                foreach(double position in new[]{110d,112d,119.8,120d,121d,123d,125d,126d})Call(c,"AddPhaseJoint",-1,"接头 "+position,position);
                Set(c,"JointMarkersVisible",true);
                using(var bitmap=new Bitmap(1500,950))using(var graphics=Graphics.FromImage(bitmap))using(var font=new Font("Microsoft YaHei UI",8.4f,FontStyle.Bold)) {
                    var layouts=((IEnumerable)Call(c,"GetJointMarkerLayout",graphics,font,((IDictionary)F(current,"Waveforms"))["TDR"],Call(c,"GetPhaseJoints",-1),finalMarker,170d)).Cast<object>().ToArray();
                    Check(layouts.Length==8,"clustered joints remain individually labelled");
                    Check(!layouts.Where((a,i)=>layouts.Skip(i+1).Any(b=>((RectangleF)F(a,"Tag")).IntersectsWith((RectangleF)F(b,"Tag")))).Any(),"joint label rectangles do not overlap");
                }
                Call(c,"SavePng",Path.Combine(root,"flat-levels-and-joints.png"),1500,950);
            }
            var planning=asm.GetType("Xiongxin.Calibration.AcquisitionPlanning",true);var windowType=asm.GetType("HVX.OWTS.Task38.CalibrationWindowResult",true);
            var input=Activator.CreateInstance(windowType);SF(input,"Windows",new[]{new[]{1d,2d,3d,4d},new[]{2d,4d,6d,8d}});
            bool qualityRejected=false;try{planning.GetMethod("RejectOutliers",B).Invoke(null,new[]{input});}catch(TargetInvocationException e){qualityRejected=e.InnerException.GetType().Name=="CalibrationQualityReviewException";}
            Check(qualityRejected,"insufficient pulses produce an explicit review choice, not generic capture success");
            var continued=planning.GetMethod("ContinueForReview",B).Invoke(null,new[]{input});
            Check(((double[])F(continued,"Average")).SequenceEqual(new[]{1.5,3d,4.5,6d}),"manual review average agrees with independent arithmetic");
            Check(((string)F(continued,"Message")).Contains("待复核"),"quality override retains review-only label");
            foreach(double[][] invalid in new[]{new double[0][],new[]{new[]{Double.NaN,1d}},new[]{new[]{1d,2d},new[]{1d,2d,3d}}}) {
                SF(input,"Windows",invalid);bool rejected=false;try{planning.GetMethod("ContinueForReview",B).Invoke(null,new[]{input});}catch(TargetInvocationException){rejected=true;}
                Check(rejected,"continue does not fabricate missing or invalid waveform values");
            }
            var store=asm.GetType("HVX.Controls.CalibrationProjectStore",true);
            string project=Path.Combine(root,"readonly");Directory.CreateDirectory(project);
            string item=Path.Combine(project,"ITEM.in"),cable=Path.Combine(project,"CABLE.in");
            File.WriteAllText(item,"A相电缆长度: = \"180\"\r\n");
            File.WriteAllText(cable,"0\tA\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\t塑料绝缘\t\r\n1\t末端\t终端\t180m\r\n");
            byte[] original=File.ReadAllBytes(cable);
            foreach(string p in new[]{item,cable})File.SetAttributes(p,File.GetAttributes(p)|FileAttributes.ReadOnly);
            bool saved=false;
            try {store.GetMethod("SaveLength",B).Invoke(null,new object[]{project,"A",190d,152d});saved=true;}
            catch(TargetInvocationException ex){Console.WriteLine("SAVE_ERROR="+ex.InnerException.Message);}
            Check(saved&&File.ReadAllText(cable).Contains("190m"),"read-only project length can be saved");
            foreach(string p in new[]{item,cable})Check((File.GetAttributes(p)&FileAttributes.ReadOnly)!=0,"read-only restored: "+Path.GetFileName(p));
            if(saved) {
                byte[] itemBefore=File.ReadAllBytes(item),cableBefore=File.ReadAllBytes(cable);
                var hook=store.GetField("BeforePublishForTests",B);hook.SetValue(null,(Action<int,string>)((i,p)=>{if(i==1)throw new IOException("injected second publish failure");}));
                bool rejected=false;
                try {store.GetMethod("SaveLength",B).Invoke(null,new object[]{project,"A",200d,152d});}catch(TargetInvocationException){rejected=true;}finally{hook.SetValue(null,null);}
                Check(rejected&&itemBefore.SequenceEqual(File.ReadAllBytes(item))&&cableBefore.SequenceEqual(File.ReadAllBytes(cable)),"second-file failure rolls back original bytes");
                Check(new[]{item,cable}.All(p=>(File.GetAttributes(p)&FileAttributes.ReadOnly)!=0),"rollback restores read-only on both files");
                Check(Directory.GetFiles(project,"*.bak").Any(p=>original.SequenceEqual(File.ReadAllBytes(p))),"recoverable original metadata backup retained");
            }
            Console.WriteLine("CALIBRATION_OPERATOR_FEEDBACK_CHECKS="+checks+"; FAILURES="+failures);return failures==0?0:1;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 2;}
    }
}
