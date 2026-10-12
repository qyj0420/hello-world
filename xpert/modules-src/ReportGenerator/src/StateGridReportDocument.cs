using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace HVX.ReportGenerator
{
    internal sealed class StateGridReportResult
    {
        internal bool Success,Skipped,PdfCreated;
        internal string DocxPath="",PdfPath="",Error="",Warning="";
    }

    // A self-contained, editable Word package. No private template paths, Office
    // automation or algorithm runtime are needed to produce the DOCX itself.
    internal sealed class StateGridReportDocument
    {
        static readonly XNamespace W="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        static readonly XNamespace R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        static readonly XNamespace WP="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        static readonly XNamespace A="http://schemas.openxmlformats.org/drawingml/2006/main";
        static readonly XNamespace PIC="http://schemas.openxmlformats.org/drawingml/2006/picture";
        static readonly XNamespace REL="http://schemas.openxmlformats.org/package/2006/relationships";
        readonly List<string> media=new List<string>();
        readonly XElement body=new XElement(W+"body");
        int pictureId;

        internal static StateGridReportResult Generate(string root,ProjectReportData project,double[,] matrix,string conclusion,string docx,string pdf,ReportOutputFormat format)
        {
            var result=new StateGridReportResult();string temp=null;
            try {
                if(HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(root)){result.Skipped=true;result.Warning="电容项目不适用电缆局部放电振荡波检测附录 M，已跳过国网格式附加报告。";return result;}
                if(File.Exists(docx)||File.Exists(pdf))throw new IOException("国网附加报告输出已存在，未覆盖："+docx);
                var data=StateGridReportData.Read(root,project,matrix,conclusion);
                temp=Path.Combine(Path.GetTempPath(),"HVX_StateGrid_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
                foreach(var figure in data.Figures)if(figure.Record!=null) {
                    try {figure.ImagePath=ProjectWaveformChartRenderer.Render(figure.Record,0,Path.Combine(temp,"wave-"+Guid.NewGuid().ToString("N")+".png"));}
                    catch(Exception error){figure.ImagePath=null;figure.Note+="；图形不可用："+error.Message;}
                }
                foreach(bool charge in new[]{true,false}) {
                    var figure=new StateGridFigure{Title=charge?"放电位置分布图（Q-L）":"放电次数分布图（N-L）",Phase="全部相",Note="缺少已接受定位分析数据"};
                    if(matrix!=null&&matrix.GetLength(0)>0&&matrix.GetLength(1)>=36&&Enumerable.Range(0,matrix.GetLength(0)).Any(r=>matrix[r,35]==1&&StateGridReportData.Finite(matrix[r,13]))) {
                        string path=Path.Combine(temp,charge?"QL.png":"NL.png");
                        if(charge)LocationChartRenderer.RenderChargeLocation(matrix,VoltageInterval.AllResults(),ReportPhase.All,data.Project,0,figure.Title,path);
                        else LocationChartRenderer.RenderCountLocation(matrix,VoltageInterval.AllResults(),ReportPhase.All,data.Project,0,figure.Title,path);
                        figure.ImagePath=path;figure.Note="来源：当前已加载分析中的已接受定位事件；不包含未接受事件。";
                    }
                    data.Figures.Add(figure);
                }
                var document=new StateGridReportDocument();document.Build(data);document.Save(docx);result.DocxPath=docx;result.Success=true;
                if(format!=ReportOutputFormat.WordOnly) {
                    string provider,error;
                    if(ReportDocumentService.TryConvertWithOfficeOrWps(docx,pdf,false,out provider,out error)) {
                        result.PdfCreated=true;result.PdfPath=pdf;
                        if(format==ReportOutputFormat.PdfOnly){try{File.Delete(docx);result.DocxPath="";}catch(Exception e){result.Warning="PDF 已生成，恢复用 Word 文件未删除："+e.Message;}}
                    } else {
                        result.Warning="国网附加报告 PDF 转换失败，已保留 Word 文件："+docx+"；"+error;
                        if(format==ReportOutputFormat.PdfOnly)result.Success=false;
                    }
                }
            }catch(Exception error){result.Success=false;result.Error=error.Message;}
            finally {
                if(temp!=null&&Directory.Exists(temp)) {
                    // Only files generated in this invocation's new private folder.
                    foreach(string path in Directory.GetFiles(temp))try{File.Delete(path);}catch{}
                    try{Directory.Delete(temp,false);}catch{}
                }
            }
            return result;
        }

        void Build(StateGridReportData data)
        {
            body.Add(P("Q/GDW 11262—2024",true,"left",22));
            body.Add(P("附  录  M",true,"center",24));body.Add(P("（资料性）",false,"center",22));
            body.Add(P("电缆局部放电振荡波检测试验报告格式",true,"center",24));
            body.Add(P("表 M.1  电缆局部放电振荡波检测试验报告",true,"center",24));
            var table=Table();
            table.Add(Row("天气："+data.Value("天气","Weather"),"温度："+data.Value("温度","Temperature")+" ℃","相对湿度："+data.Value("湿度","Humidity")+" %","试验日期："+data.Value("日期","project.date")));
            table.Add(Row("检测单位",S(data.Project.InspectionOrganization),"试验性质",data.Value("试验性质","TestNature")));
            table.Add(Row("线路名称",S(data.Project.LineName),"投运日期",data.Value("投运日期","CommissioningDate")));
            table.Add(Row("电缆厂家",data.Value("电缆厂家","制造厂家","CableManufacturer"),"电缆型号",S(data.Project.CableModel)));
            table.Add(Row("电缆起讫点",S(data.Project.StartName)+" → "+S(data.Project.EndName),"电缆接头数量",File.Exists(Path.Combine(data.Project.ProjectPath,"CABLE.in"))?data.Project.JointPositions.Length.ToString(CultureInfo.InvariantCulture):StateGridReportData.Missing));
            table.Add(Row("试验仪器",data.Value("试验仪器","检测仪器","TestInstrument"),"气体检测仪器",data.Value("气体检测仪器","GasDetectionInstrument")));
            table.Add(SpanRow("试验结果",4,true));table.Add(Row("相位","A 相","B 相","C 相"));
            table.Add(Row("背景噪声",data.Value("noise.A"),data.Value("noise.B"),data.Value("noise.C")));
            table.Add(Row("1.0U0（峰值电压 kV）",data.Value("peak.1.0.A"),data.Value("peak.1.0.B"),data.Value("peak.1.0.C")));
            table.Add(Row("1.4U0（峰值电压 kV）",data.Value("peak.1.4.A"),data.Value("peak.1.4.B"),data.Value("peak.1.4.C")));
            table.Add(Row("电缆接头放电情况",data.Discharge("A",0),data.Discharge("B",0),data.Discharge("C",0)));
            table.Add(Row("本体放电情况",data.Discharge("A",1),data.Discharge("B",1),data.Discharge("C",1)));
            table.Add(Row("终端位置放电情况",data.Discharge("A",2),data.Discharge("B",2),data.Discharge("C",2)));
            table.Add(SpanRow("结论：\n"+data.Conclusion+"\n本表未经人工审核签署，参考替代数据不作为自动合格判据。",4,false));
            table.Add(new XElement(W+"tr",Cell("主管："+data.Value("主管","Supervisor"),2400),Cell("审核："+data.Value("审核","Reviewer"),2400),Cell("试验人员："+S(data.Project.TestPersonnel)+"（待签名）",4800,2)));
            body.Add(table);body.Add(P("附图及来源见续页。部位按记录位置匹配，定位容差与缺陷性质需专业人员复核。",false,"left",18));
            int index=0;
            foreach(var group in data.Figures.Select((figure,i)=>new{figure,i}).GroupBy(x=>x.i/2)) {
                Page();body.Add(P("Q/GDW 11262—2024",true,"right",22));body.Add(P("表 M.1（续）",true,"center",24));
                var figures=Table();
                foreach(var item in group) {
                    var figure=item.figure;index++;
                    figures.Add(SpanRow("测试分项："+figure.Title,true));
                    var content=Cell("",7200,3);content.RemoveNodes();content.Add(CellProperties(7200,3));
                    if(!String.IsNullOrEmpty(figure.ImagePath))content.Add(Image(figure.ImagePath,4.8));
                    else content.Add(P(StateGridReportData.Missing,false,"center",22));
                    content.Add(P("附图 "+index+"  "+figure.Note,false,"left",18));
                    figures.Add(new XElement(W+"tr",new XElement(W+"trPr",new XElement(W+"cantSplit")),Cell(figure.Phase??"",2400),content));
                    data.Trace.Add("附图 "+index+" / "+figure.Title+" / "+figure.Phase+"："+figure.Note);
                }
                body.Add(figures);
            }
            Page();body.Add(P("数据来源及参考替代说明",true,"center",24));
            foreach(string note in data.Trace)body.Add(P(note,false,"left",20));
            body.Add(new XElement(W+"sectPr",new XElement(W+"footerReference",new XAttribute(W+"type","default"),new XAttribute(R+"id","rIdFooter")),
                new XElement(W+"pgSz",new XAttribute(W+"w",11906),new XAttribute(W+"h",16838)),
                new XElement(W+"pgMar",new XAttribute(W+"top",850),new XAttribute(W+"bottom",850),new XAttribute(W+"left",1133),new XAttribute(W+"right",1133),new XAttribute(W+"header",425),new XAttribute(W+"footer",425))));
        }

        static string S(string value){return String.IsNullOrWhiteSpace(value)||value=="—"?StateGridReportData.Missing:value;}
        void Page(){body.Add(new XElement(W+"p",new XElement(W+"r",new XElement(W+"br",new XAttribute(W+"type","page")))));}
        static XElement P(string text,bool bold=false,string align="center",int size=20)
        {
            var p=new XElement(W+"p",new XElement(W+"pPr",new XElement(W+"jc",new XAttribute(W+"val",align)),new XElement(W+"spacing",new XAttribute(W+"before",40),new XAttribute(W+"after",60),new XAttribute(W+"line",260),new XAttribute(W+"lineRule","auto"))));
            string[] lines=(text??"").Split('\n');for(int i=0;i<lines.Length;i++){
                var props=new XElement(W+"rPr",new XElement(W+"rFonts",new XAttribute(W+"ascii","Times New Roman"),new XAttribute(W+"hAnsi","Times New Roman"),new XAttribute(W+"eastAsia","宋体")),new XElement(W+"color",new XAttribute(W+"val","000000")),new XElement(W+"sz",new XAttribute(W+"val",size)),new XElement(W+"szCs",new XAttribute(W+"val",size)));
                if(bold)props.Add(new XElement(W+"b"));var run=new XElement(W+"r",props);if(i>0)run.Add(new XElement(W+"br"));run.Add(new XElement(W+"t",new XAttribute(XNamespace.Xml+"space","preserve"),lines[i]));p.Add(run);
            }return p;
        }
        static XElement Table(){return new XElement(W+"tbl",new XElement(W+"tblPr",new XElement(W+"tblW",new XAttribute(W+"w",9600),new XAttribute(W+"type","dxa")),new XElement(W+"tblLayout",new XAttribute(W+"type","fixed")),new XElement(W+"tblBorders",new[]{"top","left","bottom","right","insideH","insideV"}.Select(side=>new XElement(W+side,new XAttribute(W+"val","single"),new XAttribute(W+"sz",5),new XAttribute(W+"color","000000"))))),new XElement(W+"tblGrid",Enumerable.Range(0,4).Select(i=>new XElement(W+"gridCol",new XAttribute(W+"w",2400)))));}
        static XElement CellProperties(int width,int span){return new XElement(W+"tcPr",new XElement(W+"tcW",new XAttribute(W+"w",width),new XAttribute(W+"type","dxa")),span>1?new XElement(W+"gridSpan",new XAttribute(W+"val",span)):null,new XElement(W+"vAlign",new XAttribute(W+"val","center")),new XElement(W+"tcMar",new[]{"top","left","bottom","right"}.Select(side=>new XElement(W+side,new XAttribute(W+"w",70),new XAttribute(W+"type","dxa")))));}
        static XElement Cell(string text,int width=2400,int span=1){return new XElement(W+"tc",CellProperties(width,span),P(text));}
        static XElement Row(params string[] text){return new XElement(W+"tr",text.Select(t=>Cell(t)));}
        static XElement SpanRow(string text,bool bold){return SpanRow(text,4,bold);}
        static XElement SpanRow(string text,int span,bool bold){var cell=Cell(text,2400*span,span);cell.Element(W+"p").ReplaceWith(P(text,bold,bold?"center":"left"));return new XElement(W+"tr",cell);}

        XElement Image(string path,double inches)
        {
            media.Add(path);int id=++pictureId;long cx=(long)(inches*914400),cy;
            using(var image=System.Drawing.Image.FromFile(path))cy=(long)(cx*(double)image.Height/image.Width);
            return new XElement(W+"p",new XElement(W+"pPr",new XElement(W+"jc",new XAttribute(W+"val","center"))),new XElement(W+"r",new XElement(W+"drawing",
                new XElement(WP+"inline",new XAttribute("distT",0),new XAttribute("distB",0),new XAttribute("distL",0),new XAttribute("distR",0),
                    new XElement(WP+"extent",new XAttribute("cx",cx),new XAttribute("cy",cy)),new XElement(WP+"docPr",new XAttribute("id",id),new XAttribute("name","附图 "+id),new XAttribute("descr",Path.GetFileName(path))),
                    new XElement(A+"graphic",new XElement(A+"graphicData",new XAttribute("uri",PIC.NamespaceName),new XElement(PIC+"pic",
                        new XElement(PIC+"nvPicPr",new XElement(PIC+"cNvPr",new XAttribute("id",0),new XAttribute("name","附图 "+id)),new XElement(PIC+"cNvPicPr")),
                        new XElement(PIC+"blipFill",new XElement(A+"blip",new XAttribute(R+"embed","rIdImage"+id)),new XElement(A+"stretch",new XElement(A+"fillRect"))),
                        new XElement(PIC+"spPr",new XElement(A+"xfrm",new XElement(A+"off",new XAttribute("x",0),new XAttribute("y",0)),new XElement(A+"ext",new XAttribute("cx",cx),new XAttribute("cy",cy))),new XElement(A+"prstGeom",new XAttribute("prst","rect"),new XElement(A+"avLst"))))))))));
        }

        void Save(string output)
        {
            string full=Path.GetFullPath(output),temp=full+"."+Guid.NewGuid().ToString("N")+".tmp";Directory.CreateDirectory(Path.GetDirectoryName(full));
            try {
                using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))using(var zip=new ZipArchive(stream,ZipArchiveMode.Create)) {
                    XNamespace ct="http://schemas.openxmlformats.org/package/2006/content-types";
                    Write(zip,"[Content_Types].xml",new XElement(ct+"Types",new XElement(ct+"Default",new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),new XElement(ct+"Default",new XAttribute("Extension","xml"),new XAttribute("ContentType","application/xml")),new XElement(ct+"Default",new XAttribute("Extension","png"),new XAttribute("ContentType","image/png")),new XElement(ct+"Override",new XAttribute("PartName","/word/document.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")),new XElement(ct+"Override",new XAttribute("PartName","/word/footer.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml"))));
                    Write(zip,"_rels/.rels",new XElement(REL+"Relationships",new XElement(REL+"Relationship",new XAttribute("Id","rId1"),new XAttribute("Type",R.NamespaceName+"/officeDocument"),new XAttribute("Target","word/document.xml"))));
                    Write(zip,"word/document.xml",new XElement(W+"document",new XAttribute(XNamespace.Xmlns+"w",W),new XAttribute(XNamespace.Xmlns+"r",R),body));
                    Write(zip,"word/footer.xml",new XElement(W+"ftr",new XElement(W+"p",new XElement(W+"pPr",new XElement(W+"jc",new XAttribute(W+"val","center"))),new XElement(W+"fldSimple",new XAttribute(W+"instr","PAGE"),new XElement(W+"r",new XElement(W+"t","1"))))));
                    var relations=new XElement(REL+"Relationships",new XElement(REL+"Relationship",new XAttribute("Id","rIdFooter"),new XAttribute("Type",R.NamespaceName+"/footer"),new XAttribute("Target","footer.xml")));
                    for(int i=0;i<media.Count;i++) {
                        string name="image"+(i+1)+".png";relations.Add(new XElement(REL+"Relationship",new XAttribute("Id","rIdImage"+(i+1)),new XAttribute("Type",R.NamespaceName+"/image"),new XAttribute("Target","media/"+name)));
                        using(var entry=zip.CreateEntry("word/media/"+name,CompressionLevel.Optimal).Open())using(var input=File.OpenRead(media[i]))input.CopyTo(entry);
                    }Write(zip,"word/_rels/document.xml.rels",relations);
                }File.Move(temp,full);
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
        static void Write(ZipArchive zip,string name,XElement value){using(var stream=zip.CreateEntry(name,CompressionLevel.Optimal).Open())using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))new XDocument(new XDeclaration("1.0","UTF-8","yes"),value).Save(writer);}
    }
}
