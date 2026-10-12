using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.OWTS.Task38;
namespace HVX.OWTS.Task38.Desktop
{
    internal sealed partial class MainForm
    {
        private readonly CheckBox rawAutoSave=new CheckBox {Text="自动保存原始数据",Checked=true,AutoSize=true};
        private readonly Button rawSaveAction=Button("保存原始数据");
        private bool rawSaveBusy;
        private void AddRawCaptureActions(TableLayoutPanel strip)
        {
            strip.ColumnCount=4;strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,180));
            var panel=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false};
            panel.Controls.Add(rawAutoSave);panel.Controls.Add(rawSaveAction);
            strip.Controls.Add(panel,3,0);strip.SetRowSpan(panel,2);
            rawSaveAction.Click+=async (s,e)=>{
                if(rawSaveBusy||activeRecord==null)return;
                if(String.IsNullOrEmpty(activeRecord.RawCapturePath)){await SaveRawCapture(processed);return;}
                if(MessageBox.Show(this,"将本次原始数据移入恢复目录？试验登记信息保留。","删除原始记录",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                rawSaveBusy=true;RefreshView();
                try {
                    var record=activeRecord;string directory=activeRecordDirectory;
                    string recovery=await Task.Run(()=>Task38RawCaptureStore.MoveToRecovery(directory,record.RecordId));
                    record.RawCaptureRecoveryPath=recovery;record.RawCapturePath="";record.SaveUpdate(directory);
                    uiError="原始数据已移入恢复目录，可恢复："+recovery;
                } catch(Exception ex){uiError="删除未完成："+OperatorDiagnostics.Report(ex);}
                finally{rawSaveBusy=false;RefreshView();}
            };
        }
        private async Task SaveRawCapture(Task38CycleResult result)
        {
            if(rawSaveBusy||result==null||activeRecord==null||result.RecordId!=activeRecord.RecordId)return;
            var record=activeRecord;string directory=activeRecordDirectory;
            if(!String.IsNullOrEmpty(record.RawCapturePath))return;
            rawSaveBusy=true;RefreshView();
            try {
                string path=await Task.Run(()=>Task38RawCaptureStore.SaveNew(directory,record,result));
                record.RawCapturePath=path;record.SaveUpdate(directory);
            } catch(Exception ex){uiError="原始数据保存未完成："+OperatorDiagnostics.Report(ex);}
            finally{rawSaveBusy=false;RefreshView();}
        }
        private async void AutoSaveRawCapture(Task38CycleResult result)
        {if(rawAutoSave.Checked&&result.Success)await SaveRawCapture(result);}
        private void RefreshRawCaptureActions(bool running)
        {
            rawAutoSave.Enabled=!running&&!rawSaveBusy;
            rawSaveAction.Enabled=!running&&!rawSaveBusy&&activeRecord!=null&&processed!=null&&processed.Success&&processed.RecordId==activeRecord.RecordId;
            rawSaveAction.Text=rawSaveBusy?"正在保存…":activeRecord!=null&&!String.IsNullOrEmpty(activeRecord.RawCapturePath)?"删除原始记录":"保存原始数据";
            if(rawSaveBusy){start.Enabled=false;mode.Enabled=false;}
        }
    }
}
