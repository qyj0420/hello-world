using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
namespace Xiongxin.OWTS.Desktop
{
    internal sealed class OemBrandingResult : IDisposable
    {
        internal Icon Icon; internal Image Logo; internal string Warning="";
        internal OemLogoGeometry LogoGeometry;
        public void Dispose(){if(Icon!=null)Icon.Dispose();if(Logo!=null)Logo.Dispose();}
    }
    internal static class OemBranding
    {
        internal const int MaxFileBytes=8*1024*1024,MaxPixels=4*1024*1024,MaxSide=4096;
        internal static string AssetDirectory { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"OEMpic"); } }
        internal static bool ValidateAssetPath(string path,out string message)
        {
            message="";if(String.IsNullOrWhiteSpace(path))return true;string p=path.Trim();
            if(p.Length>32760||p.Length<3||!Char.IsLetter(p[0])||p[1]!=':'||(p[2]!='\\'&&p[2]!='/')||p.Substring(2).Contains(':')){message="品牌图片必须选择本机磁盘中的文件，不能使用网络路径。";return false;}
            try{Path.GetFullPath(p);}catch(Exception e)when(e is ArgumentException||e is NotSupportedException||e is PathTooLongException){message="品牌图片路径无效。";return false;}return true;
        }
        static string LocalPath(string path,bool allowMissing=false)
        {
            string message;if(!ValidateAssetPath(path,out message)||String.IsNullOrWhiteSpace(path))throw new InvalidDataException(message.Length>0?message:"请选择本机图片文件。");
            string full=Path.GetFullPath(path.Trim());if(new DriveInfo(Path.GetPathRoot(full)).DriveType!=DriveType.Fixed)throw new InvalidDataException("品牌图片必须位于本机固定磁盘，请先复制文件。");
            // Walk from the drive down: never stat a child through an unchecked
            // junction (which could redirect to a network share).
            string root=Path.GetPathRoot(full),item=root;
            foreach(string part in full.Substring(root.Length).Split(Path.DirectorySeparatorChar)) {
                item=Path.Combine(item,part);
                try{if((File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("品牌图片不能使用链接或重定向路径。");}
                catch(FileNotFoundException){if(!allowMissing)throw new FileNotFoundException("找不到品牌图片，请重新选择。",full);}
                catch(DirectoryNotFoundException){if(!allowMissing)throw new FileNotFoundException("找不到品牌图片，请重新选择。",full);}
            }return full;
        }
        static byte[] Read(string path)
        {
            using(var stream=new FileStream(LocalPath(path),FileMode.Open,FileAccess.Read,FileShare.Read)){if(stream.Length<8||stream.Length>MaxFileBytes)throw new InvalidDataException("品牌图片文件过大或不完整（上限 8 MB）。");byte[] bytes=new byte[(int)stream.Length];int offset=0,n;while(offset<bytes.Length&&(n=stream.Read(bytes,offset,bytes.Length-offset))>0)offset+=n;if(offset!=bytes.Length)throw new InvalidDataException("品牌图片读取不完整。");return bytes;}
        }
        static uint U32(byte[] b,int i){if(i<0||i>b.Length-4)throw new InvalidDataException("图片文件不完整。");return BitConverter.ToUInt32(b,i);}
        static int Be16(byte[] b,int i){if(i>b.Length-2)throw new InvalidDataException("图片文件不完整。");return b[i]*256+b[i+1];}
        static uint Be32(byte[] b,int i){return ((uint)Be16(b,i)<<16)|(uint)Be16(b,i+2);}
        static void SizeBudget(long w,long h){if(w<=0||h<=0||w>MaxSide||h>MaxSide||w*h>MaxPixels)throw new InvalidDataException("品牌图片尺寸过大，请使用边长不超过 4096 像素、总计不超过 400 万像素的图片。");}
        static void ValidateRaster(byte[] b)
        {
            if(b.Length>=24&&b[0]==137&&b[1]==80&&b[2]==78&&b[3]==71){SizeBudget(Be32(b,16),Be32(b,20));return;}
            if(b.Length>=26&&b[0]==66&&b[1]==77&&U32(b,14)>=40){SizeBudget((int)U32(b,18),Math.Abs((long)(int)U32(b,22)));return;}
            if(b[0]==255&&b[1]==216){int p=2;while(p+4<=b.Length){if(b[p++]!=255)throw new InvalidDataException("图片格式无效。");while(p<b.Length&&b[p]==255)p++;if(p>=b.Length)break;int tag=b[p++];if(tag==217||tag==218)break;if(tag==1||tag>=208&&tag<=215)continue;int len=Be16(b,p);if(len<2||p+len>b.Length)break;if(tag>=192&&tag<=195||tag>=197&&tag<=199||tag>=201&&tag<=203||tag>=205&&tag<=207){if(len<8)break;SizeBudget(Be16(b,p+5),Be16(b,p+3));return;}p+=len;}}
            throw new InvalidDataException("请选择 PNG、JPEG 或 BMP 图片。");
        }
        static Bitmap DecodeLogo(byte[] bytes){OemLogoGeometry ignored;return DecodeLogo(bytes,out ignored);}
        static Bitmap DecodeLogo(byte[] bytes,out OemLogoGeometry geometry)
        {ValidateRaster(bytes);using(var stream=new MemoryStream(bytes,false))using(var image=Image.FromStream(stream,false,true))
            {SizeBudget(image.Width,image.Height);using(var bitmap=new Bitmap(image))return OemLogoPresentation.Prepare(bitmap,out geometry);}}
        static Icon DecodeIcon(byte[] bytes)
        {
            if(bytes.Length>1024*1024||bytes.Length<22||bytes[0]!=0||bytes[1]!=0||bytes[2]!=1||bytes[3]!=0)throw new InvalidDataException("请选择有效的 ICO 图标（上限 1 MB）。");int count=BitConverter.ToUInt16(bytes,4);if(count<1||count>32||6+count*16>bytes.Length)throw new InvalidDataException("图标目录无效。");
            for(int i=0;i<count;i++){int row=6+i*16;uint size=U32(bytes,row+8),offset=U32(bytes,row+12);if(size<16||offset<6+count*16||offset>bytes.Length||size>bytes.Length-offset)throw new InvalidDataException("图标文件不完整。");byte[] entry=new byte[size];Buffer.BlockCopy(bytes,(int)offset,entry,0,(int)size);if(entry[0]==137)ValidateRaster(entry);else{if(U32(entry,0)<40)throw new InvalidDataException("图标格式无效。");SizeBudget((int)U32(entry,4),Math.Abs((long)(int)U32(entry,8))/2);}}
            using(var stream=new MemoryStream(bytes,false))using(var icon=new Icon(stream,48,48))return (Icon)icon.Clone();
        }
        static bool IsAssetFailure(Exception e){return e is IOException||e is InvalidDataException||e is UnauthorizedAccessException||e is ArgumentException||e is System.Runtime.InteropServices.ExternalException||e is NotSupportedException||e is OutOfMemoryException;}
        internal static Task<OemBrandingResult> LoadAsync(string logoPath,string iconPath)
        {
            return Task.Run(()=>{var r=new OemBrandingResult();try{if(!String.IsNullOrWhiteSpace(logoPath))r.Logo=DecodeLogo(Read(logoPath),out r.LogoGeometry);}catch(Exception e)when(IsAssetFailure(e)){r.Warning="Logo 无法加载，已使用中性显示。";new Diagnostics().Error("Oem","Logo unavailable; neutral presentation used.",e);}try{if(!String.IsNullOrWhiteSpace(iconPath))r.Icon=DecodeIcon(Read(iconPath));}catch(Exception e)when(IsAssetFailure(e)){r.Warning+=(r.Warning.Length>0?" ":"")+"图标无法加载，已使用中性图标。";new Diagnostics().Error("Oem","Icon unavailable; neutral icon used.",e);}return r;});
        }
        internal static Task ImportAsync(AppSettings settings)
        {
            return Task.Run(()=>{byte[] logo=String.IsNullOrWhiteSpace(settings.LogoPath)?null:Read(settings.LogoPath),icon=String.IsNullOrWhiteSpace(settings.IconPath)?null:Read(settings.IconPath);if(logo!=null)using(var image=DecodeLogo(logo)){}if(icon!=null)using(var image=DecodeIcon(icon)){}string l=Store(logo,false),i=Store(icon,true);settings.LogoPath=l;settings.IconPath=i;});
        }
        internal static Task<string> ImportAssetAsync(string path,bool icon)
        {
            return Task.Run(()=>{byte[] bytes=Read(path);if(icon){using(var image=DecodeIcon(bytes)){} }
                else {using(var image=DecodeLogo(bytes)){} }return Store(bytes,icon);});
        }
        static string Store(byte[] bytes,bool icon)
        {
            if(bytes==null)return "";string directory=AssetDirectory;LocalPath(Path.Combine(directory,"asset"),true);Directory.CreateDirectory(directory);string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");string target=Path.Combine(directory,hash+(icon?".ico":bytes[0]==137?".png":bytes[0]==66?".bmp":".jpg"));LocalPath(target,true);if(File.Exists(target)){if(!Read(target).SequenceEqual(bytes))throw new IOException("已保存的品牌资源校验失败，请重新选择文件。");return target;}using(var file=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes,0,bytes.Length);file.Flush(true);}return target;
        }
    }
}
