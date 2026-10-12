using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class OemModuleContractSmoke
{
    const BindingFlags B=BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static byte[] Bytes(Icon icon){using(var stream=new MemoryStream()){icon.Save(stream);return stream.ToArray();}}
    static object Call(Type type,string method,params object[] args){return type.GetMethod(method,B).Invoke(null,args);}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();string root=Path.GetFullPath(args[0]);
            string[] paths={"DataAnalysis/HVX.PdReview.V1.0.dll","ProjectCreation/Xiongxin.OWTS.ProjectCreation.dll","ProjectManagement/Xiongxin.OWTS.ProjectManagement.dll","DataBrowser/Xiongxin.OWTS.DataBrowser.dll","ReportGenerator/Xiongxin.OWTS.ReportGenerator.dll","PressureTest/Xiongxin.OWTS.PressureTest.dll","CalibrationTest/Xiongxin.OWTS.CalibrationTest.dll"};
            var types=paths.Select(path=>Assembly.LoadFrom(Path.Combine(root,path)).GetType("HVX.Presentation.OemUiBranding",true)).ToArray();
            Call(types[0],"Publish",null,null);var forms=new List<Form>();
            try {
                foreach(var type in types){using(var icon=(Icon)Call(type,"LoadIcon"))Check(Bytes(icon).SequenceEqual(Bytes(SystemIcons.Application)),type.Assembly.GetName().Name+" neutral default");Check(Call(type,"LoadLogo")==null,"no embedded branded logo fallback");var form=new Form();Call(type,"Apply",form);Call(type,"Apply",form);forms.Add(form);}
                var registry=(Dictionary<Form,Action>)AppDomain.CurrentDomain.GetData("HVX.OemUi.Windows.v1");Check(registry.Count==7,"all UI assemblies share one deduplicated registry");
                using(var logo=new Bitmap(35,19)){
                    using(var graphics=Graphics.FromImage(logo))graphics.Clear(Color.Goldenrod);
                    Call(types[3],"Publish",SystemIcons.Information,logo);
                    foreach(var type in types){using(var icon=(Icon)Call(type,"LoadIcon"))Check(Bytes(icon).SequenceEqual(Bytes(SystemIcons.Information)),"OEM icon same across all modules");using(var image=(Bitmap)Call(type,"LoadLogo"))Check(image.Width==35&&image.Height==19&&image.GetPixel(0,0).ToArgb()==Color.Goldenrod.ToArgb(),"OEM logo exact aspect/color from shared cache");}
                    foreach(var form in forms)Check(Bytes(form.Icon).SequenceEqual(Bytes(SystemIcons.Information)),"already open module window follows same current icon");
                }
                forms[0].Dispose();Call(types[6],"Publish",null,null);Check(registry.Count==6,"closed module window removed before subsequent publish");
                foreach(var form in forms.Skip(1))Check(Bytes(form.Icon).SequenceEqual(Bytes(SystemIcons.Application)),"neutral reset refreshes remaining windows");
                foreach(var type in types)Check(Call(type,"LoadLogo")==null,"neutral reset clears logo cache across all modules");
            } finally{foreach(var form in forms)form.Dispose();Call(types[0],"Publish",null,null);}
            var finalRegistry=(Dictionary<Form,Action>)AppDomain.CurrentDomain.GetData("HVX.OemUi.Windows.v1");Check(finalRegistry.Count==0,"cross-assembly listeners released");
            Console.WriteLine("OEM_MODULE_CONTRACT_PASS="+checks+"; MODULE_COUNT=7; PRODUCT_ENTRY_EXECUTED=false; HARDWARE_ACCESSED=false");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
