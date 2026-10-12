using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HVX.Presentation;
using Xiongxin.OWTS.Desktop;

// Only a plain fake Form and icon/resource APIs execute. The supplied desktop
// EXE is opened as data, never loaded as executable code or started.
internal static class OemWindowIconSmoke
{
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new InvalidDataException(why);}
    static byte[] Bytes(Icon icon){using(var m=new MemoryStream()){icon.Save(m);return m.ToArray();}}
    static bool Pixels(Icon actual,Icon expected)
    {using(var a=actual.ToBitmap())using(var b=expected.ToBitmap()){if(a.Size!=b.Size)return false;for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++)if(a.GetPixel(x,y).ToArgb()!=b.GetPixel(x,y).ToArgb())return false;return true;}}
    static byte[] Resource(Assembly a,string name)
    {using(var s=a.GetManifestResourceStream(name)){if(s==null)return null;using(var m=new MemoryStream()){s.CopyTo(m);return m.ToArray();}}}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr LoadLibraryEx(string path,IntPtr reserved,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll",EntryPoint="EnumResourceNamesW",SetLastError=true)]static extern bool EnumResourceNames(IntPtr module,IntPtr type,ResourceName callback,IntPtr state);
    delegate bool ResourceName(IntPtr module,IntPtr type,IntPtr name,IntPtr state);
    [DllImport("kernel32.dll",EntryPoint="FindResourceW",SetLastError=true)]static extern IntPtr FindResource(IntPtr module,IntPtr name,IntPtr type);
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr LoadResource(IntPtr module,IntPtr resource);
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll",SetLastError=true)]static extern uint SizeofResource(IntPtr module,IntPtr resource);
    static byte[] NativeResource(IntPtr module,IntPtr name,int type)
    {IntPtr r=FindResource(module,name,new IntPtr(type));Check(r!=IntPtr.Zero,"native icon resource exists");uint size=SizeofResource(module,r);Check(size>0&&size<=8*1024*1024,"native icon resource is bounded");IntPtr p=LockResource(LoadResource(module,r));Check(p!=IntPtr.Zero,"native icon resource is readable as data");var bytes=new byte[size];Marshal.Copy(p,bytes,0,bytes.Length);return bytes;}
    static void CheckWin32Icons(string path,byte[] ico)
    {
        IntPtr module=LoadLibraryEx(path,IntPtr.Zero,2);Check(module!=IntPtr.Zero,"compiled desktop is opened LOAD_LIBRARY_AS_DATAFILE only");
        try {
            int groups=0,matched=0;int count=BitConverter.ToUInt16(ico,4);var seen=new HashSet<int>();
            ResourceName visit=(m,t,n,state)=>{
                groups++;byte[] group=NativeResource(m,n,14);Check(group.Length>=6&&BitConverter.ToUInt16(group,4)==count,"Win32 group retains every supplied ICO size");
                for(int i=0;i<count;i++) {
                    int g=6+i*14;byte[] image=NativeResource(m,new IntPtr(BitConverter.ToUInt16(group,g+12)),3);bool found=false;
                    for(int k=0;k<count;k++) {
                        int s=6+k*16,length=(int)BitConverter.ToUInt32(ico,s+8),offset=(int)BitConverter.ToUInt32(ico,s+12);
                        if(!seen.Contains(k)&&length==image.Length&&ico.Skip(offset).Take(length).SequenceEqual(image)&&Enumerable.Range(0,12).All(j=>ico[s+j]==group[g+j])){seen.Add(k);found=true;matched++;break;}
                    }
                    Check(found,"compiled RT_ICON image and dimensions match exact original ICO bytes");
                }
                return true;
            };
            Check(EnumResourceNames(module,new IntPtr(14),visit,IntPtr.Zero),"enumerate compiled Win32 icon group");GC.KeepAlive(visit);
            Check(groups==1&&matched==count,"one neutral executable group contains all original icon images");
        }finally{FreeLibrary(module);}
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            string root=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);byte[] raw=File.ReadAllBytes(Path.Combine(root,"assets","shortcuts","256.ico"));
            using(var stream=new MemoryStream(raw))using(var expected=new Icon(stream)) {
                if(args.Length>4&&args[4]=="own") {
                    Check(Resource(Assembly.GetEntryAssembly(),OemUiBranding.NeutralIconResource)==null,"fallback fixture entry deliberately has no neutral resource");
                    var own=Assembly.LoadFrom(args[3]);Check(Resource(own,OemUiBranding.NeutralIconResource).SequenceEqual(raw),"independent UI assembly embeds exact original bytes");
                    var type=own.GetType("HVX.Presentation.OemUiBranding",true);using(var icon=(Icon)type.GetMethod("LoadIcon",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null))Check(Pixels(icon,expected),"no-resource embedding host falls back to the owned UI assembly pixels");
                } else {
                    Check(Resource(Assembly.GetEntryAssembly(),OemUiBranding.NeutralIconResource).SequenceEqual(raw),"entry resource retains complete original ICO bytes");
                    OemUiBranding.Publish(null,null);
                    using(var icon=AppTheme.LoadIcon())Check(Pixels(icon,expected),"default window icon matches original ICO pixels and dimensions");
                    byte[] warning=Bytes(SystemIcons.Warning),error=Bytes(SystemIcons.Error);
                    using(var form=new Form {Text="HV-DAC Xpert — offline icon fixture"}) {
                        AppTheme.ApplyDialogIcon(form);Check(Pixels(form.Icon,expected),"actual ApplyDialogIcon gives a plain fake window the neutral title icon");
                        using(var bitmap=form.Icon.ToBitmap())bitmap.Save(Path.Combine(qa,"neutral-window-icon.png"),ImageFormat.Png);
                        OemUiBranding.Publish(null,null);Check(Pixels(form.Icon,expected),"asynchronous empty OEM result cannot reset title icon to generic application");
                        using(var customer=(Icon)SystemIcons.Information.Clone()) {
                            OemUiBranding.Publish(customer,null);Check(Pixels(form.Icon,customer),"explicit customer OEM icon retains priority");
                            OemUiBranding.Publish(null,null);Check(Pixels(form.Icon,expected),"clearing a customer snapshot restores neutral artwork");
                        }
                        using(var icon=AppTheme.LoadIcon()){icon.Dispose();using(var next=AppTheme.LoadIcon())Check(Pixels(next,expected),"callers own independent clones without disposing the shared template");}
                    }
                    Check(warning.SequenceEqual(Bytes(SystemIcons.Warning))&&error.SequenceEqual(Bytes(SystemIcons.Error)),"system warning/error semantic artwork is untouched");
                    CheckWin32Icons(Path.GetFullPath(args[2]),raw);
                    var desktop=Assembly.ReflectionOnlyLoadFrom(Path.GetFullPath(args[2]));Check(Resource(desktop,OemUiBranding.NeutralIconResource).SequenceEqual(raw),"actual newly compiled desktop embeds byte-identical runtime neutral resource without execution");
                    string build=File.ReadAllText(Path.Combine(root,"Build.ps1")),modules=File.ReadAllText(Path.Combine(root,"tools","Build-UiModules.ps1"));
                    Check(build.Contains("assets\\shortcuts\\256.ico') + ',HVX.NeutralWindow.ico'")&&modules.Contains("assets\\shortcuts\\256.ico')+',HVX.NeutralWindow.ico'"),"host and common owned-UI build arguments bind exact resource name and original path");
                    Check(File.ReadAllText(Path.Combine(root,"src","MainForm.cs")).Contains("AppTheme.ApplyDialogIcon(this)"),"main-window constructor already routes through the corrected shared icon accessor");
                    string loader=File.ReadAllText(Path.Combine(root,"src","OemUiBranding.cs"));Check(!loader.Contains("File.")&&!loader.Contains("Directory.")&&!loader.Contains("ExtractAssociatedIcon"),"dialog icon path performs no disk image loading or executable icon extraction");
                    string installer=File.ReadAllText(Path.Combine(root,"installer","XIONGXIN-PublicBeta-NoDriver.iss"));
                    string[] links=installer.Split('\n').Where(line=>line.Contains("Filename: \"{app}\\Xiongxin.OWTS.Desktop.exe\"")).ToArray();
                    Check(links.Length==2&&links.All(line=>line.Contains("IconFilename: \"{app}\\DesktopShortcut.ico\"")),"desktop and Start Menu shortcuts retain the explicit installed neutral icon target");
                    Check(File.ReadAllText(Path.Combine(root,"tools","Build-PublicBetaNoDriverSetup.ps1")).Contains("AddCopy (File $root 'assets/shortcuts/256.ico') 'DesktopShortcut.ico'"),"installer stages the same original shortcut artwork");
                }
            }
            Console.WriteLine("OEM_WINDOW_ICON_PASS="+checks+"; PRODUCT_STARTED=0; MAINFORM_CREATED=0; SDK_CALLS=0; USER_SETTINGS_WRITES=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
