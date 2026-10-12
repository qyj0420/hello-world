using System;
using System.Drawing;
using System.Windows.Forms;
using Xiongxin.OWTS.Localization;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class ProjectCreationGuideDialog : Form
    {
        readonly Image[] pages;
        readonly PictureBox picture;
        readonly Label caption,description;
        readonly Button previous,next;
        readonly CheckBox suppress;
        readonly TableLayoutPanel footer;
        readonly int language;
        Icon ownedIcon;
        bool resourcesReleased;
        bool measuring;
        int index;
        internal bool DoNotShowAgain { get { return suppress.Checked; } }
        internal int PageIndex { get { return index; } }
        internal ProjectCreationGuideDialog(Image[] pages,int language,Font font)
        {
            if(pages==null||pages.Length!=3)throw new ArgumentException("Three current project guide images are required.");
            this.pages=pages;this.language=language;
            Text=L("新建项目操作引导","New project guide");Name="ProjectCreationGuideDialog";
            AutoScaleMode=AutoScaleMode.None;Font=font;StartPosition=FormStartPosition.CenterParent;
            ClientSize=new Size(1100,760);MinimumSize=new Size(720,520);
            BackColor=AppTheme.Surface;ForeColor=AppTheme.Ink;
            ownedIcon=AppTheme.LoadIcon();Icon=ownedIcon;ShowInTaskbar=false;
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(16)};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            caption=new Label{AutoSize=true,Dock=DockStyle.Fill,Padding=new Padding(0,0,0,10)};
            picture=new PictureBox{Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.White,Margin=new Padding(0)};
            description=new Label{AutoSize=true,Dock=DockStyle.Fill,Padding=new Padding(0,10,0,10)};
            footer=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,RowCount=1,AutoSize=true,Margin=Padding.Empty};
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for(int i=0;i<3;i++)footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            suppress=new CheckBox{Name="SuppressProjectCreationGuide",Text=L("不再提示","Do not show again"),AutoSize=true,Anchor=AnchorStyles.Left};
            previous=AppTheme.Button(L("上一张","Previous"),false);previous.Name="GuidePrevious";previous.AutoSize=true;
            next=AppTheme.Button(L("下一张","Next"),false);next.Name="GuideNext";next.AutoSize=true;
            var close=AppTheme.Button(L("关闭","Close"),false);close.AutoSize=true;close.DialogResult=DialogResult.OK;close.Name="GuideClose";
            close.Click+=delegate{Close();};
            previous.Click+=delegate{SelectPage(index-1);};next.Click+=delegate{SelectPage(index+1);};
            footer.Controls.Add(suppress,0,0);footer.Controls.Add(previous,1,0);footer.Controls.Add(next,2,0);footer.Controls.Add(close,3,0);
            layout.Controls.Add(caption,0,0);layout.Controls.Add(picture,0,1);layout.Controls.Add(description,0,2);layout.Controls.Add(footer,0,3);Controls.Add(layout);
            CancelButton=close;Resize+=delegate{MeasureMinimum();};FontChanged+=delegate{MeasureMinimum();};
            SelectPage(0);
        }
        string L(string zh,string en){return UiLanguage.Text(language,zh,en);}
        internal void SelectPage(int page)
        {
            if(page<0||page>=pages.Length)return;
            index=page;picture.Image=pages[page];previous.Enabled=page>0;next.Enabled=page+1<pages.Length;
            string[] titles={L("必填项、端头与接头","Required fields, terminations and joints"),L("加压列表","Pressure plan"),L("环境与北斗","Environment and GNSS")};
            string[] text={
                L("填写带 * 的始端/终端名称、相电压及所选负载对应的长度。在模型上方分别选择两端类型；在“接头与本体”填写接头位置和两侧材料，添加或选中编辑。核对后保存项目。","Fill the required start/end names, phase voltage and lengths for the selected load. Select both termination types above the cable model. Add or select and edit joints, positions and materials in Joints and body. Check and save the project."),
                L("按项目选择加压模式，核对每一步的 U0 倍数、次数和计算电压，使用编辑入口调整计划。加压测试中的计划应与此列表逐项对应。","Select the pressure profile for this project. Check each step's U0 multiplier, repetitions and calculated voltage; use the editor to adjust the plan. Plan mode in pressure testing follows these steps."),
                L("温湿度和天气可按现场填写；北斗定位、海拔、时间和来源以实际采集结果为准。连接设备后使用“读取硬件”；空白或未定位不代表已成功采集。","Enter site temperature, humidity and weather as appropriate. GNSS position, altitude, timestamp and source come from actual acquisition. Use Read hardware after connecting; blank fields or no fix do not indicate a successful reading.")};
            caption.Text=(page+1)+" / 3  ·  "+titles[page];
            description.MaximumSize=new Size(Math.Max(160,ClientSize.Width-32),0);
            description.Text=text[page]+Environment.NewLine+L("图中为当前版本生成的默认示例界面，未采集设备数据。","Images show current-version default examples; no device data was acquired.");
            MeasureMinimum();
        }
        void MeasureMinimum()
        {
            if(measuring||description==null||footer==null)return;
            measuring=true;
            try
            {
                int width=Math.Max(160,ClientSize.Width-32);description.MaximumSize=new Size(width,0);
                int height=caption.GetPreferredSize(new Size(width,0)).Height+description.GetPreferredSize(new Size(width,0)).Height+
                    footer.GetPreferredSize(new Size(width,0)).Height+220;
                MinimumSize=new Size(720,Math.Max(520,height+Height-ClientSize.Height));
            }
            finally{measuring=false;}
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing&&!resourcesReleased){resourcesReleased=true;picture.Image=null;foreach(var page in pages)if(page!=null)page.Dispose();if(ownedIcon!=null){var icon=ownedIcon;ownedIcon=null;Icon=null;icon.Dispose();}}
            base.Dispose(disposing);
        }
    }
}
