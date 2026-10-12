using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class CreationViewportEnvironmentProbe
{
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom;public override string ToString(){return Left+","+Top+".."+Right+","+Bottom+" ("+(Right-Left)+"x"+(Bottom-Top)+")";} }
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd,out Rect value);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out Rect value);
    sealed class QaForm:Form {protected override bool ShowWithoutActivation {get{return true;}}protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x08000000;return p;}}}
    static void Pump(){for(int i=0;i<20;i++)Application.DoEvents();}
    static void Dump(string tag,Form form,Control child)
    {
        Rect client,window,childClient;GetClientRect(form.Handle,out client);GetWindowRect(form.Handle,out window);GetClientRect(child.Handle,out childClient);
        Console.WriteLine(tag+" form Size="+form.Size+" Client="+form.ClientSize+" Display="+form.DisplayRectangle+" Bounds="+form.Bounds+" Max="+form.MaximumSize+" Min="+form.MinimumSize+" NativeClient="+client+" NativeWindow="+window+" childType="+child.GetType().Name+" childSize="+child.Size+" childClient="+child.ClientSize+" childDisplay="+child.DisplayRectangle+" childMax="+child.MaximumSize+" childMin="+child.MinimumSize+" childDock="+child.Dock+" childNativeClient="+childClient);
    }
    [STAThread] static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Console.WriteLine("MaxWindowTrackSize="+SystemInformation.MaxWindowTrackSize+" PrimaryMonitorSize="+SystemInformation.PrimaryMonitorSize+" VirtualScreen="+SystemInformation.VirtualScreen+" WorkingArea="+Screen.PrimaryScreen.WorkingArea);
        var assembly=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll"));
        foreach(bool real in new[]{false,true})
        using(var form=new QaForm {AutoScaleMode=AutoScaleMode.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(20,20)})
        using(var child=real?(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.ProjectCreationControl",true)):new Panel())
        {
            if(real){child.GetType().GetProperty("EmbeddedMode").SetValue(child,true,null);child.GetType().GetProperty("ShowDialogMessages").SetValue(child,false,null);child.GetType().GetProperty("OnlineWeatherEnabled").SetValue(child,false,null);}
            child.Dock=DockStyle.Fill;form.Controls.Add(child);form.ClientSize=new Size(1256,620);form.Show();Pump();Dump("initial",form,child);
            foreach(Size requested in new[]{new Size(1850,990),new Size(1065,500),new Size(1850,990)})
            {
                form.ClientSize=requested;Dump("just set requested "+requested,form,child);Pump();Dump("pumped "+requested,form,child);form.PerformLayout();Pump();Dump("forced layout "+requested,form,child);
            }
            form.Close();
        }
    }
}
