using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class ReportPresentationSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks; static Assembly report,management; static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static object Call(object o,string name,params object[] args){var t=o as Type??o.GetType();return t.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(o is Type?null:o,args);}
    static object F(object o,string name){return o.GetType().GetField(name,All).GetValue(o);}
    static object P(object o,string name){return o.GetType().GetProperty(name,All).GetValue(o,null);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name,All).SetValue(o,value,null);}
    static string Hash(string path){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(path))).Replace("-","");}
    static void Write(string path,object value){File.WriteAllText(path,Json.Serialize(value),new UTF8Encoding(false));}
    static object Core(double[,] p,int audience){return Call(report.GetType("HVX.Location.EmbeddedConclusions"),"Analyze",p,new[]{0d,200d,414d},new double[0],new double[0],8.7d,false,audience,0,1d,.1d);}
    static string Block(string phase,double length,double position){return "0\t"+phase+"\t\t\r\n1\t始端\t户内冷缩\t0\r\n2\t本体\tSECRET MATERIAL\t\r\n1\t人工接头名称\tSECRET TYPE\t"+position+"\r\n2\t本体\tSECRET MATERIAL\t\r\n1\t终端\t户内冷缩\t"+length+"\r\n";}
    static string Project(string qa,string name,string mode,string cable){string root=Path.Combine(qa,name);Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\n始端地点=QA Start\r\n终端地点=QA End\r\n电缆芯数="+mode+"\r\n电缆长度=9999\r\n相电压=8.7\r\n",Encoding.UTF8);File.WriteAllText(Path.Combine(root,"CABLE.in"),cable,Encoding.UTF8);return root;}
    static void ConclusionCases(string qa)
    {
        string project=Project(qa,"saved-v3","单相",Block("A相",414,200));string archive=Path.Combine(project,"Analysis report","v3");Directory.CreateDirectory(archive);
        var all=new double[48,36];var selected=new double[24,36];
        for(int r=0;r<48;r++){all[r,0]=1;all[r,1]=r/6;all[r,2]=13.4;all[r,3]=30+r*10;all[r,5]=414;all[r,9]=r;all[r,13]=r<24?200:370;all[r,15]=r<24?180+r:100000;all[r,22]=Double.NaN;all[r,23]=20;all[r,24]=1e7;all[r,33]=.95;all[r,35]=1;if(r<24)for(int c=0;c<36;c++)selected[r,c]=all[r,c];}
        var briefCore=Core(selected,0);var expertCore=Core(selected,1);Check((bool)P(briefCore,"Ok")&&(bool)P(expertCore,"Ok"),"actual V3 fixture evaluates");
        var regions=(double[])P(briefCore,"LocationMatrix");Check(regions.SequenceEqual((double[])P(expertCore,"LocationMatrix")),"audience does not change structured evidence or scientific grades");
        string brief=(string)P(briefCore,"SummaryText"),expert=(string)P(expertCore,"SummaryText");Check(brief!=expert&&expert.Contains("证据评分"),"fixture has genuinely distinct brief/expert presentations");
        string parameters=Path.Combine(archive,"parameters.bin"),session=Path.Combine(archive,"session.json"),manifest=Path.Combine(archive,"analysis.json");
        using(var w=new BinaryWriter(File.Create(parameters))){w.Write(Encoding.ASCII.GetBytes("HVXMAT01"));w.Write(48);w.Write(36);foreach(double v in all)w.Write(v);}
        Write(session,new{schema="HVX.AnalysisSession.Utf8",version=1});
        var rows=Enumerable.Range(0,regions.Length/9).Select(i=>new{PhaseMask=(int)regions[i*9],PositionM=regions[i*9+1],ActionCode=(int)regions[i*9+8]}).ToArray();
        Write(manifest,new{Schema="HVX.NamedAnalysis",Version=1,Id="v3",Name="V3 mode fixture",CreatedUtc="2026-09-30T01:00:00Z",SavedUtc="2026-09-30T01:02:00Z",Charts=new object[0],
            Conclusion=new{Schema="HVX.AnalysisConclusion.1",AnalysisId="v3",GeneratedUtc="2026-09-30T01:01:00Z",Api="hvx-conclusion-dotnet-3.0.0|HVX-EVIDENCE-15-V3",SummaryText=brief,ParametersSha256=Hash(parameters),SessionSha256=Hash(session),Regions=rows,
                SourceRowIndices=Enumerable.Range(0,24).ToArray(),Joints=new[]{new[]{0d,200d,414d},new double[0],new double[0]},OperatingVoltageKv=8.7},
            Files=new[]{parameters,session}.Select(p=>new{Path=Path.GetFileName(p),Bytes=new FileInfo(p).Length,Sha256=Hash(p)}).ToArray()});
        string[] hashes=new[]{manifest,parameters,session}.Select(Hash).ToArray();
        using(var control=(Control)Activator.CreateInstance(report.GetType("HVX.ReportGenerator.ReportGeneratorControl")))
        {
            Set(control,"PromptAnalysisOnLoad",false);Check((bool)Call(control,"LoadAnalysis",archive),"load actual saved V3 fixture");
            var simple=(CheckBox)F(control,"briefConclusion");var professional=(CheckBox)F(control,"professionalConclusion");
            Check(simple.Checked&&!professional.Checked&&(string)P(control,"ConclusionText")==brief,"default brief matches saved scope");
            professional.Checked=true;Check(!simple.Checked&&professional.Checked&&(string)P(control,"ConclusionText")==expert,"expert toggles mutually exclusively and updates immediately");
            Check(!((string)P(control,"ConclusionText")).Contains("370"),"unselected high-charge location never enters report conclusion");
            professional.Checked=false;Check(professional.Checked&&!simple.Checked,"unchecking sole selected style cannot leave no style");
            simple.Checked=true;Check(simple.Checked&&!professional.Checked&&(string)P(control,"ConclusionText")==brief,"brief restored immediately");
            var watch=Stopwatch.StartNew();for(int n=0;n<100;n++){professional.Checked=true;simple.Checked=true;}watch.Stop();
            Check(((IDictionary)F(control,"savedConclusionPresentations")).Count==2,"both exact-scope presentations cached, not recomputed each click");Console.WriteLine("CACHED_200_TOGGLE_MS="+watch.ElapsedMilliseconds);
            professional.Checked=true;var values=new Dictionary<string,string>();Call(control,"ApplyReportValues",values);Check(values["report.conclusion"]==expert,"generated report mapping follows selected professional text");
            Check((bool)Call(control,"LoadAnalysis",archive)&&(string)P(control,"ConclusionText")==expert,"reloading saved analysis respects current expert selection");
            simple.Checked=true;values.Clear();Call(control,"ApplyReportValues",values);Check(values["report.conclusion"]==brief,"generated report mapping follows selected brief text");
            Check(new[]{manifest,parameters,session}.Select(Hash).SequenceEqual(hashes),"style changes never write archive or original waveforms");
            Check(((double[,])F(F(control,"analysisPackage"),"ConclusionInput")).GetLength(0)==24,"saved evidence range stays 24 rows, not full 48 rows");
            Set(control,"LanguageIndex",1);Check(!((string)P(control,"ConclusionText")).Contains("未生成")&&((string)P(control,"ConclusionText")).Contains("HV-Xpert analyzed"),"validated snapshot is rendered in English, not incorrectly treated as unavailable");
            professional.Checked=true;Check(((string)P(control,"ConclusionText")).Contains("evidence score")&&!simple.Checked,"English professional mode changes immediately");
            Set(control,"LanguageIndex",18);simple.Checked=true;Check(simple.Checked&&!professional.Checked&&!((string)P(control,"ConclusionText")).Contains("evidence score"),"Traditional UI maintains selected brief mode");
            var changed=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(manifest));var saved=(Dictionary<string,object>)changed["Conclusion"];
            var altered=(Dictionary<string,object>)((IList)saved["Regions"])[0];altered["ActionCode"]=(Convert.ToInt32(altered["ActionCode"])+1)%4;Write(manifest,changed);
            string changedHash=Hash(manifest);Set(control,"LanguageIndex",0);Check((bool)Call(control,"LoadAnalysis",archive),"historical V3 fixture with different structured grade remains readable");
            professional.Checked=true;Check((string)P(control,"ConclusionText")==brief,"style switch cannot silently replace an archived grade with new computation");
            Check(((Label)F(control,"status")).Text.Contains("保留原文"),"grade mismatch is explained, not presented as successful expert conversion");
            Check(Hash(manifest)==changedHash,"mismatch protection never overwrites saved evidence");
        }
    }
    static void StructureCases(string qa)
    {
        var reader=report.GetType("HVX.ReportGenerator.ProjectReportDataReader");var renderer=report.GetType("HVX.ReportGenerator.CableTopologyRenderer");
        string[] modes={"一体三芯","单芯三相","单芯三相"};string[] names={"shared","separate","repeat-A"};
        for(int n=0;n<3;n++)
        {
            string project=Project(qa,names[n],modes[n],n==0?Block("A相",400,80)+Block("B相",400,80)+Block("C相",400,80):Block("A相",400,80)+Block(n==2?"A相":"B相",200,30)+Block(n==2?"A相":"C相",100,60));
            string hash=Hash(Path.Combine(project,"CABLE.in"));object data=Call(reader,"Read",project);var export=(Array)Call(renderer,"ReadRows",data);
            using(var diagram=(Control)Activator.CreateInstance(report.GetType("HVX.ReportGenerator.ReportCableStructurePreview"),true))using(var form=new Form{ClientSize=new Size(1000,450)})
            {
                diagram.Dock=DockStyle.Fill;form.Controls.Add(diagram);form.Show();Call(diagram,"SetProject",data,0);Application.DoEvents();var preview=(Array)F(diagram,"diagramRows");
                Check(preview.Length==export.Length&&preview.Length==(n==0?1:3),"page/export same row count "+names[n]);
                for(int i=0;i<preview.Length;i++)
                {
                    var p=preview.GetValue(i);var e=export.GetValue(i);Check((double)F(p,"Length")== (double)F(e,"Length")&&(string)F(p,"Phase")== (string)F(e,"Phase")&&((double[])F(p,"Positions")).SequenceEqual((double[])F(e,"Positions")),"page/export identical actual geometry "+names[n]+i);
                    Check((double)F(p,"Length")== (n==0?400:i==0?400:i==1?200:100),"per-block length is not global ITEM 9999");
                    if(n==2)Check((string)F(p,"Phase")=="A","repeated A is not relabelled B/C");
                }
                if(n==0)Check((bool)F(preview.GetValue(0),"Integrated"),"shared three-core uses three conductors");
                foreach(int lang in new[]{0,1,18}){Call(diagram,"SetProject",data,lang);Application.DoEvents();using(var bitmap=new Bitmap(diagram.Width,diagram.Height)){diagram.DrawToBitmap(bitmap,new Rectangle(Point.Empty,diagram.Size));bitmap.Save(Path.Combine(qa,names[n]+"-preview-"+lang+".png"));}}
                if(n==1)using(var large=new Font("Microsoft YaHei",18))
                {
                    diagram.Font=large;form.ClientSize=new Size(650,250);Call(diagram,"SetProject",data,1);Application.DoEvents();
                    var drawing=(Panel)F(diagram,"drawing");Check(drawing.AutoScrollMinSize.Height>drawing.ClientSize.Height,"small large-font structure has reachable vertical scroll");
                    drawing.AutoScrollPosition=new Point(0,drawing.AutoScrollMinSize.Height);Application.DoEvents();Check(drawing.AutoScrollPosition.Y<0,"large-font last phase reachable by scrolling");
                    using(var image=new Bitmap(diagram.Width,diagram.Height)){diagram.DrawToBitmap(image,new Rectangle(Point.Empty,diagram.Size));image.Save(Path.Combine(qa,"separate-largefont-scrolled.png"));}
                }
                string path=(string)Call(renderer,"Render",data,0,Path.Combine(qa,names[n]+"-export.png"));using(var bitmap=new Bitmap(path)){Check(bitmap.Width==3000&&bitmap.Height>=560,"export image dimensions "+names[n]);Check(bitmap.GetPixel(700,180).ToArgb()!=Color.White.ToArgb(),"actual cable is rendered "+names[n]);}
                if(n!=0){using(var bitmap=new Bitmap(path)){Check(bitmap.GetPixel(1700,362).ToArgb()==Color.White.ToArgb(),"short B/A cable not stretched to global length");Check(bitmap.GetPixel(1700,180).ToArgb()!=Color.White.ToArgb(),"long A cable retains true extent");}}
                Check(hash==Hash(Path.Combine(project,"CABLE.in")),"structure is read-only "+names[n]);form.Close();
            }
        }
        string missing=Path.Combine(qa,"missing-structure");Directory.CreateDirectory(missing);File.WriteAllText(Path.Combine(missing,"ITEM.in"),"电缆长度=9999");
        object missingData=Call(reader,"Read",missing);string missingImage=(string)Call(renderer,"Render",missingData,1,Path.Combine(qa,"missing-structure.png"));Check(File.Exists(missingImage),"missing geometry produces explicit placeholder without blocking report or inventing length");
    }
    static void ManagementCases()
    {
        using(var c=(Control)Activator.CreateInstance(management.GetType("HVX.Controls.ProjectManagementControl")))
        {
            var grid=(DataGridView)F(c,"grid");string[] labels={"结论","Conclusion","Заключение","Zaključak","Závěr","Conclusion","Schlussfolgerung","Következtetés","Conclusione","Wniosek","Concluzie","Заключение","Zaključak","Záver","Sklep","Conclusión","Sonuç","Kết luận","結論"};
            for(int lang=0;lang<labels.Length;lang++){Set(c,"LanguageIndex",lang);Check(grid.Columns["InsulationStatus"].HeaderText==labels[lang],"localized conclusion heading "+lang);}
            Set(c,"LanguageIndex",0);var record=Activator.CreateInstance(management.GetType("HVX.Controls.ProjectRecord"));var status=Activator.CreateInstance(management.GetType("HVX.Controls.ProjectInsulationStatus"));Set(record,"InsulationStatus",status);
            foreach(var pair in new[]{new[]{"Unanalyzed","未分析"},new[]{"NoConclusion","未生成结论"},new[]{"Invalid","结论不可用"}}){Set(status,"StateCode",pair[0]);Check((string)Call(c,"LocalizedInsulationStatus",record)==pair[1],"distinct conclusion state "+pair[0]);}
            Set(status,"StateCode","Ready");string[] legacy={"记录","观察","重点关注","及时处理"},current={"证据不足","计划跟踪","重点关注","优先核查"};
            for(int i=0;i<4;i++){Set(status,"SeverityCode",i);Set(status,"ApiVersion","Task15");Check((string)Call(c,"LocalizedInsulationStatus",record)==legacy[i],"legacy grade preserved "+i);Set(status,"ApiVersion","hvx-conclusion-dotnet-3.0.0");Check((string)Call(c,"LocalizedInsulationStatus",record)==current[i],"V3 grade preserved "+i);}
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try{Application.EnableVisualStyles();string build=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"presentation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            report=Assembly.LoadFrom(Path.Combine(build,"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));management=Assembly.LoadFrom(Path.Combine(build,"modules","ProjectManagement","Xiongxin.OWTS.ProjectManagement.dll"));
            ConclusionCases(qa);StructureCases(qa);ManagementCases();Console.WriteLine("REPORT_PRESENTATION_PASS="+checks+";real_project_write=false;hardware=false;print=false");Console.WriteLine("EVIDENCE="+qa);return 0;}
        catch(Exception ex){Console.Error.WriteLine(ex is TargetInvocationException?((TargetInvocationException)ex).InnerException:ex);return 1;}
    }
}
