using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Isolated source-built control and synthetic archive only. No model, learning,
// product entry point, worker, driver or device operation is invoked.
internal static class CompetitionSharedMenuOfflineSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    static void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,B).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,B).SetValue(o,v,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static string Hash(string path){using(var f=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
    static IEnumerable<Control> Descendants(Control control){yield return control;foreach(Control child in control.Controls)foreach(var x in Descendants(child))yield return x;}
    static double[,] Matrix(){var a=new double[8,36];double[] v={4.4,8.7,12,17.4,20,8.7,8.7,12};for(int r=0;r<8;r++){a[r,0]=r==7?2:1;a[r,1]=1;a[r,2]=v[r];a[r,3]=25+r*25;a[r,5]=400;a[r,9]=r;a[r,13]=r==4?-1:80+r*30;a[r,15]=r%2==0?100+r:-100-r;a[r,16]=999;a[r,23]=100+r*100;a[r,24]=1e6+r*1e6;a[r,35]=r==6?0:1;}return a;}
    static void WriteMatrix(string path,double[,] a){using(var w=new BinaryWriter(File.Create(path))){w.Write(Encoding.ASCII.GetBytes("HVXMAT01"));w.Write(a.GetLength(0));w.Write(a.GetLength(1));foreach(double d in a)w.Write(d);}}
    static string Fixture(string root){string project=Path.Combine(root,"synthetic"),dir=Path.Combine(project,"Analysis report","shared-menu");Directory.CreateDirectory(dir);WriteMatrix(Path.Combine(dir,"parameters.bin"),Matrix());WriteMatrix(Path.Combine(dir,"pdmap.bin"),new double[0,36]);File.WriteAllText(Path.Combine(dir,"session.json"),"{}");var files=new[]{"parameters.bin","pdmap.bin","session.json"}.Select(n=>new{Path=n,Bytes=new FileInfo(Path.Combine(dir,n)).Length,Sha256=Hash(Path.Combine(dir,n))}).ToArray();File.WriteAllText(Path.Combine(dir,"analysis.json"),new JavaScriptSerializer().Serialize(new{Schema="HVX.NamedAnalysis",Version=1,Id="shared-menu",Name="Synthetic menu",CreatedUtc="2026-10-11T00:00:00Z",SourceRowCount=8,Files=files,Charts=new object[0],ExpandedNodes=new string[0],SelectedIdentity=new double[0],DisplayMode=0,ViewPlane=0}));return project;}
    static int[] Rows(object host){return (int[])Get(host,"SharedSourceRowIndices");}
    static void RowCheck(object host,object left,object right,int[] rows,string why){Check(Rows(host).SequenceEqual(rows),why+" shared input");Check(((int[])Get(left,"VisibleSourceRowIndices")).SequenceEqual(rows),why+" left");Check(((int[])Get(right,"VisibleSourceRowIndices")).SequenceEqual(rows),why+" right");Check(((int[])Field(Call(host,"CaptureInput"),"Rows")).SequenceEqual(rows),why+" actual recognition snapshot");}
    static void Open(ContextMenuStrip menu){Call(menu,"OnOpening",new CancelEventArgs());}
    static ToolStripMenuItem Item(ContextMenuStrip menu,string name){return menu.Items.OfType<ToolStripMenuItem>().Single(x=>x.Name==name);}
    static void Click(ContextMenuStrip menu,string name){Open(menu);var item=Item(menu,name);Check(item.Enabled,"Menu action enabled: "+name);item.PerformClick();Application.DoEvents();}
    static void Trash(Control plot){var bounds=(Rectangle)Call(plot,"ClearSelectionBounds",plot.ClientRectangle);Check(bounds.Width>0&&bounds.Height>0,"Chart clear-region button has a usable hit target.");var click=new MouseEventArgs(MouseButtons.Left,1,bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2,0);Call(plot,"OnMouseDown",click);Call(plot,"OnMouseUp",click);Application.DoEvents();}
    static void DragRows(Control plot,params int[] rows)
    {
        var pixels=rows.Select(row=>(double[])Call(plot,"GetSourcePointPixel",row)).ToArray();Check(pixels.All(p=>p.Length==2),"Pending selection rows have genuine projected pixels.");
        int x0=(int)Math.Floor(pixels.Min(p=>p[0]))-4,x1=(int)Math.Ceiling(pixels.Max(p=>p[0]))+4;
        int y0=(int)Math.Floor(pixels.Min(p=>p[1]))-4,y1=(int)Math.Ceiling(pixels.Max(p=>p[1]))+4;
        Call(plot,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,x0,y0,0));
        Call(plot,"OnMouseMove",new MouseEventArgs(MouseButtons.Left,0,x1,y1,0));
        Call(plot,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,x1,y1,0));Application.DoEvents();
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string root=Path.GetFullPath(args[1]);if(Directory.Exists(root)||File.Exists(root))throw new IOException("Fresh fixture root required.");Directory.CreateDirectory(root);
            string project=Fixture(root);var bytes=Directory.GetFiles(project,"*",SearchOption.AllDirectories).ToDictionary(x=>x,Hash);
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            using(var host=(Control)Activator.CreateInstance(assembly.GetType("HVX.PdReview.CompetitionDefectRecognitionControl",true)))
            using(var form=new Form{Location=new Point(-20000,-20000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false,ClientSize=new Size(1400,900)}){
                form.Controls.Add(host);host.Dock=DockStyle.Fill;form.Show();Application.DoEvents();Check((bool)Call(host,"LoadProject",project)&&(bool)Call(host,"LoadSavedAnalysis","shared-menu"),"Synthetic source loaded.");
                object left=Field(host,"spectrum"),right=Field(host,"secondarySpectrum");var l=(Control)left;var r=(Control)right;
                var lm=l.ContextMenuStrip;var rm=r.ContextMenuStrip;Check(lm!=null&&rm!=null&&lm!=rm&&lm.Name=="CompetitionSpectrumContextMenu"&&rm.Name==lm.Name,"Both charts have independent host-owned complete menus.");
                string[] commands={"StatisticsSelectedRegion","ExcludeSelectedRegion","UndoSharedFilter","ShowAllSharedPulses","RectangleSelection","EllipseSelection","ShowVoltageBelowU0","ShowVoltageU0To2U0","ClearVoltageShortcut","ResetSpectrumView","ExportSpectrumData","ExportSpectrumImage"};
                foreach(var menu in new[]{lm,rm}){Open(menu);Check(menu.Items.OfType<ToolStripMenuItem>().Select(x=>x.Name).SequenceEqual(commands),"Complete ordered right-click actions present.");Check(!(bool)Get(menu==lm?left:right,"UseInternalContextMenu"),"Vendor local-only menu disabled.");Check(!menu.Items.OfType<ToolStripMenuItem>().Any(x=>x.Name=="DeleteSelectedRegion"||x.Name=="AddCableJoint"||x.Name=="CancelReviewRangeFilters"),"View menu cannot edit acceptance, joints or saved analysis.");Check(!Item(menu,"UndoSharedFilter").Enabled&&!Item(menu,"ShowAllSharedPulses").Enabled,"Unfiltered menu has no false undo/reset state.");}
                // Exercise the actual WinForms context-menu dispatch, not only
                // the Opening handler. This is an isolated control window only.
                foreach(var plot in new[]{l,r}){var menu=plot.ContextMenuStrip;try{SendMessage(plot.Handle,0x007b,plot.Handle,new IntPtr(-1));Application.DoEvents();Check(menu.Visible&&Object.ReferenceEquals(menu.SourceControl,plot),"WM_CONTEXTMENU opens this chart's host menu.");using(var image=new Bitmap(menu.Width,menu.Height)){menu.DrawToBitmap(image,menu.ClientRectangle);image.Save(Path.Combine(root,Object.ReferenceEquals(plot,l)?"context-menu-left.png":"context-menu-right.png"));}}finally{menu.Close();Application.DoEvents();}}
                var groups=Descendants(host).Where(x=>x.Name=="CompetitionSharedPhases").ToArray();Check(groups.Length==1&&groups[0].Visible,"Exactly one shared visible phase group.");
                var phases=Enumerable.Range(1,3).Select(i=>(CheckBox)Descendants(host).Single(x=>x.Name=="CompetitionPhase"+i)).ToArray();Check(phases.All(x=>x.Visible&&x.Checked),"Three shared phase controls initially shown.");
                foreach(var plot in new[]{left,right}){Check(!(bool)Get(plot,"PhaseSelectorVisible")&&!((Control)Field(plot,"phaseToolbar")).Visible,"Duplicate vendor phase group hidden.");}
                Set(host,"LeftSpectrumMode",2);Set(host,"RightSpectrumMode",4);RowCheck(host,left,right,new[]{0,1,2,3,4,5,7},"Initial accepted rows.");
                int pendingGeneration=(int)Field(host,"generation");long pendingSequence=Convert.ToInt64(Get(right,"RegionActionSequence"));
                DragRows(r,0,1,2);
                Check((int)Get(right,"SelectionRegionCount")==1&&!(bool)Get(right,"RegionStatisticsActive"),"Actual mouse release leaves one pending region, not regional statistics.");
                Check((int)Field(host,"generation")==pendingGeneration&&Convert.ToInt64(Get(right,"RegionActionSequence"))==pendingSequence,"Drawing alone neither submits an action nor invalidates recognition context.");
                RowCheck(host,left,right,new[]{0,1,2,3,4,5,7},"Pending mouse region does not filter either chart or recognition.");
                Open(rm);Check(Item(rm,"StatisticsSelectedRegion").Text=="统计选中区域","Chinese menu states explicit statistics action.");
                Click(rm,"StatisticsSelectedRegion");RowCheck(host,left,right,new[]{0,1,2},"Explicit statistics commits the pending mouse region to both charts.");
                Click(lm,"UndoSharedFilter");DragRows(r,0,1,2);Call(right,"OnKeyDown",new KeyEventArgs(Keys.Escape));
                Check((int)Get(right,"SelectionRegionCount")==0,"Escape cancels pending region.");Open(rm);Check(!Item(rm,"StatisticsSelectedRegion").Enabled,"Canceled region cannot be submitted later.");
                RowCheck(host,left,right,new[]{0,1,2,3,4,5,7},"Escape leaves committed input unchanged.");
                phases[0].Checked=false;RowCheck(host,left,right,new[]{7},"Shared phase A off.");phases[1].Checked=false;RowCheck(host,left,right,new int[0],"No source in remaining phase C.");Check(!((Control)Field(host,"recognize")).Enabled&&!((Control)Field(host,"add")).Enabled,"Empty phase set cannot identify or learn.");phases[0].Checked=phases[1].Checked=true;
                Call(right,"SetPhaseVisibility",new[]{false,true,false});Check(phases.Select(x=>x.Checked).SequenceEqual(new[]{false,true,false}),"Programmatic right phase change synchronizes single group.");Call(left,"SetPhaseVisibility",new[]{true,true,true});
                Click(lm,"EllipseSelection");Check((int)Get(left,"SelectionShapeIndex")==1,"Ellipse command changes only requested chart gesture.");Click(lm,"RectangleSelection");Check((int)Get(left,"SelectionShapeIndex")==0,"Rectangle selection restored.");
                Call(left,"AddSelectionRegion",20d,80d,-110d,110d,false);Click(lm,"StatisticsSelectedRegion");RowCheck(host,left,right,new[]{0,1,2},"Left menu selects same logical pulses in right chart.");
                Open(rm);Check(Item(rm,"ShowAllSharedPulses").Enabled&&Item(rm,"UndoSharedFilter").Enabled&&Item(rm,"ExcludeSelectedRegion").Enabled,"Shared mask remains operable after local region is cleared.");
                Click(rm,"ExcludeSelectedRegion");RowCheck(host,left,right,new[]{3,4,5,7},"Right menu excludes only selected shared pulse ids.");Click(lm,"UndoSharedFilter");RowCheck(host,left,right,new[]{0,1,2},"Left undo restores shared selection.");Click(rm,"UndoSharedFilter");RowCheck(host,left,right,new[]{0,1,2,3,4,5,7},"Second undo restores original accepted view.");
                Call(right,"AddSelectionRegion",20e6,30e6,2000d,3000d,false);Open(rm);Check(!Item(rm,"ExcludeSelectedRegion").Enabled,"Empty region cannot exclude unrelated rows.");int[] prior=Rows(host);Item(rm,"ExcludeSelectedRegion").PerformClick();RowCheck(host,left,right,prior,"Empty region exclusion is a no-op.");Click(rm,"StatisticsSelectedRegion");RowCheck(host,left,right,new int[0],"Empty region statistics accurately shows no pulses.");Click(lm,"UndoSharedFilter");RowCheck(host,left,right,prior,"Undo empty statistics restores prior view.");
                Set(left,"OperatingVoltageKv",0d);Set(right,"OperatingVoltageKv",0d);foreach(var menu in new[]{lm,rm}){Open(menu);Check(!Item(menu,"ShowVoltageBelowU0").Enabled&&!Item(menu,"ShowVoltageU0To2U0").Enabled,"Unknown U0 does not invent voltage bounds.");}
                Set(left,"OperatingVoltageKv",8.7d);Set(right,"OperatingVoltageKv",8.7d);Click(lm,"ShowVoltageBelowU0");RowCheck(host,left,right,new[]{0,1,5},"Low U0 shortcut shared.");Click(rm,"ShowVoltageU0To2U0");RowCheck(host,left,right,new[]{2,3,7},"Mid U0 shortcut uses strict lower and inclusive upper boundary.");Click(lm,"ClearVoltageShortcut");RowCheck(host,left,right,new[]{0,1,2,3,4,5,7},"Clearing voltage shortcut restores shared scope.");
                Click(lm,"ShowVoltageBelowU0");Call(left,"AddSelectionRegion",20d,80d,-110d,110d,false);Click(lm,"StatisticsSelectedRegion");RowCheck(host,left,right,new[]{0,1},"Region intersects shared voltage filter.");Trash(r);RowCheck(host,left,right,new[]{0,1,5},"Right chart trash clears shared region but preserves voltage filter.");Click(lm,"UndoSharedFilter");RowCheck(host,left,right,new[]{0,1},"Trash region clear is undoable.");Click(rm,"ShowAllSharedPulses");
                Call(left,"AddSelectionRegion",20d,80d,-110d,110d,false);Click(lm,"StatisticsSelectedRegion");Click(rm,"ExcludeSelectedRegion");Call(right,"AddSelectionRegion",3.9e6,6.1e6,390d,610d,false);Click(rm,"StatisticsSelectedRegion");RowCheck(host,left,right,new[]{3,4,5},"Region intersects shared exclusion.");Trash(l);RowCheck(host,left,right,new[]{3,4,5,7},"Left chart trash preserves independent exclusions.");Click(rm,"UndoSharedFilter");RowCheck(host,left,right,new[]{3,4,5},"Left trash clear is undoable from right menu.");Click(lm,"ShowAllSharedPulses");
                Call(right,"AddSelectionRegion",.9e6,3.1e6,90d,310d,false);Click(rm,"StatisticsSelectedRegion");RowCheck(host,left,right,new[]{0,1,2},"Right menu selection synchronizes left chart.");
                phases[0].Checked=false;Click(lm,"ShowAllSharedPulses");RowCheck(host,left,right,new[]{7},"Show all removes shared filters but preserves explicit phase choice.");phases[0].Checked=true;
                // Export through non-dialog public writer to an owned fixture file.
                string csv=Path.Combine(root,"current-spectrum.csv"),png=Path.Combine(root,"current-spectrum.png");Check((bool)Call(right,"TryExportDataCsv",csv,false)&&new FileInfo(csv).Length>0,"CSV exporter remains functional without opening a file dialog.");Check((bool)Call(left,"TrySavePng",png,800,500)&&new FileInfo(png).Length>0,"PNG exporter remains functional.");
                Click(rm,"ResetSpectrumView");RowCheck(host,left,right,new[]{0,1,2,3,4,5,7},"Reset view does not mutate shared pulse scope.");
                for(int i=0;i<40;i++)Click(i%2==0?lm:rm,i%2==0?"ShowVoltageBelowU0":"ShowVoltageU0To2U0");Check((int)Field(host,"linkedFilterHistory").GetType().GetProperty("Count").GetValue(Field(host,"linkedFilterHistory"),null)<=32,"Shared undo history bounded at 32 states.");Click(rm,"ShowAllSharedPulses");
                foreach(var size in new[]{new Size(760,900),new Size(1400,900)}){form.ClientSize=size;host.PerformLayout();Application.DoEvents();Check(groups[0].Visible&&phases.All(x=>x.Visible&&x.Right<=x.Parent.ClientSize.Width),"Single phase row usable at width "+size.Width);var table=(TableLayoutPanel)groups[0].Parent;Check(table.GetColumnSpan(groups[0])==2,"Shared phase row spans both plots.");using(var image=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(image,host.ClientRectangle);image.Save(Path.Combine(root,"shared-menu-"+size.Width+".png"));}}
                DragRows(r,0,1,2);Check((int)Get(right,"SelectionRegionCount")==1,"Pending region exists before reload.");
                Check((bool)Call(host,"LoadSavedAnalysis","shared-menu"),"Reload original archive.");Open(lm);Check(!Item(lm,"UndoSharedFilter").Enabled&&!Item(lm,"ShowAllSharedPulses").Enabled,"Reload discards prior view filter/undo state.");
                foreach(var plot in new[]{left,right})Check((int)Get(plot,"SelectionRegionCount")==0,"Reload removes pending regions from both charts.");
                foreach(var plot in new[]{left,right})Check(Matrix().Cast<double>().SequenceEqual(((double[,])Get(plot,"ResultParameterMatrix")).Cast<double>()),"All source samples/acceptance flags unchanged.");Check(bytes.All(x=>Hash(x.Key)==x.Value),"Saved archive bytes unchanged by menus, phases, undo and export.");
            }
            Console.WriteLine("PASS "+checks+" shared-menu/shared-phase offline checks; ProductStarted=False; InferenceInvoked=False; LearningInvoked=False; WorkerStarted=False; SDKCalled=False; HardwareAccessed=False; UserProjectsWritten=False");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
