using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace HVX.Controls
{
    // Reference metadata, not a replacement for the licensed standard or a
    // verified clause-specific acceptance rule. Never infer an IR pass limit.
    internal static class InsulationReferenceRules
    {
        internal sealed class Profile
        {
            internal string Code, Title, ScopeZh, ScopeEn, StatusZh, StatusEn, Source;
            public override string ToString() { return Title; }
        }
        internal static readonly Profile[] Profiles = {
            new Profile {
                Code="DLT596-2021", Title="DL/T 596-2021",
                ScopeZh="电力设备预防性试验规程：运行中交流电力设备的预防性试验，包含电力电缆。须另行核对电缆类别与适用条款。",
                ScopeEn="Preventive tests of AC power equipment, including power cables. Confirm the cable category and applicable clause separately.",
                StatusZh="国家能源局2023版现行目录列入本版并列明替代1996版；2025年官方翻译项目仍列本版。该目录不是2026年的实时有效性证明。",
                StatusEn="Listed in the NEA 2023 current-standard catalogue, replacing the 1996 edition; also listed in the official 2025 translation programme. This is not a live 2026 status certification.",
                Source="https://prpq.nea.gov.cn/uploads/file1/20240911/66e1071abaa30.pdf"
            },
            new Profile {
                Code="IEEE400-2023", Title="IEEE 400-2023",
                ScopeZh="额定电压5 kV及以上屏蔽电力电缆系统绝缘的现场试验与评价指南。它是现场方法指南，不能据其标题推导绝缘电阻合格值。",
                ScopeEn="Field testing and insulation evaluation of shielded power cable systems rated 5 kV and above. Its scope does not establish an insulation-resistance acceptance value for these entries.",
                StatusZh="IEEE官方目录：Active Standard，2023版替代2012版。元数据核对日期：2026-09-28。",
                StatusEn="IEEE catalogue: Active Standard; the 2023 edition supersedes 2012. Metadata checked on 2026-09-28.",
                Source="https://standards.ieee.org/ieee/400/7618/"
            },
            new Profile {
                Code="IEC60502-2-2014-AMD1-2024-COR1-2026", Title="IEC 60502-2:2014+A1:2024 / COR1:2026",
                ScopeZh="6至30 kV挤包绝缘电力电缆的结构、尺寸与试验要求；含2026-07勘误。产品试验要求不能直接作为现场两次绝缘电阻记录的合格阈值。",
                ScopeEn="Construction, dimensions and tests for extruded-insulation power cables from 6 to 30 kV, with July 2026 corrigendum. Product-test requirements are not a direct pass limit for these two field IR measurements.",
                StatusZh="IEC官方合订版：3.1版，2014版+2024修订，含2026-07勘误。元数据核对日期：2026-09-28。",
                StatusEn="IEC consolidated edition 3.1: 2014 plus Amendment 1:2024, including the July 2026 corrigendum. Metadata checked on 2026-09-28.",
                Source="https://webstore.iec.ch/en/publication/95394"
            }
        };
        internal enum ReadingKind { Missing, Exact, LowerBound, Invalid }
        internal struct Reading
        {
            internal ReadingKind Kind;
            internal double Value;
        }
        internal static Profile Find(string code) { return Profiles.FirstOrDefault(p=>string.Equals(p.Code,code,StringComparison.Ordinal)); }
        internal static string DefaultCode(int language) { return language==0?"DLT596-2021":"IEEE400-2023"; }
        internal static Reading Parse(string text)
        {
            string value=(text??string.Empty).Trim();
            string upper=value.ToUpperInvariant();
            if(value.Length==0||value=="—"||value=="-"||value=="未测"||value=="未测量"||value=="未測"||value=="未測量"||upper=="N/A"||upper=="NA"||upper=="NOT MEASURED"||upper=="NOT TESTED")
                return new Reading {Kind=ReadingKind.Missing};
            bool lower=value.StartsWith(">",StringComparison.Ordinal)||value.StartsWith("≥",StringComparison.Ordinal);
            if(lower) value=value.Substring(1).TrimStart('=',' ','\t');
            foreach(string unit in new[]{"MΩ","MΩ","MOHM","兆欧","兆歐"})
                if(value.EndsWith(unit,StringComparison.OrdinalIgnoreCase)) { value=value.Substring(0,value.Length-unit.Length).Trim();break; }
            double number;
            // Explicit MΩ field. No guesses about decimal/thousands separators,
            // no conversions from unlabelled GΩ and no infinity-as-pass shortcut.
            if(!double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out number)||double.IsNaN(number)||double.IsInfinity(number)||number<0)
                return new Reading {Kind=ReadingKind.Invalid};
            return new Reading {Kind=lower?ReadingKind.LowerBound:ReadingKind.Exact,Value=number};
        }
        internal static string PairAdvice(string phase,string first,string second,bool comparable,bool english)
        {
            Reading a=Parse(first),b=Parse(second);
            string prefix=phase+": ";
            if(a.Kind==ReadingKind.Invalid||b.Kind==ReadingKind.Invalid)
                return prefix+(english?"Check the entry: use a non-negative MΩ value, >range, or Not measured.":"请检查输入：填写非负MΩ数值、>量程上限或未测。");
            if(a.Kind==ReadingKind.Missing||b.Kind==ReadingKind.Missing)
                return prefix+(english?"Measurement record incomplete; no comparison.":"测量记录未完整，暂不比较。");
            if(a.Kind==ReadingKind.LowerBound||b.Kind==ReadingKind.LowerBound)
                return prefix+(english?"A range-limit reading is not an exact value; no percentage comparison.":"包含超量程下限记录，不作为准确数值计算变化比例。");
            if(a.Value==0||b.Value==0)
                return prefix+(english?"Zero recorded: review the instrument range, connections and measurement record.":"存在0读数，请复核仪表量程、接线和测量记录。");
            if(!comparable)
                return prefix+(english?"Both measurements recorded. Confirm comparable measurement conditions before comparing.":"两次测量已记录，请确认测量条件一致后再比较。");
            double change=(b.Value/a.Value-1)*100;
            if(double.IsNaN(change)||double.IsInfinity(change))
                return prefix+(english?"Values differ beyond the comparison range; review manually.":"数值差异超出比较范围，请人工复核。");
            string trend=change>0?(english?"increased":"增加"):change<0?(english?"decreased":"减少"):(english?"unchanged":"无变化");
            return prefix+(english?"Second vs first: ":"二次相对首次：")+trend+
                (change==0?string.Empty:" "+Math.Abs(change).ToString("0.##",CultureInfo.InvariantCulture)+"%")+
                (english?" (numerical change only, not an insulation verdict).":"（仅为数值变化，不代表绝缘性能判定）。");
        }
    }

    public partial class ProjectCreationControl
    {
        string insulationReferenceProfile=string.Empty;
        bool insulationConditionsComparable;
        bool updatingInsulationGuidance;
        TableLayoutPanel insulationGuidancePanel;
        ComboBox insulationReferenceCombo;
        Label insulationReferenceCaption,insulationGuidanceStatus;
        CheckBox insulationComparableCheck;
        ProjectCreationButton insulationDetailsButton;

        [Browsable(false)]
        public string InsulationReferenceProfile
        {
            get { return string.IsNullOrEmpty(insulationReferenceProfile)?InsulationReferenceRules.DefaultCode(languageIndex):insulationReferenceProfile; }
            set {
                string code=(value??string.Empty).Trim();
                if(code.Length!=0&&InsulationReferenceRules.Find(code)==null) throw new ArgumentException("Unknown insulation reference profile.","value");
                insulationReferenceProfile=code;
                RefreshInsulationGuidance();
            }
        }
        [Browsable(false)]
        public bool InsulationConditionsComparable
        {
            get { return insulationConditionsComparable; }
            set { insulationConditionsComparable=value;RefreshInsulationGuidance(); }
        }
        internal Control BuildInsulationGuidance()
        {
            if(insulationGuidancePanel!=null)return insulationGuidancePanel;
            insulationGuidancePanel=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty,Padding=new Padding(4),Font=ProjectCreationTheme.UiFont(9.5f),BackColor=ProjectCreationTheme.Surface};
            insulationGuidancePanel.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            insulationGuidancePanel.RowStyles.Add(new RowStyle(SizeType.Absolute,32));
            insulationGuidancePanel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var top=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            insulationReferenceCaption=new Label {AutoSize=true,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(0,0,8,0),ForeColor=ProjectCreationTheme.Ink};
            insulationReferenceCombo=new ComboBox {Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,Font=ProjectCreationTheme.UiFont(9.5f),Margin=new Padding(0,2,8,2)};
            insulationReferenceCombo.Items.AddRange(InsulationReferenceRules.Profiles);
            insulationReferenceCombo.DropDownWidth=520;
            insulationDetailsButton=new ProjectCreationButton {AutoSize=false,Width=150,Dock=DockStyle.Fill,Margin=Padding.Empty};
            top.Controls.Add(insulationReferenceCaption,0,0);top.Controls.Add(insulationReferenceCombo,1,0);top.Controls.Add(insulationDetailsButton,2,0);
            insulationComparableCheck=new CheckBox {Dock=DockStyle.Fill,AutoSize=false,Margin=Padding.Empty,ForeColor=ProjectCreationTheme.Ink};
            insulationGuidanceStatus=new Label {Dock=DockStyle.Fill,AutoEllipsis=true,UseMnemonic=false,TextAlign=ContentAlignment.MiddleLeft,Margin=Padding.Empty,ForeColor=ProjectCreationTheme.SecondaryInk};
            insulationGuidancePanel.Controls.Add(top,0,0);insulationGuidancePanel.Controls.Add(insulationComparableCheck,0,1);insulationGuidancePanel.Controls.Add(insulationGuidanceStatus,0,2);
            insulationReferenceCombo.SelectedIndexChanged+=delegate {
                if(updatingInsulationGuidance)return;
                var selected=insulationReferenceCombo.SelectedItem as InsulationReferenceRules.Profile;
                if(selected==null)return;
                insulationReferenceProfile=selected.Code;MarkDirty();RefreshInsulationGuidance();
            };
            insulationComparableCheck.CheckedChanged+=delegate {
                if(updatingInsulationGuidance)return;
                insulationConditionsComparable=insulationComparableCheck.Checked;MarkDirty();RefreshInsulationGuidance();
            };
            insulationDetailsButton.Click+=delegate {ShowInsulationReferenceDetails();};
            foreach(TextBox input in InsulationInputs())if(input!=null)input.TextChanged+=delegate {RefreshInsulationGuidance();};
            RefreshInsulationGuidance();
            return insulationGuidancePanel;
        }
        IEnumerable<TextBox> InsulationInputs()
        {
            return new[]{insulationA1Text,insulationA2Text,insulationB1Text,insulationB2Text,insulationC1Text,insulationC2Text};
        }
        internal void RefreshInsulationGuidance()
        {
            if(insulationGuidancePanel==null||updatingInsulationGuidance)return;
            updatingInsulationGuidance=true;
            try {
                bool english=!Xiongxin.OWTS.Localization.UiLanguage.IsChinese(languageIndex);
                insulationReferenceCaption.Text=english?"Reference standard":"参考标准";
                insulationReferenceCombo.SelectedItem=InsulationReferenceRules.Find(InsulationReferenceProfile);
                insulationComparableCheck.Checked=insulationConditionsComparable;
                insulationComparableCheck.Text=english?"Same test voltage, duration, temperature and connections for both measurements":"两次测量的测试电压、时长、温度及接线条件一致";
                insulationDetailsButton.Text=english?"Reference details":"参考说明";
                bool invalid=InsulationInputs().Where(t=>t!=null).Any(t=>InsulationReferenceRules.Parse(t.Text).Kind==InsulationReferenceRules.ReadingKind.Invalid);
                insulationGuidanceStatus.ForeColor=invalid?Color.DarkOrange:ProjectCreationTheme.SecondaryInk;
                insulationGuidanceStatus.Text=invalid?(english?"Check insulation entries. Open Reference details for guidance.":"绝缘输入有待核对，请打开“参考说明”查看。")
                    :(english?"Independent measurements · MΩ · Manual review required; no automatic pass/fail.":"独立测量 · 单位MΩ · 需人工复核，不自动判定合格/不合格。");
                if (languageIndex == 18)
                    foreach (Control builtIn in new Control[] { insulationReferenceCaption, insulationComparableCheck, insulationDetailsButton, insulationGuidanceStatus })
                        builtIn.Text = Xiongxin.OWTS.Localization.UiLanguage.ToTraditional(builtIn.Text);
                insulationDetailsButton.Width=Math.Max(142,TextRenderer.MeasureText(insulationDetailsButton.Text,insulationDetailsButton.Font).Width+22);
                insulationGuidancePanel.RowStyles[0].Height=Math.Max(38,insulationReferenceCombo.PreferredSize.Height+4);
                insulationGuidancePanel.RowStyles[1].Height=Math.Max(32,insulationComparableCheck.Font.Height+6);
            }
            finally {updatingInsulationGuidance=false;}
        }
        string InsulationReferenceDetails()
        {
            bool english=!Xiongxin.OWTS.Localization.UiLanguage.IsChinese(languageIndex);
            var profile=InsulationReferenceRules.Find(InsulationReferenceProfile);
            var text=new StringBuilder();
            text.AppendLine(profile.Title).AppendLine().AppendLine(english?profile.ScopeEn:profile.ScopeZh).AppendLine().AppendLine(english?profile.StatusEn:profile.StatusZh).AppendLine();
            text.AppendLine(english?"First and second measurements are independent records in MΩ, not 15 s / 60 s readings or automatic before/after records. No absorption ratio is calculated.":"首次测量与二次测量是独立记录，单位MΩ，不是15s/60s读数，也不自动代表试验前后；不计算吸收比。").AppendLine();
            TextBox[] input=InsulationInputs().ToArray();
            for(int i=0;i<3;i++)text.AppendLine(InsulationReferenceRules.PairAdvice(english?"L"+(i+1):((char)('A'+i))+"相",input[i*2]==null?"":input[i*2].Text,input[i*2+1]==null?"":input[i*2+1].Text,insulationConditionsComparable,english));
            text.AppendLine().AppendLine(english?"No verified clause-specific resistance threshold is embedded. Confirm applicability, instrument range and measurement conditions against the licensed standard before issuing a conclusion.":"本功能未内置经核实的具体条文电阻阈值。出具结论前，请依合法标准原文确认适用条款、仪表量程和测量条件。");
            text.AppendLine().AppendLine(english?"Official reference (copy the address if needed):":"官方参考（可复制地址查阅）：").AppendLine(profile.Source);
            // This content contains only built-in guidance and calculated numbers;
            // original measurement strings are not inserted into the description.
            return languageIndex == 18 ? Xiongxin.OWTS.Localization.UiLanguage.ToTraditional(text.ToString()) : text.ToString();
        }
        void ShowInsulationReferenceDetails()
        {
            using(var dialog=new Form {Name="InsulationReferenceDialog",Text=Xiongxin.OWTS.Localization.UiLanguage.Text(languageIndex,"绝缘测量参考","Insulation measurement reference"),StartPosition=FormStartPosition.CenterParent,Size=new Size(880,680),MinimumSize=new Size(480,330),AutoScaleMode=AutoScaleMode.Dpi,AutoScaleDimensions=new SizeF(96,96),MaximizeBox=true,MinimizeBox=false,Font=Font,BackColor=ProjectCreationTheme.Surface})
            {
                ProjectDialogBrand.Apply(dialog);
                var content=new TextBox {Name="InsulationReferenceText",Multiline=true,ReadOnly=true,BorderStyle=BorderStyle.None,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical,WordWrap=true,Text=InsulationReferenceDetails(),BackColor=ProjectCreationTheme.Surface,ForeColor=ProjectCreationTheme.Ink};
                var close=new ProjectCreationButton {Name="CloseInsulationReference",Text=Xiongxin.OWTS.Localization.UiLanguage.Text(languageIndex,"关闭","Close"),Font=dialog.Font,Dock=DockStyle.Bottom,Height=Math.Max(40,Font.Height+14),DialogResult=DialogResult.OK};
                bool arranging=false;Action arrange=delegate
                {
                    if(arranging||dialog.IsDisposed)return;arranging=true;
                    try{close.Font=dialog.Font;close.Height=Math.Max(40,dialog.Font.Height+18);ProjectDialogLayout.FitToWorkingArea(dialog,Screen.FromControl(FindForm()??dialog).WorkingArea,new Size(480,330));}
                    finally{arranging=false;}
                };
                dialog.Shown+=delegate{arrange();};dialog.FontChanged+=delegate{arrange();};dialog.SizeChanged+=delegate{arrange();};
                dialog.Padding=new Padding(16);dialog.Controls.Add(content);dialog.Controls.Add(close);dialog.AcceptButton=close;dialog.CancelButton=close;dialog.ShowDialog(this);
            }
        }
    }
}
