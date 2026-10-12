using System;
using System.Drawing;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        private readonly TrackBar capturePosition=new TrackBar {AutoSize=false,Minimum=-50,Maximum=50,Value=0,TickFrequency=25,SmallChange=1,LargeChange=5,Width=210,Height=26};
        private readonly Label capturePositionText=new Label {AutoSize=true,Padding=new Padding(0,6,0,0)};
        private Control BuildCapturePosition()
        {
            var bar=new FlowLayoutPanel {Dock=DockStyle.Top,Height=32,WrapContents=false,AutoScroll=false};
            bar.Controls.Add(new Label {Text="采集位置 −50 振荡 ←",AutoSize=true,Padding=new Padding(0,6,0,0)});
            bar.Controls.Add(capturePosition);bar.Controls.Add(capturePositionText);
            capturePosition.ValueChanged+=(s,e)=>{RefreshCapturePosition();ScheduleParameterSave();};
            Shown+=(s,e)=>RefreshCapturePosition();
            return bar;
        }
        private void RefreshCapturePosition()
        {
            double total=values.ContainsKey("duration")?N("duration"):60,pre=total*(capturePosition.Value+50)/100;
            capturePositionText.Text="→ +50 直流  |  "+capturePosition.Value+"：触发前 "+pre.ToString("0.###")+" ms / 后 "+(total-pre).ToString("0.###")+" ms（仅非零加压；下一轮生效）";
        }
    }
}
