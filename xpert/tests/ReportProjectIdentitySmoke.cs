using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// Offline fixtures only: no document application, printer, or device is opened.
internal static class ReportProjectIdentitySmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object Field(object value,string name){return value.GetType().GetField(name,All).GetValue(value);}
    static void SetField(object value,string name,object content){value.GetType().GetField(name,All).SetValue(value,content);}
    static object Property(object value,string name){return value.GetType().GetProperty(name,All).GetValue(value,null);}
    static void SetProperty(object value,string name,object content){value.GetType().GetProperty(name,All).SetValue(value,content,null);}
    static object Call(object value,string name,params object[] args){Type t=value as Type??value.GetType();return t.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value is Type?null:value,args);}
    static string MakeProject(string qa,string name,string cable)
    {
        string root=Path.Combine(qa,name);Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"电缆长度=1000\r\n相电压=8.7\r\n负载类型=单芯三相\r\n",Encoding.UTF8);
        File.WriteAllText(Path.Combine(root,"CABLE.in"),cable,Encoding.UTF8);return root;
    }
    static string Block(string phase,string first,string last,string joints)
    {return "0\t"+phase+"\t\t\r\n1\t"+first+"\t终端\t0\r\n2\t本体\t塑料绝缘\t\r\n"+joints+"1\t"+last+"\t终端\t1000\r\n";}
    static object Package(Assembly module,string project)
    {
        object package=Activator.CreateInstance(module.GetType("HVX.ReportGenerator.ReportAnalysisPackage",true));
        SetProperty(package,"ProjectPath",project);SetProperty(package,"AnalysisName","Synthetic saved analysis");return package;
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            string qa=Path.Combine(Path.GetFullPath(args[1]),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            string a=MakeProject(qa,"ProjectA",Block("A相","接头厂区起点","接头厂区终点",
                "1\t人工名称\t中间接头\t120\r\n2\t本体\t塑料绝缘\t\r\n1\t接头 2\t中间接头\t800\r\n2\t本体\t塑料绝缘\t\r\n")+
                Block("B相","始端","终端","1\tB自定义位置\t中间接头\t340\r\n2\t本体\t塑料绝缘\t\r\n"));
            string b=MakeProject(qa,"ProjectB",Block("A相","始端","终端",""));
            string legacy=MakeProject(qa,"Legacy", "0\tALL\r\n1\t始端\t终端\t0\r\n1\t接头1\t中间接头\t80\r\n1\t终端\t终端\t1000\r\n");
            byte[] original=File.ReadAllBytes(Path.Combine(a,"CABLE.in"));
            Assembly module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));
            Type reader=module.GetType("HVX.ReportGenerator.ProjectReportDataReader",true);
            object data=Call(reader,"Read",a);double[] positions=(double[])Property(data,"JointPositions");
            Check(positions.SequenceEqual(new[]{120d,340d,800d}),"topology includes custom names, excludes endpoints even named joint");
            string summary=(string)Property(data,"JointSummary");
            Check(summary.Contains("A · 人工名称")&&summary.Contains("B · B自定义位置"),"summary preserves custom name and phase");
            Check(!summary.Contains("接头厂区"),"endpoint labels are never counted as joints");
            data=Call(reader,"Read",b);Check(((double[])Property(data,"JointPositions")).Length==0,"no-joint topology stays empty");
            data=Call(reader,"Read",legacy);Check(((double[])Property(data,"JointPositions")).SequenceEqual(new[]{80d}),"legacy non-four-column compatibility retained");
            Check(((string[])Property(data,"Warnings")).Any(w=>w.Contains("legacy name-based")),"legacy lookup limitation is explicit");
            string output=Path.Combine(qa,"Reports");Directory.CreateDirectory(output);
            string oldPdf=Path.Combine(output,"ProjectA_OWTS_Report_zh-CN.pdf"),newDocx=Path.ChangeExtension(oldPdf,".docx");
            File.WriteAllText(oldPdf,"synthetic report path fixture");File.WriteAllText(newDocx,"synthetic report path fixture");
            DateTime clock=new DateTime(2026,9,29,9,0,0,DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(oldPdf,clock);File.SetLastWriteTimeUtc(newDocx,clock.AddMinutes(1));
            using(var control=(Control)Activator.CreateInstance(module.GetType("HVX.ReportGenerator.ReportGeneratorControl",true)))
            {
                SetProperty(control,"PromptAnalysisOnLoad",false);SetProperty(control,"OutputDirectory",output);
                Check((bool)Call(control,"LoadProject",a),"project A loads");
                var values=new Dictionary<string,string>();Call(control,"ApplyReportValues",values);
                Check(values["cable.joint_count"]=="3","formal report count includes all custom joints");
                Check(values["cable.accessories"].Contains("人工名称"),"formal report retains named accessory data");
                Check((string)Call(control,"FindOpenableReportPath")==newDocx,"new Word-only revision wins over stale paired PDF");
                File.SetLastWriteTimeUtc(oldPdf,clock.AddMinutes(2));
                Check((string)Call(control,"FindOpenableReportPath")==oldPdf,"newer PDF remains preferred");
                File.SetLastWriteTimeUtc(oldPdf,clock.AddMinutes(1));
                Check((string)Call(control,"FindOpenableReportPath")==oldPdf,"same-generation PDF can be preferred");
                SetField(control,"lastGeneratedPdfPath",oldPdf);SetField(control,"lastGeneratedDocxPath",newDocx);
                Check((bool)Call(control,"ApplyAnalysisPackage",Package(module,a+Path.DirectorySeparatorChar)),"same project with trailing separator loads");
                Check((string)Field(control,"lastGeneratedPdfPath")==oldPdf&&(string)Field(control,"lastGeneratedDocxPath")==newDocx,"same project retains generated output identity");
                Check((bool)Call(control,"ApplyAnalysisPackage",Package(module,b)),"other-project analysis loads");
                Check((string)Property(control,"ProjectPath")==b,"current project matches newly loaded package");
                Check((string)Field(control,"lastGeneratedPdfPath")==""&&(string)Field(control,"lastGeneratedDocxPath")=="","cross-project transition clears both output caches");
                Check((string)Call(control,"FindOpenableReportPath")=="","A output cannot be opened or printed while viewing B");
                string bDocx=Path.Combine(output,"ProjectB_OWTS_Report_zh-CN.docx");File.WriteAllText(bDocx,"synthetic report path fixture");
                Check((string)Call(control,"FindOpenableReportPath")==bDocx,"new project resolves its own report in shared output directory");
            }
            Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(a,"CABLE.in"))),"read-only report path never edits cable metadata");
            Console.WriteLine("REPORT_PROJECT_IDENTITY_PASS="+checks+"; PRINTED=False; HARDWARE_ACCESSED=False; EVIDENCE="+qa);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex is TargetInvocationException?((TargetInvocationException)ex).InnerException:ex);return 1;}
    }
}
