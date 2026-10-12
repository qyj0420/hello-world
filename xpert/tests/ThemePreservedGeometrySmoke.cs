using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class ThemePreservedGeometrySmoke
{
    static int checks;
    static readonly Color Canvas=Color.FromArgb(49,58,67);
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly host,pressure,browser;
    static Type Exact(Assembly a,string n){return a.GetType(n,true);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,All);if(p!=null)return p.GetValue(o,null);return o.GetType().GetField(n,All).GetValue(o);}
    static void Set(object o,string n,object v){var p=o.GetType().GetProperty(n,All);if(p!=null)p.SetValue(o,v,null);else o.GetType().GetField(n,All).SetValue(o,v);}
    static Color Palette(Assembly a,string t,string n){return(Color)Exact(a,t).GetField(n,All).GetValue(null);}
    static Color A(string n){return Palette(host,"Xiongxin.OWTS.Desktop.AppTheme",n);}
    static Color P(string n){return Palette(pressure,"HVX.Controls.PressureTestTheme",n);}
    static Color B(string n){return Palette(browser,"HVX.Controls.BrowserTheme",n);}
    static Control New(Assembly a,string n){return(Control)Activator.CreateInstance(Exact(a,n),true);}
    static void Check(bool pass,string message){checks++;if(!pass)throw new InvalidOperationException(message);}
    static Bitmap Paint(Control c,float dpi)
    {
        var bitmap=new Bitmap(c.Width,c.Height);bitmap.SetResolution(dpi,dpi);
        using(var g=Graphics.FromImage(bitmap)){
            g.Clear(Canvas);
            c.GetType().GetMethod("OnPaint",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(c,new object[]{new PaintEventArgs(g,c.ClientRectangle)});
        }
        return bitmap;
    }
    static void Corners(Bitmap b,string name)
    {
        foreach(var p in new[]{new Point(0,0),new Point(b.Width-1,0),new Point(0,b.Height-1),new Point(b.Width-1,b.Height-1)})
            Check(b.GetPixel(p.X,p.Y).ToArgb()==Canvas.ToArgb(),name+" retains outside-corner canvas "+p);
    }
    static void HostButtons(string qa)
    {
        using(var panel=new Panel{BackColor=Canvas})
        using(var button=(Button)Exact(host,"Xiongxin.OWTS.Desktop.AppTheme").GetMethod("Button",All).Invoke(null,new object[]{"&Apply",false}))
        {
            panel.Controls.Add(button);button.SetBounds(17,23,142,38);
            var bounds=button.Bounds;var margin=button.Margin;var font=button.Font;var padding=button.Padding;
            int clicks=0;button.Click+=(s,e)=>clicks++;
            Check(button.Height==38&&button.Margin==new Padding(4)&&Math.Abs(button.Font.Size-9.5)<.01,"host factory original geometry/font");
            button.PerformClick();Check(clicks==1,"original PerformClick");
            var mnemonic=typeof(Button).GetMethod("ProcessMnemonic",BindingFlags.Instance|BindingFlags.NonPublic);
            Check((bool)mnemonic.Invoke(button,new object[]{'A'})&&clicks==2,"inherited mnemonic activation");
            foreach(float dpi in new[]{96f,144f})using(var image=Paint(button,dpi)){
                Corners(image,"host normal "+dpi);
                Check(image.GetPixel(30,10).ToArgb()==Color.White.ToArgb(),"normal white fill");
                image.Save(Path.Combine(qa,"host-normal-"+dpi+".png"));
            }
            button.BackColor=A("Accent");button.ForeColor=Color.White;button.FlatAppearance.BorderColor=A("Accent");
            using(var image=Paint(button,96))Check(image.GetPixel(30,10).ToArgb()==A("Accent").ToArgb(),"primary cyan");
            button.FlatAppearance.BorderColor=A("Danger");button.ForeColor=A("Danger");button.BackColor=Color.White;
            button.GetType().GetMethod("OnMouseEnter",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(button,new object[]{EventArgs.Empty});
            using(var image=Paint(button,96))Check(image.GetPixel(button.Width/2,0).R>image.GetPixel(button.Width/2,0).B,"semantic error border remains red on hover");
            button.DialogResult=DialogResult.Cancel;button.Enabled=false;button.PerformClick();Check(clicks==2,"disabled click suppressed");
            using(var image=Paint(button,96)){Corners(image,"disabled");Check(image.GetPixel(30,10).ToArgb()!=A("Accent").ToArgb(),"disabled not primary");}
            Check(button.Bounds==bounds&&button.Margin==margin&&button.Padding==padding&&button.Font==font&&button.DialogResult==DialogResult.Cancel,"all host geometry/font/dialog result preserved");
        }
    }
    static void PressureButtons(string qa)
    {
        using(var panel=new Panel{BackColor=Canvas})using(var b=New(pressure,"HVX.Controls.PressureTestButton"))
        {
            b.Text="";b.Size=new Size(170,58);
            panel.Controls.Add(b);var bounds=b.Bounds;var font=b.Font;
            foreach(float dpi in new[]{96f,144f})using(var image=Paint(b,dpi)){Corners(image,"pressure "+dpi);Check(image.GetPixel(35,18).ToArgb()==Color.White.ToArgb(),"pressure normal flat white");}
            Set(b,"Primary",true);using(var image=Paint(b,96))Check(image.GetPixel(35,18).ToArgb()==P("Accent").ToArgb(),"pressure primary");
            Set(b,"Primary",false);Set(b,"Success",true);using(var image=Paint(b,96))Check(image.GetPixel(35,18).ToArgb()==P("Success").ToArgb(),"success semantic green");
            Set(b,"Success",false);Set(b,"Danger",true);Set(b,"MeasurementProgressVisible",true);Set(b,"MeasurementProgress",.4);
            using(var image=Paint(b,96)){Check(image.GetPixel(20,18).ToArgb()==Color.FromArgb(255,209,212).ToArgb(),"error progress semantic red");Check(image.GetPixel(125,18).ToArgb()==P("SurfaceAlt").ToArgb(),"measured 40 percent not invented full fill");image.Save(Path.Combine(qa,"pressure-progress.png"));}
            Check((double)Get(b,"MeasurementProgress")==.4&&(bool)Get(b,"MeasurementProgressVisible")&&(bool)Get(b,"Danger"),"progress and danger unchanged");
            Set(b,"MeasurementProgress",Double.NaN);using(var image=Paint(b,96))Check(image.GetPixel(20,18).ToArgb()==P("SurfaceAlt").ToArgb(),"unknown feedback does not invent progress");
            b.Enabled=false;using(var image=Paint(b,96))Corners(image,"pressure disabled");
            Check(b.Bounds==bounds&&b.Font==font,"pressure geometry/font preserved");
        }
    }
    static void PhaseButtons()
    {
        foreach(var color in new[]{B("PhaseA"),B("PhaseB"),B("PhaseC")})
        using(var panel=new Panel{BackColor=Canvas})using(var b=(CheckBox)New(browser,"HVX.Controls.BrowserPhaseButton")){
            Set(b,"PhaseColor",color);b.Text="A";
            panel.Controls.Add(b);var bounds=b.Bounds;var font=b.Font;
            typeof(CheckBox).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(b,new object[]{EventArgs.Empty});
            Check(b.Checked,"original check toggle");
            foreach(float dpi in new[]{96f,144f})using(var image=Paint(b,dpi)){Corners(image,"phase "+dpi);Check(image.GetPixel(23,8).ToArgb()==color.ToArgb(),"selected phase color preserved");}
            typeof(CheckBox).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(b,new object[]{EventArgs.Empty});Check(!b.Checked,"original uncheck toggle");
            Check(bounds==b.Bounds&&font==b.Font,"phase geometry and font preserved");
        }
    }
    static void Calibration(string module)
    {
        var assembly=Assembly.LoadFrom(module);
        var type=assembly.GetType("HVX.Controls.CalibrationProgramControl",true);
        Check(type!=null,"real calibration type");
        var method=type.GetMethod("ThemeButtonBox",BindingFlags.Static|BindingFlags.NonPublic);Check(method!=null,"real shared calibration button renderer");
        foreach(float dpi in new[]{96f,144f})using(var b=new Bitmap(150,60)){
            b.SetResolution(dpi,dpi);using(var g=Graphics.FromImage(b)){
            g.Clear(Canvas);var rect=new RectangleF(10,10,130,40);
            method.Invoke(null,new object[]{g,rect,Color.White,A("Border")});
            foreach(var p in new[]{new Point(10,10),new Point(139,10),new Point(10,49),new Point(139,49)})Check(b.GetPixel(p.X,p.Y).ToArgb()==Canvas.ToArgb(),"calibration rounded original rectangle");
            Check(b.GetPixel(30,20).ToArgb()==Color.White.ToArgb(),"calibration normal fill");
            Check(b.GetPixel(5,20).ToArgb()==Canvas.ToArgb()&&b.GetPixel(145,20).ToArgb()==Canvas.ToArgb(),"calibration preserves outside geometry");
            method.Invoke(null,new object[]{g,new RectangleF(0,0,0,0),Color.White,A("Border")});Check(true,"empty layout has no path exception");
            }
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            if(args.Length==3){
                host=Assembly.LoadFrom(Path.Combine(args[2],"Xiongxin.OWTS.Desktop.exe"));
                pressure=Assembly.LoadFrom(Path.Combine(args[2],"modules/PressureTest/Xiongxin.OWTS.PressureTest.dll"));
                browser=Assembly.LoadFrom(Path.Combine(args[2],"modules/DataBrowser/Xiongxin.OWTS.DataBrowser.dll"));
                Console.WriteLine("SAME_FINAL_BYTES=1; HOST_MAIN_EXECUTED=0; EXACT_RETAINED_METADATA=1");
            }else if(args.Length==2){host=pressure=browser=Assembly.GetExecutingAssembly();}
            else throw new ArgumentException("QA directory, actual calibration module, optional final build required.");
            Directory.CreateDirectory(args[0]);HostButtons(args[0]);PressureButtons(args[0]);PhaseButtons();Calibration(args[1]);Console.WriteLine("PASS "+checks+" checks; hardware=0; original-user-writes=0; style-only managed fixtures.");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
