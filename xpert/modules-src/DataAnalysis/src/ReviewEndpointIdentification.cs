using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using LabVIEWPdMap;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        CheckBox overviewEndpointButton;
        bool endpointDialogOpen;
        bool endpointIdentificationEnabled,endpointIdentificationSync;
        void InitializeEndpointReview(FlowLayoutPanel overviewOptions)
        {
            overviewEndpointButton=new CheckBox{Name="ReviewOverviewEndpointIdentification",Height=28,Width=135};
            overviewEndpointButton.AutoSize=true;overviewEndpointButton.Text=L("Near/far ends","近远端识别");
            overviewEndpointButton.CheckedChanged+=(s,e)=>{if(!endpointIdentificationSync)SetEndpointIdentificationEnabled(overviewEndpointButton.Checked,true);};
            overviewOptions.Controls.Add(overviewEndpointButton);
            pdmap.ConfigureEndpointIdentification(enabled=>SetEndpointIdentificationEnabled(enabled,false));
            CurrentPdMapChanged+=(s,e)=>RefreshEndpointIdentification();
        }
        void SetEndpointIdentificationEnabled(bool enabled,bool overview)
        {
            endpointIdentificationEnabled=enabled;
            endpointIdentificationSync=true;
            try{overviewEndpointButton.Checked=enabled;pdmap.SetEndpointIdentificationDisplay(enabled,"");}
            finally{endpointIdentificationSync=false;}
            if(!enabled)return;
            if(overview&&locationOverviewMode)ShowEndpointIdentificationDialog();
            else RefreshEndpointIdentification();
        }
        void RefreshEndpointIdentification()
        {
            if(!endpointIdentificationEnabled||selected<0||locationOverviewMode)return;
            var result=pdmap.IdentifyEndpoint();
            pdmap.SetEndpointIdentificationDisplay(true,EndpointDirectionLabel(result.Direction));
        }
        int[] EndpointReviewRows()
        {
            if(!locationOverviewMode)return selected<0?new int[0]:new[]{selected};
            string[] paths=analysisTree.GetDataPaths();double[,] catalog=analysisTree.GetDataCatalog();
            int index=Array.FindIndex(paths,p=>string.Equals(p,overviewPath,StringComparison.OrdinalIgnoreCase));
            if(index<0)return new int[0];
            return Enumerable.Range(0,SourceRowCount).Where(r=>working[r,0]==catalog[index,0]&&working[r,1]==catalog[index,1]).ToArray();
        }
        public bool ShowEndpointIdentificationDialog()
        {
            if(endpointDialogOpen||IsAnalysisRunning)return false;
            int[] rows=EndpointReviewRows();
            if(rows.Length==0){SetReviewOperationStatus(L("No extracted MAPs in this file. Analyse it first.","该文件尚无已提取的 MAP，请先分析。"));return false;}
            endpointDialogOpen=true;
            try
            {
                using(var window=new Form{Name="ReviewEndpointIdentificationDialog",Text=L("Near/far endpoint identification","近远端识别"),StartPosition=FormStartPosition.CenterScreen,
                    FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,ShowInTaskbar=false,Font=Font,BackColor=ReviewTheme.Surface,ClientSize=new Size(720,440)})
                using(var clock=new Timer{Interval=20})
                {
                    ReviewTheme.ApplyDialogIcon(window);
                    var note=new Label{Dock=DockStyle.Top,Height=48,Padding=new Padding(10,7,10,3),ForeColor=ReviewTheme.Ink,
                        Text=L("Original waveform evidence only. Engineering classification, not probability. Position, confidence and acceptance are unchanged.","只读比较原始波形与校准。分类为工程判据，不是概率；不修改定位位置、确信度或接受状态。")};
                    var grid=new DataGridView{Name="EndpointEvidenceRows",Dock=DockStyle.Top,Height=165,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
                        AllowUserToResizeRows=false,RowHeadersVisible=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,
                        BackgroundColor=ReviewTheme.Surface,BorderStyle=BorderStyle.None};
                    grid.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
                    grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize;grid.ColumnHeadersDefaultCellStyle.WrapMode=DataGridViewTriState.True;
                    grid.Columns.Add("Map","PDmap");grid.Columns.Add("Position",L("Position / m","位置 / m"));grid.Columns.Add("Direction",L("Identification","识别结果"));grid.Columns.Add("Width",L("Width / CAL","脉宽 / CAL"));grid.Columns.Add("Reason",L("Evidence summary","判定依据"));grid.Columns[4].FillWeight=240;
                    var details=new TextBox{Name="EndpointEvidenceDetails",Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=ReviewTheme.SurfaceAlt,ForeColor=ReviewTheme.Ink};
                    var footer=new Panel{Dock=DockStyle.Bottom,Height=42,Padding=new Padding(8)};
                    var progress=new Label{Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
                    var close=ActionButton(95);close.Dock=DockStyle.Right;close.Text=L("Close","关闭");close.DialogResult=DialogResult.Cancel;
                    footer.Controls.Add(progress);footer.Controls.Add(close);window.CancelButton=close;
                    window.Controls.Add(details);window.Controls.Add(grid);window.Controls.Add(note);window.Controls.Add(footer);
                    Action fit=()=>{
                        note.Height=TextRenderer.MeasureText(note.Text,note.Font,new Size(Math.Max(100,window.ClientSize.Width-note.Padding.Horizontal),int.MaxValue),TextFormatFlags.WordBreak).Height+note.Padding.Vertical+6;
                        footer.Height=Math.Max(42,close.Font.Height+footer.Padding.Vertical+12);close.Width=Math.Max(95,TextRenderer.MeasureText(close.Text,close.Font).Width+24);
                        grid.Height=Math.Max(70,Math.Min(165,window.ClientSize.Height-note.Height-footer.Height-70));
                    };
                    window.Resize+=(s,e)=>fit();note.FontChanged+=(s,e)=>fit();close.FontChanged+=(s,e)=>fit();window.Shown+=(s,e)=>{ReviewTheme.FitDialogToWorkingArea(window);fit();};
                    grid.SelectionChanged+=(s,e)=>{if(grid.CurrentRow!=null){var result=grid.CurrentRow.Tag as EndpointIdentificationResult;if(result!=null)details.Text=result.EvidenceText;}};
                    int cursor=0;bool wholeFile=locationOverviewMode;PdMapControl reader=null;
                    double[,] parameters=null,waves=null;
                    try
                    {
                        if(wholeFile)
                        {
                            // One detached file snapshot, then cache-only row selection.
                            // Never walk the visible control through each MAP or change its tree selection.
                            SubsetWorkspace(rows,out parameters,out waves);
                            reader=new PdMapControl{EnglishVersion=english,ProjectPath=projectPath,InputGeometryAlreadyCompensated=true};
                            reader.PositionTolerancePercent=pdmap.PositionTolerancePercent;
                            reader.ManufacturerEndpointPolicy=pdmap.ManufacturerEndpointPolicy;
                            reader.MixedCableCalculation=pdmap.MixedCableCalculation;
                            reader.RestoreConnectingLengthWithoutNotification(ConnectingCableLengthM);
                            var geometry=pdmap.SnapshotReviewConnectingGeometry();
                            reader.SetReviewConnectingGeometry(rows.Select(r=>r<geometry.Length?geometry[r]:null).ToArray());
                        }
                        clock.Tick+=(s,e)=>
                        {
                            clock.Stop();
                            if(cursor>=rows.Length)return;
                            try
                            {
                                EndpointIdentificationResult result;
                                if(wholeFile)
                                {
                                    bool loaded=cursor==0?reader.LoadDataMatricesAtRow(parameters,waves,0):reader.SelectSourceRow(cursor);
                                    if(!loaded)throw new InvalidOperationException(reader.LastError);
                                    result=reader.IdentifyEndpoint();
                                }
                                else result=pdmap.IdentifyEndpoint();
                                double position=wholeFile?parameters[cursor,13]:working[rows[cursor],13];
                                int added=grid.Rows.Add(result.PdMapId,MetricNumber(position),EndpointDirectionLabel(result.Direction),MetricNumber(result.WidthMultiple),result.Summary);
                                grid.Rows[added].Tag=result;
                                if(added==0){grid.CurrentCell=grid.Rows[0].Cells[0];details.Text=result.EvidenceText;}
                            }
                            catch(Exception ex)
                            {
                                if(IsFatalHostException(ex))throw;
                                grid.Rows.Add(working.GetLength(0)>rows[cursor]?working[rows[cursor],9]:cursor,"—",L("Unavailable","无法判定"),"—",ex.Message);
                            }
                            cursor++;progress.Text=L("Reviewed: ","已复核：")+cursor+" / "+rows.Length;
                            if(cursor<rows.Length)clock.Start();else SetReviewOperationStatus(L("Endpoint review complete: ","近远端复核完成：")+cursor+L(" MAPs; original results retained."," 个 MAP，原分析结果保持不变。"));
                        };
                        window.Shown+=(s,e)=>clock.Start();window.FormClosing+=(s,e)=>clock.Stop();
                        window.ShowDialog(FindForm());
                    }
                    finally {clock.Stop();if(reader!=null)reader.Dispose();}
                }
                return true;
            }
            catch(Exception ex){if(IsFatalHostException(ex))throw;SetReviewOperationStatus(L("Endpoint review unavailable: ","近远端复核不可用：")+ex.Message);return false;}
            finally{endpointDialogOpen=false;}
        }
        string EndpointDirectionLabel(string direction)
        {return direction=="Near"?L("Near end","近端"):direction=="Far"?L("Far end","远端"):direction=="NotApplicable"?L("Not an endpoint","非端部"):L("Indeterminate","无法判定");}
    }
}
