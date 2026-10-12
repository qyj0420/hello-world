using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class AnalysisCameraDisplayOnlySmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
    static object Get(object value,string name){var p=value.GetType().GetProperty(name,B);return p!=null?p.GetValue(value,null):value.GetType().GetField(name,B).GetValue(value);}
    static void Field(object value,string name,object content){value.GetType().GetField(name,B).SetValue(value,content);}
    static void Set(object value,string name,object content){value.GetType().GetProperty(name,B).SetValue(value,content,null);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethods(B).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value,args);}
    static void Mouse(Control c,string name,Point point){Call(c,name,new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0));}
    static bool Chinese(string text){return (text??"").Any(c=>c>='\u4e00'&&c<='\u9fff');}
    static System.Collections.Generic.IEnumerable<Control> Descendants(Control c){yield return c;foreach(Control child in c.Controls)foreach(Control d in Descendants(child))yield return d;}
    static void ScanLanguage(Control control,System.Collections.Generic.List<string> errors,Func<string,string> traditional)
    {
        Action<string,string> audit=(kind,value)=>{if(traditional==null?Chinese(value):value!=traditional(value))errors.Add(kind+" / "+control.Name+" / "+value);};
        audit("Text",control.Text);audit("AccessibleName",control.AccessibleName??"");audit("AccessibleDescription",control.AccessibleDescription??"");
        if(control is DataGridView)foreach(DataGridViewColumn column in ((DataGridView)control).Columns){audit("Grid header",column.HeaderText);audit("Grid tooltip",column.HeaderCell.ToolTipText);}
        for(Type t=control.GetType();t!=null&&t!=typeof(Control);t=t.BaseType)
            foreach(var f in t.GetFields(B|BindingFlags.DeclaredOnly))if(f.FieldType==typeof(ToolTip)){
                var tip=(ToolTip)f.GetValue(control);if(tip!=null)foreach(Control target in Descendants(control))audit("Tooltip "+target.Name,tip.GetToolTip(target));
            }else if(typeof(ToolStrip).IsAssignableFrom(f.FieldType)){var menu=(ToolStrip)f.GetValue(control);if(menu!=null)foreach(ToolStripItem item in menu.Items)ScanMenu(item,audit);}
        foreach(Control child in control.Controls)ScanLanguage(child,errors,traditional);
    }
    static void ScanMenu(ToolStripItem item,Action<string,string> audit){audit("Menu",item.Text);audit("Menu tooltip",item.ToolTipText);var menu=item as ToolStripDropDownItem;if(menu!=null)foreach(ToolStripItem child in menu.DropDownItems)ScanMenu(child,audit);}
    [STAThread]static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();string root=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);
            if(Directory.Exists(qa))throw new Exception("Fresh fixture required.");Directory.CreateDirectory(qa);
            var asm=Assembly.LoadFrom(Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            asm.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",B).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            using(var page=(Control)Activator.CreateInstance(asm.GetType("HVX.PdReview.PdReviewControl",true)))
            using(var form=new Form{ClientSize=new Size(1450,930),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-2500,-2500)}){
                Set(page,"EnableAiFeatures",false);Set(page,"PromptSavedAnalysisOnProjectLoad",false);
                form.Controls.Add(page);page.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
                var languageErrors=new System.Collections.Generic.List<string>();Set(page,"LanguageIndex",1);ScanLanguage(page,languageErrors,null);
                Check(languageErrors.Count==0,"Whole English analysis control tree/tooltips: "+String.Join(" | ",languageErrors));
                var trad=asm.GetType("Xiongxin.OWTS.Localization.UiLanguage",true).GetMethod("ToTraditional",B);languageErrors.Clear();Set(page,"LanguageIndex",18);
                ScanLanguage(page,languageErrors,s=>(string)trad.Invoke(null,new object[]{s}));
                Check(languageErrors.Count==0,"Whole Traditional analysis control tree/tooltips: "+String.Join(" | ",languageErrors));
                Set(page,"LanguageIndex",0);
                var spectrum=(Control)Get(page,"spectrum");Set(spectrum,"DisplayModeIndex",5);
                var matrix=new double[12,36];for(int r=0;r<12;r++){matrix[r,0]=0;matrix[r,1]=r;matrix[r,2]=5+r;matrix[r,5]=400;matrix[r,13]=20*r;matrix[r,15]=100+r*20;matrix[r,16]=matrix[r,15];matrix[r,35]=1;}
                Call(spectrum,"LoadDataMatrix",matrix);
                var number=(NumericUpDown)Get(page,"thresholdNumber");number.Value=37.5m;
                var manual=(CheckBox)Get(page,"manualThresholdBox");manual.Checked=true;
                int enabled=0,values=0,layout=0,cameraNotifications=0,otherNotifications=0;
                number.EnabledChanged+=(s,e)=>enabled++;number.ValueChanged+=(s,e)=>values++;
                ((Control)Get(page,"charts")).Layout+=(s,e)=>layout++;
                spectrum.GetType().GetEvent("ViewChanged").AddEventHandler(spectrum,new EventHandler((s,e)=>{if((bool)Get(spectrum,"IsCameraViewNotification"))cameraNotifications++;else otherNotifications++;}));
                int recognition=(int)Get(page,"recognitionUiGeneration"),evaluation=(int)Get(page,"automaticEvaluationGeneration");
                object working=Get(page,"working"),filters=Get(page,"fileThresholds");int selected=(int)Get(page,"selected");
                Rectangle plot=(Rectangle)Call(spectrum,"PlotArea",spectrum.ClientRectangle);Point start=new Point(plot.Left+plot.Width/2,plot.Top+plot.Height/2);
                double azimuth=(double)Get(spectrum,"ViewAzimuthDegrees");Mouse(spectrum,"OnMouseDown",start);
                Check((bool)Get(spectrum,"Is3DRotating"),"Actual spectrum rotation hit");
                for(int i=1;i<=10;i++)Mouse(spectrum,"OnMouseMove",new Point(start.X+i*4,start.Y+i));
                Mouse(spectrum,"OnMouseUp",new Point(start.X+40,start.Y+10));
                Check(cameraNotifications>0&&otherNotifications==0&&(double)Get(spectrum,"ViewAzimuthDegrees")!=azimuth,"Camera notifications are semantically display-only");
                Check(!(bool)Get(spectrum,"IsCameraViewNotification")&&!spectrum.Capture,"Semantic flag and mouse capture are restored after gesture");
                Check(enabled==0&&values==0&&number.Value==37.5m&&manual.Checked,"Rotation cannot edit or refresh threshold controls");
                Check(layout==0&&recognition==(int)Get(page,"recognitionUiGeneration")&&evaluation==(int)Get(page,"automaticEvaluationGeneration"),"Rotation does not relayout or reschedule evaluation/recognition");
                Check(Object.ReferenceEquals(working,Get(page,"working"))&&Object.ReferenceEquals(filters,Get(page,"fileThresholds"))&&selected==(int)Get(page,"selected"),"Working data, threshold map and source selection remain unchanged");
                Field(page,"analysisBusy",true);Set(spectrum,"ViewAzimuthDegrees",17d);Set(spectrum,"ViewPlaneIndex",3);
                Check((bool)Get(page,"IsAnalysisRunning")&&enabled==0&&values==0&&layout==0,"Camera/plane display preserves existing busy state");
                Field(page,"analysisBusy",false);
                Call(spectrum,"ResetView");
                Check(otherNotifications==1&&(int)Get(page,"recognitionUiGeneration")>recognition,"A real spectrum reset retains the non-camera refresh contract");
                Set(spectrum,"ConfidenceClassificationEnabled",!(bool)Get(spectrum,"ConfidenceClassificationEnabled"));
                Check(otherNotifications>1,"Classification still publishes its normal view notification");
                form.Close();
            }
            Console.WriteLine("ANALYSIS_CAMERA_DISPLAY_ONLY_PASS="+checks+";HARDWARE_ACCESSED=False;SDK_CALLED=False;QA="+qa);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
