using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class PressureUiInteractionSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
    static object Get(object value,string name){var p=value.GetType().GetProperty(name,B);return p!=null?p.GetValue(value,null):value.GetType().GetField(name,B).GetValue(value);}
    static void Field(object value,string name,object content){value.GetType().GetField(name,B).SetValue(value,content);}
    static void Set(object value,string name,object content){value.GetType().GetProperty(name,B).SetValue(value,content,null);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethods(B).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value,args);}
    static Control Find(Control parent,string name){return parent.Controls.Find(name,true).Single();}
    static void Mouse(Control control,string name,Point point){Call(control,name,new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0));}
    static bool Chinese(string text){return (text??"").Any(c=>c>='\u4e00'&&c<='\u9fff');}
    static void ScanEnglish(Control control)
    {
        var errors=new System.Collections.Generic.List<string>();ScanLanguage(control,errors,null);
        if(errors.Count>0)throw new Exception("Untranslated controls/tooltips:\n"+String.Join("\n",errors));
        Check(true,"Whole English control tree and tooltip fields audited");
    }
    static void ScanLanguage(Control control,System.Collections.Generic.List<string> errors,Func<string,string> traditional)
    {
        Action<string,string> audit=(kind,value)=>{if(traditional==null?Chinese(value):value!=traditional(value))errors.Add(kind+" / "+control.Name+" / "+control.GetType().FullName+" / "+value);};
        audit("Text",control.Text);audit("AccessibleName",control.AccessibleName??"");audit("AccessibleDescription",control.AccessibleDescription??"");
        if(control is DataGridView)foreach(DataGridViewColumn column in ((DataGridView)control).Columns){audit("Grid header",column.HeaderText);audit("Grid tooltip",column.HeaderCell.ToolTipText);}
        for(Type t=control.GetType();t!=null&&t!=typeof(Control);t=t.BaseType)
            foreach(var f in t.GetFields(B|BindingFlags.DeclaredOnly))if(f.FieldType==typeof(ToolTip)){
                var tip=(ToolTip)f.GetValue(control);if(tip!=null)foreach(Control target in Descendants(control))audit("Tooltip "+target.Name,tip.GetToolTip(target));
            }else if(typeof(ToolStrip).IsAssignableFrom(f.FieldType)){var menu=(ToolStrip)f.GetValue(control);if(menu!=null)foreach(ToolStripItem item in menu.Items)ScanMenu(item,audit);}
        foreach(Control child in control.Controls)ScanLanguage(child,errors,traditional);
    }
    static void ScanMenu(ToolStripItem item,Action<string,string> audit){audit("Menu",item.Text);audit("Menu tooltip",item.ToolTipText);var menu=item as ToolStripDropDownItem;if(menu!=null)foreach(ToolStripItem child in menu.DropDownItems)ScanMenu(child,audit);}
    static System.Collections.Generic.IEnumerable<Control> Descendants(Control c){yield return c;foreach(Control child in c.Controls)foreach(Control d in Descendants(child))yield return d;}
    static double[,] Matrix(){var m=new double[12,36];for(int r=0;r<12;r++){m[r,0]=0;m[r,1]=r;m[r,2]=5+r;m[r,5]=400;m[r,13]=20*r;m[r,15]=100+r*20;m[r,16]=m[r,15];m[r,35]=1;}return m;}
    [STAThread]static int Main(string[] args)
    {
        try{
            Console.OutputEncoding=new System.Text.UTF8Encoding(false);Application.EnableVisualStyles();string root=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);
            if(Directory.Exists(qa))throw new Exception("Fresh fixture required.");Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var page=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true)))
            using(var form=new Form{ClientSize=new Size(1360,850),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-2400,-2400)}){
                form.Controls.Add(page);page.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
                var dashboard=(Control)Get(page,"spectrumDashboardView");var review=(Control)Get(dashboard,"Review");var waveform=(Control)Get(page,"waveformView");
                var manual=(CheckBox)Find(dashboard,"PressureManualThreshold");var numeric=(NumericUpDown)Find(dashboard,"PressureThresholdPc");
                Set(page,"ProjectName","Fixture-Project");
                string[] names={"PressureManualThreshold","PressureThresholdAll","PressureSnapJoint","PressureReversePosition","PressureCompensatedCharge","PressureEndpointReview"};
                Set(page,"LanguageIndex",1);ScanEnglish(page);Check(true,"English control tree including self-translating dashboard has no Chinese captions");
                var tip=(ToolTip)Get(dashboard,"adviceTip");
                foreach(string name in names)Check(tip.GetToolTip(Find(dashboard,name)).Length>0&&!Chinese(tip.GetToolTip(Find(dashboard,name))),"English options tooltip "+name);
                Set(page,"LanguageIndex",18);Check(manual.Text.Contains("手動")&&manual.Text.Contains("閾值"),"Traditional threshold caption is not overwritten by Chinese capture");
                Check(Find(dashboard,"PressureAutoDisplay").Text.Contains("自動"),"Traditional auto advice caption");
                foreach(string name in names)Check(Chinese(tip.GetToolTip(Find(dashboard,name))),"Traditional options tooltip "+name);
                var trad=assembly.GetType("Xiongxin.OWTS.Localization.UiLanguage",true).GetMethod("ToTraditional",B);var tradErrors=new System.Collections.Generic.List<string>();
                ScanLanguage(page,tradErrors,s=>(string)trad.Invoke(null,new object[]{s}));
                Check(tradErrors.Count==0,"Whole Traditional control tree and tooltip audit: "+String.Join(" | ",tradErrors));
                Set(page,"LanguageIndex",0);Call(page,"SetPressureProgramMode","CUSTOM");Set(page,"BaseU0VoltageKv",8.7d);Call(page,"SetCustomVoltageMode","U0");Call(page,"SetCustomVoltageValue",2.4d);
                var hint=Find(page,"CustomVoltageHintLabel");var custom=Find(page,"CustomVoltageValueEditor");
                Check(custom.Text=="2.4U0"&&!custom.Text.Contains("×"),"U0 multiplier has no multiplication sign");
                int height=hint.Height;var optionsBounds=dashboard.Bounds;int invalidated=0,arranged=0;waveform.Invalidated+=(s,e)=>invalidated++;
                var originalArrange=(Action)Get(page,"arrangePressureSettings");Field(page,"arrangePressureSettings",(Action)(()=>{arranged++;originalArrange();}));
                Set(page,"ActionHandshakeEnabled",true);
                EventHandler ack=(s,e)=>Call(page,"CompleteAction",(long)Get(page,"ActionSequence"),true,"");page.GetType().GetEvent("ActionPublished").AddEventHandler(page,ack);
                Call(page,"ExecuteParameterAction","VOLTAGE_INCREASE",(Action)(()=>Call(page,"SetCustomVoltageValue",2.5d)));
                Check(hint.Height==height&&!hint.AutoSize&&!hint.Text.Contains("\n"),"Custom hint fixed single-line height");
                Check(arranged==0&&invalidated==0&&dashboard.Bounds==optionsBounds,"Custom parameter/ACK does not relayout or invalidate waveform");
                page.GetType().GetEvent("ActionPublished").RemoveEventHandler(page,ack);Set(page,"ActionHandshakeEnabled",false);
                Call(page,"SetPressureAnalysisThreshold",true,40d,true);Call(page,"SetSpectrumDataMatrix",Matrix());
                Field(page,"selectedChannelCode","U-Q-L");Set(review,"DisplayModeIndex",5);
                dashboard.Parent=null;form.Controls.Add(dashboard);dashboard.Dock=DockStyle.Fill;dashboard.Visible=true;Application.DoEvents();
                Set(page,"ActionHandshakeEnabled",true);
                int published=0,views=0,enabledChanges=0,valueChanges=0;page.GetType().GetEvent("ActionPublished").AddEventHandler(page,new EventHandler((s,e)=>published++));
                page.GetType().GetEvent("SpectrumViewChanged").AddEventHandler(page,new EventHandler((s,e)=>views++));
                numeric.EnabledChanged+=(s,e)=>enabledChanges++;numeric.ValueChanged+=(s,e)=>valueChanges++;invalidated=0;
                Rectangle plot=(Rectangle)Call(review,"PlotArea",review.ClientRectangle);Point start=new Point(plot.Left+plot.Width/2,plot.Top+plot.Height/2);
                long sequence=(long)Get(page,"SpectrumInteractionSequence"),request=(long)Get(page,"PressureRequestSequence"),action=(long)Get(page,"ActionSequence");
                double azimuth=(double)Get(review,"ViewAzimuthDegrees");Mouse(review,"OnMouseDown",start);
                Check((bool)Get(review,"Is3DRotating"),"Actual 3D plot mouse hit starts rotation");
                for(int i=1;i<=8;i++)Mouse(review,"OnMouseMove",new Point(start.X+i*5,start.Y+i));
                Check(views==0&&(long)Get(page,"SpectrumInteractionSequence")==sequence,"Rotation frames are coalesced before audit, not parameter actions");
                Mouse(review,"OnMouseUp",new Point(start.X+40,start.Y+8));
                Check(views==1&&(long)Get(page,"SpectrumInteractionSequence")==sequence+1&&Get(page,"SpectrumInteractionCode").Equals("SPECTRUM_VIEW"),"Finished gesture retains final view and emits exactly one display audit");
                Check((double)Get(review,"ViewAzimuthDegrees")!=azimuth&&!review.Capture,"Actual camera changes and releases capture");
                Check(published==0&&enabledChanges==0&&valueChanges==0&&invalidated==0&&numeric.Enabled&&numeric.Value==40m,"Rotation never refreshes, locks or changes manual threshold/waveform");
                Check(!(bool)Get(page,"IsBusy")&&!(bool)Get(page,"ActionPending")&&(long)Get(page,"ActionSequence")==action&&(long)Get(page,"PressureRequestSequence")==request,"Idle rotation issues no action or pressure request");
                Field(page,"actionBusy",true);Field(page,"actionPending",true);Field(page,"actionCode","PRESSURE");Field(page,"zeroHardwareRunning",true);Field(page,"pressureStateCode","RAMPING");
                Mouse(review,"OnMouseDown",start);Mouse(review,"OnMouseMove",new Point(start.X+20,start.Y-10));review.Capture=false;Application.DoEvents();
                Check(views==2&&(bool)Get(page,"IsBusy")&&(bool)Get(page,"ActionPending")&&(bool)Get(page,"HasActiveHardwareSession")&&Get(page,"ActionCode").Equals("PRESSURE")&&Get(page,"PressureStateCode").Equals("RAMPING"),"Busy rotation/capture loss preserves pending pressure and hardware state");
                Check(published==0&&enabledChanges==0&&valueChanges==0&&numeric.Value==40m&&invalidated==0,"Busy view gesture neither edits threshold nor ACKs hardware");
                Set(review,"ViewAzimuthDegrees",15d);Check(views==3&&(bool)Get(page,"IsBusy"),"Explicit camera change is display-only while busy");
                Field(page,"zeroHardwareRunning",false);Field(page,"actionBusy",false);Field(page,"actionPending",false);Field(page,"pressureStateCode","WAITING");
                foreach(Action actualChange in new Action[]{()=>Call(review,"ResetView"),()=>Call(review,"ApplyToolbarPhaseVisibility",new bool[]{true,false,false}),()=>Set(review,"ConfidenceClassificationEnabled",!(bool)Get(review,"ConfidenceClassificationEnabled"))}){
                    int previousPublished=published,previousViews=views;actualChange();
                    Check(published==previousPublished+1&&views==previousViews&&(bool)Get(page,"ActionPending")&&Get(page,"ActionCode").Equals("SPECTRUM_VIEW"),"Genuine reset/phase/classification change retains mailbox and is not camera-only");
                    Call(page,"CompleteAction",(long)Get(page,"ActionSequence"),true,"");
                }
                int filterPublished=published;var statistics=(CheckBox)Get(dashboard,"statisticsCheck");statistics.Checked=!statistics.Checked;
                Check(published==filterPublished+1&&(bool)Get(page,"ActionPending")&&Get(page,"ActionCode").Equals("STATISTICS_FILTER"),"Genuine statistical filtering retains mailbox");
                Call(page,"CompleteAction",(long)Get(page,"ActionSequence"),true,"");
                int modePublished=published;
                Set(review,"DisplayModeIndex",0);Call(page,"HandleSpectrumUserAction","SPECTRUM_VIEW");
                Check(published==modePublished+1&&(bool)Get(page,"ActionPending")&&Get(page,"ActionCode").Equals("SPECTRUM"),"Different spectrum selection retains its existing parameter mailbox contract");
                Call(page,"CompleteAction",(long)Get(page,"ActionSequence"),true,"");form.Close();
            }
            Console.WriteLine("PRESSURE_UI_INTERACTION_PASS="+checks+";HARDWARE_ACCESSED=False;SDK_CALLED=False;QA="+qa);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
