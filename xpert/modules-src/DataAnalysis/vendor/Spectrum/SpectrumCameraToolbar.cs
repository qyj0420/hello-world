using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        Panel cameraToolbar;
        Button[] cameraButtons;
        CheckBox chargeCountToggle;
        static readonly int[] CameraButtonPlanes={1,2,3,0};
        static readonly string[] CameraButtonNames={"XY","XZ","YZ","3D"};

        void InitializeCameraToolbar()
        {
            if(cameraToolbar!=null)return;
            cameraToolbar=new Panel{Name="SpectrumCameraToolbar",BackColor=BackColor,TabIndex=1,Visible=false};
            Controls.Add(cameraToolbar);cameraButtons=new Button[4];
            for(int i=0;i<4;i++)
            {
                int plane=CameraButtonPlanes[i];
                var button=new HVX.PdReview.RoundedReviewButton{Name="SpectrumCamera"+CameraButtonNames[i],Text=CameraButtonNames[i],
                    Bounds=new Rectangle(i*46,0,42,26),TabIndex=i,FlatStyle=FlatStyle.Flat,
                    UseVisualStyleBackColor=false,AccessibleRole=AccessibleRole.PushButton};
                button.FlatAppearance.BorderSize=1;
                button.Click+=delegate{ViewPlaneIndex=plane;};
                cameraButtons[i]=button;cameraToolbar.Controls.Add(button);
            }
            chargeCountToggle=new CheckBox{Name="SpectrumChargeCountOverlay",AutoSize=false,TabIndex=4};
            chargeCountToggle.CheckedChanged+=delegate{if(!syncingToolbar)ShowChargeCountOverlay=chargeCountToggle.Checked;};
            LayoutCameraToolbar();SyncCameraToolbar();
        }

        void SyncCameraToolbar()
        {
            if(cameraToolbar==null||cameraToolbar.IsDisposed)return;
            cameraToolbar.Visible=SupportsPlaneViews;
            cameraToolbar.Font=Font;cameraToolbar.BackColor=BackColor;
            if(chargeCountToggle!=null)
            {
                // Unrelated spectrum profiles keep their original four-button
                // toolbar. Attach this optional control only for U-Q-L.
                if(mode==5&&chargeCountToggle.Parent!=cameraToolbar)cameraToolbar.Controls.Add(chargeCountToggle);
                if(mode!=5&&chargeCountToggle.Parent==cameraToolbar)cameraToolbar.Controls.Remove(chargeCountToggle);
                chargeCountToggle.Visible=mode==5;
                chargeCountToggle.Enabled=ChargeCountOverlayAvailable;
                chargeCountToggle.Text=L("Count bins","次数分箱");
                if(chargeCountToggle.Checked!=ShowChargeCountOverlay)chargeCountToggle.Checked=ShowChargeCountOverlay;
                chargeCountToggle.AccessibleName=chargeCountToggle.Text;
                chargeCountToggle.AccessibleDescription=L("Overlay pulse counts in charge intervals for each voltage in ZY view. Darker cells mean more pulses; scatter points are retained.","在ZY视图按电压分组叠加放电量区间内的脉冲次数；颜色越深次数越多，保留原散点。");
                if(tooltip!=null)tooltip.SetToolTip(chargeCountToggle,chargeCountToggle.AccessibleDescription);
            }
            for(int i=0;i<4;i++)
            {
                var button=cameraButtons[i];bool selected=Camera.PlaneIndex==CameraButtonPlanes[i];
                string planeName=!competitionProfile&&mode==12&&i==1?"ZX":(mode==5||mode==6||mode==12||CompetitionVoltagePhaseView)&&i==2?"ZY":CameraButtonNames[i];
                button.Text=planeName;
                button.BackColor=selected?HVX.PdReview.ReviewTheme.Active:HVX.PdReview.ReviewTheme.Surface;
                button.ForeColor=selected?Color.White:ForeColor;
                button.FlatAppearance.BorderColor=selected?HVX.PdReview.ReviewTheme.Active:HVX.PdReview.ReviewTheme.Border;
                string depth=mode==12?"相位":"电压";
                string axes=i==0?(mode==6?"位置／次数":"位置／放电量"):i==1?"位置／"+depth:(mode==6?"次数／":"放电量／")+depth;
                if(mode==5&&i==2)axes="电压／放电量";
                if(mode==6&&i==2)axes="电压／次数";
                if(mode==12&&i==1)axes=competitionProfile?"位置／相位":"相位／位置";
                if(mode==12&&i==2)axes="相位／放电量";
                if(CompetitionVoltagePhaseView)axes=i==0?(mode==8?"相位／次数":"相位／放电量"):i==1?"相位／电压":mode==8?"电压／次数":"电压／放电量";
                button.AccessibleName=i==3?(L("Restore 3D view","返回三维视角")):L(planeName+" plane",planeName+"平面："+axes);
                button.AccessibleDescription=selected?(L("Selected","已选择")):"";
                if(tooltip!=null)tooltip.SetToolTip(button,button.AccessibleName);
            }
            LayoutCameraToolbar();
        }

        void LayoutCameraToolbar()
        {
            if(cameraToolbar==null||cameraToolbar.IsDisposed)return;
            int buttonWidth=CameraButtonWidth,height=ViewActionButtonHeight;
            int right=ClientSize.Width-16-ChartExportReservedWidth-(LPrpdColorReferenceButtonVisible?ColorModeButtonWidth+6:0);
            cameraToolbar.Bounds=new Rectangle(Math.Max(0,right-CameraToolbarWidth),Math.Max(0,ClientSize.Height-ToolbarHeightForWidth(ClientSize.Width)-height-6),CameraToolbarWidth,height);
            for(int i=0;i<cameraButtons.Length;i++)cameraButtons[i].Bounds=new Rectangle(i*(buttonWidth+4),0,buttonWidth,height);
            if(chargeCountToggle!=null)chargeCountToggle.Bounds=new Rectangle(buttonWidth*4+18,0,ChargeCountToggleWidth,height);
            cameraToolbar.BringToFront();
        }

        int ViewActionButtonHeight { get { return Math.Max(26,Font.Height+8); } }
        int CameraButtonWidth { get { return Math.Max(42,TextRenderer.MeasureText("3D",Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width+14); } }
        int ChargeCountToggleWidth {get{return mode==5?TextRenderer.MeasureText(L("Count bins","次数分箱"),Font).Width+26:0;}}
        int CameraToolbarWidth { get { return CameraButtonWidth*4+12+(mode==5?ChargeCountToggleWidth+8:0); } }
        int CameraFooterReservedWidth { get { return CameraToolbarWidth+32+ChartExportReservedWidth+(LPrpdColorReferenceButtonVisible?ColorModeButtonWidth+6:0); } }
        [Browsable(false)]public bool ViewPlaneButtonsVisible {get{return SupportsPlaneViews;}}
        public Rectangle GetViewPlaneButtonBounds(int planeIndex)
        {
            if(planeIndex<0||planeIndex>3)throw new ArgumentOutOfRangeException("planeIndex");
            if(!ViewPlaneButtonsVisible||cameraToolbar==null)return Rectangle.Empty;
            int buttonIndex=planeIndex==0?3:planeIndex-1;
            Rectangle bounds=cameraButtons[buttonIndex].Bounds;bounds.Offset(cameraToolbar.Location);return bounds;
        }
        public void PerformViewPlaneButtonClick(int planeIndex)
        {
            if(planeIndex<0||planeIndex>3)throw new ArgumentOutOfRangeException("planeIndex");
            if(!ViewPlaneButtonsVisible)throw new InvalidOperationException("This spectrum does not support plane view buttons.");
            ViewPlaneIndex=planeIndex;
        }

        void GetPlaneAxes(out string horizontal,out string vertical,out double xmin,out double xmax,out double ymin,out double ymax)
        {
            int plane=Camera.PlaneIndex;
            horizontal=plane==3?view.YKey:view.XKey;vertical=plane==1?view.YKey:view.ZKey;
            xmin=plane==3?State.Y0:State.X0;xmax=plane==3?State.Y1:State.X1;
            ymin=plane==1?State.Y0:view.ZMin;ymax=plane==1?State.Y1:view.ZMax;
            if(IsChargeVoltagePlane)
            {horizontal=view.ZKey;vertical=view.YKey;xmin=view.ZMin;xmax=view.ZMax;ymin=State.Y0;ymax=State.Y1;}
            if(IsPhaseHorizontalPlane)
            {horizontal=view.ZKey;vertical=plane==2?view.XKey:view.YKey;xmin=view.ZMin;xmax=view.ZMax;ymin=plane==2?State.X0:State.Y0;ymax=plane==2?State.X1:State.Y1;}
        }
        void DrawPlaneAxes(Graphics g,Rectangle a)
        {
            string horizontal,vertical;double xmin,xmax,ymin,ymax;
            GetPlaneAxes(out horizontal,out vertical,out xmin,out xmax,out ymin,out ymax);
            using(var grid=new Pen(HVX.PdReview.ReviewTheme.Grid))using(var axis=new Pen(HVX.PdReview.ReviewTheme.SecondaryInk))using(var ink=new SolidBrush(ForeColor))
            {
                foreach(double value in AxisTicks(xmin,xmax,horizontal,Math.Max(1,Math.Min(5,a.Width/75))))
                {float x=a.Left+(float)((value-xmin)/(xmax-xmin)*a.Width);g.DrawLine(grid,x,a.Top,x,a.Bottom);string text=Tick(value,horizontal);var size=g.MeasureString(text,Font);g.DrawString(text,Font,ink,x-size.Width/2,a.Bottom+5);}
                foreach(double value in AxisTicks(ymin,ymax,vertical,5))
                {float y=a.Bottom-(float)((value-ymin)/(ymax-ymin)*a.Height);g.DrawLine(grid,a.Left,y,a.Right,y);string text=Tick(value,vertical);var size=g.MeasureString(text,Font);g.DrawString(text,Font,ink,a.Left-size.Width-8,y-size.Height/2);}
                g.DrawRectangle(axis,a);CenterText(g,AxisLabel(horizontal),Font,ink,new RectangleF(a.Left,a.Bottom+29,a.Width,24));
                var save=g.Save();g.TranslateTransform(a.Left-67,a.Top+a.Height/2);g.RotateTransform(-90);
                CenterText(g,AxisLabel(vertical),Font,ink,new RectangleF(-a.Height/2,-12,a.Height,24));g.Restore(save);
            }
        }
    }
}
