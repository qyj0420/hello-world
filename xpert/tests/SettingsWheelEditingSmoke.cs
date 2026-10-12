using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class SettingsWheelEditingSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static int checks;
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr handle,int message,IntPtr wParam,IntPtr lParam);
    static void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
    static void Wheel(Control control,int delta){control.GetType().GetMethod("OnMouseWheel",B).Invoke(control,new object[]{new HandledMouseEventArgs(MouseButtons.None,0,10,10,delta)});}
    [STAThread]static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();string root=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);if(Directory.Exists(qa))throw new Exception("Fresh fixture required.");Directory.CreateDirectory(qa);
            string dll=Path.Combine(root,"SettingsWheelControls.dll");if(!File.Exists(dll))dll=Path.Combine(root,"Xiongxin.OWTS.Desktop.exe");var assembly=Assembly.LoadFrom(dll);
            using(var form=new Form{ClientSize=new Size(460,300),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-2200,-2200)})
            using(var panel=new Panel{Dock=DockStyle.Fill,AutoScroll=true})
            using(var number=(NumericUpDown)Activator.CreateInstance(assembly.GetType("Xiongxin.OWTS.Desktop.SettingsNumber",true)))
            using(var combo=(ComboBox)Activator.CreateInstance(assembly.GetType("Xiongxin.OWTS.Desktop.SettingsComboBox",true),true)){
                form.Controls.Add(panel);number.SetBounds(20,20,160,30);number.Minimum=0;number.Maximum=100;number.DecimalPlaces=2;number.Value=12.34m;combo.SetBounds(20,60,180,30);combo.DropDownStyle=ComboBoxStyle.DropDownList;combo.Items.AddRange(new object[]{"DIV","VMON","other"});combo.SelectedIndex=1;
                panel.Controls.Add(number);panel.Controls.Add(combo);panel.Controls.Add(new Label{Top=1000,Text="end"});form.Show();Application.DoEvents();Check(panel.VerticalScroll.Visible,"Scrollable settings fixture");
                int changed=0,selected=0;number.ValueChanged+=(s,e)=>changed++;combo.SelectedIndexChanged+=(s,e)=>selected++;
                number.Focus();Wheel(number,-120);Check(number.Value==12.34m&&changed==0&&panel.AutoScrollPosition.Y<0,"Focused numeric wheel scrolls settings without editing");
                panel.AutoScrollPosition=new Point(0,0);combo.Focus();Wheel(combo,-120);Check(combo.SelectedIndex==1&&selected==0&&panel.AutoScrollPosition.Y<0,"Focused enum wheel scrolls settings without changing selection");
                panel.AutoScrollPosition=new Point(0,0);combo.DroppedDown=true;Application.DoEvents();
                var list=(IntPtr)combo.GetType().GetProperty("DropDownWheelHandle",B).GetValue(combo,null);
                Check(list!=IntPtr.Zero,"Real expanded COMBOLBOX wheel hook attached");
                SendMessage(list,0x020A,new IntPtr(unchecked((int)0xff880000)),IntPtr.Zero);Application.DoEvents();
                Check(combo.SelectedIndex==1&&selected==0&&panel.AutoScrollPosition.Y<0,"Expanded native list wheel scrolls page without selecting");
                SendMessage(list,0x020A,new IntPtr(120<<16),IntPtr.Zero);Application.DoEvents();
                Check(combo.SelectedIndex==1&&selected==0,"Expanded opposite wheel cannot change selection");
                combo.DroppedDown=false;Application.DoEvents();Check((IntPtr)combo.GetType().GetProperty("DropDownWheelHandle",B).GetValue(combo,null)==IntPtr.Zero,"Dropdown closure releases the list hook");
                int y=panel.AutoScrollPosition.Y;Wheel(combo,0);Check(panel.AutoScrollPosition.Y==y&&selected==0,"Zero wheel is inert");
                Wheel(number,120);Check(number.Value==12.34m&&changed==0,"Opposite wheel leaves precise value intact");
                number.UpButton();Check(number.Value==13.34m&&changed==1,"Explicit spinner arrow still edits");
                number.Text="23.45";number.GetType().GetMethod("ValidateEditText",B).Invoke(number,new object[0]);Check(number.Value==23.45m,"Explicit numeric input still edits");
                combo.SelectedIndex=2;Check(selected==1&&combo.SelectedIndex==2,"Explicit enum selection still edits");
                panel.AutoScroll=false;Wheel(number,-120);Wheel(combo,120);Check(number.Value==23.45m&&combo.SelectedIndex==2,"Wheel with no scrolling ancestor cannot edit parameters");form.Close();
            }
            Console.WriteLine("SETTINGS_WHEEL_EDITING_PASS="+checks+";HARDWARE_ACCESSED=False;SDK_CALLED=False;QA="+qa);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
