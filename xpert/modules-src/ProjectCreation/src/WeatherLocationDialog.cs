using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace HVX.Controls
{
    internal sealed class WeatherLocationDialog : Form
    {
        readonly TextBox latitude, longitude;
        readonly CheckBox agreement;
        readonly ProjectCreationButton accept;
        readonly TableLayoutPanel root;
        readonly Label notice;
        readonly FlowLayoutPanel actions;
        readonly ProjectCreationButton cancel;
        bool arranging;
        internal double Latitude { get; private set; }
        internal double Longitude { get; private set; }

        internal WeatherLocationDialog(int language, bool hardwarePosition, double lat, double lon)
        {
            bool zh = Xiongxin.OWTS.Localization.UiLanguage.IsChinese(language);
            Text = zh ? "天气位置" : "Weather location";
            Font = ProjectCreationTheme.UiFont(9f);
            BackColor = ProjectCreationTheme.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(590, 350);
            ProjectDialogBrand.Apply(this);
            root = new TableLayoutPanel { Name="WeatherLocationBody", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(16), AutoScroll=true };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            notice = new Label { Name="WeatherPrivacyNotice", Dock = DockStyle.Fill, AutoSize = false, UseMnemonic = false, ForeColor = ProjectCreationTheme.Ink,
                Text = zh ? "联网天气由 MET Norway（挪威气象局）提供，仅作预报参考，不是现场实测。\r\n\r\n确认后软件会发送约三位小数的经纬度至 api.met.no；该服务也会看到联网 IP。不发送项目名称、客户资料、设备序列号或试验数据。位置仅用于天气，不修改设备定位。" :
                    "MET Norway provides forecast reference, not on-site measurements.\r\n\r\nAfter confirmation, coordinates rounded to three decimals are sent to api.met.no. The service also sees the connection IP. No project names, customer information, device serials or test data are sent. This location does not change the device position." };
            root.Controls.Add(notice, 0, 0); root.SetColumnSpan(notice, 2);
            root.Controls.Add(new Label { Text = zh ? "纬度（北纬为正）" : "Latitude (north positive)", Dock = DockStyle.Fill }, 0, 1);
            root.Controls.Add(new Label { Text = zh ? "经度（东经为正）" : "Longitude (east positive)", Dock = DockStyle.Fill }, 1, 1);
            latitude = new TextBox { Dock = DockStyle.Top, ReadOnly = hardwarePosition, Text = double.IsNaN(lat) ? string.Empty : lat.ToString("F3", CultureInfo.InvariantCulture), MaxLength = 16 };
            longitude = new TextBox { Dock = DockStyle.Top, ReadOnly = hardwarePosition, Text = double.IsNaN(lon) ? string.Empty : lon.ToString("F3", CultureInfo.InvariantCulture), MaxLength = 16 };
            root.Controls.Add(latitude, 0, 2); root.Controls.Add(longitude, 1, 2);
            agreement = new CheckBox { Text = zh ? "我了解并允许使用上述位置获取联网天气参考" : "Allow online forecasts for this location", Dock = DockStyle.Fill, AutoSize = false };
            root.Controls.Add(agreement, 0, 3); root.SetColumnSpan(agreement, 2);
            var source = new LinkLabel { Text = "MET Norway · CC BY 4.0", Dock = DockStyle.Fill, LinkColor = ProjectCreationTheme.Active };
            source.LinkClicked += delegate { try { Process.Start(new ProcessStartInfo(MetWeatherClient.SourceUrl) { UseShellExecute = true }); } catch { } };
            root.Controls.Add(source, 0, 4); root.SetColumnSpan(source, 2);
            actions = new FlowLayoutPanel { Name="WeatherLocationActions", Dock = DockStyle.Bottom, Height=46, Padding=new Padding(16,0,16,6), FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
            accept = new ProjectCreationButton { Text = zh ? "获取天气" : "Get forecast", Primary = true, Width = 145, Enabled = false };
            cancel = new ProjectCreationButton { Text = zh ? "取消" : "Cancel", Width = 105, DialogResult = DialogResult.Cancel };
            accept.Click += delegate { if (!Valid()) return; DialogResult = DialogResult.OK; };
            agreement.CheckedChanged += delegate { accept.Enabled = Valid(); };
            latitude.TextChanged += delegate { accept.Enabled = Valid(); };
            longitude.TextChanged += delegate { accept.Enabled = Valid(); };
            actions.Controls.Add(accept); actions.Controls.Add(cancel);
            Controls.Add(root);
            Controls.Add(actions);
            if (Xiongxin.OWTS.Localization.UiLanguage.ResolveIndex(language) == 18)
            {
                Text = Xiongxin.OWTS.Localization.UiLanguage.ToTraditional(Text);
                ConvertBuiltInLabels(root);
                ConvertBuiltInLabels(actions);
            }
            AcceptButton = accept; CancelButton = cancel;
            Shown += delegate { ArrangeLayout(true); };
            FontChanged += delegate { ArrangeLayout(true); };
            SizeChanged += delegate { ArrangeLayout(false); };
        }

        void FitToWorkingArea(Rectangle area)
        {
            ProjectDialogLayout.FitToWorkingArea(this,area,Size.Empty);
        }
        void ArrangeLayout(bool grow)
        {
            if(arranging||root==null||actions==null||IsDisposed)return;
            arranging=true;
            try
            {
                Rectangle area=Screen.FromControl(Owner??this).WorkingArea;
                FitToWorkingArea(area);
                int available=Math.Max(40,ClientSize.Width-root.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth-6);
                int textHeight=TextRenderer.MeasureText(notice.Text,notice.Font,new Size(available,10000),TextFormatFlags.WordBreak).Height+12;
                root.RowStyles[0].Height=textHeight;
                int contentHeight=root.Padding.Vertical+textHeight;
                for(int row=1;row<5;row++)
                {
                    int height=0;
                    foreach(Control control in root.Controls)if(root.GetRow(control)==row)
                    {
                        int span=root.GetColumnSpan(control);
                        int width=Math.Max(20,span==2?available:available/2-6);
                        if(control is CheckBox)width=Math.Max(20,width-Math.Max(24,control.Font.Height+8));
                        int measured=TextRenderer.MeasureText(control.Text,control.Font,new Size(width,10000),TextFormatFlags.WordBreak).Height;
                        height=Math.Max(height,Math.Max(control.PreferredSize.Height,measured)+control.Margin.Vertical+8);
                    }
                    root.RowStyles[row].Height=Math.Max(row==2?40:24,height);contentHeight+=(int)root.RowStyles[row].Height;
                }
                foreach(Button button in actions.Controls)
                {button.Font=Font;button.Width=Math.Max(button==accept?145:105,TextRenderer.MeasureText(button.Text,button.Font).Width+28);button.Height=Math.Max(36,button.Font.Height+18);}
                actions.Height=actions.GetPreferredSize(new Size(Math.Max(20,ClientSize.Width),0)).Height+4;
                root.AutoScrollMinSize=new Size(0,contentHeight);
                if(grow)
                {
                    int nonClient=Height-ClientSize.Height;
                    ClientSize=new Size(ClientSize.Width,Math.Min(Math.Max(ClientSize.Height,contentHeight+actions.Height),Math.Max(1,area.Height-24-nonClient)));
                    FitToWorkingArea(area);
                }
            }
            finally{arranging=false;}
        }

        static void ConvertBuiltInLabels(Control root)
        {
            foreach (Control control in root.Controls)
            {
                if (control is Label || control is Button || control is CheckBox)
                    control.Text = Xiongxin.OWTS.Localization.UiLanguage.ToTraditional(control.Text);
                // Latitude and longitude input values are never converted.
                if (control.HasChildren) ConvertBuiltInLabels(control);
            }
        }

        bool Valid()
        {
            double lat, lon;
            if (!agreement.Checked || !double.TryParse(latitude.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lat) ||
                !double.TryParse(longitude.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lon) ||
                double.IsNaN(lat) || double.IsInfinity(lat) || double.IsNaN(lon) || double.IsInfinity(lon) || lat < -90 || lat > 90 || lon < -180 || lon > 180) return false;
            Latitude = Math.Round(lat, 3, MidpointRounding.AwayFromZero);
            Longitude = Math.Round(lon, 3, MidpointRounding.AwayFromZero);
            return true;
        }
    }
}
