using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using System.Xml;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        bool environmentLimitsEnabled;
        bool environmentPopupEnabled;
        double environmentMinTemperature = -50, environmentMaxTemperature = 120;
        double environmentMinHumidity = 0, environmentMaxHumidity = 100;
        string environmentAlertKey = String.Empty;

        string EnvironmentPreferencesPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIONGXIN", "HV DAC Xpert", "environment-limits.xml"); } }
        void LoadEnvironmentLimits()
        {
            try
            {
                if (!File.Exists(EnvironmentPreferencesPath)) return;
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(EnvironmentPreferencesPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16384 })) doc.Load(reader);
                var e = doc.DocumentElement;
                if (e == null || e.Name != "EnvironmentLimits") return;
                double t0, t1, h0, h1;
                if (!Double.TryParse(e.GetAttribute("minT"), NumberStyles.Float, CultureInfo.InvariantCulture, out t0) || !Double.TryParse(e.GetAttribute("maxT"), NumberStyles.Float, CultureInfo.InvariantCulture, out t1) || !Double.TryParse(e.GetAttribute("minRH"), NumberStyles.Float, CultureInfo.InvariantCulture, out h0) || !Double.TryParse(e.GetAttribute("maxRH"), NumberStyles.Float, CultureInfo.InvariantCulture, out h1) || !ValidEnvironmentLimits(t0,t1,h0,h1)) return;
                environmentMinTemperature=t0; environmentMaxTemperature=t1; environmentMinHumidity=h0; environmentMaxHumidity=h1;
                environmentLimitsEnabled=e.GetAttribute("enabled")=="true"; environmentPopupEnabled=e.GetAttribute("popup")=="true";
            }
            catch { /* A damaged preference must not supply fabricated sensor readings. */ }
        }
        static bool ValidEnvironmentLimits(double t0,double t1,double h0,double h1)
        { return IsFinite(t0)&&IsFinite(t1)&&IsFinite(h0)&&IsFinite(h1)&&t0>=-50&&t1<=120&&t0<=t1&&h0>=0&&h1<=100&&h0<=h1; }

        void ShowEnvironmentLimits()
        {
            using (var dialog = new Form { Name="EnvironmentLimitsDialog", Text=EnvironmentText("温湿度范围与提醒","Temperature / humidity limits"), StartPosition=FormStartPosition.CenterParent, ClientSize=new Size(660,370), MinimumSize=new Size(460,310), AutoScaleMode=AutoScaleMode.Dpi, AutoScaleDimensions=new SizeF(96,96), Font=Font, MaximizeBox=false, MinimizeBox=false, BackColor=ProjectCreationTheme.Surface })
            {
                var owner=FindForm(); ProjectDialogBrand.Apply(dialog);
                var root=new TableLayoutPanel {Name="EnvironmentLimitsBody",Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=3,RowCount=5,AutoScroll=true};
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,44)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,28)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,28));
                for(int r=0;r<5;r++) root.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
                var enabled=new CheckBox { Text=EnvironmentText("启用范围检查","Enable range checking"), Checked=environmentLimitsEnabled,Dock=DockStyle.Fill,AutoSize=false };root.Controls.Add(enabled,0,0);root.SetColumnSpan(enabled,3);
                root.Controls.Add(new Label {Text=EnvironmentText("范围由用户设定","User-defined limits"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,1);
                root.Controls.Add(new Label {Text=EnvironmentText("下限","Minimum"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},1,1);
                root.Controls.Add(new Label {Text=EnvironmentText("上限","Maximum"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},2,1);
                var t0=LimitNumber(-50,120,environmentMinTemperature); var t1=LimitNumber(-50,120,environmentMaxTemperature);var h0=LimitNumber(0,100,environmentMinHumidity);var h1=LimitNumber(0,100,environmentMaxHumidity);
                root.Controls.Add(new Label {Text=EnvironmentText("温度 / °C","Temperature / °C"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,2);root.Controls.Add(t0,1,2);root.Controls.Add(t1,2,2);
                root.Controls.Add(new Label {Text=EnvironmentText("相对湿度 / %","Relative humidity / %"),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,3);root.Controls.Add(h0,1,3);root.Controls.Add(h1,2,3);
                var popup=new CheckBox {Text=EnvironmentText("超出范围时弹窗提醒","Show a notification when out of range"),Checked=environmentPopupEnabled,Dock=DockStyle.Fill};root.Controls.Add(popup,0,4);root.SetColumnSpan(popup,3);
                var actions=new FlowLayoutPanel {Name="EnvironmentLimitsActions",Dock=DockStyle.Bottom,Padding=new Padding(18,0,18,8),FlowDirection=FlowDirection.RightToLeft,WrapContents=true};
                var save=new ProjectCreationButton {Name="SaveEnvironmentLimits",Text=EnvironmentText("保存","Save"),Primary=true};var cancel=new ProjectCreationButton {Name="CancelEnvironmentLimits",Text=EnvironmentText("取消","Cancel"),DialogResult=DialogResult.Cancel};actions.Controls.Add(save);actions.Controls.Add(cancel);
                bool arranging=false;
                Action<bool> arrange=delegate(bool grow)
                {
                    if(arranging||dialog.IsDisposed)return;arranging=true;
                    try
                    {
                        Rectangle area=Screen.FromControl(owner??dialog).WorkingArea;
                        ProjectDialogLayout.FitToWorkingArea(dialog,area,new Size(460,310));
                        ProjectDialogLayout.MeasureActions(actions,dialog.Font,dialog.ClientSize.Width);
                        int width=Math.Max(80,dialog.ClientSize.Width-root.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth-6);
                        int height=root.Padding.Vertical;
                        for(int row=0;row<5;row++)
                        {
                            int rowHeight=48;
                            foreach(Control control in root.Controls)if(root.GetRow(control)==row)
                            {
                                int available=root.GetColumnSpan(control)==3?width:(int)(width*(root.GetColumn(control)==0?.44:.28))-6;
                                if(control is CheckBox)available-=Math.Max(24,control.Font.Height+8);
                                int measured=TextRenderer.MeasureText(control.Text,control.Font,new Size(Math.Max(20,available),10000),TextFormatFlags.WordBreak).Height;
                                rowHeight=Math.Max(rowHeight,Math.Max(control.PreferredSize.Height,measured)+control.Margin.Vertical+12);
                            }
                            root.RowStyles[row].Height=rowHeight;height+=rowHeight;
                        }
                        root.AutoScrollMinSize=new Size(0,height);
                        if(grow)
                        {
                            int nonClient=dialog.Height-dialog.ClientSize.Height;
                            dialog.ClientSize=new Size(dialog.ClientSize.Width,Math.Min(Math.Max(dialog.ClientSize.Height,height+actions.Height),Math.Max(1,area.Height-24-nonClient)));
                            ProjectDialogLayout.FitToWorkingArea(dialog,area,new Size(460,310));
                        }
                    }
                    finally{arranging=false;}
                };
                dialog.Shown+=delegate{arrange(true);};dialog.FontChanged+=delegate{arrange(true);};dialog.SizeChanged+=delegate{arrange(false);};
                save.Click+=delegate
                {
                    if(!ValidEnvironmentLimits((double)t0.Value,(double)t1.Value,(double)h0.Value,(double)h1.Value)) { MessageBox.Show(dialog,EnvironmentText("下限不能大于上限。","The minimum must not exceed the maximum."),dialog.Text,MessageBoxButtons.OK,MessageBoxIcon.Information);return; }
                    try
                    {
                        var doc=new XmlDocument();var e=doc.CreateElement("EnvironmentLimits");doc.AppendChild(e);
                        e.SetAttribute("enabled",enabled.Checked?"true":"false");e.SetAttribute("popup",popup.Checked?"true":"false");e.SetAttribute("minT",t0.Value.ToString(CultureInfo.InvariantCulture));e.SetAttribute("maxT",t1.Value.ToString(CultureInfo.InvariantCulture));e.SetAttribute("minRH",h0.Value.ToString(CultureInfo.InvariantCulture));e.SetAttribute("maxRH",h1.Value.ToString(CultureInfo.InvariantCulture));
                        string path=EnvironmentPreferencesPath;Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                        try { doc.Save(temp);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path); } finally { if(File.Exists(temp))File.Delete(temp); }
                        environmentLimitsEnabled=enabled.Checked;environmentPopupEnabled=popup.Checked;environmentMinTemperature=(double)t0.Value;environmentMaxTemperature=(double)t1.Value;environmentMinHumidity=(double)h0.Value;environmentMaxHumidity=(double)h1.Value;environmentAlertKey="";
                        dialog.DialogResult=DialogResult.OK;
                    }
                    catch { MessageBox.Show(dialog,EnvironmentText("设置未保存，请检查用户目录访问权限。","Settings could not be saved. Check access to your user folder."),dialog.Text,MessageBoxButtons.OK,MessageBoxIcon.Warning); }
                };
                dialog.Controls.Add(root);dialog.Controls.Add(actions);dialog.CancelButton=cancel;dialog.AcceptButton=save;dialog.ShowDialog(owner);
            }
        }
        static NumericUpDown LimitNumber(decimal min,decimal max,double value)
        { return new NumericUpDown { Minimum=min,Maximum=max,Value=(decimal)value,DecimalPlaces=1,Dock=DockStyle.Top,Margin=new Padding(3,8,3,0) }; }
        void CheckEnvironmentLimits()
        {
            if(!environmentLimitsEnabled||!HasValidEnvironmentSample) return;
            bool t=TemperatureCelsius<environmentMinTemperature||TemperatureCelsius>environmentMaxTemperature;
            bool h=RelativeHumidity<environmentMinHumidity||RelativeHumidity>environmentMaxHumidity;
            if(!t&&!h){environmentAlertKey="";return;}
            string key=(t?"T":"")+(h?"H":"");
            string message=EnvironmentText("实测温湿度超出您设置的范围，请核对现场条件。","Measured conditions are outside your limits. Check the test environment.");
            SetStatus(message,false);
            if(environmentPopupEnabled&&environmentAlertKey!=key&&Visible) MessageBox.Show(FindForm(),message,EnvironmentText("环境提醒","Environment notice"),MessageBoxButtons.OK,MessageBoxIcon.Warning);
            environmentAlertKey=key;
        }
    }
}
