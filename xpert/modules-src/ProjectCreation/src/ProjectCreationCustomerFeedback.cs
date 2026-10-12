using System;
using System.ComponentModel;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        [Browsable(false)] public Action<string> DiagnosticSink {get;set;}
        [Browsable(false)] public string LastCustomerDiagnostic {get;private set;}="";
        string CustomerProjectMessage(string diagnostic,string chinese,string english)
        {
            LastCustomerDiagnostic=diagnostic??"";
            try {if(DiagnosticSink!=null&&LastCustomerDiagnostic.Length>0)DiagnosticSink(LastCustomerDiagnostic);}
            catch(Exception error){if(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)throw;}
            return MaterialText(chinese,english);
        }
        string CustomerJointFailure(string diagnostic)
        {return CustomerProjectMessage(diagnostic,"接头未修改。请核对接头位置和材料；若项目已变化，请重新加载后再试。","The joint was not changed. Check its position and materials. If the project changed, reload it before retrying.");}
    }
}
