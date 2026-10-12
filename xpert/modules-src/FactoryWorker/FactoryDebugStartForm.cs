using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    // Navigation only. This window never enumerates devices or constructs a backend.
    internal sealed class FactoryDebugStartForm : Form
    {
        internal enum Workspace { None, Automatic, Manual, Full }
        internal Workspace Selected { get; private set; }
        private readonly Button automatic, manual, full;
        internal FactoryDebugStartForm()
        {
            Text = "雄新电力科技 · 出厂测试程序 V1.0.30 调试入口";
            Font = new Font("Microsoft YaHei UI", 10);
            Size = new Size(960, 675); MinimumSize = new Size(900, 650);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(239,244,247);
            using(var stream=GetType().Assembly.GetManifestResourceStream("Factory.Brand.ico")) Icon=new Icon(stream);
            var root=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(24), ColumnCount=1, RowCount=8 };
            foreach(int height in new[]{70,40,68,68,68,108,64}) root.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); Controls.Add(root);
            var heading=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2 };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,235));
            heading.Controls.Add(new Label { Text="出厂调试程序", Dock=DockStyle.Fill, Font=new Font(Font.FontFamily,23,FontStyle.Bold), TextAlign=ContentAlignment.MiddleLeft });
            using(var stream=GetType().Assembly.GetManifestResourceStream("Factory.Brand.Logo"))
                heading.Controls.Add(new PictureBox { Image=new Bitmap(stream), Dock=DockStyle.Fill, SizeMode=PictureBoxSizeMode.Zoom },1,0);
            root.Controls.Add(heading,0,0);
            root.Controls.Add(new Label { Text="V1.0.30 · BUSY分类 · 轮间等待 · 有界光补发", Dock=DockStyle.Fill },0,1);
            automatic=Option("自动单次调试  ·  0～30 kV（默认5 kV）", "三路 AI 趋势、先布置两卡、关断诊断；独立过压上限30 kV。",Workspace.Automatic);
            manual=Option("逐项手动调试  ·  ≤5 kV", "手动 HV ON、指示灯、HV UP、布置与 HV OFF、光触发采集。",Workspace.Manual);
            full=Option("完整出厂与校准页面  ·  本版主入口", "AI4窗口闭环、三路监测；四波形/TF、校准；量程弹窗仅修改下一轮。",Workspace.Full);
            root.Controls.Add(full,0,2); root.Controls.Add(automatic,0,3); root.Controls.Add(manual,0,4);
            root.Controls.Add(new Label { Dock=DockStyle.Fill, Padding=new Padding(0,12,0,0), Text=
                "非零自动流程\n首次有效达压停止递增AO → 布置两卡并计时（切换监测 AI）\n→ 布置调用通过且开始起满 500 ms → HV OFF / AO=0 → 光触发 1.5 秒。\n异常先关断并只读观察残压；不自动重连、重加压或发光。" },0,5);
            root.Controls.Add(new Label { Dock=DockStyle.Fill, ForeColor=Color.Maroon, Text=
                "联机前关闭占用采集卡的 DMC、LabVIEW 和旧版程序，核对比例与接线。\n本版完成离线验证，未作实机验收；程序通过和 P0.4 高均不代表设备已放电。" },0,6);
            var actions=new FlowLayoutPanel { Dock=DockStyle.Fill };
            var guide=new Button { Text="打开调试说明", AutoSize=true, Height=34 };
            guide.Click+=(s,e)=>OpenGuide(); actions.Controls.Add(guide);
            var cancel=new Button { Text="退出", AutoSize=true, Height=34, DialogResult=DialogResult.Cancel };
            actions.Controls.Add(cancel); CancelButton=cancel; root.Controls.Add(actions,0,7);
        }
        private Button Option(string title,string detail,Workspace selection)
        {
            var b=new Button { Text=title+"\n"+detail, Dock=DockStyle.Fill, TextAlign=ContentAlignment.MiddleLeft,
                Padding=new Padding(12,0,0,0), FlatStyle=FlatStyle.Flat, BackColor=Color.White, Margin=new Padding(0,0,0,8) };
            b.Click+=(s,e)=>{Selected=selection;DialogResult=DialogResult.OK;Close();}; return b;
        }
        private void OpenGuide()
        {
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"V1.0.30使用与测试说明.txt");
            try { if(!File.Exists(path)) throw new FileNotFoundException("说明文件未找到，请从完整交付目录打开程序。",path);
                Process.Start(new ProcessStartInfo(path){UseShellExecute=true}); }
            catch(Exception ex) {MessageBox.Show(this,ex.Message,"说明文件",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        }
        internal void Smoke(string directory)
        {
            Directory.CreateDirectory(directory);
            if(Selected!=Workspace.None || Icon==null || !automatic.Enabled || !manual.Enabled || !full.Enabled)
                throw new Exception("Debug entry default state invalid.");
            using(var bitmap=new Bitmap(Width,Height)){DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));bitmap.Save(Path.Combine(directory,"debug-entry.png"));}
            File.WriteAllText(Path.Combine(directory,"debug-entry-test.txt"),"R28 entry: PASS; Selected=None; no automatic action; HardwareAccessed=False\r\n");
        }
    }
}
