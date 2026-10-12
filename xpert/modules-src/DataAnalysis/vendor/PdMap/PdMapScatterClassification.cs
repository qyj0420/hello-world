using System;
using System.ComponentModel;
using System.Drawing;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        bool signalClassificationEnabled, standaloneOverlay;
        double interferenceThresholdPercent=double.NaN, pdThresholdPercent=double.NaN;
        int classificationConfidenceColumn=33, classificationConfidenceScale;
        double[,] overlayClassificationSource;
        int[] overlaySourceRows=Array.Empty<int>(), overlayClasses=Array.Empty<int>();
        double[] overlayConfidence=Array.Empty<double>();

        [Category("Classification"),DefaultValue(false)]
        [Description("Enable confidence-based display rules. This is not a validated physical PD diagnosis.")]
        public bool SignalClassificationEnabled {get{return signalClassificationEnabled;}set{if(signalClassificationEnabled==value)return;signalClassificationEnabled=value;ReclassifyOverlay();}}
        [Category("Classification")]
        public double InterferenceThresholdPercent {get{return interferenceThresholdPercent;}set{if(interferenceThresholdPercent.Equals(value))return;interferenceThresholdPercent=value;ReclassifyOverlay();}}
        [Category("Classification")]
        public double PdThresholdPercent {get{return pdThresholdPercent;}set{if(pdThresholdPercent.Equals(value))return;pdThresholdPercent=value;ReclassifyOverlay();}}
        [Category("Classification"),DefaultValue(33)]
        [Description("Zero-based confidence column: current positioning contract 33; explicit legacy mapping may use 22. Never use correlation column 21.")]
        public int ClassificationConfidenceColumn {get{return classificationConfidenceColumn;}set{if(classificationConfidenceColumn==value)return;classificationConfidenceColumn=value;RefreshOverlayConfidence();}}
        [Category("Classification"),DefaultValue(0)]
        [Description("Confidence input units: 0 = ratio [0,1]; 1 = percent [0,100]. No per-row unit guessing.")]
        public int ClassificationConfidenceScale {get{return classificationConfidenceScale;}set{if(classificationConfidenceScale==value)return;classificationConfidenceScale=value;ReclassifyOverlay();}}
        [Browsable(false)]
        public string ClassificationStatus
        {
            get
            {
                if(!signalClassificationEnabled)return L("Classification disabled","分类显示未启用");
                if(!ClassificationConfigured)return L("Unassessed: configure valid confidence thresholds, column and scale","未评估：请配置有效的确信度阈值、列和单位");
                int known=0;for(int i=0;i<overlayClasses.Length;i++)if(overlayClasses[i]>=0)known++;
                return String.Format(L("Rule-based display: {0} classified; {1} unassessed","规则分类显示：{0} 条已分类；{1} 条未评估"),known,overlayClasses.Length-known);
            }
        }
        public void ConfigureConfidenceClassification(double interferenceMaxPercent,double pdMinPercent,bool enabled)
        {interferenceThresholdPercent=interferenceMaxPercent;pdThresholdPercent=pdMinPercent;signalClassificationEnabled=enabled;ReclassifyOverlay();}
        bool ClassificationConfigured => IsFinite(interferenceThresholdPercent)&&IsFinite(pdThresholdPercent)&&interferenceThresholdPercent>=0&&interferenceThresholdPercent<pdThresholdPercent&&pdThresholdPercent<=100&&classificationConfidenceColumn>=0&&(classificationConfidenceScale==0||classificationConfidenceScale==1);
        bool ValidOverlayConfidence(double score) => IsFinite(score)&&score>=0&&score<=(classificationConfidenceScale==0?1:100)&&(classificationConfidenceScale==0||classificationConfidenceScale==1);

        void SetOverlayClassificationSource(double[,] source,int[] sourceRows)
        {overlayClassificationSource=source;overlaySourceRows=sourceRows??Array.Empty<int>();RefreshOverlayConfidence();}
        void RefreshOverlayConfidence()
        {
            int count=Math.Min(positions.Length,charges.Length);overlayConfidence=new double[count];
            bool readable=overlayClassificationSource!=null&&classificationConfidenceColumn>=0&&classificationConfidenceColumn<overlayClassificationSource.GetLength(1);
            for(int i=0;i<count;i++)
            {
                int row=i<overlaySourceRows.Length?overlaySourceRows[i]:-1;
                overlayConfidence[i]=readable&&row>=0&&row<overlayClassificationSource.GetLength(0)?overlayClassificationSource[row,classificationConfidenceColumn]:double.NaN;
                if(classificationConfidenceColumn==33&&(HVX.SignalEvidence.CoincidentConfidenceMath.IsCoincidentRow(overlayClassificationSource,row)||PdMapTimingEvidence.IsNegativeDelayRow(overlayClassificationSource,row)))overlayConfidence[i]=double.NaN;
            }
            ReclassifyOverlay();
        }
        void ReclassifyOverlay()
        {
            if(overlayClasses.Length!=overlayConfidence.Length)overlayClasses=new int[overlayConfidence.Length];
            bool active=signalClassificationEnabled&&ClassificationConfigured;
            double divisor=classificationConfidenceScale==0?100:1;
            for(int i=0;i<overlayClasses.Length;i++)
            {
                double score=overlayConfidence[i];
                overlayClasses[i]=!active||!ValidOverlayConfidence(score)?-1:score<=interferenceThresholdPercent/divisor?0:score>=pdThresholdPercent/divisor?2:1;
            }
            Invalidate(); // Do not reload/filter/reset view or move cursors on a display-rule change.
        }

        /// <summary>Snapshot columns: original source row, phase, position m, charge pC, confidence %, display class (-1 unassessed, 0 interference, 1 suspected, 2 PD).</summary>
        public double[,] GetSignalClassMatrix()
        {
            int count=Math.Min(positions.Length,charges.Length);var result=new double[count,6];
            for(int i=0;i<count;i++)
            {
                result[i,0]=i<overlaySourceRows.Length?overlaySourceRows[i]:i;
                result[i,1]=i<overlayPhases.Length?overlayPhases[i]:0;result[i,2]=positions[i];result[i,3]=charges[i];
                double score=i<overlayConfidence.Length?overlayConfidence[i]:double.NaN;
                result[i,4]=ValidOverlayConfidence(score)?score*(classificationConfidenceScale==0?100:1):double.NaN;
                result[i,5]=i<overlayClasses.Length?overlayClasses[i]:-1;
            }
            return result;
        }

        static Color ScatterPhaseColor(int phase)
        {return phase==1?Color.FromArgb(154,103,0):phase==2?Color.FromArgb(46,155,98):phase==3?Color.FromArgb(240,75,58):Color.FromArgb(82,91,104);}
        static Color ScatterClassColor(int phase,int classification)
        {return classification==0?Color.FromArgb(155,163,171):Color.FromArgb(classification==1?85:255,ScatterPhaseColor(phase));}

        void DrawClassifiedOverlay(Graphics g,Rectangle plot)
        {
            // Sixteen reusable style pairs per paint, not two GDI objects per point.
            var pens=new Pen[16];var brushes=new SolidBrush[16];var triangle=new PointF[3];
            try
            {
                int count=Math.Min(positions.Length,charges.Length);
                for(int i=0;i<count;i++)
                {
                    if(!IsFinite(positions[i])||!IsFinite(charges[i]))continue;
                    double index=PositionToWaveIndex(positions[i]);if(!IsFinite(index)||index<viewX0||index>viewX1)continue;
                    float x=X(index,plot),y=Y(charges[i],plot);if(float.IsNaN(x)||float.IsNaN(y)||float.IsInfinity(x)||float.IsInfinity(y)||y<plot.Top-5||y>plot.Bottom+5)continue;
                    double rawPhase=i<overlayPhases.Length?overlayPhases[i]:0;
                    int phase=rawPhase>=1&&rawPhase<=3&&rawPhase==Math.Round(rawPhase)?(int)rawPhase:0;
                    int classification=i<overlayClasses.Length?overlayClasses[i]:-1,key=phase*4+classification+1;
                    if(pens[key]==null){Color color=ScatterClassColor(phase,classification);pens[key]=new Pen(color,1.25f);brushes[key]=new SolidBrush(color);}
                    Pen pen=pens[key];SolidBrush fill=brushes[key];bool filled=classification>=0;
                    if(phase==1){if(filled)g.FillEllipse(fill,x-3.5f,y-3.5f,7,7);g.DrawEllipse(pen,x-3.5f,y-3.5f,7,7);if(!filled)g.FillEllipse(fill,x-1,y-1,2,2);}
                    else if(phase==2){if(filled)g.FillRectangle(fill,x-3.5f,y-3.5f,7,7);g.DrawRectangle(pen,x-3.5f,y-3.5f,7,7);}
                    else if(phase==3){triangle[0]=new PointF(x,y-4.5f);triangle[1]=new PointF(x-4.5f,y+3.5f);triangle[2]=new PointF(x+4.5f,y+3.5f);if(filled)g.FillPolygon(fill,triangle);g.DrawPolygon(pen,triangle);}
                    else{g.DrawLine(pen,x-3,y,x+3,y);g.DrawLine(pen,x,y-3,x,y+3);}
                }
            }
            finally{for(int i=0;i<pens.Length;i++){if(pens[i]!=null)pens[i].Dispose();if(brushes[i]!=null)brushes[i].Dispose();}}
        }
    }
}
