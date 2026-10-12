using System;
using System.Collections;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;

internal static class StateGridReportSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new InvalidOperationException(message);}
    static object F(object target,string name){return target.GetType().GetField(name,All).GetValue(target);}
    static void S(object target,string name,object value){target.GetType().GetField(name,All).SetValue(target,value);}
    static object P(object target,string name){return target.GetType().GetProperty(name,All).GetValue(target,null);}
    static void Put(object target,string name,object value){target.GetType().GetProperty(name,All).SetValue(target,value,null);}
    static object Call(object target,string name,params object[] args){Type type=target as Type??target.GetType();return type.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target is Type?null:target,args);}
    [STAThread]static int Main(string[] args)
    {
        try {
            string release=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            string root=Path.Combine(qa,"SyntheticStateGrid");Directory.CreateDirectory(root);Fixture(root);
            var source=Directory.GetFiles(root).ToDictionary(Path.GetFileName,File.ReadAllBytes);
            var assembly=Assembly.LoadFrom(Path.Combine(release,"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));
            Type dataType=assembly.GetType("HVX.ReportGenerator.StateGridReportData",true),recordType=assembly.GetType("HVX.Controls.BrowserRecord",true);
            var candidates=Array.CreateInstance(recordType,2);
            for(int i=0;i<2;i++){object record=Activator.CreateInstance(recordType);S(record,"IsValid",true);S(record,"Kind","cal");S(record,"Phase",i==0?"A":"B");S(record,"Path",i==0?"A_1nC.cal":"B_500pC.cal");candidates.SetValue(record,i);}
            object selection=Call(dataType,"SelectCalibration",candidates,"A",500d);
            Check((double)F(selection,"Actual")==1000&&(bool)F(selection,"Reference"),"nearest same-phase calibration selected; exact other phase is not borrowed");
            Check(((string)F(selection,"Note")).Contains("1000 pC")&&((string)F(selection,"Note")).Contains("A_1nC.cal"),"reference substitution retains actual charge and source");
            Check(F(Call(dataType,"SelectCalibration",candidates,"C",500d),"Record")==null,"missing phase remains missing");
            var projectType=assembly.GetType("HVX.ReportGenerator.ProjectReportDataReader",true);object project=Call(projectType,"Read",root);
            var matrix=new double[3,36];for(int r=0;r<3;r++){matrix[r,0]=1;matrix[r,2]=12.3;matrix[r,13]=r==0?80:r==1?140:170;matrix[r,15]=matrix[r,16]=100+r*10;matrix[r,35]=1;}
            object data=Call(dataType,"Read",root,project,matrix,"");var figures=((IEnumerable)F(data,"Figures")).Cast<object>().ToArray();
            Type reader=assembly.GetType("HVX.Controls.BrowserReader",true),chart=assembly.GetType("HVX.ReportGenerator.ProjectWaveformChartRenderer",true);
            object browserProject=F(data,"Browser");object dem=((IEnumerable)F(browserProject,"Tests")).Cast<object>().First(r=>(string)F(r,"Phase")=="A");
            object knownWave=Call(reader,"LoadWaveform",dem);
            Check(Math.Abs((double)F(knownWave,"SampleRateHz")-100000)<.001,"fixture explicit DEM interval is100kHz");
            Check(Math.Abs((double)Call(chart,"HorizontalExtent",knownWave,false)-39.99)<1e-8,"4000samples at100kHz end at39.99ms");
            Check(((string)Call(chart,"HorizontalLabel",knownWave,false,0)).Contains("ms"),"known timebase carries millisecond label");
            // Keep all original samples but remove a reliable timebase. The report
            // must preserve sample indices rather than mislabel them milliseconds.
            string unknownDir=Path.Combine(qa,"UnknownTimebase");Directory.CreateDirectory(unknownDir);
            string unknownPath=Path.Combine(unknownDir,Path.GetFileName((string)F(dem,"Path")));
            string knownText=File.ReadAllText((string)F(dem,"Path"));File.WriteAllText(unknownPath,knownText.Replace("Delta-x (s)\t0.00001","Unspecified-rate\t0"),new UTF8Encoding(true));
            object unknown=Activator.CreateInstance(recordType);S(unknown,"Path",unknownPath);S(unknown,"Kind","dem");S(unknown,"IsValid",true);S(unknown,"Phase","A");S(unknown,"VoltageKv",F(dem,"VoltageKv"));S(unknown,"Order",F(dem,"Order"));
            object unknownWave=Call(reader,"LoadWaveform",unknown);
            Check(double.IsNaN((double)F(unknownWave,"SampleRateHz")),"zero-voltage unknown DEM rate remains unknown");
            Check((double)Call(chart,"HorizontalExtent",unknownWave,false)==3999,"unknown timebase ends at sample3999, without seconds conversion");
            Check(!(bool)Call(chart,"HasTimebase",unknownWave),"unknown timebase prohibits second-based markers");
            Check(!((string)Call(chart,"HorizontalLabel",unknownWave,false,0)).Contains("ms"),"unknown timebase is not labelled milliseconds");
            Check(((double[])F(knownWave,"Y")).SequenceEqual((double[])F(unknownWave,"Y")),"unknown-timebase regression preserves every raw DEM sample");
            Call(chart,"Render",unknown,0,Path.Combine(qa,"unknown-timebase.png"));
            Check(figures.Length==11,"all required calibration noise and voltage slots are present before location charts");
            object cal=figures.First(f=>(string)F(f,"Title")=="500 pC 标准脉冲校准");
            Check((bool)F(cal,"Reference")&&(double)F(cal,"Actual")==5000,"500pC slot openly substitutes available 5nC");
            object exact=figures.First(f=>(string)F(f,"Title")=="100000 pC 标准脉冲校准");Check(!(bool)F(exact,"Reference"),"100nC is exactly100000pC");
            object a1=figures.First(f=>(string)F(f,"Title")=="1.0U0 时测试波形"&&(string)F(f,"Phase")=="A");
            Check(Math.Abs((double)F(a1,"Actual")-12.3)<.001,"table peak comes from sampled12.3kV not file RMS8.7 or scale99");
            Check((bool)F(a1,"Reference"),"rounded measured voltage is not silently certified as exact target");
            Check(figures.Where(f=>(string)F(f,"Phase")=="C").All(f=>F(f,"Record")==null),"C slots do not borrow A B or ALL measurement data");
            var values=(IDictionary)F(data,"Values");Check(((string)values["noise.A"]).Contains("0.2 pC"),"noise is actual waveform absolute peak, not PD scale999");
            Check(((string)values["noise.B"]).StartsWith("参考替代"),"nonzero B measurement is explicitly noise reference only");
            Check((string)F(data,"Conclusion")=="未形成结论，待专业人员复核。","missing conclusion does not become pass");
            Check(((string)Call(data,"Discharge","A",0)).Contains("80 m")&&((string)Call(data,"Discharge","A",1)).Contains("140 m")&&((string)Call(data,"Discharge","A",2)).Contains("170 m"),"accepted location records remain traceable by physical part");
            // Phase-specific matrix length wins over the common project maximum.
            matrix[2,5]=140;matrix[2,13]=140;Check(((string)Call(data,"Discharge","A",2)).Contains("140 m"),"terminal uses actual phase length rather than project maximum");matrix[2,5]=0;matrix[2,13]=170;
            object browser=F(data,"Browser");var joints=(IDictionary)F(browser,"JointPositionsByPhase");object common=joints["ALL"];joints.Remove("ALL");joints["A"]=new[]{80d};
            matrix[0,0]=2;matrix[0,5]=170;Check(((string)Call(data,"Discharge","B",0)).Contains("本相结构"),"missing B structure never borrows union of A joints");matrix[0,0]=1;matrix[0,5]=0;joints["ALL"]=common;joints.Remove("A");
            Type format=assembly.GetType("HVX.ReportGenerator.ReportOutputFormat",true),document=assembly.GetType("HVX.ReportGenerator.StateGridReportDocument",true);
            string docx=Path.Combine(qa,"state-grid-fixture.docx"),pdf=Path.Combine(qa,"state-grid-fixture.pdf");
            object result=Call(document,"Generate",root,project,matrix,"",docx,pdf,Enum.Parse(format,"WordOnly"));
            Check((bool)F(result,"Success"),"actual DOCX generated: "+F(result,"Error"));Check(!(bool)F(result,"PdfCreated"),"Word-only does not call PDF conversion");
            using(var zip=ZipFile.OpenRead(docx)) {
                XNamespace w="http://schemas.openxmlformats.org/wordprocessingml/2006/main",r="http://schemas.openxmlformats.org/package/2006/relationships";
                Check(zip.GetEntry("[Content_Types].xml")!=null&&zip.GetEntry("_rels/.rels")!=null,"valid Word package parts");
                XDocument xml;using(var input=zip.GetEntry("word/document.xml").Open())xml=XDocument.Load(input);
                string text=String.Join("\n",xml.Descendants(w+"t").Select(e=>e.Value));
                foreach(string term in new[]{"Q/GDW 11262—2024","表 M.1","相对湿度","试验性质","电缆厂家","气体检测仪器","1.0U0","1.4U0","主管","审核","500 pC","100000 pC","Q-L","N-L","参考替代","A_8.7kV_2026-09-29-10'01'00.dem","12.3","未采集 / 缺数据"})Check(text.Contains(term),"standard field/provenance present: "+term);
                Check(!text.Contains("测试合格")&&!text.Contains("符合标准全文"),"no invented pass conclusion");
                Check(zip.Entries.Count(e=>e.FullName.StartsWith("word/media/"))>=8,"readable charts embedded, not merely external links");
                foreach(var entry in zip.Entries.Where(e=>e.FullName.EndsWith(".rels")))using(var stream=entry.Open()) {
                    foreach(var rel in XDocument.Load(stream).Descendants(r+"Relationship"))Check(((string)rel.Attribute("Type")).StartsWith("http://schemas.openxmlformats.org/officeDocument/2006/relationships/"),"valid OOXML relationship URI");
                }
                Check(!xml.Descendants(w+"trHeight").Any(h=>(string)h.Attribute(w+"hRule")=="exact"),"table rows can expand, preventing truncation");
            }
            byte[] existing=File.ReadAllBytes(docx);result=Call(document,"Generate",root,project,matrix,"",docx,pdf,Enum.Parse(format,"WordOnly"));Check(!(bool)F(result,"Success")&&existing.SequenceEqual(File.ReadAllBytes(docx)),"existing supplementary output is never silently overwritten");
            Application.EnableVisualStyles();
            using(var control=(Control)Activator.CreateInstance(assembly.GetType("HVX.ReportGenerator.ReportGeneratorControl",true))) {
                Check(!(bool)P(control,"GenerateStateGridReport"),"supplementary report default disabled");Put(control,"PromptAnalysisOnLoad",false);Check((bool)Call(control,"LoadProject",root),"fixture loaded");
                Put(control,"OutputFormat",Enum.Parse(format,"WordOnly"));Call(control,"SetLocationMatrix",matrix);
                string normal=Path.Combine(qa,"ordinary.pdf");File.WriteAllText(normal,"normal report sentinel");
                Check(Call(control,"GenerateStateGridSupplement",normal)==null,"unchecked report option does not generate a supplement");
                Put(control,"TemplatePath",Path.Combine(qa,"missing-normal-template.docx"));Put(control,"GenerateStateGridReport",true);
                object supplement=Call(control,"GenerateStateGridSupplement",normal);Check((bool)F(supplement,"Success"),"supplement independent of missing normal report template");
                string first=(string)P(control,"LastStateGridDocxPath");supplement=Call(control,"GenerateStateGridSupplement",normal);Check((bool)F(supplement,"Success")&&first!=(string)P(control,"LastStateGridDocxPath"),"repeated output chooses a new name");Check(File.ReadAllText(normal)=="normal report sentinel","normal report preserved byte-for-byte");
            }
            string cap=Path.Combine(qa,"Capacitor");Directory.CreateDirectory(cap);File.WriteAllText(Path.Combine(cap,"ITEM.in"),"负载类型 = 电容局放",Encoding.UTF8);
            result=Call(document,"Generate",cap,null,matrix,"",Path.Combine(qa,"must-not-cap.docx"),Path.Combine(qa,"must-not-cap.pdf"),Enum.Parse(format,"WordOnly"));Check((bool)F(result,"Skipped")&&!File.Exists(Path.Combine(qa,"must-not-cap.docx")),"capacitor never masquerades as standard cable report");
            foreach(var pair in source)Check(pair.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(root,pair.Key))),"input untouched "+pair.Key);
            if(args.Length>2&&args[2]=="--pdf") {
                string provider="",error="";object[] conversion={docx,pdf,false,provider,error};
                bool converted=(bool)assembly.GetType("HVX.ReportGenerator.ReportDocumentService",true).GetMethod("TryConvertWithOfficeOrWps",All).Invoke(null,conversion);
                Check(converted&&File.Exists(pdf),"actual Word/WPS PDF conversion: "+conversion[4]);Console.WriteLine("PDF_PROVIDER="+conversion[3]);
            }
            Console.WriteLine("STATE_GRID_REPORT_PASS="+checks+";hardware=false");Console.WriteLine("DOCX="+docx);Console.WriteLine("PDF="+pdf);Console.WriteLine("EVIDENCE="+qa);return 0;
        }catch(Exception error){Console.Error.WriteLine(error is TargetInvocationException?((TargetInvocationException)error).InnerException:error);return 1;}
    }
    static void Fixture(string root)
    {
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\n电缆长度 = 170\r\n相电压 = 8.7\r\n始端地点 = QA起点\r\n终端地点 = QA终点\r\n负载类型 = 一体三芯\r\n天气 = 晴\r\n温度 = 23.5\r\n湿度 = 60\r\n日期 = 2026-09-29\r\n检测单位 = QA检测单位\r\n试验性质 = 交接试验\r\n线路名称 = QA线路\r\n投运日期 = 2020-01-01\r\n电缆厂家 = QA电缆厂\r\n电缆型号 = QA型号\r\n试验仪器 = QA仪器\r\n测试人员 = QA测试人\r\n",new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\r\n1\t始端\t冷缩\t0\r\n1\t接头1\t冷缩\t80\r\n2\t本体\tXLPE\r\n1\t终端\t冷缩\t170\r\n",new UTF8Encoding(true));
        foreach(string name in new[]{"ALL_5nC.cal","ALL_100nC.cal"}) {
            var text=new StringBuilder("SR\t100000000\r\nCable length (m)\t170\r\nV (m/usec)\t170\r\nStart_point\t0.000001\r\nFinal_point\t0.000003\r\n");for(int i=0;i<8;i++)text.Append("Field").Append(i).Append("\t0\r\n");
            for(int i=0;i<4001;i++){double t=i*.01,y=Math.Exp(-Math.Pow((t-1)/.07,2))+.4*Math.Exp(-Math.Pow((t-3)/.09,2));text.Append(y.ToString("R",CultureInfo.InvariantCulture)).Append("\r\n");}File.WriteAllText(Path.Combine(root,name),text.ToString(),new UTF8Encoding(true));
        }
        Dem(root,"A",0,0,0);Dem(root,"A",8.7,12.3,1);Dem(root,"B",17.2,17.2,2);
    }
    static void Dem(string root,string phase,double nameVoltage,double actual,int minute)
    {
        var text=new StringBuilder("Delta-x (s)\t0.00001\r\nHz\t100\r\nC[uF]\t0.1\r\nD-losses\t0.1\r\nV-scale max\t99\r\nPD-scale max\t999\r\nField\t0\r\nDATA-TOTAL\t4000\r\n");
        for(int i=0;i<7;i++)text.Append("Field").Append(i).Append("\t0\r\n");text.Append("Voltage\tPD\r\n");
        for(int i=0;i<4000;i++)text.Append((actual*Math.Cos(i*2*Math.PI/1000)).ToString("R",CultureInfo.InvariantCulture)).Append('\t').Append((actual==0?.2*Math.Cos(i*.15):(i%500==0?150:Math.Abs(Math.Sin(i*.71)))).ToString("R",CultureInfo.InvariantCulture)).Append("\r\n");
        File.WriteAllText(Path.Combine(root,phase+"_"+nameVoltage.ToString("0.0",CultureInfo.InvariantCulture)+"kV_2026-09-29-10'"+minute.ToString("00")+"'00.dem"),text.ToString(),new UTF8Encoding(true));
    }
}
