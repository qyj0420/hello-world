using System;
using System.Drawing;
using System.Windows.Forms;
using Xiongxin.OWTS.Localization;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        // Capture an isolated, current-version form with default example values.
        // The live draft and its selected page are never touched.
        public Image[] CreateProjectGuidePages()
        {
            var images=new Image[3];
            try
            {
                using(var host=new GuideCaptureHost())
                using(var example=new ProjectCreationControl())
                {
                    example.EmbeddedMode=true;example.LanguageIndex=LanguageIndex;
                    example.Font=Font;example.Size=new Size(1850,990);
                    example.StartName=UiLanguage.Text(LanguageIndex,"示例始端","Example start");example.EndName=UiLanguage.Text(LanguageIndex,"示例终端","Example end");
                    example.CableLengthA=414.8;example.CableLengthB=414.8;example.CableLengthC=414.8;example.PhaseVoltage="8.7kV";
                    host.ClientSize=example.Size;host.Controls.Add(example);host.Show();
                    example.CreateControl();
                    string[] sections={JointSection,TestConfigurationSection,EnvironmentSection};
                    for(int i=0;i<sections.Length;i++)
                    {
                        example.SetOptionalSectionExpanded(sections[i],true);
                        example.PerformLayout();
                        EnsureGuideHandles(example);
                        var bitmap=new Bitmap(example.Width,example.Height);
                        images[i]=bitmap;example.DrawToBitmap(bitmap,example.ClientRectangle);
                        using(var graphics=Graphics.FromImage(bitmap))
                        using(var border=new Pen(Color.FromArgb(0,163,193),4))
                        {
                            Control[] focuses=i==0?new Control[]{example.startNameText,example.endNameEditor,example.phaseVoltageCombo,example.lengthAField,example.startTypeCombo,example.endTypeCombo,example.cableGrid}:
                                i==1?new Control[]{example.pressureModeCombo,example.pressurePreviewGrid}:
                                new Control[]{example.temperatureNumber,example.humidityNumber,example.fixStatusValue};
                            foreach(var focus in focuses)
                            {
                                if(focus==null||!focus.Visible)continue;
                                var box=new Rectangle(example.PointToClient(focus.PointToScreen(Point.Empty)),focus.Size);
                                box.Inflate(3,3);graphics.DrawRectangle(border,box);
                            }
                        }
                    }
                }
                return images;
            }
            catch {foreach(var image in images)if(image!=null)image.Dispose();throw;}
        }
        static void EnsureGuideHandles(Control control)
        {
            // Owner-drawn combo boxes need their own HWND for DrawToBitmap.
            // Creating handles does not show or activate this isolated example.
            if(control.Visible){var handle=control.Handle;}
            foreach(Control child in control.Controls)EnsureGuideHandles(child);
        }
        sealed class GuideCaptureHost:Form
        {
            internal GuideCaptureHost(){AutoScaleMode=AutoScaleMode.None;ShowInTaskbar=false;FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;Location=new Point(-32000,-32000);}
            protected override bool ShowWithoutActivation{get{return true;}}
            protected override CreateParams CreateParams{get{var value=base.CreateParams;value.ExStyle|=0x08000000;return value;}}
        }
    }
}
