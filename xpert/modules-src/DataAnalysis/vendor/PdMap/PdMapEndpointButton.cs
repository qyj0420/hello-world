using System;
using System.Drawing;
using System.Windows.Forms;
namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        CheckBox endpointIdentificationButton;
        Action<bool> endpointIdentificationAction;
        string endpointIdentificationCaption="";
        bool endpointIdentificationSync;
        internal void ConfigureEndpointIdentification(Action<bool> action)
        {
            endpointIdentificationAction=action;
            if(endpointIdentificationButton==null)
            {
                endpointIdentificationButton=new CheckBox{Name="PdMapEndpointIdentification",Font=displayOptionsFont??Font,Height=24,AutoSize=false,Anchor=AnchorStyles.Left|AnchorStyles.Bottom};
                endpointIdentificationButton.CheckedChanged+=(s,e)=>{if(!endpointIdentificationSync&&endpointIdentificationAction!=null)endpointIdentificationAction(endpointIdentificationButton.Checked);};
                Controls.Add(endpointIdentificationButton);
            }
            endpointIdentificationButton.Visible=action!=null;
            displayOptionsLayoutWidth=-1;LayoutDisplayOptions();Invalidate();
        }
        internal void SetEndpointIdentificationDisplay(bool enabled,string result)
        {
            if(endpointIdentificationButton==null)return;
            endpointIdentificationCaption=result??"";
            endpointIdentificationSync=true;
            try{if(endpointIdentificationButton.Checked!=enabled)endpointIdentificationButton.Checked=enabled;}
            finally{endpointIdentificationSync=false;}
            endpointIdentificationButton.Text=L("Near/far ends","近远端识别")+(endpointIdentificationCaption.Length==0?"":"："+endpointIdentificationCaption);
        }
        int EndpointIdentificationWidth
        {get{return Math.Max(128,TextRenderer.MeasureText(L("Near/far ends","近远端识别")+"："+L("Indeterminate","无法判定"),displayOptionsFont??Font,Size.Empty,TextFormatFlags.NoPadding).Width+28);}}
        void LayoutEndpointIdentification(Rectangle[] rectangles,int top,ref Rectangle contentBounds)
        {
            if(endpointIdentificationButton==null||endpointIdentificationAction==null||rectangles.Length<8)return;
            Rectangle bounds=rectangles[7];bounds.Offset(0,top);endpointIdentificationButton.Bounds=bounds;
            endpointIdentificationButton.Text=L("Near/far ends","近远端识别")+(endpointIdentificationCaption.Length==0?"":"："+endpointIdentificationCaption);
            endpointIdentificationButton.AccessibleDescription=L("Compare original pulse width and lobe shapes with calibration; does not change position or acceptance.","按原始波形的脉宽与峰形对比校准进行近远端识别；不改变定位位置和接受状态。");
            contentBounds=Rectangle.Union(contentBounds,bounds);endpointIdentificationButton.BringToFront();
        }
    }
}
