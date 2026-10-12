using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        Timer recognitionUiTimer;
        int recognitionUiGeneration;
        bool recognitionUiRunning;
        void InitializeDischargeRecognitionUi()
        {
            recognitionUiTimer=new Timer{Interval=450};recognitionUiTimer.Tick+=RunDischargeIdentification;
            spectrum.ConfigureLearningActions(ShowConfirmLearningSample,()=>ManageLearningSamples(false),()=>ManageLearningSamples(true));
            spectrum.ViewChanged+=(s,e)=>{if(!spectrum.IsCameraViewNotification)QueueDischargeIdentification();};
            spectrum.RegionStatisticsChanged+=(s,e)=>QueueDischargeIdentification();
            ResultsChanged+=(s,e)=>QueueDischargeIdentification();
            AnalysisCompleted+=(s,e)=>QueueDischargeIdentification();
            Disposed+=(s,e)=>{recognitionUiGeneration++;recognitionUiTimer.Stop();recognitionUiTimer.Dispose();};
            QueueDischargeIdentification();
        }
        void QueueDischargeIdentification()
        {
            if(recognitionUiTimer==null||IsDisposed||Disposing)return;
            recognitionUiGeneration++;recognitionUiTimer.Stop();
            if(!EnableAiFeatures)
            {
                spectrum.SetLearningActionsEnabled(false);
                spectrum.SetDischargeIdentificationText(spectrum.DisplayModeIndex==11||spectrum.DisplayModeIndex==12?L("Discharge type: AI disabled","放电类型：AI 功能已关闭"):"");return;
            }
            spectrum.SetLearningActionsEnabled(!recognitionBusy&&!IsAnalysisRunning);
            if(!EnableDefectRecognition)
            {
                spectrum.SetDischargeIdentificationText(spectrum.DisplayModeIndex==11||spectrum.DisplayModeIndex==12?L("Discharge type: recognition disabled","放电类型：缺陷识别已关闭"):"");return;
            }
            if(AiLearningMode)
            {
                spectrum.SetDischargeIdentificationText(L("Discharge type: manual label required","放电类型：请人工标注"));
                spectrum.SetLearningActionsEnabled(true);return;
            }
            if(spectrum.DisplayModeIndex!=11&&spectrum.DisplayModeIndex!=12){spectrum.SetDischargeIdentificationText("");return;}
            spectrum.SetDischargeIdentificationText(L("Discharge type: awaiting recognition","放电类型：等待识别"));
            recognitionUiTimer.Start();
        }
        async void RunDischargeIdentification(object sender,EventArgs args)
        {
            recognitionUiTimer.Stop();
            if(IsDisposed||Disposing||!DefectRecognitionEnabled)return;
            if(AiLearningMode){spectrum.SetDischargeIdentificationText(L("Discharge type: manual label required","放电类型：请人工标注"));return;}
            if(recognitionUiRunning||DischargeRecognitionBusy||IsAnalysisRunning||pdmap.Capture||spectrum.Capture){recognitionUiTimer.Start();return;}
            if(spectrum.DisplayModeIndex!=11&&spectrum.DisplayModeIndex!=12)return;
            int generation=recognitionUiGeneration;recognitionUiRunning=true;spectrum.SetLearningActionsEnabled(false);
            spectrum.SetDischargeIdentificationText(L("Discharge type: recognizing…","放电类型：识别中…"));
            try
            {
                var result=await RecognizeCurrentDischargeAsync();
                if(IsDisposed||Disposing||!DefectRecognitionEnabled||generation!=recognitionUiGeneration)return;
                string label=result.DisplayLabel;
                if(string.IsNullOrEmpty(label))label=result.StatusCode=="InsufficientInput"?L("Insufficient data","数据不足"):L("Unavailable","不可用");
                spectrum.SetDischargeIdentificationText(L("Discharge type: ","放电类型：")+label);
                SetReviewOperationStatus(result.StatusMessage);
            }
            catch(OperationCanceledException) { }
            catch(Exception ex){if(IsFatalHostException(ex))throw;if(!IsDisposed&&generation==recognitionUiGeneration){spectrum.SetDischargeIdentificationText(L("Discharge type: unavailable","放电类型：不可用"));SetReviewOperationStatus(ex.Message);}}
            finally
            {
                recognitionUiRunning=false;
                if(!IsDisposed&&!Disposing){spectrum.SetLearningActionsEnabled(EnableAiFeatures&&!IsAnalysisRunning);if(generation!=recognitionUiGeneration)QueueDischargeIdentification();}
            }
        }
        void ShowConfirmLearningSample()
        {
            if(!EnableAiFeatures||IsAnalysisRunning||DischargeRecognitionBusy)return;
            using(var form=ArchiveDialog(L("Confirm a training sample","确认待训练样本"),new Size(660,640)))
            {
                var labels=AvailableDischargeLabels;
                var list=new CheckedListBox{Dock=DockStyle.Fill,CheckOnClick=true,BorderStyle=BorderStyle.None,IntegralHeight=false,HorizontalScrollbar=true};
                for(int i=0;i<labels.GetLength(0);i++)list.Items.Add(labels[i,english?2:1]);
                var options=new Panel{Dock=DockStyle.Bottom,Height=36,Padding=new Padding(0,2,0,2)};
                var expand=new CheckBox{Name="ExpandLearning",Dock=DockStyle.Fill,Checked=true,AutoSize=false,
                    Text=L("Expanded learning (generate variants to broaden recognition)","拓展学习（对样本生成变体以扩大识别范围）")};
                options.Controls.Add(expand);
                var manualHost=new Panel{Dock=DockStyle.Bottom,Height=58,Padding=new Padding(0,3,0,3)};
                var manualLabel=new Label{Dock=DockStyle.Top,Height=22,Text=L("Manually confirmed discharge type (optional)","人工确认的放电类型（可选）"),ForeColor=ReviewTheme.SecondaryInk};
                var manual=new TextBox{Name="ManualDischargeType",Dock=DockStyle.Bottom,MaxLength=ReviewLearningTransactions.ManualDischargeTypeMaxLength,
                    AccessibleDescription=L("Known text maps to a fixed type; unknown text is kept for taxonomy review only.","已知文字映射到固定类型；未知文字仅保存为待类型体系审核样本。")};
                manualHost.Controls.Add(manual);manualHost.Controls.Add(manualLabel);
                var note=new Label{Dock=DockStyle.Top,Height=88,Text=L("Show one phase. Select a type or enter a verified type. Unrecognized types are saved for review only. Confirming may retrain with the best available algorithm after optional expanded learning.","请仅显示一个相别，勾选放电类型或填写人工确认的类型。未识别的类型仅保存供审核；确认后可先拓展学习，再按最佳算法重训模型。"),ForeColor=ReviewTheme.SecondaryInk};
                var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=36,FlowDirection=FlowDirection.RightToLeft};
                var cancel=ActionButton(90);cancel.Text=L("Cancel","取消");cancel.DialogResult=DialogResult.Cancel;
                var add=ActionButton(145);add.Text=L("Add to learning","加入学习");ReviewTheme.ButtonRole(add,true);
                footer.Controls.Add(cancel);footer.Controls.Add(add);form.Controls.Add(list);form.Controls.Add(options);form.Controls.Add(manualHost);form.Controls.Add(note);form.Controls.Add(footer);form.CancelButton=cancel;form.AcceptButton=add;
                Action fit=()=>{
                    int width=Math.Max(100,form.ClientSize.Width-form.Padding.Horizontal);
                    note.Height=TextRenderer.MeasureText(note.Text,note.Font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak).Height+12;
                    manualLabel.Height=TextRenderer.MeasureText(manualLabel.Text,manualLabel.Font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak).Height+6;
                    manualHost.Height=manualLabel.Height+manual.PreferredHeight+12;
                    expand.Height=TextRenderer.MeasureText(expand.Text,expand.Font,new Size(Math.Max(80,width-28),int.MaxValue),TextFormatFlags.WordBreak).Height+10;
                    options.Height=expand.Height+8;
                    foreach(var button in new[]{add,cancel}){button.Width=Math.Min(width-8,TextRenderer.MeasureText(button.Text,button.Font).Width+32);button.Height=Math.Max(32,button.Font.Height+12);}
                    footer.WrapContents=true;
                    footer.Height=(add.Width+cancel.Width+add.Margin.Horizontal+cancel.Margin.Horizontal>width?2:1)*(add.Height+add.Margin.Vertical)+4;
                };
                form.Shown+=(s,e)=>fit();form.Resize+=(s,e)=>fit();note.TextChanged+=(s,e)=>fit();
                add.Click+=(s,e)=>{
                    string keys=string.Join("+",list.CheckedIndices.Cast<int>().Select(i=>labels[i,0]));
                    if(keys.Length==0&&String.IsNullOrWhiteSpace(manual.Text)){note.Text=L("Select a fixed type or enter a manually verified type.","请选择固定类型或输入人工确认的类型。");return;}
                    double overlap;string conflictMessage;bool conflict=ProbeLearningModelConflict(keys,manual.Text,out overlap,out conflictMessage);
                    if(conflict){
                        string prompt=conflictMessage+L("\n\nContinue and retrain? Existing model data may change.","\n\n是否继续并重训？保存模型会影响已有模型数据。");
                        if(MessageBox.Show(form,prompt,L("Learning conflict","加入学习冲突提示"),MessageBoxButtons.OKCancel,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.OK)return;
                    }
                    add.Enabled=false;form.UseWaitCursor=true;
                    try{bool ok=AddConfirmedLearningSample(keys,Environment.UserName,manual.Text,expand.Checked,conflict);SetReviewOperationStatus(LearningStatusMessage);
                        if(ok){form.DialogResult=DialogResult.OK;form.Close();}else{note.Text=LearningStatusMessage;note.ForeColor=Color.Firebrick;}}
                    finally{add.Enabled=true;form.UseWaitCursor=false;}
                };
                form.ShowDialog(FindForm());
            }
        }
        void ManageLearningSamples(bool all)
        {
            if(!EnableAiFeatures||IsAnalysisRunning||DischargeRecognitionBusy)return;
            string question=all?L("Move all samples added by this control to history? The original model and unrelated libraries will remain unchanged.","将本控件累计加入的学习样本移至历史备份？原始模型和其他样本库保持不变。"):
                L("Move the latest sample addition by this control to history?","撤销本控件最近一次加入的学习样本，并移至历史备份？");
            if(MessageBox.Show(FindForm(),question,L("Learning samples","学习样本"),MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return;
            if(all)ResetLearningSamples();else UndoLatestLearningSample();
            SetReviewOperationStatus(LearningStatusMessage);
        }
    }
}
