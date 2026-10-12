using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xiongxin.OWTS.Ui;

internal static class DialogPlacementSmoke
{
    static int checks;
    static void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
    [STAThread]static int Main()
    {
        try {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Check(DialogPlacement.Install(),"Thread-local dialog placement installed");
            Check(DialogPlacement.Install(),"Install is idempotent");
            var work=new Rectangle(-1920,0,1920,1040);
            Check(DialogPlacement.Calculate(new Rectangle(-1800,100,1000,700),work,new Size(400,200))==new Rectangle(-1500,350,400,200),"Secondary monitor negative coordinates");
            Check(work.Contains(DialogPlacement.Calculate(new Rectangle(-2200,-400,800,600),work,new Size(600,400))),"Offscreen owner clamps to work area");
            Check(DialogPlacement.Calculate(new Rectangle(0,0,1200,900),new Rectangle(0,0,1920,1080),new Size(750,450))==new Rectangle(225,225,750,450),"Scaled physical bounds centre without fixed DPI assumptions");
            using(var owner=new Form{Text="Dialog placement offline test",StartPosition=FormStartPosition.Manual,Size=new Size(820,600),Location=new Point(Screen.PrimaryScreen.WorkingArea.Left+40,Screen.PrimaryScreen.WorkingArea.Top+30)}) {
                owner.Show();Application.DoEvents();
                using(var dialog=new Form{Text="Placement fixture",ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=Point.Empty,Size=new Size(360,210)}) {
                    dialog.Shown+=delegate {
                        Check(dialog.Bounds==DialogPlacement.Calculate(owner.Bounds,Screen.FromControl(owner).WorkingArea,dialog.Size),"Owned managed dialog centred before first shown event");
                        dialog.Location=new Point(dialog.Left+23,dialog.Top+17);var moved=dialog.Location;
                        owner.Activate();dialog.Activate();Application.DoEvents();
                        Check(dialog.Location==moved,"Reactivation preserves user movement");dialog.Close();
                    };
                    dialog.ShowDialog(owner);
                }
                string title="Dialog-placement-native-"+Guid.NewGuid().ToString("N");bool located=false,centered=false;
                using(var timer=new Timer{Interval=40}) {
                    timer.Tick+=delegate {
                        IntPtr w=FindWindow("#32770",title);if(w==IntPtr.Zero)return;
                        uint process;GetWindowThreadProcessId(w,out process);if(process!=(uint)System.Diagnostics.Process.GetCurrentProcess().Id)return;
                        Rect r;if(!GetWindowRect(w,out r))return;
                        var bounds=Rectangle.FromLTRB(r.L,r.T,r.R,r.B);
                        centered=bounds==DialogPlacement.Calculate(owner.Bounds,Screen.FromControl(owner).WorkingArea,bounds.Size);located=true;
                        timer.Stop();PostMessage(w,0x0010,IntPtr.Zero,IntPtr.Zero);
                    };
                    timer.Start();MessageBox.Show(owner,"Offline native-dialog positioning check.",title,MessageBoxButtons.OK,MessageBoxIcon.Information);
                }
                Check(located&&centered,"Native MessageBox centred on owner");owner.Close();
            }
            Console.WriteLine("DIALOG_PLACEMENT_PASS="+checks);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    [StructLayout(LayoutKind.Sequential)]struct Rect{public int L,T,R,B;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr FindWindow(string type,string title);
    [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll")]static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
}
