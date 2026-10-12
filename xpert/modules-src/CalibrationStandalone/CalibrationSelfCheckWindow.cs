using System;
using System.Drawing;
using System.Windows.Forms;

namespace Xiongxin.Calibration
{
    // Progress only: closing/cancelling aborts the current preflight. This window
    // never stays over the operator waveform during the actual calibration.
    internal sealed class CalibrationSelfCheckWindow : Form
    {
        readonly Label detail=new Label{Dock=DockStyle.Fill,Padding=new Padding(16),TextAlign=ContentAlignment.MiddleLeft};
        readonly Action cancel;
        readonly Func<string,string> translate;
        bool completed;
        internal CalibrationSelfCheckWindow(Icon icon,Action cancel,Func<string,string> translate=null)
        {
            this.cancel=cancel;this.translate=translate??(text=>text);Icon=icon;Text=this.translate("采集自校验");
            Size=new Size(540,220);Font=new Font("Microsoft YaHei UI",10);FormBorderStyle=FormBorderStyle.FixedDialog;
            MinimizeBox=MaximizeBox=false;ShowInTaskbar=false;StartPosition=FormStartPosition.CenterParent;
            var stop=new HVX.Presentation.R2RoundedButton{Dock=DockStyle.Bottom,Height=38,Text=this.translate("取消采集")};
            stop.Click+=delegate{detail.Text=this.translate("正在停止自校验…");stop.Enabled=false;cancel();};
            Controls.Add(detail);Controls.Add(stop);
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(!completed){e.Cancel=true;cancel();detail.Text=this.translate("正在停止自校验…");}};
        }
        internal void UpdateStep(string text){detail.Text=translate(text);}
        internal void Complete(){completed=true;Close();}
    }
}
