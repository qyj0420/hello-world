using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class OemLogoPresentationSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static void Wait(Func<bool> condition){var clock=Stopwatch.StartNew();while(!condition()&&clock.ElapsedMilliseconds<5000){Application.DoEvents();System.Threading.Thread.Sleep(5);}Check(condition(),"bounded preview completes");}
    static object Field(object o,string name){return o.GetType().GetField(name,B).GetValue(o);}
    static string Fixture(string root,string name,Size size,Color background,Rectangle content)
    {
        string path=Path.Combine(root,name+".png");
        using(var bitmap=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppArgb))
        {using(var g=Graphics.FromImage(bitmap)){g.Clear(background);using(var brush=new SolidBrush(Color.MediumPurple))g.FillRectangle(brush,content);}bitmap.Save(path,ImageFormat.Png);}
        return path;
    }
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();string qa=Path.GetFullPath(args[0]);Directory.CreateDirectory(qa);
            typeof(AppPaths).GetField("LocalDataDirectory",B).SetValue(null,Path.Combine(qa,"profile"));
            var fixtures=new[]{
                new{Path=Fixture(qa,"alpha",new Size(100,70),Color.Transparent,new Rectangle(12,10,60,35)),Bounds=new Rectangle(12,10,60,35)},
                new{Path=Fixture(qa,"white-edge",new Size(400,150),Color.White,new Rectangle(40,30,300,80)),Bounds=new Rectangle(40,30,300,80)},
                new{Path=Fixture(qa,"wide",new Size(900,25),Color.Transparent,new Rectangle(0,0,900,25)),Bounds=new Rectangle(0,0,900,25)},
                new{Path=Fixture(qa,"tall",new Size(25,900),Color.Transparent,new Rectangle(0,0,25,900)),Bounds=new Rectangle(0,0,25,900)},
                new{Path=Fixture(qa,"near-white",new Size(100,70),Color.FromArgb(254,254,254),new Rectangle(12,10,60,35)),Bounds=new Rectangle(0,0,100,70)}
            };
            foreach(var fixture in fixtures)
            {
                byte[] original=File.ReadAllBytes(fixture.Path);
                using(var source=new Bitmap(fixture.Path))using(var result=OemBranding.LoadAsync(fixture.Path,null).GetAwaiter().GetResult())
                {
                    Check(result.Warning==""&&result.Logo!=null&&result.LogoGeometry!=null,"valid PNG accepted "+fixture.Path);
                    Check(result.LogoGeometry.OriginalSize==source.Size&&result.LogoGeometry.ContentBounds==fixture.Bounds&&result.LogoGeometry.DisplaySize==fixture.Bounds.Size,"exact original/crop metadata "+fixture.Path);
                    using(var shown=new Bitmap(result.Logo))for(int y=0;y<shown.Height;y+=Math.Max(1,shown.Height/7))for(int x=0;x<shown.Width;x+=Math.Max(1,shown.Width/9))
                        Check(shown.GetPixel(x,y).ToArgb()==source.GetPixel(fixture.Bounds.Left+x,fixture.Bounds.Top+y).ToArgb(),"retained original pixel color/alpha "+fixture.Path);
                    foreach(var viewport in new[]{new RectangleF(0,0,295,67),new RectangleF(7,9,150,61),new RectangleF(0,0,590,134)})
                    {
                        RectangleF fit=OemLogoPresentation.Contain(result.Logo.Size,viewport);
                        Check(fit.Left>=viewport.Left-.001&&fit.Top>=viewport.Top-.001&&fit.Right<=viewport.Right+.001&&fit.Bottom<=viewport.Bottom+.001,"contain never clips viewport "+fixture.Path);
                        Check(Math.Abs(fit.Width/fit.Height-result.LogoGeometry.DisplayAspect)<.0001,"wide/tall logo never stretches "+fixture.Path);
                        Check(Math.Abs(fit.Left+fit.Width/2-(viewport.Left+viewport.Width/2))<.001&&Math.Abs(fit.Top+fit.Height/2-(viewport.Top+viewport.Height/2))<.001,"contain preserves center "+fixture.Path);
                    }
                    Check(OemLogoPresentation.NormalHeaderViewport==new Size(295,67),"ratio recommendation derives from unchanged normal header viewport");
                }
                var settings=new AppSettings{LogoPath=fixture.Path};OemBranding.ImportAsync(settings).GetAwaiter().GetResult();
                Check(File.ReadAllBytes(settings.LogoPath).SequenceEqual(original)&&File.ReadAllBytes(fixture.Path).SequenceEqual(original),"original source and preserved copy remain exact bytes "+fixture.Path);
            }
            using(var bitmap=new Bitmap(10,10,PixelFormat.Format32bppArgb))
            {
                bitmap.SetPixel(2,2,Color.FromArgb(1,80,90,100));bitmap.SetPixel(7,8,Color.FromArgb(200,60,90,120));
                OemLogoGeometry geometry;using(var prepared=OemLogoPresentation.Prepare(bitmap,out geometry))
                {Check(geometry.ContentBounds==new Rectangle(2,2,6,7),"alpha1 antialiased content is not discarded");Check(prepared.GetPixel(0,0).A==1&&prepared.GetPixel(5,6).A==200,"PNG alpha is retained, not composited on white");}
            }
            using(var bitmap=new Bitmap(10,10))
            {using(var g=Graphics.FromImage(bitmap))g.Clear(Color.White);bitmap.SetPixel(0,3,Color.Red);bitmap.SetPixel(5,5,Color.Blue);
                OemLogoGeometry geometry;using(var result=OemLogoPresentation.Prepare(bitmap,out geometry))Check(result.Size==bitmap.Size&&!geometry.Trimmed,"content touching perimeter disables white-background trim");}
            string bad=Path.Combine(qa,"invalid.png");File.WriteAllText(bad,"not a valid PNG");
            var invalid=new AppSettings{LogoPath=bad,IconPath=""};bool rejected=false;try{OemBranding.ImportAsync(invalid).GetAwaiter().GetResult();}catch{rejected=true;}
            Check(rejected&&invalid.LogoPath==bad&&invalid.IconPath=="","invalid image cannot rewrite candidate configuration");
            string large=Path.Combine(qa,"too-large.png");using(var stream=File.Create(large))stream.SetLength(OemBranding.MaxFileBytes+1);
            rejected=false;try{OemBranding.ImportAsync(new AppSettings{LogoPath=large}).GetAwaiter().GetResult();}catch(InvalidDataException){rejected=true;}Check(rejected,"8MB quota checked before decode");
            byte[] bomb=new byte[24];bomb[0]=137;bomb[1]=80;bomb[2]=78;bomb[3]=71;bomb[16]=127;bomb[20]=127;
            string dimensions=Path.Combine(qa,"oversize-header.png");File.WriteAllBytes(dimensions,bomb);
            rejected=false;try{OemBranding.ImportAsync(new AppSettings{LogoPath=dimensions}).GetAwaiter().GetResult();}catch(InvalidDataException){rejected=true;}Check(rejected,"dimension quota rejects before image allocation");
            foreach(string language in new[]{"zh-CN","en","zh-TW"})
            using(var form=new SettingsForm(new AppSettings{LanguageCode=language},SettingsSection.Oem))
            {
                form.AuditEnabled=false;form.Location=new Point(-30000,-30000);form.ShowInTaskbar=false;form.Show();Application.DoEvents();
                var path=(TextBox)Field(form,"logoPath");var info=(Label)Field(form,"oemLogoInfo");path.Text=fixtures[1].Path;
                Wait(()=>info.Text.Contains("400×150")&&info.Text.Contains("300×80"));
                Check(info.Text.Contains("4:1")&&info.Text.Contains("5:1"),"natural ratio suggestion "+language);
                Check(info.Height>=info.GetPreferredSize(new Size(info.Width,0)).Height,"metadata note fits "+language);
                if(language=="en")Check(!info.Text.Any(c=>c>='\u4e00'&&c<='\u9fff')&&info.Text.Contains("Original"),"English metadata not untranslated");
                if(language=="zh-TW")Check(info.Text.Contains("圖")&&info.Text.Contains("顯示"),"traditional metadata translated");
                path.Text="";Wait(()=>!info.Text.Contains("400×150"));Check(((AppSettings)Field(form,"original")).LogoPath=="","preview never edits original configuration");
                path.Text=fixtures[0].Path;form.Dispose();Application.DoEvents();Check(form.IsDisposed,"pending preview safely disposed");
            }
            using(var empty=OemBranding.LoadAsync("","").GetAwaiter().GetResult())Check(empty.Logo==null&&empty.LogoGeometry==null,"blank logo has no displayed image or dimensions");
            Console.WriteLine("OEM_LOGO_PRESENTATION_PASS="+checks+";HardwareAccessed=False;SDKCalled=False;ProductEntryStarted=False;QA="+qa);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
