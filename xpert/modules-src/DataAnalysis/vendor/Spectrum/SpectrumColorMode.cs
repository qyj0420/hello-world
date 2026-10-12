using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        int lPrpdColorReference;
        Button colorModeButton;

        [Category("Appearance"), DefaultValue(0), Description("L-PRPD only: 0=N, colour by bin pulse count; 1=Q, colour by absolute displayed charge. Does not reload or modify the matrix.")]
        public int LPrpdColorReferenceIndex
        {
            get { return lPrpdColorReference; }
            set
            {
                if(value<0||value>1)throw new ArgumentOutOfRangeException("LPrpdColorReferenceIndex");
                if(lPrpdColorReference==value)return;
                lPrpdColorReference=value;DropDrawingCaches();SyncColorModeButton();Invalidate();NotifyHost(ViewChanged);
            }
        }
        [Browsable(false)]public bool LPrpdColorReferenceButtonVisible { get { return mode==12; } }
        bool UseChargeColorReference { get { return mode==12&&lPrpdColorReference==1; } }

        void InitializeColorModeButton()
        {
            if(colorModeButton!=null)return;
            colorModeButton=new HVX.PdReview.RoundedReviewButton{Name="SpectrumColorReference",Text="N",TabIndex=2,FlatStyle=FlatStyle.Flat,
                UseVisualStyleBackColor=false,AccessibleRole=AccessibleRole.PushButton,Visible=false};
            colorModeButton.FlatAppearance.BorderSize=1;
            colorModeButton.Click+=delegate{PerformLPrpdColorReferenceButtonClick();};
            Controls.Add(colorModeButton);SyncColorModeButton();
        }
        void SyncColorModeButton()
        {
            if(colorModeButton==null||colorModeButton.IsDisposed)return;
            colorModeButton.Visible=LPrpdColorReferenceButtonVisible;colorModeButton.Font=Font;
            colorModeButton.Text=lPrpdColorReference==1?"Q":"N";
            colorModeButton.BackColor=HVX.PdReview.ReviewTheme.Surface;colorModeButton.ForeColor=HVX.PdReview.ReviewTheme.Ink;
            colorModeButton.FlatAppearance.BorderColor=HVX.PdReview.ReviewTheme.Border;
            colorModeButton.AccessibleName=lPrpdColorReference==1?L("Color by charge; click for count","按放电量着色；点击切换为次数"):
                L("Color by count; click for charge","按放电次数着色；点击切换为放电量");
            if(tooltip!=null)tooltip.SetToolTip(colorModeButton,colorModeButton.AccessibleName);
            LayoutColorModeButton();
        }
        void LayoutColorModeButton()
        {
            if(colorModeButton==null||colorModeButton.IsDisposed)return;
            int width=ColorModeButtonWidth,height=ViewActionButtonHeight;
            colorModeButton.Bounds=new Rectangle(Math.Max(0,ClientSize.Width-width-16-ChartExportReservedWidth),Math.Max(0,ClientSize.Height-ToolbarHeightForWidth(ClientSize.Width)-height-6),width,height);
            colorModeButton.BringToFront();
        }
        int ColorModeButtonWidth { get { return Math.Max(36,TextRenderer.MeasureText("Q",Font).Width+14); } }
        public Rectangle GetLPrpdColorReferenceButtonBounds()
        { return LPrpdColorReferenceButtonVisible&&colorModeButton!=null?colorModeButton.Bounds:Rectangle.Empty; }
        public void PerformLPrpdColorReferenceButtonClick()
        {
            if(!LPrpdColorReferenceButtonVisible)throw new InvalidOperationException("Color reference button is available only for L-PRPD.");
            LPrpdColorReferenceIndex=1-lPrpdColorReference;
        }

        double ChargeColorMaximum()
        { return view==null||view.Points.Count==0?0:view.Points.Max(p=>Math.Abs(p.Y)); }
        int LPrpdPointColorIndex(SpectrumPoint point,double chargeMaximum)
        { return UseChargeColorReference?ChargeColorIndex(point.Y,chargeMaximum):DensityColorIndex(point.DensityCount,view.DensityMaximum); }
        static int ChargeColorIndex(double charge,double maximum)
        {
            if(!Finite(charge)||!Finite(maximum)||maximum<=0)return 0;
            return (int)Math.Round(255*Math.Max(0,Math.Min(1,Math.Abs(charge)/maximum)));
        }
        string ChargeColorTick(double value)
        {
            double scaled=value/ChargeDisplayScale;
            // Keep the exact colour-scale endpoint readable when a display unit
            // produces sub-unit values, without changing its physical domain.
            if(value>0&&scaled<.000000001)return "<0.000000001";
            return scaled==0?"0":scaled<1?scaled.ToString("0.#########",CultureInfo.InvariantCulture):scaled.ToString("0.#",CultureInfo.InvariantCulture);
        }
    }
}
