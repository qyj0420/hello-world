using System;
using System.Drawing;
using System.Windows.Forms;
namespace HVX.PdReview
{
    internal sealed class ReviewRangeFilterDialog : Form
    {
        readonly RangeEditor charge,voltage,count;
        readonly double[] defaults;
        readonly int languageIndex;
        readonly CheckBox u0Mode;
        string L(string en,string zh){return ReviewLanguages.Text(languageIndex,en,zh);}
        internal bool ChargeEnabled { get { return charge.Active; } }
        internal bool VoltageEnabled { get { return voltage.Active; } }
        internal bool CountEnabled { get { return count.Active; } }
        internal double MinimumCharge { get { return charge.Lower; } }
        internal double MaximumCharge { get { return charge.Upper; } }
        internal double MinimumVoltage { get { return voltage.Lower; } }
        internal double MaximumVoltage { get { return voltage.Upper; } }
        internal int MinimumCount { get { return (int)count.Lower; } }
        internal ReviewRangeFilterDialog(PdReviewControl owner,bool english)
        {
            languageIndex=owner.LanguageIndex;
            Text=L("Data filters","数据过滤");Name="ReviewRangeFilterDialog";Font=owner.Font;
            FormBorderStyle=FormBorderStyle.FixedDialog;StartPosition=FormStartPosition.CenterParent;
            ShowInTaskbar=false;MaximizeBox=MinimizeBox=false;ClientSize=new Size(552,440);
            BackColor=Color.FromArgb(246,248,251);ForeColor=ReviewTheme.Ink;
            defaults=owner.ObservedRangeDefaults();
            charge=new RangeEditor(L("Charge / pC","放电量 / pC"),0,false,english,languageIndex){Name="ChargeRange",Location=new Point(12,12)};
            voltage=new RangeEditor(L("Voltage / kV","电压 / kV"),1,false,english,languageIndex){Name="VoltageRange",Location=new Point(192,12)};
            count=new RangeEditor(L("Count ≥ N","放电次数 ≥ N"),0,true,english,languageIndex){Name="CountRange",Location=new Point(372,12)};
            Controls.AddRange(new Control[]{charge,voltage,count});
            double u0=owner.FilterOperatingVoltageKv;
            bool validU0=!double.IsNaN(u0)&&!double.IsInfinity(u0)&&u0>0&&defaults[3]/u0<=1e20;
            u0Mode=new RoundedReviewToggle{Name="VoltageU0Mode",Text="×U0",Appearance=Appearance.Button,AutoSize=false,
                Location=new Point(99,20),Size=new Size(58,26),BackColor=ReviewTheme.Accent,ForeColor=Color.White,Enabled=validU0};
            u0Mode.AccessibleName="U0";u0Mode.AccessibleDescription=validU0?"U0 = "+u0.ToString("0.###")+" kV":L("Set rated U0 first.","请先设置额定相电压 U0。");
            voltage.Controls.Add(u0Mode);voltage.ReserveModeButtonSpace();
            u0Mode.CheckedChanged+=(s,e)=>{voltage.SetDisplayScale(u0Mode.Checked?u0:1,1);voltage.Text=L("Voltage","电压")+(u0Mode.Checked?" / U0":" / kV");};
            ResetDefaults();
            if(owner.RangeChargeFilterEnabled){charge.SetValues(owner.RangeChargeBoundsPc[0],owner.RangeChargeBoundsPc[1]);charge.Active=true;}
            if(owner.RangeVoltageFilterEnabled){voltage.SetValues(owner.RangeVoltageBoundsKv[0],owner.RangeVoltageBoundsKv[1]);voltage.Active=true;}
            if(owner.RangeCountFilterEnabled){count.SetValues(owner.MinimumRangeDischargeCount,Math.Max(defaults[4],owner.MinimumRangeDischargeCount));count.Active=true;}
            var ok=MakeButton(L("Apply","确定"),16);var reset=MakeButton(L("Default","默认"),196);var cancel=MakeButton(L("Cancel","取消"),376);
            ok.Name="ApplyRangeFilters";reset.Name="ResetRangeDefaults";cancel.Name="CancelRangeFilters";
            ok.DialogResult=DialogResult.OK;cancel.DialogResult=DialogResult.Cancel;reset.Click+=(s,e)=>ResetDefaults();
            Controls.AddRange(new Control[]{ok,reset,cancel});AcceptButton=ok;CancelButton=cancel;
        }
        Button MakeButton(string text,int x)
        {return new RoundedReviewButton{Text=text,Location=new Point(x,390),Size=new Size(160,36),BackColor=ReviewTheme.Accent,ForeColor=Color.White,FlatStyle=FlatStyle.Flat};}
        void ResetDefaults()
        {charge.SetValues(defaults[0],defaults[1]);voltage.SetValues(defaults[2],defaults[3]);count.SetValues(0,defaults[4]);charge.Active=voltage.Active=count.Active=false;}

        sealed class RangeEditor : GroupBox
        {
            readonly CheckBox enabled;
            readonly NumericUpDown lower,upper;
            readonly VerticalInterval interval;
            readonly bool countOnly;
            int digits;
            double scale=1,canonicalLower,canonicalUpper;
            bool syncing;
            internal bool Active {get{return enabled.Checked;}set{enabled.Checked=value;}}
            internal double Lower {get{return canonicalLower;}}
            internal double Upper {get{return canonicalUpper;}}
            internal RangeEditor(string caption,int decimals,bool onlyMinimum,bool english,int languageIndex=0)
            {
                Text=caption;Size=new Size(168,364);digits=decimals;countOnly=onlyMinimum;
                enabled=new CheckBox{Name="Enabled",Text=ReviewLanguages.Text(languageIndex,"Enable","开启"),Location=new Point(26,23),AutoSize=true};
                upper=Number("Maximum",new Point(22,52),decimals);lower=Number("Minimum",new Point(22,314),decimals);
                if(onlyMinimum)upper.Maximum=lower.Maximum=int.MaxValue;
                interval=new VerticalInterval{Location=new Point(18,86),Size=new Size(130,220)};
                Controls.AddRange(new Control[]{enabled,upper,interval,lower});
                enabled.CheckedChanged+=(s,e)=>{lower.Enabled=interval.Enabled=enabled.Checked;upper.Enabled=enabled.Checked&&!countOnly;};
                lower.ValueChanged+=(s,e)=>Changed(true);upper.ValueChanged+=(s,e)=>Changed(false);
                interval.IntervalChanged+=(s,e)=>{
                    if(syncing)return;
                    bool lowEdited=interval.LowerHandleChanged;
                    var edited=lowEdited?lower:upper;decimal value=DisplayValue(lowEdited?interval.Low:interval.High);
                    if(edited.Value==value){interval.SetRange(0,interval.Maximum,(double)lower.Value,(double)upper.Value,countOnly);return;}
                    syncing=true;edited.Value=value;
                    syncing=false;Changed(lowEdited);
                };
                lower.Enabled=interval.Enabled=upper.Enabled=false;
            }
            static NumericUpDown Number(string name,Point point,int decimals)
            {return new NumericUpDown{Name=name,Location=point,Width=124,DecimalPlaces=decimals,Minimum=0,Maximum=100000000000000000000M,Increment=decimals==0?1M:.1M};}
            decimal Clamp(double v){return Math.Max(lower.Minimum,Math.Min(lower.Maximum,(decimal)v));}
            decimal DisplayValue(double value){return decimal.Round(Clamp(value),digits,MidpointRounding.AwayFromZero);}
            internal void ReserveModeButtonSpace(){enabled.AutoSize=false;enabled.AutoEllipsis=true;enabled.Location=new Point(12,23);enabled.Size=new Size(82,24);}
            internal void SetDisplayScale(double value,int precision)
            {
                if(double.IsNaN(value)||double.IsInfinity(value)||value<=0||canonicalUpper/value>1e20)throw new ArgumentOutOfRangeException("value");
                scale=value;digits=precision;syncing=true;lower.DecimalPlaces=upper.DecimalPlaces=digits;lower.Increment=upper.Increment=digits==0?1M:.1M;
                RenderCanonical();syncing=false;
            }
            void RenderCanonical()
            {
                lower.Value=DisplayValue(canonicalLower/scale);
                upper.Value=DisplayValue(canonicalUpper/scale);
                interval.SetRange(0,Math.Max(digits==0?1:.1,(double)upper.Value),(double)lower.Value,(double)upper.Value,countOnly);
            }
            internal void SetValues(double lo,double hi)
            {
                if(double.IsNaN(lo)||double.IsNaN(hi)||lo<0||hi<lo||hi>1e20)throw new ArgumentException("Filter bounds cannot be represented in the dialog.");
                syncing=true;
                // Display rounding must not alter saved kV bounds or exclude an untouched observed extremum.
                canonicalLower=lo;canonicalUpper=hi;
                RenderCanonical();
                syncing=false;
            }
            void Changed(bool lowEdited)
            {
                if(syncing)return;syncing=true;
                // Commit only the edited endpoint; its rounded counterpart may hide extra precision.
                var edited=lowEdited?lower:upper;
                edited.Value=decimal.Round(edited.Value,digits,MidpointRounding.AwayFromZero);
                if(lowEdited){canonicalLower=(double)edited.Value*scale;if(canonicalLower>canonicalUpper)canonicalUpper=canonicalLower;}
                else{canonicalUpper=(double)edited.Value*scale;if(canonicalUpper<canonicalLower)canonicalLower=canonicalUpper;}
                double intervalMaximum=interval.Maximum;RenderCanonical();
                interval.SetRange(0,Math.Max(intervalMaximum,interval.Maximum),(double)lower.Value,(double)upper.Value,countOnly);
                syncing=false;
            }
        }
        sealed class VerticalInterval : Control
        {
            internal double Low,High,Maximum=1;
            double minimum;bool lowerDrag,countOnly;
            internal bool LowerHandleChanged {get{return lowerDrag;}}
            internal event EventHandler IntervalChanged;
            internal VerticalInterval(){SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true);TabStop=false;}
            internal void SetRange(double lo,double hi,double low,double high,bool onlyLow)
            {minimum=lo;Maximum=hi;Low=low;High=high;countOnly=onlyLow;Invalidate();}
            int Y(double v){return 12+(int)Math.Round((Maximum-v)/(Maximum-minimum)*Math.Max(1,Height-24));}
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);int x=Width-24;
                using(var p=new Pen(Color.Silver,4))e.Graphics.DrawLine(p,x,12,x,Height-12);
                using(var p=new Pen(Enabled?ReviewTheme.Accent:Color.DarkGray,5))e.Graphics.DrawLine(p,x,Y(High),x,Y(Low));
                for(int i=0;i<=4;i++){int y=12+(Height-24)*i/4;double value=Maximum-(Maximum-minimum)*i/4;
                    TextRenderer.DrawText(e.Graphics,value.ToString(countOnly?"0":"0.#"),Font,new Rectangle(0,y-9,Width-35,20),ForeColor,TextFormatFlags.Right);
                    e.Graphics.DrawLine(Pens.Gray,x-8,y,x+5,y);}
                foreach(double v in new[]{Low,High}){var rect=new Rectangle(x-9,Y(v)-5,18,10);e.Graphics.FillRectangle(Enabled?Brushes.White:Brushes.LightGray,rect);e.Graphics.DrawRectangle(Pens.Gray,rect);}
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {base.OnMouseDown(e);if(e.Button!=MouseButtons.Left||!Enabled)return;lowerDrag=countOnly||Math.Abs(e.Y-Y(Low))<=Math.Abs(e.Y-Y(High));Capture=true;MoveHandle(e.Y);}
            protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(Capture)MoveHandle(e.Y);}
            protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(e.Button==MouseButtons.Left)Capture=false;}
            void MoveHandle(int y)
            {
                double v=Maximum-(Math.Max(12,Math.Min(Height-12,y))-12.0)/Math.Max(1,Height-24)*(Maximum-minimum);
                if(lowerDrag)Low=Math.Min(High,Math.Max(minimum,v));else High=Math.Max(Low,Math.Min(Maximum,v));
                if(IntervalChanged!=null)IntervalChanged(this,EventArgs.Empty);Invalidate();
            }
        }
    }
}
