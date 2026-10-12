using System;
using System.Globalization;
using System.Windows.Forms;
namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class SettingsForm
    {
        private NumericUpDown endpointWidth,endpointRatio;
        private ComboBox endpointFirstOperator,endpointSecondOperator;
        private string loadedEndpointPolicy;
        private int loadedFirstOperator,loadedSecondOperator;
        private static ComboBox EndpointOperator()
        {
            var box=new SettingsComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
            box.Items.AddRange(new object[]{"或（任一满足）","与（同时满足）"});
            box.SelectedIndex=0;return box;
        }
        // Only constructed inside the separately authorized Plus Data page.
        private void AddEndpointPolicyRows(TableLayoutPanel grid)
        {
            AddWideRow(grid,CreateNote("首末端识别组合规则：原始波形、校准、采样率等质量检查始终有效。"));
            endpointFirstOperator=EndpointOperator();endpointSecondOperator=EndpointOperator();
            endpointWidth=CreateNumeric(1M,100M,3,0.1M);endpointRatio=CreateNumeric(1M,100M,3,0.1M);
            AddRow(grid,"① 反射主瓣分裂；与②的关系",endpointFirstOperator);
            AddRow(grid,"② 入射半高宽 ≥ 校准半高宽 ×",endpointWidth);
            AddRow(grid,"①②组合与③的关系",endpointSecondOperator);
            AddRow(grid,"③ 入射正反峰比 ≤ 校准峰比 ÷",endpointRatio);
            AddWideRow(grid,CreateNote("组合顺序：(① 与/或 ②) 与/或 ③。条件未满足不直接判作近端；证据缺失保留待判断。已有结果须重新分析。"));
        }
        private void LoadEndpointPolicy(string value)
        {
            double width,ratio; bool firstAnd,secondAnd; string message;
            if(!AppSettings.TryParseEndpointPolicy(value,out width,out ratio,out firstAnd,out secondAnd,out message))
                throw new InvalidOperationException(message);
            SetNumeric(endpointWidth,width);SetNumeric(endpointRatio,ratio);
            endpointFirstOperator.SelectedIndex=firstAnd?1:0;endpointSecondOperator.SelectedIndex=secondAnd?1:0;
            loadedEndpointPolicy=value;loadedFirstOperator=endpointFirstOperator.SelectedIndex;loadedSecondOperator=endpointSecondOperator.SelectedIndex;
        }
        private string CaptureEndpointPolicy()
        {
            if(!plusDataUnlocked)throw new InvalidOperationException("HV DAC Xpert Plus Data 尚未授权。");
            if (((SettingsNumber)endpointWidth).SourceUnchanged && ((SettingsNumber)endpointRatio).SourceUnchanged &&
                endpointFirstOperator.SelectedIndex==loadedFirstOperator && endpointSecondOperator.SelectedIndex==loadedSecondOperator) return loadedEndpointPolicy;
            return ReadNumber(endpointWidth).ToString("R",CultureInfo.InvariantCulture)+";"+ReadNumber(endpointRatio).ToString("R",CultureInfo.InvariantCulture)+";"+
                (endpointFirstOperator.SelectedIndex==1?"AND":"OR")+";"+(endpointSecondOperator.SelectedIndex==1?"AND":"OR");
        }
    }
}
