using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        sealed class ConnectorHitArea
        {
            internal int Index;
            internal RectangleF MarkerBounds,LabelBounds;
        }
        readonly List<ConnectorHitArea> connectorHitAreas=new List<ConnectorHitArea>();
        RectangleF connectorHitBounds;
        ToolTip connectorToolTip;
        string currentConnectorToolTip=string.Empty;
        bool showCableMaterials;
        readonly List<RectangleF> cableEndpointMarkerBounds=new List<RectangleF>();
        readonly List<RectangleF> cableMaterialLabelBounds=new List<RectangleF>();

        [Category("Appearance"),DefaultValue(false)]
        [Description("Show the project's insulation names and colored segments along the bottom cable ruler. Does not reload waveforms or reset zoom.")]
        public bool ShowCableMaterials
        {
            get{return showCableMaterials;}
            set{if(showCableMaterials==value)return;showCableMaterials=value;Invalidate();}
        }

        string ConnectorLabel(int index,bool compact)
        {
            if(index<0||index>=connectors.Length)return string.Empty;
            string distance=PresentPosition(connectors[index]).ToString("0.###",CultureInfo.InvariantCulture)+" m";
            return distance;
        }

        void DrawConnectors(Graphics g,Rectangle p,Rectangle canvas,bool interactive)
        {
            // Export uses a different canvas and must not replace the live hit geometry.
            if(interactive){connectorHitAreas.Clear();connectorHitBounds=RectangleF.Empty;cableEndpointMarkerBounds.Clear();cableMaterialLabelBounds.Clear();}
            if(pulse.Length==0)return;
            DrawCableMaterialSegments(g,p,canvas,false,interactive);
            DrawCableEndpoints(g,p,canvas,interactive);
            if(!Visible(6,true))
            {
                if(interactive)ClearConnectorToolTip();
                DrawCableMaterialSegments(g,p,canvas,true,interactive);return;
            }
            using(var font=F(canvas,12,FontStyle.Regular))
            using(var brush=new SolidBrush(connectorColor))
            for(int i=0;i<connectors.Length;i++)
            {
                if(!IsFinite(connectors[i])||connectors[i]<0||connectors[i]>cableLengthM)continue;
                double index=PositionToWaveIndex(connectors[i]);
                if(!IsFinite(index)||index<viewX0||index>viewX1)continue;
                float x=X(index,p);
                g.FillPolygon(brush,new[]{new PointF(x-7,p.Bottom+2),new PointF(x+7,p.Bottom+2),new PointF(x,p.Bottom-9)});
                RectangleF labelBounds;
                if(!TryDrawBottomLabel(g,p,font,brush,ConnectorLabel(i,false),x,out labelBounds))
                    TryDrawBottomLabel(g,p,font,brush,ConnectorLabel(i,true),x,out labelBounds);
                if(interactive)
                {
                    var markerBounds=new RectangleF(x-9,p.Bottom-12,18,16);
                    var bounds=labelBounds.IsEmpty?markerBounds:RectangleF.Union(markerBounds,labelBounds);
                    connectorHitBounds=connectorHitAreas.Count==0?bounds:RectangleF.Union(connectorHitBounds,bounds);
                    connectorHitAreas.Add(new ConnectorHitArea{Index=i,MarkerBounds=markerBounds,LabelBounds=labelBounds});
                }
            }
            DrawCableMaterialSegments(g,p,canvas,true,interactive);
        }

        void DrawCableEndpoints(Graphics g,Rectangle p,Rectangle canvas,bool interactive)
        {
            if(!Visible(7,true)||!IsFinite(cableLengthM)||cableLengthM<=0)return;
            using(var font=F(canvas,12,FontStyle.Regular))
            using(var brush=new SolidBrush(connectorColor))
            foreach(double position in new[]{0.0,cableLengthM})
            {
                // An explicitly provided endpoint is already drawn in the connector pass.
                if(Visible(6,true)&&connectors.Any(v=>v==position))continue;
                double index=PositionToWaveIndex(position);
                if(!IsFinite(index)||index<viewX0||index>viewX1)continue;
                float x=X(index,p);
                g.FillPolygon(brush,new[]{new PointF(x-7,p.Bottom),new PointF(x+7,p.Bottom),new PointF(x,p.Bottom-10)});
                RectangleF label;
                TryDrawBottomLabel(g,p,font,brush,PresentPosition(position).ToString("0.###",CultureInfo.InvariantCulture)+" m",x,out label);
                if(interactive)cableEndpointMarkerBounds.Add(new RectangleF(x-9,p.Bottom-12,18,14));
            }
        }

        static Color CableMaterialColor(string name)
        {
            switch((name??string.Empty).Trim().ToUpperInvariant())
            {
                case "XLPE":return Color.FromArgb(55,142,183);
                case "EPR":case "HEPR":return Color.FromArgb(185,104,120);
                case "EVA":return Color.FromArgb(183,137,72);
                case "PVC":return Color.FromArgb(69,150,120);
                case "PILC":return Color.FromArgb(155,128,84);
                default:return Color.FromArgb(117,139,153);
            }
        }

        void DrawCableMaterialSegments(Graphics g,Rectangle p,Rectangle canvas,bool labels,bool interactive)
        {
            if(!showCableMaterials||!IsFinite(cableLengthM)||cableLengthM<=0)return;
            // Small secondary labels deliberately do not inherit the main curve's weight.
            using(var font=new Font(Font.FontFamily,Font.SizeInPoints,FontStyle.Regular,GraphicsUnit.Point))
            for(int i=0;i<currentMaterialNames.Length;i++)
            {
                double a=PositionToWaveIndex(currentMaterialStartsM[i]),b=PositionToWaveIndex(currentMaterialEndsM[i]);
                if(!IsFinite(a)||!IsFinite(b))continue;
                double left=Math.Max(viewX0,Math.Min(a,b)),right=Math.Min(viewX1,Math.Max(a,b));
                if(right<=left)continue;
                float x0=Math.Max(p.Left+1,X(left,p)),x1=Math.Min(p.Right-1,X(right,p));
                if(x1<=x0)continue;
                Color color=CableMaterialColor(currentMaterialNames[i]);
                if(!labels)
                {
                    using(var pen=new Pen(Color.FromArgb(150,color),2f))g.DrawLine(pen,x0,p.Bottom-3,x1,p.Bottom-3);
                    continue;
                }
                string text=currentMaterialNames[i];SizeF size=g.MeasureString(text,font);
                if(size.Width>x1-x0-12||p.Height<size.Height*4+18)continue;
                for(int row=0;row<2;row++)
                {
                    var bounds=new RectangleF((x0+x1-size.Width)/2,p.Bottom-size.Height-12-row*(size.Height+3),size.Width,size.Height);
                    var space=bounds;space.Inflate(4,2);
                    if(bottomLabelBounds.Any(r=>space.IntersectsWith(r)))continue;
                    using(var brush=new SolidBrush(Color.FromArgb(195,color)))g.DrawString(text,font,brush,bounds.Location);
                    bottomLabelBounds.Add(bounds);if(interactive)cableMaterialLabelBounds.Add(bounds);break;
                }
            }
        }

        string ConnectorToolTipAt(Point point)
        {
            if(!Visible(6,true)||pulse.Length==0||draggingCursor!=0||boxZoomActive||!connectorHitBounds.Contains(point))return string.Empty;
            StringBuilder text=null;
            foreach(var area in connectorHitAreas)
            {
                if(!area.MarkerBounds.Contains(point)&&!area.LabelBounds.Contains(point))continue;
                string label=ConnectorLabel(area.Index,false);
                if(label.Length==0)continue;
                if(text==null)text=new StringBuilder();else text.AppendLine();
                text.Append(label);
            }
            return text==null?string.Empty:text.ToString();
        }

        void UpdateConnectorToolTip(Point point)
        {
            string text=ConnectorToolTipAt(point);
            if(text==currentConnectorToolTip)return;
            if(text.Length==0){ClearConnectorToolTip();return;}
            if(connectorToolTip==null)connectorToolTip=new ToolTip{InitialDelay=300,ReshowDelay=100,AutoPopDelay=10000,ShowAlways=true};
            currentConnectorToolTip=text;
            connectorToolTip.SetToolTip(this,text);
        }

        void ClearConnectorToolTip()
        {
            if(currentConnectorToolTip.Length==0)return;
            currentConnectorToolTip=string.Empty;
            if(connectorToolTip!=null){connectorToolTip.Hide(this);connectorToolTip.SetToolTip(this,string.Empty);}
        }

        void DisposeConnectorToolTip()
        {
            if(connectorToolTip!=null){connectorToolTip.Dispose();connectorToolTip=null;}
            connectorHitAreas.Clear();currentConnectorToolTip=string.Empty;
        }
    }
}
