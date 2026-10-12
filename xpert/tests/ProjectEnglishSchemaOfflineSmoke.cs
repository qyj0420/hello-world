using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HVX.Controls;
using HVX.ProjectCables;
using HVX.ProjectModel;
using LabVIEWItemInformation;

// Fixture files only. No UI, product entry, profiles, algorithms, workers or SDK.
internal static class ProjectEnglishSchemaOfflineSmoke
{
    static int checks;
    static void Check(bool ok,string why) { checks++;if(!ok)throw new Exception(why); }
    static void Equal(double a,double b,string why) { Check(Math.Abs(a-b)<.001,why); }
    static string Item(string root) { return File.ReadAllText(Path.Combine(root,"ITEM.in"),new UTF8Encoding(false,true)).TrimStart('\uFEFF'); }
    static void English(string text)
    {
        Check(text.StartsWith("[ITEM]"),"fixed English section");
        foreach(string line in text.Replace("\r","").Split('\n')) {
            int eq=line.IndexOf('=');if(eq<0)continue;
            string key=line.Substring(0,eq).Trim().TrimEnd(':','：').Trim();
            Check(key.All(c=>c<128),"English key: "+key);
        }
        Check(!text.Contains("电缆长度: =")&&!text.Contains("所属单位: ="),"old known aliases are not re-emitted");
    }
    static ProjectDraft Draft()
    {
        var d=new ProjectDraft { Organization="雄新 Zürich Łódź", OperatorName="张三 Müller",TestLocation="上海 København",
            TestDateTime=new DateTime(2026,10,11,15,29,33),StartName="始端",EndName="终端",LengthA=166.2,LengthB=166.2,LengthC=166.2,
            Notes="备注 español Ελληνικά",Latitude=31.123456, Longitude=121.654321, Temperature=25.5, Humidity=62.5,
            HardwareSampleTime=new DateTime(2026,10,11,7,29,33,DateTimeKind.Utc),EnvironmentSampleValid=true, PhaseVoltage="8.7kV" };
        d.PressureSteps.Add(new PressureStep {Multiplier=0,Repetitions=1});
        d.PressureSteps.Add(new PressureStep {Multiplier=1.5,Repetitions=3});
        d.UnknownItemValues["AdoptedVelocityA"]="159.8";
        d.Joints.Add(new JointData {Phase="A相",Name="接头 Łódź",Position=78.4,Material="塑料绝缘"});
        return d;
    }
    static void NewProject(string qa,string culture)
    {
        Thread.CurrentThread.CurrentCulture=CultureInfo.GetCultureInfo(culture);
        Thread.CurrentThread.CurrentUICulture=CultureInfo.GetCultureInfo(culture);
        var d=Draft(); string root=ProjectCreationStorage.SaveNew(d,Path.Combine(qa,culture));
        byte[] bytes=File.ReadAllBytes(Path.Combine(root,"ITEM.in"));
        Check(bytes.Take(3).SequenceEqual(new byte[]{239,187,191}),"UTF8 BOM");
        string item=Item(root);English(item);
        Check(item.Contains("Temperature: = \"25.5\"")&&item.Contains("Latitude: = \"31.123456\""),"invariant numeric format");
        Check(item.Contains("Date: = \"2026-10-11\"")&&item.Contains("Time: = \"15:29:33\""),"invariant date/time");
        var loaded=ProjectCreationStorage.Load(root);
        Check(loaded.Organization==d.Organization&&loaded.OperatorName==d.OperatorName&&loaded.Notes==d.Notes,"Unicode values roundtrip");
        Equal(loaded.LengthA,d.LengthA,"cable length roundtrip");Equal(loaded.Latitude,d.Latitude,"latitude roundtrip");
        Check(loaded.PressureSteps.Count==2&&loaded.PressureSteps[1].Repetitions==3,"English pressure plan roundtrip");
        string cable=File.ReadAllText(Path.Combine(root,"CABLE.in"));
        Check(cable.Contains("0\tA\t")&&cable.Contains("1\tStart\t")&&cable.Contains("2\tBody\t")&&cable.Contains("1\tEnd\t"),"English structural labels with numeric role codes");
        Check(cable.Contains("接头 Łódź"),"custom cable name untouched");
        Check(CableJointStore.Read(root).Blocks.Count==3,"shared cable projection parses English structure");
        var plan=ProjectPlanReader.Read(Path.Combine(root,"ITEM.in"));
        Check(plan.IsValid&&plan.Rows.Count==2&&plan.Rows[1].RepetitionCount==3,"pressure consumer English schema");
        var defaults=Xiongxin.Calibration.ProjectDefaults.Load(root,"A");
        Equal(defaults.LengthM,166.2,"calibration defaults English length");Equal(defaults.VelocityMPerUs,159.8,"calibration defaults English velocity");
        var record=ProjectScanner.ReadProject(root);
        Check(record.StartTerminal=="始端"&&record.EndTerminal=="终端"&&record.HasTestTime,"project scanner English metadata");
        Equal(record.CableLengthMetres,166.2,"project scanner English total length");
        var view=ItemDocument.Load(root);Check(view.Values["TestingOrganization"]==d.Organization,"browser English information");
        string copy=Path.Combine(root,"EnglishCopy.in");view.Values["Remarks"]="保存副本 Français";view.SaveCopy(copy);
        English(File.ReadAllText(copy));Check(File.ReadAllBytes(Path.Combine(root,"ITEM.in")).SequenceEqual(bytes),"copy does not modify source");
    }
    static void LegacyProject(string qa,Encoding encoding,string name)
    {
        string root=Path.Combine(qa,name);Directory.CreateDirectory(root);
        string item="[ITEM]\r\n所属单位: = \"雄新电力\"\r\n测试人员: = \"张三\"\r\n始端地点: = \"上海\"\r\n终端地点: = \"北京\"\r\n日期: = \"2026-10-11\"\r\n时间: = \"15:29:33\"\r\n电缆长度: = \"166.2\"\r\nA相电缆长度: = \"166.2\"\r\nB相电缆长度: = \"166.2\"\r\nC相电缆长度: = \"166.2\"\r\n电缆芯数: = \"三芯\"\r\n相电压: = \"8.7kV\"\r\n加压模式: = \"新电缆\"\r\n加压倍数: = \"0.0,1.5\"\r\n加压次数: = \"1,3\"\r\nA相采用波速: = \"159.8\"\r\n备注: = \"历史备注\"\r\n";
        string cable=string.Join("",new[]{"A相","B相","C相"}.Select(p=>"0\t"+p+"\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\t塑料绝缘\t\r\n1\t终端\t户内冷缩\t166.2m\r\n"));
        File.WriteAllText(Path.Combine(root,"ITEM.in"),item,encoding);File.WriteAllText(Path.Combine(root,"CABLE.in"),cable,encoding);
        byte[] before=File.ReadAllBytes(Path.Combine(root,"CABLE.in"));
        var d=ProjectCreationStorage.Load(root);Check(d.Organization=="雄新电力"&&d.OperatorName=="张三","legacy Chinese values "+name);
        Equal(d.LengthA,166.2,"legacy length "+name);Check(d.PressureSteps.Count==2,"legacy plan "+name);
        var defaults=Xiongxin.Calibration.ProjectDefaults.Load(root,"A");Equal(defaults.VelocityMPerUs,159.8,"legacy calibration defaults "+name);
        var record=ProjectScanner.ReadProject(root);Check(record.StartTerminal=="上海"&&record.EndTerminal=="北京","legacy scanner "+name);
        var view=ItemDocument.Load(root);Check(view.Values["TestingOrganization"]=="雄新电力","legacy browser "+name);
        view.Values["Remarks"]="Łódź 已保存";string copy=Path.Combine(root,"EnglishCopy.in");view.SaveCopy(copy);
        English(File.ReadAllText(copy,new UTF8Encoding(false,true)));Check(ItemDocument.Load(copy).Values["Remarks"]=="Łódź 已保存","GBK copy converted UTF8 without Unicode loss "+name);
        d.Notes="更新 Zürich";ProjectCreationStorage.UpdateExisting(d,root);English(Item(root));
        Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"CABLE.in"))),"notes-only save preserves exact legacy cable bytes "+name);
        Check(ProjectCreationStorage.Load(root).Notes==d.Notes,"edited Unicode persists "+name);
        CalibrationProjectStore.SaveLength(root,"ALL",170,161);
        var saved=ProjectCreationStorage.Load(root);Equal(saved.LengthA,170,"calibration mutation English keys "+name);
        string changed=Item(root);Check(changed.Split(new[]{"CableLengthA:"},StringSplitOptions.None).Length==2,"known alias not duplicated "+name);
    }
    static int Main(string[] args)
    {
        try {
            string qa=Path.GetFullPath(args[0]);
            foreach(string culture in new[]{"en-US","de-DE","fr-FR","zh-CN","tr-TR"}) NewProject(qa,culture);
            LegacyProject(qa,new UTF8Encoding(true,true),"legacy-utf8");
            LegacyProject(qa,Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback),"legacy-gbk");
            LegacyProject(qa,new UnicodeEncoding(false,true,true),"legacy-utf16");
            Check(ProjectPlanReader.Parse("[ITEM]\nPhaseVoltage=8.7kV\nTestMode=Arbitrary\nPressureMultipliers=0\nPressureRepetitions=1\n").IsFreeValue,"English free-value mode remains nonautomatic");
            Check(ProjectMetadataKeys.Canonical("自定义厂家字段")=="自定义厂家字段","opaque extension key untouched");
            Console.WriteLine("PROJECT_ENGLISH_SCHEMA_PASS="+checks+";Cultures=5;LegacyEncodings=UTF8/GBK/UTF16;Hardware=false;Profiles=false;ProductEntry=false;UserProjectWritten=false");return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
