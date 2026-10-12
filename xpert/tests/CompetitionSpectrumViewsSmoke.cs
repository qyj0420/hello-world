using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class CompetitionSpectrumViewsSmoke
{
    static int checks;
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static object Get(object target,string name){return target.GetType().GetProperty(name,All).GetValue(target,null);}
    static object Field(object target,string name){return target.GetType().GetField(name,All).GetValue(target);}
    static void Set(object target,string name,object value){target.GetType().GetProperty(name,All).SetValue(target,value,null);}
    static object Call(object target,string name,params object[] values)
    {return target.GetType().GetMethod(name,All,null,values.Select(x=>x.GetType()).ToArray(),null).Invoke(target,values);}
    static void Axes(object target,string horizontal,string vertical)
    {
        var args=new object[]{null,null,0d,0d,0d,0d};target.GetType().GetMethod("GetPlaneAxes",All).Invoke(target,args);
        Check((string)args[0]==horizontal&&(string)args[1]==vertical,"plane axes "+horizontal+"/"+vertical);
        Check((double)args[3]>(double)args[2]&&(double)args[5]>(double)args[4],"finite positive plane spans");
    }
    static double[,] Matrix()
    {
        var matrix=new double[6,36];double[] lengths={80,82,81,170,240,380},charges={100,-101,102,55,75,150},angles={30,210,60,180,300,120},volts={8.7,12.2,8.7,10,15,8.7};
        for(int i=0;i<6;i++){matrix[i,0]=i==3?2:i==4?3:1;matrix[i,1]=i;matrix[i,2]=volts[i];matrix[i,3]=angles[i];matrix[i,5]=414;matrix[i,9]=i;matrix[i,13]=lengths[i];matrix[i,15]=charges[i];matrix[i,16]=999;matrix[i,35]=i==5?0:1;}
        return matrix;
    }
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();string build=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"competition-views-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));var type=assembly.GetType("HVX.UnifiedSpectrum.UnifiedSpectrumControl",true);
            using(var control=(Control)Activator.CreateInstance(type,All,null,new object[]{true},null))
            using(var host=new Form{ClientSize=new Size(1200,760),Location=new Point(-20000,-20000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false})
            {
                host.Controls.Add(control);control.Dock=DockStyle.Fill;host.Show();Set(control,"AcceptedOnly",true);var matrix=Matrix();Call(control,"LoadDataMatrix",matrix);Application.DoEvents();
                Check(((int[])Call(control,"GetVisibleModeIndices")).SequenceEqual(new[]{11,2,3,7,8,12}),"six modes include real L-PRPD");
                Check(Field(control,"chartExportButton")!=null,"explicit image export entry exists");
                Set(control,"DisplayModeIndex",12);Check((string)Get(control,"DisplayModeName")=="L-PRPD","L-PRPD label");
                var view=Field(control,"view");Check((bool)Field(view,"Is3D")&&(string)Field(view,"XKey")=="L"&&(string)Field(view,"YKey")=="Q"&&(string)Field(view,"ZKey")=="PHI","true distance / signed charge / phase geometry");
                var points=(double[,])Call(control,"GetDisplayedPoints");Check(points.GetLength(0)==5&&Enumerable.Range(0,5).Any(i=>points[i,1]==-101),"signed source charge retained, compensated charge not substituted");
                Check(!(bool)Get(control,"RegionSelectionEnabled"),"free 3D drag is rotation rather than ambiguous region selection");
                foreach(int mode in new[]{12,7,8})
                {
                    Set(control,"DisplayModeIndex",mode);Check((bool)Get(control,"ViewPlaneButtonsVisible"),"plane buttons available "+mode);
                    int builds=(int)Get(control,"DataBuildCount"),clones=(int)Get(control,"MatrixCloneCount");
                    foreach(int plane in new[]{1,2,3,0})
                    {
                        Call(control,"PerformViewPlaneButtonClick",plane);Check((int)Get(control,"ViewPlaneIndex")==plane,"camera plane changes "+mode+"/"+plane);
                        var bounds=(Rectangle)Call(control,"GetViewPlaneButtonBounds",plane);Check(!bounds.IsEmpty&&control.ClientRectangle.Contains(bounds),"camera button fits "+mode+"/"+plane);
                        if(plane!=0)Axes(control,plane==3?(mode==12?"PHI":"U"):mode==12?"L":"PHI",plane==1||plane==3?(mode==8?"N":"Q"):mode==12?"PHI":"U");
                        using(var image=new Bitmap(control.Width,control.Height)){control.DrawToBitmap(image,control.ClientRectangle);image.Save(Path.Combine(qa,"mode-"+mode+"-plane-"+plane+".png"));}
                        Check((bool)Get(control,"SelfCheckPassed"),"camera render self-check "+mode+"/"+plane);
                    }
                    Check((int)Get(control,"DataBuildCount")==builds&&(int)Get(control,"MatrixCloneCount")==clones,"plane changes do not rebuild or clone source data "+mode);
                }
                Set(control,"DisplayModeIndex",12);Set(control,"ViewPlaneIndex",1);Check((bool)Get(control,"RegionSelectionEnabled"),"XY enables distance-charge region selection");
                Rectangle shape=(Rectangle)Get(control,"SelectionShapeButtonBounds"),clear=(Rectangle)Get(control,"SelectionClearButtonBounds");
                foreach(int plane in new[]{0,1,2,3}){var button=(Rectangle)Call(control,"GetViewPlaneButtonBounds",plane);Check(!shape.IntersectsWith(button)&&!clear.IntersectsWith(button),"region actions do not cover camera "+plane);}
                Rectangle plot=(Rectangle)Call(control,"PlotArea",control.ClientRectangle);Point first=new Point(plot.Left+plot.Width/8,plot.Top+plot.Height/3),last=new Point(plot.Left+plot.Width/3,plot.Top+plot.Height*2/3);
                Call(control,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,first.X,first.Y,0));Check(!(bool)Get(control,"Is3DRotating")&&(bool)Field(control,"dragging"),"XY mouse drag begins a region, never rotation");
                Call(control,"OnMouseMove",new MouseEventArgs(MouseButtons.Left,0,last.X,last.Y,0));Call(control,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,last.X,last.Y,0));Check((int)Get(control,"SelectionRegionCount")==1,"mouse region completed");Set(control,"RegionActionCommand",2);
                Call(control,"AddSelectionRegion",79d,83d,-110d,110d,false);Set(control,"RegionActionCommand",0);Check(((int[])Get(control,"VisibleSourceRowIndices")).SequenceEqual(new[]{0,1,2}),"XY region preserves original source-row identities");
                foreach(int mode in new[]{7,8,11,12}){Set(control,"DisplayModeIndex",mode);Check(((int[])Get(control,"VisibleSourceRowIndices")).SequenceEqual(new[]{0,1,2}),"regional mask persists across spectrum "+mode);}
                Set(control,"ViewPlaneIndex",0);Check(!(bool)Get(control,"RegionSelectionEnabled"),"returning to 3D keeps selection gestures disabled");
                Call(control,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,first.X,first.Y,0));Check((bool)Get(control,"Is3DRotating"),"3D mouse drag rotates");Call(control,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,last.X,last.Y,0));
                Check((bool)Call(control,"ExitRegionStatistics")&&(int)Get(control,"VisibleSourceRowCount")==5,"exit regional view restores accepted rows");
                var unchanged=(double[,])Get(control,"ResultParameterMatrix");Check(Enumerable.Range(0,6).All(r=>Enumerable.Range(0,36).All(c=>matrix[r,c]==unchanged[r,c])),"source matrix remains byte-equivalent in values");
            }
            using(var ordinary=(Control)Activator.CreateInstance(type))
            {
                Call(ordinary,"LoadDataMatrix",Matrix());Set(ordinary,"DisplayModeIndex",12);Set(ordinary,"ViewPlaneIndex",2);Axes(ordinary,"PHI","L");Check(!(bool)Get(ordinary,"RegionSelectionEnabled"),"ordinary L-PRPD unchanged");Set(ordinary,"DisplayModeIndex",7);Check(!(bool)Get(ordinary,"ViewPlaneButtonsVisible"),"ordinary legacy mode7 availability unchanged");
            }
            Console.WriteLine("PASS "+checks+" competition spectrum-view checks; QA "+qa);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
