using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

static class WindowLayoutMemorySmoke
{
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static readonly BindingFlags Flags=BindingFlags.NonPublic|BindingFlags.Instance;
    [STAThread]static int Main(string[] args){try{
        string qa=Path.Combine(args[0],"window-memory");Directory.CreateDirectory(qa);string file=Path.Combine(qa,"window.xml");
        var memory=new WindowLayoutMemory {Monitor="QA-DISPLAY",ScreenBounds=new Rectangle(0,0,1920,1080),WorkingArea=new Rectangle(0,0,1920,1040),
            Bounds=new Rectangle(30,40,1280,800),Dpi=144,Maximized=false};
        Check(memory.Save(file),"save normal bounds");var loaded=WindowLayoutMemory.Load(file);
        Check(loaded!=null&&loaded.Bounds==memory.Bounds&&loaded.Matches(memory.Monitor,memory.ScreenBounds,memory.WorkingArea,144),"unchanged display restores exact layout");
        Check(!loaded.Matches(memory.Monitor,memory.ScreenBounds,memory.WorkingArea,96),"changed scaling requires layout");
        Check(!loaded.Matches("OTHER",memory.ScreenBounds,memory.WorkingArea,144),"removed/changed monitor is not reused");
        Check(!loaded.Matches(memory.Monitor,new Rectangle(0,0,2560,1440),memory.WorkingArea,144),"resolution change invalidates");
        Check(!loaded.Matches(memory.Monitor,memory.ScreenBounds,new Rectangle(0,0,1920,1000),144),"taskbar/work area change invalidates");
        memory.Maximized=true;Check(memory.Save(file)&&WindowLayoutMemory.Load(file).Maximized,"atomic update preserves normal bounds and maximized preference");
        Check(Directory.GetFiles(qa,"*.tmp").Length==0,"no pending preference files");
        memory.Bounds=new Rectangle(-5000,-5000,1280,800);Check(!memory.Save(file),"offscreen record rejected");
        File.WriteAllText(file,"<WindowLayout version='1' dpi='oops'/>");Check(WindowLayoutMemory.Load(file)==null,"invalid numeric preference ignored");
        File.WriteAllText(file,"<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///nonexistent'>]><WindowLayout version='1'/>");Check(WindowLayoutMemory.Load(file)==null,"external entities rejected");
        File.WriteAllText(file,new string('x',9000));Check(WindowLayoutMemory.Load(file)==null,"oversize preference bounded");
        var settings=new AppSettings {DataRoot=qa};settings.Hardware.Enabled=false;
        using(var form=new MainForm(settings,new ApplicationState(),true)){
            var apply=typeof(MainForm).GetMethod("ApplyResponsiveChrome",Flags);
            apply.Invoke(form,null);int count=form.ResponsiveChromeBuildCount;
            for(int i=0;i<30;i++)apply.Invoke(form,null);
            Check(form.ResponsiveChromeBuildCount==count,"unchanged metrics avoid all repeated shell measurement passes");
            form.ClientSize=new Size(1170,780);apply.Invoke(form,null);
            Check(form.ResponsiveChromeBuildCount>count,"actual client-size change relayouts");count=form.ResponsiveChromeBuildCount;
            for(int i=0;i<20;i++)apply.Invoke(form,null);
            Check(form.ResponsiveChromeBuildCount==count,"new settled size reuses layout");
            var button=(Button)typeof(MainForm).GetField("settingsButton",Flags).GetValue(form);button.Text="A significantly longer translated label";apply.Invoke(form,null);
            Check(form.ResponsiveChromeBuildCount>count,"localized text change invalidates");count=form.ResponsiveChromeBuildCount;
            button.Font=new Font(button.Font.FontFamily,button.Font.Size+2);apply.Invoke(form,null);
            Check(form.ResponsiveChromeBuildCount>count,"font change invalidates");
        }
        Console.WriteLine("WINDOW_LAYOUT_MEMORY_PASS="+checks+"; HARDWARE=False; USER_SETTINGS_WRITTEN=False");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
