using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.Presentation;
using Xiongxin.OWTS.Desktop;

static class OemBrandingSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static byte[] IconBytes(Icon icon){using(var m=new MemoryStream()){icon.Save(m);return m.ToArray();}}
    static bool SameIconPixels(Icon actual,Icon expected){using(var a=actual.ToBitmap())using(var b=expected.ToBitmap()){if(a.Size!=b.Size)return false;for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++)if(a.GetPixel(x,y).ToArgb()!=b.GetPixel(x,y).ToArgb())return false;return true;}}
    static Icon NeutralIcon(){using(var stream=typeof(OemBrandingSmoke).Assembly.GetManifestResourceStream("HVX.NeutralWindow.ico")){if(stream==null)throw new InvalidDataException("Neutral icon test resource missing.");using(var icon=new Icon(stream))return (Icon)icon.Clone();}}
    static void Wait(Func<bool> done){var sw=Stopwatch.StartNew();while(!done()&&sw.ElapsedMilliseconds<10000){Application.DoEvents();System.Threading.Thread.Sleep(3);}Check(done(),"bounded async operation completes");}
    static T Get<T>(object x,string name){return (T)x.GetType().GetField(name,B).GetValue(x);}
    static void Set(object x,string name,object value){x.GetType().GetField(name,B).SetValue(x,value);}
    static void Reject(Task task,string why){bool rejected=false;try{task.GetAwaiter().GetResult();}catch(Exception e){rejected=e is IOException||e is InvalidDataException||e is ArgumentException||e is UnauthorizedAccessException||e is System.Runtime.InteropServices.ExternalException;}Check(rejected,why);}
    [STAThread] static int Main(string[] args)
    {
        try {
            string qa=Path.GetFullPath(args[0]);Directory.CreateDirectory(qa);typeof(AppPaths).GetField("LocalDataDirectory",B).SetValue(null,Path.Combine(qa,"profile"));
            Application.EnableVisualStyles();OemUiBranding.Publish(null,null);
            var defaults=new AppSettings();Check(defaults.IconPath==""&&defaults.LogoPath=="","neutral defaults have no customer assets");Check(!defaults.CopyrightText.Contains("雄新")&&!defaults.CopyrightText.Contains("XIONGXIN")&&!defaults.CopyrightText.Contains("KEHUI"),"default copyright neutral");
            using(var icon=AppTheme.LoadIcon())using(var expected=NeutralIcon())Check(SameIconPixels(icon,expected),"neutral default uses exact shortcut artwork pixels and dimensions, not generic or customer brand");Check(AppTheme.LoadLogo()==null,"neutral default has no logo");
            string reason;foreach(string path in new[]{@"\\server\share\logo.png",@"\\?\C:\logo.png",@"\\.\C:\logo.png","https://example.invalid/logo.png","logo.png","C:logo.png",@"C:\logo.png:payload"})Check(!OemBranding.ValidateAssetPath(path,out reason),"unsafe path rejected without file I/O: "+path);
            Check(OemBranding.ValidateAssetPath("",out reason)&&OemBranding.ValidateAssetPath(Path.Combine(qa,"absent.png"),out reason),"blank and temporarily absent local path remain configurable");
            string logo=Path.Combine(qa,"logo.png"),iconFile=Path.Combine(qa,"logo.ico");using(var bitmap=new Bitmap(48,24)){using(var g=Graphics.FromImage(bitmap))g.Clear(Color.MediumPurple);bitmap.Save(logo,ImageFormat.Png);}using(var f=File.Create(iconFile))SystemIcons.Information.Save(f);
            foreach(var format in new[]{ImageFormat.Jpeg,ImageFormat.Bmp}){string path=Path.Combine(qa,format.ToString()+".img");using(var bitmap=new Bitmap(17,13))bitmap.Save(path,format);using(var result=OemBranding.LoadAsync(path,null).GetAwaiter().GetResult())Check(result.Warning==""&&result.Logo.Width==17&&result.Logo.Height==13,"supported bounded format: "+format);}
            byte[] originalLogo=File.ReadAllBytes(logo),originalIcon=File.ReadAllBytes(iconFile);
            using(var loaded=OemBranding.LoadAsync(logo,iconFile).GetAwaiter().GetResult()){
                Check(loaded.Warning==""&&loaded.Logo.Width==48&&loaded.Logo.Height==24&&loaded.Icon!=null,"valid icon/logo decode without changing aspect");
                using(var form=new Form()){AppTheme.ApplyDialogIcon(form);OemUiBranding.Publish(loaded.Icon,loaded.Logo);Check(IconBytes(form.Icon).SequenceEqual(IconBytes(loaded.Icon)),"existing registered window updates to same OEM source");using(var clone=AppTheme.LoadLogo())Check(clone.Width==48&&clone.Height==24,"memory-only logo accessor");}
            }
            using(var exclusive=File.Open(logo,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Check(exclusive.Length>0,"loader releases source image handle");
            var candidate=new AppSettings{LogoPath=logo,IconPath=iconFile};OemBranding.ImportAsync(candidate).GetAwaiter().GetResult();
            Check(candidate.LogoPath.StartsWith(OemBranding.AssetDirectory+Path.DirectorySeparatorChar)&&candidate.IconPath.StartsWith(OemBranding.AssetDirectory+Path.DirectorySeparatorChar),"assets are preserved in installation OEMpic, not left pointing to input files");
            Check(File.ReadAllBytes(candidate.LogoPath).SequenceEqual(originalLogo)&&File.ReadAllBytes(candidate.IconPath).SequenceEqual(originalIcon),"persisted OEM resources retain exact bytes");
            string storedLogo=candidate.LogoPath,storedIcon=candidate.IconPath;OemBranding.ImportAsync(candidate).GetAwaiter().GetResult();Check(candidate.LogoPath==storedLogo&&candidate.IconPath==storedIcon,"same content is reused");
            File.Move(logo,logo+".moved");File.Move(iconFile,iconFile+".moved");using(var restored=OemBranding.LoadAsync(storedLogo,storedIcon).GetAwaiter().GetResult())Check(restored.Logo!=null&&restored.Icon!=null&&restored.Warning=="","moved original does not break saved OEM resources");
            candidate.Save();AppSettings read;string diagnostic;Check(AppSettings.TryLoad(AppPaths.SettingsFile,out read,out diagnostic)&&read.LogoPath==storedLogo&&read.IconPath==storedIcon,"both paths survive settings save/reload");
            string bad=Path.Combine(qa,"bad.png");File.WriteAllText(bad,"not an image at all");
            using(var invalid=OemBranding.LoadAsync(bad,bad).GetAwaiter().GetResult()){Check(invalid.Warning.Length>0&&invalid.Icon==null&&invalid.Logo==null,"bad assets produce neutral result and explicit warning");OemUiBranding.Publish(invalid.Icon,invalid.Logo);}
            using(var icon=AppTheme.LoadIcon())using(var expected=NeutralIcon())Check(SameIconPixels(icon,expected),"failed OEM load uses neutral shortcut artwork pixels and dimensions, never customer brand");
            using(var missing=OemBranding.LoadAsync(logo,iconFile).GetAwaiter().GetResult())Check(missing.Warning.Length>0,"missing assets are visible failures");
            using(var remote=OemBranding.LoadAsync(@"\\never-contact.invalid\logo.png",null).GetAwaiter().GetResult())Check(remote.Warning.Length>0&&remote.Logo==null,"UNC never reaches image read");
            var wrong=new AppSettings{LogoPath=bad,IconPath=storedIcon};Reject(OemBranding.ImportAsync(wrong),"invalid selected logo rejects import");Check(wrong.LogoPath==bad&&wrong.IconPath==storedIcon,"failed import leaves configuration paths unchanged");
            string oversize=Path.Combine(qa,"large.png");using(var f=File.Create(oversize))f.SetLength(OemBranding.MaxFileBytes+1);Reject(OemBranding.ImportAsync(new AppSettings{LogoPath=oversize}),"file size checked before allocation/decode");
            var header=new byte[24];header[0]=137;header[1]=80;header[2]=78;header[3]=71;header[16]=127;header[20]=127;string bomb=Path.Combine(qa,"dimensions.png");File.WriteAllBytes(bomb,header);Reject(OemBranding.ImportAsync(new AppSettings{LogoPath=bomb}),"oversized header dimensions rejected before decode");
            using(var settings=new SettingsForm(new AppSettings(),SettingsSection.Oem)){
                Check(Get<TextBox>(settings,"iconPath")!=null,"OEM exposes separate icon selection");
                Get<TextBox>(settings,"logoPath").Text=storedLogo;Get<TextBox>(settings,"iconPath").Text=storedIcon;
                var captured=(AppSettings)typeof(SettingsForm).GetMethod("CaptureValues",B).Invoke(settings,null);Check(captured.LogoPath==storedLogo&&captured.IconPath==storedIcon,"OEM control values enter saved candidate");
                typeof(SettingsForm).GetMethod("SaveClicked",B).Invoke(settings,new object[]{null,EventArgs.Empty});Wait(()=>settings.DialogResult==DialogResult.OK);
                Check(settings.Settings.LogoPath==storedLogo&&settings.Settings.IconPath==storedIcon,"actual settings save completes after importing resources");
            }
            byte[] beforeDisposedSave=File.ReadAllBytes(AppPaths.SettingsFile);
            foreach(bool invalid in new[]{false,true}){
                var settings=new SettingsForm(new AppSettings(),SettingsSection.Oem);Get<TextBox>(settings,"logoPath").Text=invalid?bad:storedLogo;
                typeof(SettingsForm).GetMethod("SaveClicked",B).Invoke(settings,new object[]{null,EventArgs.Empty});settings.Dispose();Wait(()=>!Get<bool>(settings,"savingOemAssets"));
                Check(beforeDisposedSave.SequenceEqual(File.ReadAllBytes(AppPaths.SettingsFile)),"forced disposal does not save or show disposed-owner error: "+invalid);
            }
            var registry=AppDomain.CurrentDomain.GetData("HVX.OemUi.Windows.v1") as Dictionary<Form,Action>;int initial=registry==null?0:registry.Count;
            var closing=new Form();AppTheme.ApplyDialogIcon(closing);AppTheme.ApplyDialogIcon(closing);registry=(Dictionary<Form,Action>)AppDomain.CurrentDomain.GetData("HVX.OemUi.Windows.v1");Check(registry.Count==initial+1,"repeated Apply registers one owner");
            var handle=closing.Handle;Task.Run(()=>OemUiBranding.Publish(SystemIcons.Warning,null)).GetAwaiter().GetResult();closing.Dispose();Application.DoEvents();
            Check(registry.Count==initial,"queued refresh then disposal releases owner subscription");
            using(var survivor=new Form())using(var fake=new Form()){
                AppTheme.ApplyDialogIcon(survivor);registry.Add(fake,()=>{throw new IOException("synthetic listener error");});bool surfaced=false;
                try{OemUiBranding.Publish(SystemIcons.Information,null);}catch(AggregateException){surfaced=true;}finally{registry.Remove(fake);}
                Check(surfaced,"unexpected listener error remains observable");Check(IconBytes(survivor.Icon).SequenceEqual(IconBytes(SystemIcons.Information)),"one failing listener does not block other window updates");
            }
            Check(registry.Count==initial,"all temporary owners removed");
            // Exercise only the branding partial on an uninitialized shell: no
            // MainForm constructor, timers, device discovery or product entry.
            var shell=(MainForm)FormatterServices.GetUninitializedObject(typeof(MainForm));var box=new PictureBox();var panel=new Panel();
            Set(shell,"settings",new AppSettings{LogoPath=bad});Set(shell,"brandLogo",box);Set(shell,"headerPanel",panel);
            typeof(MainForm).GetMethod("ReplaceLogo",B).Invoke(shell,null);Wait(()=>shell.OemLoadTask!=null&&shell.OemLoadTask.IsCompleted);shell.OemLoadTask.GetAwaiter().GetResult();
            Check(box.Image==null&&Get<Label>(shell,"oemWarning").Text.Contains("无法加载"),"main branding displays missing/bad resource warning with neutral logo");
            var before=shell.OemLoadTask;typeof(MainForm).GetMethod("ReplaceLogo",B).Invoke(shell,null);Check(Object.ReferenceEquals(before,shell.OemLoadTask),"normal navigation does not reload the same asset or repeat I/O");
            box.Dispose();panel.Dispose();OemUiBranding.Publish(null,null);
            Console.WriteLine("OEM_BRANDING_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_PROJECT_WRITES=0; USER_SETTINGS_WRITES=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
