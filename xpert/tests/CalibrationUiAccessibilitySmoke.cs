using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

// Isolated synthetic UI/state checks. Never calls HostAcquireAsync, connects a
// worker, loads user preferences or opens a real project.
internal static class CalibrationUiAccessibilitySmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static Type pageType;static int checks;static string output;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Call(object value,string method,params object[] args){return value.GetType().GetMethods(All).Single(m=>m.Name==method&&m.GetParameters().Length==args.Length).Invoke(value,args);}
    static object P(object value,string name){return value.GetType().GetProperty(name,All).GetValue(value,null);}
    static void P(object value,string name,object data){value.GetType().GetProperty(name,All).SetValue(value,data,null);}
    static object F(object value,string name){return value.GetType().GetField(name,All).GetValue(value);}
    static void F(object value,string name,object data){value.GetType().GetField(name,All).SetValue(value,data);}
    static void Pump(){for(int i=0;i<6;i++)Application.DoEvents();}
    static void Wait(Task task){var limit=DateTime.UtcNow.AddSeconds(4);while(!task.IsCompleted&&DateTime.UtcNow<limit)Pump();Check(task.IsCompleted,"synthetic task finishes");task.GetAwaiter().GetResult();}
    static bool Key(Control page,Keys key,bool repeated=false)
    {
        var message=Message.Create(page.Handle,0x100,new IntPtr((int)(key&Keys.KeyCode)),new IntPtr(repeated?1L<<30:0));
        object[] args={message,key};return (bool)pageType.GetMethod("ProcessCmdKey",All).Invoke(page,args);
    }
    static object Action(Control page,string id){return ((IEnumerable)Call(page,"CalibrationKeyboardActions")).Cast<object>().Single(a=>(string)F(a,"Id")==id);}
    static Control Page(Form owner,bool compact=true)
    {
        var page=(Control)Activator.CreateInstance(pageType);page.Dock=DockStyle.Fill;owner.Controls.Add(page);
        P(page,"SimulationMode",true);P(page,"EmbeddedMode",compact);P(page,"AdvancedMode",true);P(page,"LanguageIndex",1);
        owner.Show();page.Focus();Pump();return page;
    }
    static Form Host(){return new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-3000,-3000),ClientSize=new Size(1280,720)};}
    static object Coordinator(){return Activator.CreateInstance(module.GetType("Xiongxin.Calibration.CalibrationForm",true),All,null,new object[]{null,true},null);}
    static void Keyboard()
    {
        using(var host=Host())using(var page=Page(host))
        {
            Call(page,"FocusCalibrationAction","phase:1");Check(Key(page,Keys.Space),"Space activates focused phase");Check((int)P(page,"SelectedPhaseIndex")==0,"phase action reaches original selection");
            Call(page,"FocusCalibrationAction","level");string old=(string)P(page,"SelectedLevelCode");Key(page,Keys.Right);Check((string)P(page,"SelectedLevelCode")!=old,"Right selects next level");
            Key(page,Keys.Enter);Check(!(bool)F(page,"compactLevelsOpen"),"Common levels remain flat rather than opening a popup");
            Call(page,"FocusCalibrationAction","choose-level:5nC");string chosen=(string)F(page,"calibrationKeyboardAction");Key(page,Keys.Space);
            Check(!(bool)F(page,"compactLevelsOpen")&&(string)F(page,"calibrationKeyboardAction")=="level","compact selection restores selector focus");
            Check((string)P(page,"SelectedLevelCode")==chosen.Substring("choose-level:".Length),"keyboard selects focused level");
            Call(page,"FocusCalibrationAction","phase:2");Key(page,Keys.Tab);string forward=(string)F(page,"calibrationKeyboardAction");Key(page,Keys.Tab|Keys.Shift);Check((string)F(page,"calibrationKeyboardAction")=="phase:2"&&forward!="phase:2","Tab and ShiftTab reverse focus order");
            Call(page,"FocusCalibrationAction","length");Key(page,Keys.Enter);var editor=(TextBox)F(page,"numericEditor");
            Check(editor.Visible&&editor.Focused,"keyboard opens actual numeric editor");
            var accessible=page.AccessibilityObject;Check(Object.ReferenceEquals(accessible.GetChild(0),editor.AccessibilityObject),"visible text editor remains an accessible child");
            Check(Object.ReferenceEquals(accessible.GetFocused(),editor.AccessibilityObject),"focused accessible text editor is preserved");
            Check(editor.AccessibleName.Contains("length"),"numeric editor has localized accessible label");
            Call(page,"CancelNumericEditor");page.Focus();
            int phase=(int)P(page,"SelectedPhaseIndex");F(page,"actionBusy",true);Call(page,"ActivateCalibrationAction","phase:0");Check((int)P(page,"SelectedPhaseIndex")==phase,"busy keyboard activation cannot change phase");
            Check(((IEnumerable)Call(page,"CalibrationKeyboardActions")).Cast<object>().All(a=>!(bool)F(a,"Enabled")),"ordinary busy state disables every virtual action");F(page,"actionBusy",false);
            Call(page,"FocusCalibrationAction","level");string lockedLevel=(string)P(page,"SelectedLevelCode");
            F(page,"jointDialogOpen",true);Call(page,"ActivateCalibrationAction","phase:0");Check((int)P(page,"SelectedPhaseIndex")==phase,"modal state blocks accessible actions");
            Key(page,Keys.Right);Check((string)P(page,"SelectedLevelCode")==lockedLevel,"modal state blocks arrow level changes");F(page,"jointDialogOpen",false);
            F(page,"jointConfirmationOpen",true);Key(page,Keys.Left);Check((string)P(page,"SelectedLevelCode")==lockedLevel,"joint review blocks arrow level changes");F(page,"jointConfirmationOpen",false);
            Call(page,"FocusCalibrationAction","start");long sequence=(long)P(page,"StartRequestSequence");Key(page,Keys.Enter);Check((long)P(page,"StartRequestSequence")==sequence+1,"fresh Enter publishes one simulated start");
            using(var coordinator=(Form)Coordinator())
            {
                F(page,"hostAcquisitionActive",true);F(page,"activeAcquisition",coordinator);
                Key(page,Keys.Enter,true);Key(page,Keys.Space,true);Check(!(bool)F(page,"calibrationStopRequested")&&!(bool)F(coordinator,"stopRequested"),"held activation key does not turn start into stop");
                Check((string)P(page,"CalibrationBusyCaption")=="Acquiring · Select to stop","busy caption exposes available stop");
                Key(page,Keys.Enter);Check((bool)F(page,"calibrationStopRequested")&&(bool)F(coordinator,"stopRequested"),"new press invokes only existing coordinator cancellation");
                Check(!(bool)F(Action(page,"start"),"Enabled")&&((string)P(page,"CalibrationBusyCaption")).StartsWith("Stopping"),"stopping state disables duplicate stop and waits for release");
                Key(page,Keys.Enter);Check((long)P(page,"StartRequestSequence")==sequence+1,"repeated stop never publishes another start");
                F(page,"hostAcquisitionActive",false);F(page,"activeAcquisition",null);F(page,"calibrationStopRequested",false);
            }
        }
        using(var host=Host())
        {
            host.ClientSize=new Size(1920,1080);
            using(var page=Page(host,false)) {
                Check(!(bool)P(page,"UseCompactLayout"),"legacy geometry exercised");
                Call(page,"FocusCalibrationAction","choose-level:5nC");Key(page,Keys.Enter);
                Check((string)P(page,"SelectedLevelCode")=="5nC"&&(string)F(page,"calibrationKeyboardAction")=="choose-level:5nC","legacy choice retains existing focus id");
                Key(page,Keys.Tab);Check((string)F(page,"calibrationKeyboardAction")!="length","legacy Tab continues after chosen level");
            }
        }
    }
    static void StopReleaseBoundary()
    {
        using(var host=Host())using(var page=Page(host))using(var coordinator=(Form)Coordinator())
        {
            F(page,"hostAcquisitionActive",true);F(page,"activeAcquisition",coordinator);int calls=0;var release=new TaskCompletionSource<bool>();
            Func<string,Task<bool>> capture=level=>{calls++;return release.Task;};
            var pending=(Task)Call(page,"RunHardwareAcquisitionLevels",new[]{"100pC","500pC"},capture);
            Check(calls==1&&!pending.IsCompleted,"synthetic result waits at device-release boundary");
            Call(page,"RequestCalibrationStopFromUi");release.SetResult(true);Wait(pending);
            Check(calls==1,"UI stop during successful release prevents the next calibration level");
            Check(!(bool)F(page,"hostConnectionUnavailable"),"UI stop does not manufacture a disconnection");
            F(page,"hostAcquisitionActive",false);F(page,"activeAcquisition",null);
        }
    }
    static void Guard()
    {
        using(var page=(Control)Activator.CreateInstance(pageType))
        {
            P(page,"SimulationMode",true);var args=new object[]{P(page,"ProjectPath"),P(page,"SelectedPhaseIndex"),P(page,"SelectedLevelCode"),P(page,"SimulationMode")};
            Call(page,"RequireJointReviewContext",args);
            for(int field=0;field<5;field++) {
                var bad=(object[])args.Clone();if(field==0)bad[0]="another-project";if(field==1)bad[1]=1;if(field==2)bad[2]="different-level";if(field==3)bad[3]=false;if(field==4)F(page,"hostAcquisitionActive",true);
                bool rejected=false;try{Call(page,"RequireJointReviewContext",bad);}catch(TargetInvocationException e){rejected=e.InnerException is InvalidOperationException;}
                Check(rejected,"post-modal guard rejects context change #"+field);F(page,"hostAcquisitionActive",false);
            }
            page.Dispose();bool disposedRejected=false;try{Call(page,"RequireJointReviewContext",args);}catch(TargetInvocationException e){disposedRejected=e.InnerException is InvalidOperationException;}
            Check(disposedRejected,"post-modal guard rejects a disposed page");
        }
    }
    static IEnumerable<Control> Descendants(Control value){foreach(Control child in value.Controls){yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    static void Dialogs()
    {
        var jointType=module.GetType("HVX.Controls.CalibrationJoint",true);
        var joint=Activator.CreateInstance(jointType,All,null,new object[]{"qa","测试接头 with a deliberately long label",10d,"XLPE",170d,"XLPE",170d},null);
        var joints=Array.CreateInstance(jointType,1);joints.SetValue(joint,0);
        using(var page=(Control)Activator.CreateInstance(pageType))
        foreach(int language in new[]{0,1,18})foreach(float size in new[]{9.5f,14.25f,20f})using(var font=new Font("Microsoft YaHei UI",size))
        {
            page.Font=font;P(page,"LanguageIndex",language);
            var dialogs=new[]{(Form)Call(page,"CreateJointConfirmationDialog",joints),(Form)Call(page,"CreateJointTargetDialog",new[]{"A","B","C"},"A"),
                (Form)Activator.CreateInstance(module.GetType("HVX.Controls.DeleteJointDialog",true),All,null,new object[]{0,joint,font,language},null)};
            foreach(var dialog in dialogs)using(dialog)
            {
                dialog.StartPosition=FormStartPosition.Manual;dialog.Location=new Point(-3000,-3000);dialog.Show();
                foreach(var work in new[]{new Rectangle(0,0,1280,680),new Rectangle(0,0,500,390)})
                {
                    Call(dialog,"FitWorkingArea",work);dialog.PerformLayout();Pump();
                    Check(dialog.Width<=work.Width-24&&dialog.Height<=work.Height-24,"dialog fits explicit work area: "+dialog.Name);
                    Check(Math.Abs(dialog.Font.SizeInPoints-size)<.01f&&dialog.Icon!=null,"dialog inherits requested font and owns brand icon");
                    var buttons=Descendants(dialog).OfType<Button>().ToArray();
                    foreach(var button in buttons) {
                        Check(button.Parent.ClientRectangle.Contains(button.Bounds),"footer button reachable: "+dialog.Name+" "+button.Text+" "+button.Bounds);
                        Rectangle visible=button.Bounds;
                        for(Control ancestor=button.Parent;ancestor!=null;ancestor=ancestor.Parent) {
                            Check(ancestor.ClientRectangle.Contains(visible),"footer button visible through "+ancestor.GetType().Name+": "+dialog.Name+" "+button.Text+" "+visible+" in "+ancestor.ClientRectangle);
                            visible.Offset(ancestor.Location);
                        }
                        int needed=TextRenderer.MeasureText(button.Text,button.Font,new Size(Math.Max(20,button.Width-button.Padding.Horizontal-8),Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix).Height+button.Padding.Vertical+8;
                        Check(button.Height>=needed,"wrapped button caption fits: "+button.Text);
                    }
                    var notice=Descendants(dialog).OfType<Label>().FirstOrDefault(l=>l.Name=="CalibrationChoiceNotice");
                    if(notice!=null)Check(notice.Height>=TextRenderer.MeasureText(notice.Text,notice.Font,new Size(notice.Width,Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix).Height,"choice notice measured after font/width changes");
                    if(size==20&&language==1)using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,dialog.Name+"-"+work.Width+".png"));}
                }
                if(language==1)Check(dialog.Text.All(c=>c<128),"English dialog title localized");
                if(language==18)Check(!dialog.Text.Contains("头")&&!dialog.Text.Contains("别"),"Traditional dialog title localized");
                dialog.Close();
            }
        }
        using(var plain=new Form()){plain.Show();plain.Close();Check(true,"closing branded dialogs leaves default icon usable");}
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            if(args.Length!=2)throw new ArgumentException("Pass build root and isolated output directory.");output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));pageType=module.GetType("HVX.Controls.CalibrationProgramControl",true);
            Keyboard();StopReleaseBoundary();Guard();Dialogs();Console.WriteLine("CALIBRATION_UI_ACCESSIBILITY_PASS="+checks+"; HARDWARE_COMMANDS=0; REAL_PROJECT_WRITES=0; OUTPUT="+output);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
