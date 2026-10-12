using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Xiongxin.OWTS.Desktop
{
    // Shared by shell and analysis runtime. Model bytes remain encrypted PDM2.
    internal static class DefectModelCatalog
    {
        internal const string Standard="standard", Competition="competition";
        internal const string StandardHash="5581BF7A60798CB8F349A3EE3A59C2F8C121D44AB98D23172453EC856D360D16";
        internal const string CompetitionHash="A32A19B489CD7FB0CF51F9D027C214E7636A5621D7663384D88925F2A4CEB32B";
        internal const string FileFilter="PD model (*.pdm;*.hxm)|*.pdm;*.hxm|All files (*.*)|*.*";
        internal static string Normalize(string kind)
        {
            if(String.IsNullOrWhiteSpace(kind))return Standard;
            if(kind==Standard||kind==Competition)return kind;
            throw new ArgumentException("未知缺陷模型选择 / Unknown defect model selection.");
        }
        internal static string FileName(string kind)
        { return (Normalize(kind)==Competition?CompetitionHash:StandardHash).ToLowerInvariant()+".hxm"; }
        internal static string InstalledDirectory
        { get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"XIONGXIN","HVX","Models","v1");} }
        internal static bool ModelExtension(string path)
        { string ext=Path.GetExtension(path);return String.Equals(ext,".pdm",StringComparison.OrdinalIgnoreCase)||String.Equals(ext,".hxm",StringComparison.OrdinalIgnoreCase); }
        internal static string Resolve(string kind,string standardPath,string competitionPath,string buildRoot=null)
        {
            kind=Normalize(kind);
            string custom=(kind==Competition?competitionPath:standardPath)??String.Empty;
            if(!String.IsNullOrWhiteSpace(custom))return Path.GetFullPath(custom.Trim());
            // Isolated development payload only. Installer excludes this directory
            // and deploys the exact bytes to ProgramData instead. Never fall back
            // from a missing competition model to a standard model.
            string staged=Path.Combine(buildRoot??AppDomain.CurrentDomain.BaseDirectory,"model-payload",FileName(kind));
            return File.Exists(staged)?staged:Path.Combine(InstalledDirectory,FileName(kind));
        }
        internal static void ValidateHeader(string path)
        {
            if(!ModelExtension(path))throw new InvalidDataException("请选择 PDM 或 HXM 加密模型 / Select an encrypted PDM or HXM model.");
            using(var stream=File.OpenRead(path)){
                if(stream.Length<32||stream.ReadByte()!='P'||stream.ReadByte()!='D'||stream.ReadByte()!='M'||stream.ReadByte()!='2')
                    throw new InvalidDataException("模型格式无效 / Invalid encrypted model format.");
            }
        }
        internal static void ValidatePinnedHash(string path,string hash)
        {
            string name=Path.GetFileName(path);
            foreach(string kind in new[]{Standard,Competition})
                if(String.Equals(name,FileName(kind),StringComparison.OrdinalIgnoreCase)&&
                    !String.Equals(hash,kind==Competition?CompetitionHash:StandardHash,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("模型校验失败，请修复安装 / Model integrity check failed. Repair the installation.");
            // Trust follows reviewed bytes, never a user-controlled filename or
            // an unauthenticated PDM2 magic value. Renamed approved models work.
            if(!String.Equals(hash,StandardHash,StringComparison.OrdinalIgnoreCase)&&
                !String.Equals(hash,CompetitionHash,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("此模型尚未列入本版本支持的模型。请选择随软件提供的标准模型或竞赛模型；更新训练模型请使用经确认的模型更新包。 / This model is not approved for this version. Select the supplied standard or competition model, or obtain a verified model update.");
        }

        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint mode,uint flags,IntPtr template);
        [StructLayout(LayoutKind.Sequential)] struct FileIdentity
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;
            internal uint VolumeSerial,SizeHigh,SizeLow,LinkCount,IndexHigh,IndexLow;
        }
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle file,out FileIdentity info);

        // Keep the verified leaf and directory chain stable until the model
        // engine has finished opening it. This reads ciphertext only.
        internal sealed class VerifiedModel : IDisposable
        {
            readonly List<SafeFileHandle> parents=new List<SafeFileHandle>();
            FileStream stream;
            internal string Path { get; private set; }
            internal string Hash { get; private set; }
            internal VerifiedModel(string path)
            {
                Path=System.IO.Path.GetFullPath(path);
                if(!ModelExtension(Path))throw new InvalidDataException("请选择 PDM 或 HXM 模型 / Select a PDM or HXM model.");
                try
                {
                    var directories=new List<string>();
                    for(string parent=System.IO.Path.GetDirectoryName(Path);!String.IsNullOrEmpty(parent);parent=System.IO.Path.GetDirectoryName(parent))directories.Add(parent);
                    directories.Reverse();
                    foreach(string directory in directories)
                    {
                        var held=CreateFile(directory,0x80u,3u,IntPtr.Zero,3u,0x02200000u,IntPtr.Zero);
                        parents.Add(held);FileIdentity info;
                        if(held.IsInvalid||!GetFileInformationByHandle(held,out info)||(info.Attributes&0x400u)!=0)
                            throw new IOException("无法稳定读取模型所在目录，请将模型复制到本机普通文件夹后重试。 / Copy the model to a regular local folder and retry.");
                    }
                    SafeFileHandle leaf=CreateFile(Path,0x80000000u,1u,IntPtr.Zero,3u,0x00200000u,IntPtr.Zero);
                    try
                    {
                        FileIdentity info;
                        if(leaf.IsInvalid||!GetFileInformationByHandle(leaf,out info)||(info.Attributes&(0x400u|0x10u))!=0||info.LinkCount!=1)
                            throw new IOException("无法稳定读取模型文件，请复制原始模型后重试。 / Copy the original model file and retry.");
                        stream=new FileStream(leaf,FileAccess.Read,4096,false);leaf=null;
                    }
                    finally {if(leaf!=null)leaf.Dispose();}
                    if(stream.Length<32||stream.Length>16*1024*1024||stream.ReadByte()!='P'||stream.ReadByte()!='D'||stream.ReadByte()!='M'||stream.ReadByte()!='2')
                        throw new InvalidDataException("模型格式无效 / Invalid encrypted model format.");
                    stream.Position=0;
                    using(var sha=SHA256.Create())Hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
                    ValidatePinnedHash(Path,Hash);
                }
                catch {Dispose();throw;}
            }
            public void Dispose()
            {
                if(stream!=null){stream.Dispose();stream=null;}
                foreach(var parent in parents)parent.Dispose();parents.Clear();
            }
        }
        internal static VerifiedModel OpenVerified(string path) {return new VerifiedModel(path);}
    }
}
