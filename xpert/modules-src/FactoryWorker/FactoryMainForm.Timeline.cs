using System;
using System.IO;
using System.Threading.Tasks;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        private int timelineGeneration;
        private async Task LoadProcessTimeline(string directory,FactoryResult capture=null)
        {
            int generation=++timelineGeneration;
            try
            {
                var data=await Task.Run(()=>FactoryProcessTimeline.Read(directory));
                if(IsDisposed || generation!=timelineGeneration)return;
                shutdownDiagnostic.SetTimeline(data,capture==null?null:capture.Capture);
            }
            catch(Exception ex){if(!IsDisposed && generation==timelineGeneration)diagnostics.AppendText("全程诊断未载入："+ex.Message+Environment.NewLine);}
        }
    }
}
