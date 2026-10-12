using System;
using System.IO;
using System.Linq;
using System.Text;
using HVX.Controls;
using HVX.CalibrationStorage;

// Synthetic owned files only. No controls, profiles, SDK, capture or product entry.
internal static class ProjectCalibrationScopeSaveSmoke
{
    static int checks;
    static void Check(bool value,string message) { checks++;if(!value)throw new Exception(message); }
    static bool Rejects(Action action) { try { action();return false; } catch(InvalidDataException) { return true; } }
    static bool Conflicts(Action action) { try { action();return false; } catch(IOException) { return true; } }
    static string Project(string qa,string name,Encoding encoding)
    {
        string root=Path.Combine(qa,name);Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\n; vendor retained comment\r\n所属单位 : = \"雄新电力\"\r\n测试人员: = \"张三\"\r\n始端地点: = \"上海\"\r\n终端地点: = \"北京\"\r\n日期: = \"2026-10-11\"\r\n时间: = \"15:29:33\"\r\n电缆长度: = \"170\"\r\nA相电缆长度: = \"170\"\r\nB相电缆长度: = \"170\"\r\nC相电缆长度: = \"170\"\r\n电缆芯数: = \"三芯\"\r\n相电压: = \"8.7kV\"\r\n加压模式: = \"新电缆\"\r\n加压倍数: = \"0.0,1.5\"\r\n加压次数: = \"1,3\"\r\nA相采用波速: = \"170\"\r\n备注: = \"原备注\"\r\n厂商扩展: = \"opaque\tdata\"\r\n",encoding);
        File.WriteAllText(Path.Combine(root,"CABLE.in"),String.Concat(new[]{"A相","B相","C相"}.Select(p=>"0\t"+p+"\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\t塑料绝缘\t\r\n1\t终端\t户内冷缩\t170m\r\n")),encoding);
        return root;
    }
    static string Archive(string root)
    {
        string raw=Path.Combine(root,"synthetic.raw");File.WriteAllBytes(raw,new byte[]{1,2,3});
        var wave=new double[512];wave[20]=1;wave[220]=.5;
        return new LiveCalibrationRecord {Version=3,Phase="ALL",Level="5nC",DeviceSerial="SYNTHETIC",DeviceModel="Offline",
            ProjectHash=LiveCalibrationRecord.Identity(root),FilterId=HVX.SignalProcessing.PdLinearHighPass.FilterId,
            CreatedUtc=DateTime.UtcNow.ToString("o"),LengthM=170,VelocityMPerUs=170,SampleRateHz=100e6,
            IncidentSeconds=20/100e6,ReflectionSeconds=220/100e6,IncidentPeakIndex=20,ReflectionPeakIndex=220,
            CoefficientPcPerV=5000,ChargePc=5000,SnrDb=40,Correlation=.99,RangeIndex=5,PulseCount=3,
            FirstArrival=true,Average=wave,CoefficientProcessing="HP10K_NO_LP",
            RawFiles=new[]{new CalibrationRawReference{RelativePath="synthetic.raw",Sha256=LiveCalibrationRecord.HashFile(raw)}}}.Save(root);
    }
    static void Case(string qa,string name,Encoding encoding,bool audit)
    {
        string root=Project(qa,name,encoding),file=Archive(root),calHash=LiveCalibrationRecord.HashFile(file);
        string exact=LiveCalibrationRecord.Identity(root),scope=LiveCalibrationRecord.ScopeIdentity(root);
        Check(LiveCalibrationRecord.Load(root,file).ProjectScopeHash==scope,"fixture initially legal "+name);
        var draft=ProjectCreationStorage.Load(root);ProjectCreationStorage.UpdateExisting(draft,root);
        if(audit) {
            Check(LiveCalibrationRecord.ScopeIdentity(root)!=scope,"pre-fix no-op save rewrites scope "+name);
            Check(Rejects(()=>LiveCalibrationRecord.Load(root,file)),"pre-fix existing calibration becomes unusable "+name);
        } else {
            Check(LiveCalibrationRecord.ScopeIdentity(root)==scope,"no-op save retains exact scope "+name);
            Check(LiveCalibrationRecord.Identity(root)==exact,"no-op save retains full original bytes "+name);
            Check(LiveCalibrationRecord.Load(root,file).CoefficientPcPerV==5000,"existing calibration remains legal "+name);
        }
        Check(LiveCalibrationRecord.HashFile(file)==calHash,"archive never rewritten "+name);
        if (!audit) {
            // The production measured-project route is a different transaction.
            var loaded=ProjectCreationStorage.Load(root);
            string committed, cableHash=LiveCalibrationRecord.HashFile(Path.Combine(root,"CABLE.in"));
            ProjectCreationStorage.UpdateMeasuredMetadata(loaded,root,cableHash,LiveCalibrationRecord.HashFile(Path.Combine(root,"ITEM.in")),out committed);
            Check(LiveCalibrationRecord.Identity(root)==exact,"measured no-op preserves original bytes "+name);
            Check(LiveCalibrationRecord.Load(root,file)!=null,"measured no-op keeps calibration legal "+name);
            loaded=ProjectCreationStorage.Load(root);loaded.UnknownItemValues["AdoptedVelocityA"]="171.5";
            ProjectCreationStorage.UpdateMeasuredMetadata(loaded,root,cableHash,LiveCalibrationRecord.HashFile(Path.Combine(root,"ITEM.in")),out committed);
            Check(LiveCalibrationRecord.ScopeIdentity(root)==scope,"true mutable edit keeps bound scope "+name);
            Check(ProjectCreationStorage.Load(root).UnknownItemValues["AdoptedVelocityA"]=="171.5","mutable editor delta is actually saved "+name);
            Check(LiveCalibrationRecord.HashFile(Path.Combine(root,"CABLE.in"))==cableHash,"mutable metadata never rewrites cable "+name);
            Check(LiveCalibrationRecord.Load(root,file).VelocityMPerUs==170,"recorded velocity is not relabelled as edited reference "+name);
            loaded=ProjectCreationStorage.Load(root);loaded.Notes="编辑 Zürich";ProjectCreationStorage.UpdateExisting(loaded,root);
            Check(ProjectCreationStorage.Load(root).Notes=="编辑 Zürich","actual nonmutable edit saved "+name);
            Check(LiveCalibrationRecord.ScopeIdentity(root)!=scope&&Rejects(()=>LiveCalibrationRecord.Load(root,file)),"actual nonmutable edit still rejects old scope "+name);
            Check(LiveCalibrationRecord.HashFile(file)==calHash,"all metadata operations preserve original archive "+name);
        }
    }
    static void DeltaEdges(string qa)
    {
        string root=Project(qa,"duplicates",new UTF8Encoding(true,true)),item=Path.Combine(root,"ITEM.in");
        File.AppendAllText(item,"Remarks: = \"原备注\"\r\n",new UTF8Encoding(false,true));
        string file=Archive(root),scope=LiveCalibrationRecord.ScopeIdentity(root),archive=LiveCalibrationRecord.HashFile(file);
        byte[] before=File.ReadAllBytes(item);var draft=ProjectCreationStorage.Load(root);
        ProjectCreationStorage.UpdateExisting(draft,root);
        Check(File.ReadAllBytes(item).SequenceEqual(before),"no-op preserves duplicate aliases, comments and opaque tab values exactly");
        draft=ProjectCreationStorage.Load(root);draft.Notes="最新备注";ProjectCreationStorage.UpdateExisting(draft,root);
        string text=File.ReadAllText(item);
        Check(text.Contains("备注: = \"最新备注\"")&&text.Contains("Remarks: = \"最新备注\""),"edited duplicate aliases all receive the same new value");
        Check(ProjectCreationStorage.Load(root).Notes=="最新备注","no trailing alias can shadow editor delta");
        Check(Rejects(()=>LiveCalibrationRecord.Load(root,file)),"duplicate edit does not grant a calibration alias");
        Check(text.Contains("厂商扩展: = \"opaque\tdata\""),"unmodified vendor extension retained verbatim");
        Check(LiveCalibrationRecord.HashFile(file)==archive,"duplicate update never rewrites archive");

        root=Project(qa,"new-extension",new UTF8Encoding(true,true));item=Path.Combine(root,"ITEM.in");file=Archive(root);
        scope=LiveCalibrationRecord.ScopeIdentity(root);draft=ProjectCreationStorage.Load(root);
        draft.UnknownItemValues["VendorNew"]="opaque \"quoted\"\tvalue";ProjectCreationStorage.UpdateExisting(draft,root);
        Check(ProjectCreationStorage.Load(root).UnknownItemValues["VendorNew"]=="opaque \"quoted\"\tvalue","new opaque extension roundtrips without quote/tab loss");
        Check(LiveCalibrationRecord.ScopeIdentity(root)!=scope&&Rejects(()=>LiveCalibrationRecord.Load(root,file)),"new extension is real scope input, not silently excluded");

        root=Project(qa,"baseline-conflict",new UTF8Encoding(true,true));item=Path.Combine(root,"ITEM.in");file=Archive(root);
        draft=ProjectCreationStorage.Load(root);draft.Notes="must not commit";
        File.AppendAllText(item,"OtherEditor: = \"external\"\r\n");before=File.ReadAllBytes(item);
        Check(Conflicts(()=>ProjectCreationStorage.UpdateExisting(draft,root)),"external baseline conflict still fails closed");
        Check(File.ReadAllBytes(item).SequenceEqual(before),"conflict never overwrites another editor");

        root=Project(qa,"damaged-container",new UTF8Encoding(true,true));item=Path.Combine(root,"ITEM.in");file=Archive(root);
        byte[] corrupt=File.ReadAllBytes(file);corrupt[corrupt.Length-1]^=1;File.SetAttributes(file,FileAttributes.Normal);File.WriteAllBytes(file,corrupt);
        draft=ProjectCreationStorage.Load(root);before=File.ReadAllBytes(item);ProjectCreationStorage.UpdateExisting(draft,root);
        Check(File.ReadAllBytes(item).SequenceEqual(before),"container detection is only a format-preservation hint");
        Check(Rejects(()=>LiveCalibrationRecord.Load(root,file)),"damaged calibration is never trusted by format preservation");

        root=Project(qa,"no-formal-archive",new UTF8Encoding(true,true));item=Path.Combine(root,"ITEM.in");
        File.WriteAllText(Path.Combine(root,"ALL_5nC.cal"),"legacy text is not a formal container");
        draft=ProjectCreationStorage.Load(root);ProjectCreationStorage.UpdateExisting(draft,root);text=File.ReadAllText(item);
        Check(text.Contains("TestingOrganization:")&&!text.Contains("所属单位"),"unbound legacy project still migrates to English protocol keys");

        var fresh=new ProjectDraft {StartName="Start",EndName="End",LengthA=170,LengthB=170,LengthC=170};
        root=ProjectCreationStorage.SaveNew(fresh,Path.Combine(qa,"fresh-English"));file=Archive(root);
        scope=LiveCalibrationRecord.ScopeIdentity(root);before=File.ReadAllBytes(Path.Combine(root,"ITEM.in"));
        draft=ProjectCreationStorage.Load(root);ProjectCreationStorage.UpdateExisting(draft,root);
        Check(File.ReadAllBytes(Path.Combine(root,"ITEM.in")).SequenceEqual(before),"English calibrated project no-op also retains its own exact syntax");
        Check(LiveCalibrationRecord.ScopeIdentity(root)==scope&&LiveCalibrationRecord.Load(root,file)!=null,"English project retains the same strict binding");
    }
    static int Main(string[] args)
    {
        try {
            string qa=Path.GetFullPath(args[0]);bool audit=args.Length>1&&args[1]=="audit";
            Case(qa,"utf8",new UTF8Encoding(true,true),audit);
            Case(qa,"gbk",Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback),audit);
            Case(qa,"utf16",new UnicodeEncoding(false,true,true),audit);
            if(!audit)DeltaEdges(qa);
            Console.WriteLine((audit?"PROJECT_CALIBRATION_SCOPE_AUDIT_PASS=":"PROJECT_CALIBRATION_SCOPE_SAVE_PASS=")+checks+";Hardware=false;ProductEntry=false;UserProjectWritten=false");return 0;
        } catch(Exception error) {Console.Error.WriteLine(error);return 1;}
    }
}
