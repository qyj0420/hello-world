using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Localization;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class LocalizationCForm:Form
    {
        readonly int languageIndex;
        readonly TextBox loc=new TextBox();
        readonly NumericUpDown length=new NumericUpDown{Minimum=0,Maximum=1000000,DecimalPlaces=2,Value=0},velocity=new NumericUpDown{Minimum=0,Maximum=299.79M,DecimalPlaces=2,Value=0};
        readonly ComboBox phase=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList};
        readonly ComboBox algorithm=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList};
        readonly CheckBox reference=new CheckBox{AutoSize=true};
        readonly Label status=new Label{Dock=DockStyle.Fill,AutoEllipsis=true,ForeColor=AppTheme.Muted};
        readonly DataGridView pairs=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,RowHeadersVisible=false,BackgroundColor=Color.White,BorderStyle=BorderStyle.None};
        readonly Button run=AppTheme.Button("",true),cancel=AppTheme.Button("",false),export=AppTheme.Button("",false);
        CancellationTokenSource cancellation;string savedResult="";bool busy;
        string T(string zh,string en){return UiLanguage.Text(languageIndex,zh,en);}
        internal LocalizationCForm(int language)
        {
            languageIndex=language;Text=T("定位结果复核","Location cross-check");Font=AppTheme.Font(9,FontStyle.Regular);BackColor=AppTheme.Canvas;
            ClientSize=new Size(960,650);MinimumSize=new Size(650,500);StartPosition=FormStartPosition.CenterParent;AutoScaleMode=AutoScaleMode.Dpi;
            AppTheme.ApplyDialogIcon(this);AppTheme.FitDialogToWorkingArea(this);
            var grid=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=9,Padding=new Padding(16)};
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for(int i=0;i<9;i++)grid.RowStyles.Add(new RowStyle(i==7?SizeType.Percent:SizeType.AutoSize,i==7?100:0));
            var notice=new Label{AutoSize=true,Dock=DockStyle.Fill,ForeColor=AppTheme.Warning,Text=T("复核结果供比较，不覆盖原分析，也不自动计入已确认放电。交叉复核按默认提取设置最多处理100个对照脉冲；完整提示随导出保留。未找到候选位置不代表没有放电。","Cross-check results are for comparison: they do not overwrite or confirm original results. Cross-checking uses default extraction settings and up to 100 comparison pulses; export retains all notices. No candidate location does not mean no discharge.")};
            grid.Controls.Add(notice,0,0);grid.SetColumnSpan(notice,3);
            algorithm.Items.AddRange(new object[]{T("独立定位复核","Independent cross-check"),T("多方法交叉复核","Multiple-method cross-check")});algorithm.SelectedIndex=1;
            Row(grid,1,T("复核方法","Review method"),algorithm,null);
            Row(grid,2,T("定位记录","Location record"),loc,BrowseButton(loc,"LOC|*.loc"));
            Row(grid,3,T("电缆长度 / m","Cable length / m"),length,null);
            Row(grid,4,T("完整波速 / m/μs","Full velocity / m/μs"),velocity,null);
            phase.Items.AddRange(new object[]{"ALL","A","B","C"});phase.SelectedIndex=0;
            Row(grid,5,T("相别","Phase"),phase,null);
            reference.Text=T("已核对：定位参考面为被测电缆入口，连接线影响已正确处理","Verified: reference plane is the cable input and connecting-lead effects have been handled");
            grid.Controls.Add(reference,0,6);grid.SetColumnSpan(reference,3);
            foreach(string column in new[]{T("入射样本","Incident sample"),T("反射样本","Reflection sample"),T("候选位置 / m","Candidate / m"),T("状态","State")})pairs.Columns.Add(column,column);
            grid.Controls.Add(pairs,0,7);grid.SetColumnSpan(pairs,3);
            var footer=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true};
            run.Text=T("开始对照","Run comparison");cancel.Text=T("取消","Cancel");export.Text=T("导出对照结果","Export comparison");
            run.AutoSize=cancel.AutoSize=export.AutoSize=true;cancel.Enabled=export.Enabled=false;
            footer.Controls.AddRange(new Control[]{run,cancel,export});grid.Controls.Add(footer,0,8);grid.SetColumnSpan(footer,3);
            var statusPanel=new Panel{Dock=DockStyle.Bottom,Height=70,Padding=new Padding(16,8,16,8)};statusPanel.Controls.Add(status);Controls.Add(grid);Controls.Add(statusPanel);
            status.Text=T("请选择定位记录，并核对相别、电缆长度、波速及连接线设置。不同方法的结果分别保留，供您比较确认。","Select a location record and check the phase, cable length, velocity and connecting lead. Results from different methods are retained for your review.");
            run.Click+=async(s,e)=>await RunComparison();cancel.Click+=(s,e)=>{if(cancellation!=null)cancellation.Cancel();};export.Click+=(s,e)=>Export();
            FormClosing+=(s,e)=>{if(busy){e.Cancel=true;cancellation.Cancel();status.Text=T("正在取消，请稍候再关闭。","Cancelling; close after it settles.");}};
        }
        internal void SetInitialRecord(string path){
            // Reuse the selected record only. Do not infer phase or geometry
            // from a filename, a different row, or the global default settings.
            if(!String.IsNullOrWhiteSpace(path)&&File.Exists(path)&&String.Equals(Path.GetExtension(path),".loc",StringComparison.OrdinalIgnoreCase))loc.Text=Path.GetFullPath(path);
        }
        void Row(TableLayoutPanel grid,int row,string caption,Control input,Control action){input.Dock=DockStyle.Fill;grid.Controls.Add(new Label{Text=caption,AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(0,8,12,8)},0,row);grid.Controls.Add(input,1,row);if(action!=null)grid.Controls.Add(action,2,row);}
        Button BrowseButton(TextBox target,string filter){var button=AppTheme.Button(T("浏览…","Browse…"),false);button.AutoSize=true;button.Click+=(s,e)=>{using(var pick=new OpenFileDialog{Filter=filter,CheckFileExists=true})if(pick.ShowDialog(this)==DialogResult.OK)target.Text=pick.FileName;};return button;}
        async Task RunComparison()
        {
            if(busy)return;string pythonPath="",locPath=loc.Text,method=algorithm.SelectedIndex==0?"C":"D";double cable=(double)length.Value,speed=(double)velocity.Value;bool confirmed=reference.Checked;string selectedPhase=(string)phase.SelectedItem;
            cancellation=new CancellationTokenSource();busy=true;savedResult="";pairs.Rows.Clear();Toggle();
            status.Text=T("正在读取并核验记录…","Reading and checking the record…");
            try{
                var token=cancellation.Token;
                var context=await Task.Run(()=>{
                    string modules=EmbeddedModulePayload.ResolveModulesRoot(),sha;
                    var request=LocalizationCBridge.BuildRequest(modules,locPath,cable,speed,confirmed,selectedPhase,token,out sha,method);
                    return new{Modules=modules,Hash=sha,Request=request};
                },token);
                string output=await LocalizationCBridge.RunAsync(context.Modules,pythonPath,context.Request,token,elapsed=>{
                    if(!IsDisposed&&IsHandleCreated)try{BeginInvoke(new Action(()=>{if(busy)status.Text=T("正在对照，可取消。已用时 ","Comparing; cancellation available. Elapsed ")+elapsed;}));}catch(InvalidOperationException){ }
                });
                var prepared=await Task.Run(()=>{
                  var result=LocalizationCBridge.Serializer().DeserializeObject(output) as Dictionary<string,object>;
                  var rows=new List<object[]>();
                  var records=method=="C"?(object[])result["records"]:new object[]{new Dictionary<string,object>{{"pairs",result["candidates"]}}};
                  foreach(var item in records){var record=(Dictionary<string,object>)item;
                    foreach(var pairValue in (object[])record["pairs"]){var pair=(Dictionary<string,object>)pairValue;
                        if(rows.Count>=2000)break;
                        object position=pair.ContainsKey("candidate_position_m")?pair["candidate_position_m"]:null;
                        rows.Add(new object[]{pair["direct_index"],pair["reflection_index"],position==null?"—":Convert.ToDouble(position).ToString("F2"),
                            (method=="D"&&pair.ContainsKey("selected_for_review")&&Object.Equals(pair["selected_for_review"],true)?T("优先复核；","Prioritized review; "):"")+T("待复核","Needs review")+" · "+((object[])pair["reasons"]).Length+T(" 项提示（详见导出）"," notices (see export)")});
                    }
                  }
                  var config=(Dictionary<string,object>)context.Request["config"];
                  string json=LocalizationCBridge.Serializer().Serialize(new Dictionary<string,object>{{"schema","HVX.LocalizationComparison.CD.v2"},{"candidate",LocalizationCBridge.Candidate},{"source_file",Path.GetFullPath(locPath)},{"source_sha256",context.Hash},{"config",config},{"manual_acceptance",false},{"engine_result",result}});
                  return new{Rows=rows,Json=json,Summary=(Dictionary<string,object>)result["summary"]};
                },token);
                token.ThrowIfCancellationRequested();savedResult=prepared.Json;
                foreach(var row in prepared.Rows)pairs.Rows.Add(row);
                var summary=prepared.Summary;
                status.Text=(method=="C"?T("复核完成。波对 / 符合筛选条件 / 达到处理上限的记录：","Cross-check completed. Pairs / meeting criteria / records at processing limit: ")+summary["pair_count"]+" / "+summary["accepted_candidate_count"]+" / "+summary["candidate_limited_record_count"]:
                    T("交叉复核完成。候选位置 / 优先复核 / 符合筛选条件：","Cross-check completed. Candidates / prioritized / meeting criteria: ")+summary["candidate_count"]+" / "+summary["selected_for_review"]+" / "+summary["engineering_candidates"])+
                    T("。表格最多显示2000条；完整诊断和警告随导出保留。参考面未确认或触及限额时，不代表无放电。",". Table shows up to 2000 pairs; export retains full diagnostics and warnings. Unconfirmed reference or limits do not mean no discharge.");
            }catch(OperationCanceledException){status.Text=T("已取消；项目和原分析结果未改变。","Cancelled; project and original analysis are unchanged.");}
            catch(Exception ex){status.Text=T("对照未完成，请检查运行环境、参数和记录。","Comparison incomplete; check environment, parameters and record.")+" "+ex.GetBaseException().Message;}
            finally{busy=false;cancellation.Dispose();cancellation=null;Toggle();}
        }
        void Toggle(){run.Enabled=!busy;cancel.Enabled=busy;export.Enabled=!busy&&savedResult.Length>0;algorithm.Enabled=loc.Enabled=length.Enabled=velocity.Enabled=phase.Enabled=reference.Enabled=!busy;}
        void Export(){using(var save=new SaveFileDialog{Filter="JSON|*.json",FileName="Location-CD-comparison.json"})if(save.ShowDialog(this)==DialogResult.OK){
            try{
                string path=Path.GetFullPath(save.FileName);
                string inputDirectory=Path.GetDirectoryName(Path.GetFullPath(loc.Text)).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(!String.Equals(Path.GetExtension(path),".json",StringComparison.OrdinalIgnoreCase)||path.StartsWith(inputDirectory,StringComparison.OrdinalIgnoreCase)||
                    path.StartsWith(Path.GetFullPath(EmbeddedModulePayload.ResolveModulesRoot()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    throw new IOException(T("请将JSON导出到原记录和程序组件目录之外。","Export JSON outside the original-record and software-component directories."));
                File.WriteAllText(path,savedResult,new UTF8Encoding(false,true));status.Text=T("对照结果已导出。原始数据未改写。","Comparison exported; original data unchanged.");
            }catch(Exception ex){MessageBox.Show(this,T("无法导出：","Cannot export: ")+ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }}
    }
}
