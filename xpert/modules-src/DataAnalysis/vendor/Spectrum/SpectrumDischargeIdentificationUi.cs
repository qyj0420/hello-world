using System;
using System.Drawing;
using System.Windows.Forms;
namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        string dischargeIdentificationText="";
        Button[] learningButtons;
        internal void ConfigureLearningActions(Action add,Action undo,Action reset)
        {
            if(learningButtons!=null)return;
            learningButtons=new Button[3];var actions=new[]{add,undo,reset};
            for(int i=0;i<3;i++)
            {
                int index=i;
                var button=new HVX.PdReview.RoundedReviewButton{Name=new[]{"AddConfirmedLearningSample","UndoLearningSample","ResetLearningSamples"}[i],FlatStyle=FlatStyle.Flat,UseVisualStyleBackColor=false};
                HVX.PdReview.ReviewTheme.ButtonRole(button,false);button.Click+=(s,e)=>actions[index]();learningButtons[i]=button;Controls.Add(button);
            }
            LayoutLearningActions();
        }
        internal void SetDischargeIdentificationText(string value)
        {value=value??"";if(value==dischargeIdentificationText)return;dischargeIdentificationText=value;DropDrawingCaches();LayoutSpectrumToolbar();Invalidate();}
        internal void SetLearningActionsEnabled(bool enabled)
        {if(learningButtons!=null)foreach(var button in learningButtons)button.Enabled=enabled;}
        void LayoutLearningActions()
        {
            if(learningButtons==null)return;
            string[] labels=L(new[]{"Add sample","Undo sample","Reset samples"},new[]{"加入样本","撤销样本","清空样本"});
            string[] accessibleNames=L(new[]{"Add a confirmed sample to the pending training library","Undo the latest learning sample","Reset this control's learning samples"},
                new[]{"加入人工确认的待训练样本库","撤销本次学习样本","清空本控件学习样本"});
            string[] hints=L(new[]{"Confirm the correct type and add the displayed single-phase data to the pending training library. The model is unchanged.","Undo the most recent sample addition made by this control.","Reset this control's learning samples; keep the original model and unrelated samples."},
                new[]{"人工确认正确类型后，将当前单相数据加入待训练库；不会立即改变识别模型。","回退本控件最近一次加入的学习样本。","清空本控件累计学习样本并保留备份；原始模型和其他样本不变。"});
            int right=ClientSize.Width-50;
            if(ViewPlaneButtonsVisible&&cameraToolbar!=null)right=Math.Min(right,cameraToolbar.Left-8);
            if(LPrpdColorReferenceButtonVisible&&colorModeButton!=null)right=Math.Min(right,colorModeButton.Left-8);
            int width=Math.Max(24,Math.Min(120,(right-24)/3)),height=ViewActionButtonHeight;
            int top=Math.Max(0,ClientSize.Height-ToolbarHeightForWidth(ClientSize.Width)-height-6);
            for(int i=0;i<3;i++)
            {
                var button=learningButtons[i];button.Visible=mode==11||mode==12;button.Font=Font;button.Text=labels[i];
                button.AccessibleName=accessibleNames[i];button.AccessibleDescription=hints[i];
                button.Bounds=new Rectangle(12+i*(width+4),top,width,height);if(tooltip!=null)tooltip.SetToolTip(button,hints[i]);button.BringToFront();
            }
        }
    }
}
