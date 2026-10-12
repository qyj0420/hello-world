using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.CalibrationStorage;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class LegacyCalibrationImportDialog : Form
    {
        readonly string project,phase,pico,art;readonly bool firstArrival,zh;readonly Action validate;
        readonly ComboBox candidates;readonly TextBox details;readonly CheckBox confirmation;readonly Button apply;readonly Label status;
        bool saving;Icon ownedIcon;
        string candidateReadFailures=String.Empty;
        internal LegacyCalibrationBinding AcceptedBinding {get;private set;}
        internal LegacyCalibrationBinding PersistedBinding {get;private set;}
        internal LegacyCalibrationImportDialog(string project,string phase,string pico,string art,bool firstArrival,bool zh,Action validate)
        {
            this.project=project;this.phase=phase;this.pico=pico;this.art=art;this.firstArrival=firstArrival;this.zh=zh;this.validate=validate??(()=>{});
            Text=T("确认使用旧校准","Confirm earlier calibration");StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;MinimizeBox=false;MaximizeBox=false;
            AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(700,545);MinimumSize=new Size(630,515);Font=new Font("Microsoft YaHei UI",9.5f);ownedIcon=AppTheme.LoadIcon();Icon=ownedIcon;
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=1,RowCount=6};
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,43));Controls.Add(layout);
            layout.Controls.Add(new Label{AutoSize=true,MaximumSize=new Size(640,0),Text=T("当前项目没有适用的新校准记录。可以核对并确认旧记录中的校准系数和参考波速，或重新进行校准。","No applicable new calibration was found. Review an earlier coefficient and reference propagation speed, or perform a new calibration."),Dock=DockStyle.Fill},0,0);
            candidates=new ComboBox{Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,Enabled=false,Name="LegacyCalibrationCandidates"};layout.Controls.Add(candidates,0,1);
            details=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.White,Name="LegacyCalibrationDetails"};layout.Controls.Add(details,0,2);
            confirmation=new CheckBox{Dock=DockStyle.Fill,Checked=false,Enabled=false,Name="LegacyCalibrationOperatorConfirmation",Text=T("我已核对：此校准系数和参考波速适用于当前设备、接线与测量回路。","I have checked that this coefficient and reference propagation speed apply to the current devices, wiring and measurement circuit.")};layout.Controls.Add(confirmation,0,3);
            status=new Label{AutoSize=true,MaximumSize=new Size(640,0),Dock=DockStyle.Fill,Text=T("正在读取当前项目的旧校准…","Reading earlier project calibrations…")};layout.Controls.Add(status,0,4);
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false};layout.Controls.Add(buttons,0,5);
            apply=new HVX.Presentation.R2RoundedButton{Text=T("确认使用此系数","Confirm and use"),Width=160,Height=34,Enabled=false,Name="ConfirmLegacyCalibration"};
            var cancel=new HVX.Presentation.R2RoundedButton{Text=T("返回","Back"),Width=90,Height=34,DialogResult=DialogResult.Cancel};
            var calibrate=new HVX.Presentation.R2RoundedButton{Text=T("重新校准","New calibration"),Width=130,Height=34,DialogResult=DialogResult.Retry};
            buttons.Controls.Add(apply);buttons.Controls.Add(calibrate);buttons.Controls.Add(cancel);CancelButton=cancel;
            candidates.SelectedIndexChanged+=(s,e)=>ShowCandidate();confirmation.CheckedChanged+=(s,e)=>apply.Enabled=!saving&&PersistedBinding==null&&confirmation.Checked&&candidates.SelectedItem is CandidateItem;
            apply.Click+=async(s,e)=>await ConfirmSelectedAsync();Shown+=async(s,e)=>await LoadCandidatesAsync();
            FormClosing+=(s,e)=>{if(saving)e.Cancel=true;};
        }
        string T(string chinese,string english){return zh?chinese:english;}
        sealed class CandidateItem {internal LegacyCalibrationCandidate Value;public override string ToString(){return Path.GetFileName(Value.SourceRelativePath);}}
        async Task LoadCandidatesAsync()
        {
            try {
                var values=await Task.Run(()=> {
                    var list=new List<CandidateItem>();var failures=new List<string>();int count=0;
                    foreach(string file in Directory.EnumerateFiles(project,"*.cal",SearchOption.TopDirectoryOnly)) {
                        if(++count>256)throw new InvalidDataException("项目内旧校准过多，请先整理文件。");
                        try{list.Add(new CandidateItem{Value=LegacyCalibrationBinding.ReadCandidate(project,file)});}
                        catch(Exception error)when(error is IOException||error is InvalidDataException||error is InvalidOperationException||error is ArgumentException){
                            failures.Add(Path.GetFileName(file)+": "+error.Message);
                            new Diagnostics().Error("LegacyCalibration","Calibration candidate rejected: "+Path.GetFileName(file),error);
                        }
                    }
                    candidateReadFailures=String.Join(Environment.NewLine,failures);
                    return list.OrderBy(item=>item.Value.SourcePhase==phase?0:item.Value.SourcePhase=="ALL"?1:2).ThenBy(item=>item.Value.SourceRelativePath,StringComparer.OrdinalIgnoreCase).ToArray();
                });
                if(IsDisposed)return;validate();candidates.Items.AddRange(values);candidates.Enabled=values.Length>0;
                status.Text=values.Length>0?T("请选择记录并核对下列信息。原文件将保留。","Select a record and review its details. The original file will be retained."):
                    T("未找到可读取且系数有效的旧校准，请重新校准。","No readable earlier calibration with a valid coefficient was found. Perform a new calibration.");
                if(values.Length>0)candidates.SelectedIndex=0;
                else details.Text=T("请完成校准并保存后再试。原校准文件保持不变。","Complete and save calibration, then try again. Original calibration files are unchanged.");
            }catch(Exception error){if(!IsDisposed){status.Text=T("旧校准读取失败，请核对文件或重新校准。","Earlier calibrations could not be read. Check the files or perform a new calibration.");}new Diagnostics().Error("LegacyCalibration","Could not list earlier calibration candidates.",error);}
        }
        void ShowCandidate()
        {
            confirmation.Checked=false;var item=candidates.SelectedItem as CandidateItem;confirmation.Enabled=item!=null;if(item==null){details.Clear();return;}
            var c=item.Value;double current;
            try{validate();current=LegacyCalibrationBinding.CurrentLengthForProject(project,phase);}
            catch(Exception error){confirmation.Enabled=false;status.Text=T("请重新校准并保存后再试。","Perform and save a new calibration, then try again.");new Diagnostics().Error("LegacyCalibration","Candidate context is not applicable.",error);return;}
            string n(double value){return value.ToString("0.########",CultureInfo.InvariantCulture);}
            details.Text=String.Join(Environment.NewLine,new[]{
                T("来源文件：","Source file: ")+c.SourceRelativePath,
                T("记录相别 / 档位：","Recorded phase / level: ")+c.SourcePhase+" / "+c.Level,
                T("适用相别：","Confirm for phase: ")+phase,
                T("校准系数：","Calibration coefficient: ")+n(c.CoefficientPcPerV)+" pC/V",
                T("旧记录波速：","Recorded propagation speed: ")+n(c.HistoricalVelocityMPerUs)+" m/μs"+T("（作为定位参考，未经本次重新测量）"," (reference only; not remeasured now)"),
                T("旧记录电缆长度：","Recorded cable length: ")+c.HistoricalLengthM.ToString("0.0",CultureInfo.InvariantCulture)+" m",
                T("当前项目电缆长度：","Current project cable length: ")+current.ToString("0.0",CultureInfo.InvariantCulture)+" m",
                "",T("旧记录的设备身份、采集质量及滤波过程未经本次核验。","The earlier device identity, acquisition quality and filtering have not been verified in this session."),
                T("确认仅记录此系数和参考波速对当前回路的适用性；不会修改旧文件、重算旧长度或生成新的采集记录。","Confirmation records applicability to the current circuit only. It does not change the original file or its length, or create a new acquisition record."),
                T("加压前的设备、急停及现场安全检查仍须执行。","Device, emergency-stop and on-site safety checks are still required before applying voltage.")});
        }
        async Task ConfirmSelectedAsync()
        {
            var item=candidates.SelectedItem as CandidateItem;if(saving||PersistedBinding!=null||item==null||!confirmation.Checked)return;
            saving=true;apply.Enabled=false;candidates.Enabled=false;confirmation.Enabled=false;status.Text=T("正在保存适用确认…","Saving applicability confirmation…");
            try {
                validate();string acknowledgement=details.Text+Environment.NewLine+confirmation.Text;
                // The source and project are verified again inside Confirm. The
                // UI identity is checked immediately before and after this write.
                PersistedBinding=await Task.Run(()=>LegacyCalibrationBinding.Confirm(project,item.Value,phase,pico,art,firstArrival,true,acknowledgement));
                validate();AcceptedBinding=PersistedBinding;saving=false;DialogResult=DialogResult.OK;Close();
            }catch(Exception error){AcceptedBinding=null;saving=false;
                if(PersistedBinding!=null){
                    status.Text=T("确认记录已保存到原项目和设备组合。当前项目或设备状态已变化，本页面未应用；请返回后重新核对。",
                        "The confirmation was saved for the original project and device pair. The current context has changed, so it was not applied here. Return and review the current project and devices.");
                    new Diagnostics().Error("LegacyCalibration","Legacy applicability was saved at "+PersistedBinding.BindingPath+" but the current context no longer permits applying it.",error);
                    candidates.Enabled=false;confirmation.Checked=false;confirmation.Enabled=false;apply.Enabled=false;
                }else{
                    status.Text=T("确认未完成。请重新核对项目、设备和所选文件。","Confirmation was not completed. Recheck the project, devices and selected file.");
                    new Diagnostics().Error("LegacyCalibration","Could not save explicit legacy applicability.",error);candidates.Enabled=true;confirmation.Enabled=true;confirmation.Checked=false;
                }
            }
        }
        protected override void Dispose(bool disposing){if(disposing&&ownedIcon!=null){ownedIcon.Dispose();ownedIcon=null;}base.Dispose(disposing);}
    }
}
