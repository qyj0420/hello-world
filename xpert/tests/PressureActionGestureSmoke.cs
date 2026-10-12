using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

internal static class PressureActionGestureSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static double now;
    static void Check(bool condition,string text){checks++;if(!condition)throw new InvalidOperationException(text);}
    static object Get(object o,string name){return o.GetType().GetProperty(name,All).GetValue(o,null);}
    static void Call(object o,string method,object value){o.GetType().GetMethod(method,All).Invoke(o,new[]{value});}
    static string Hash(string path){using(var s=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","");}
    static void Native(Button b,int msg,int x,int y)
    {
        var m=Message.Create(b.Handle,msg,new IntPtr(msg==0x202?0:1),new IntPtr((y<<16)|(x&0xffff)));
        object[] arguments={m};b.GetType().GetMethod("WndProc",All).Invoke(b,arguments);
    }
    static void Down(Button b,int clicks,int x=20,int y=20)
    {Native(b,clicks>1?0x203:0x201,x,y);}
    static void Up(Button b,int x=20,int y=20){Native(b,0x202,x,y);}
    // Only this synthetic offscreen control receives WinForms mouse messages.
    // No physical input injection, application Main, worker, or SDK is invoked.
    static Button Create(Assembly assembly,Panel parent)
    {
        var b=(Button)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureActionButton",true),true);
        b.Bounds=new Rectangle(15,17,186,84);b.Text="&Start\n18.5 kV / 1/3";b.TabIndex=4;
        b.Margin=new Padding(1,2,3,4);parent.Controls.Add(b);b.CreateControl();
        b.GetType().GetField("NowMillisecondsForTests",All).SetValue(b,new Func<double>(()=>now));
        return b;
    }
    static Bitmap Paint(Button b)
    {
        var bitmap=new Bitmap(b.Width,b.Height);
        using(var g=Graphics.FromImage(bitmap))Call(b,"OnPaint",new PaintEventArgs(g,b.ClientRectangle));
        return bitmap;
    }
    static void GestureCases(Assembly assembly,string qa)
    {
        using(var form=new Form{ClientSize=new Size(320,200),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
        using(var panel=new Panel{Dock=DockStyle.Fill,BackColor=Color.White})
        {
            form.Controls.Add(panel);form.Show();panel.CreateControl();
            using(var b=Create(assembly,panel))
            {
                int clicks=0,starts=0,stops=0,layouts=0;bool running=false;Bitmap busy=null;
                b.Click+=delegate{
                    Check((bool)Get(b,"SubmissionBusy"),"busy feedback is active before dispatch");
                    Check(b.Enabled,"input feedback does not disable stop");
                    clicks++;if(running){stops++;running=false;}else{starts++;running=true;}
                    if(busy==null)busy=Paint(b);
                };
                var bounds=b.Bounds;var margin=b.Margin;var font=b.Font;int tab=b.TabIndex;
                panel.Layout+=delegate{layouts++;};
                now=0;Down(b,1);Up(b);
                Check(clicks==1&&starts==1&&stops==0,"first native single click starts exactly once");
                now=100;Native(b,0x203,20,20);Up(b);
                Check(clicks==1&&running,"native double-click tail does not immediately stop start");
                now=110;Down(b,1);Up(b);
                Check(clicks==2&&stops==1&&!running,"independent rapid single click stops immediately");
                now=115;Down(b,1);Up(b);now=120;Down(b,2);
                b.PerformClick();
                Check(clicks==4&&stops==2&&!running,"explicit PerformClick during tail mouse-down is never swallowed");
                Up(b);Check(clicks==4&&!running,"explicit stop does not let remaining double tail restart");
                now=125;Down(b,2);Call(b,"OnKeyDown",new KeyEventArgs(Keys.Space));b.PerformClick();
                Check(clicks==5&&running,"keyboard activation bypasses pointer tail");
                Call(b,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,20,20,0));
                now=200;Down(b,1);Up(b);now=451;Down(b,2);Up(b);
                Check(clicks==7&&running,"tail outside short 250 ms guard remains explicit action");
                now=500;Down(b,1);Up(b);now=510;Down(b,2,80,50);Up(b,80,50);
                Check(clicks==9&&running,"different pointer location is not a same-gesture tail");
                now=600;Down(b,1);Up(b);now=590;Down(b,2);Up(b);
                Check(clicks==11&&running,"backwards test clock does not suppress stop");
                now=700;Down(b,1);Up(b);now=710;Down(b,2);Up(b);
                Check(clicks==12&&!running,"a synthetic double tail is suppressed once");
                now=711;Down(b,2);Up(b);Check(clicks==13&&running,"tail consumption does not create an ongoing cooldown");
                b.Enabled=false;b.PerformClick();Check(clicks==13,"disabled inherited PerformClick remains disabled");b.Enabled=true;
                Check(!(bool)Get(b,"SubmissionBusy"),"busy state clears after click dispatch");
                using(var idle=Paint(b))
                {
                    int differences=0;for(int y=b.Height-12;y<b.Height-5;y++)for(int x=b.Width-24;x<b.Width-7;x++)
                        if(idle.GetPixel(x,y).ToArgb()!=busy.GetPixel(x,y).ToArgb())differences++;
                    Check(differences>0,"short synchronous submission indicator is actually painted");
                    busy.Save(Path.Combine(qa,"button-dispatch-busy.png"));idle.Save(Path.Combine(qa,"button-dispatch-idle.png"));
                }
                busy.Dispose();
                Check(b.Bounds==bounds&&b.Margin==margin&&b.Font==font&&b.TabIndex==tab,"geometry typography and tab order unchanged");
                Check(layouts==0,"pointer feedback/repaint does not relayout parent");
            }
            using(var b=Create(assembly,panel))
            {
                int count=0;bool nested=false;
                b.Click+=delegate{count++;if(!nested){nested=true;b.PerformClick();Check((bool)Get(b,"SubmissionBusy"),"nested explicit action preserves outer busy scope");}};
                b.PerformClick();Check(count==2&&!(bool)Get(b,"SubmissionBusy"),"nested explicit stop is not delayed or blocked");
            }
            using(var b=Create(assembly,panel))
            {
                b.Click+=delegate{throw new InvalidOperationException("test event failure");};
                bool failed=false;try{b.PerformClick();}catch(InvalidOperationException){failed=true;}
                Check(failed&&!(bool)Get(b,"SubmissionBusy")&&b.Enabled,"throwing callback unwinds busy visual without hiding error");
            }
            using(var b=Create(assembly,panel))
            {
                int count=0;b.Click+=delegate{count++;};
                now=900;Down(b,2);Up(b);
                Check(count==1,"native double message with no recorded first click is not discarded");
                Call(b,"OnMouseDown",new MouseEventArgs(MouseButtons.Right,2,20,20,0));b.PerformClick();
                Check(count==2,"right-pointer state does not mute explicit stop");
                Call(b,"OnMouseUp",new MouseEventArgs(MouseButtons.Right,1,20,20,0));
                Call(b,"OnKeyDown",new KeyEventArgs(Keys.Enter));b.PerformClick();
                Check(count==3,"Enter activation is not a mouse double tail");
            }
            using(var b=Create(assembly,panel))
            {
                b.Click+=delegate{b.Dispose();};b.PerformClick();
                Check(b.IsDisposed&&!(bool)Get(b,"SubmissionBusy"),"control disposal during dispatch leaves no busy timer or callback");
            }
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try{
            if(args.Length!=2)throw new ArgumentException("Exact module root (or --source) and QA path required.");
            bool source=args[0]=="--source";
            string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            Assembly assembly;
            if(source)assembly=typeof(PressureActionGestureSmoke).Assembly;
            else{
                string root=Path.GetFullPath(args[0]);
                AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string dependency=Path.Combine(root,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;};
                var analysis=Assembly.LoadFrom(Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
                analysis.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"synthetic-review-preferences.json")));
                string path=Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll");
                Console.WriteLine("ACTUAL_PRESSURE_SHA256="+Hash(path));assembly=Assembly.LoadFrom(path);
                using(var control=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true)))
                {var field=control.GetType().GetField("pressureActionButton",All);Check(field.GetValue(control).GetType().FullName=="HVX.Controls.PressureActionButton","actual control instantiates guarded button");}
            }
            GestureCases(assembly,qa);
            Console.WriteLine("PRESSURE_ACTION_GESTURE_PASS checks="+checks+" HardwareAccessed=False ProductMainCalled=False SourceOnly="+source);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);Console.Error.WriteLine("PRESSURE_ACTION_GESTURE_FAIL checks="+checks);return 1;}
    }
}
