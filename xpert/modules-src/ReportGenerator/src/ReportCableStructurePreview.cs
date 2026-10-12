using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using HVX.ProjectCables;

namespace HVX.ReportGenerator
{
    internal sealed class ReportCableDiagramRow
    {
        internal string Phase;
        internal double Length;
        internal double[] Positions;
        internal bool Integrated;
    }
    // Display-only projection of saved joint positions. No insulation/material types.
    internal sealed class ReportCableStructurePreview : UserControl
    {
        readonly ToolTip tip = new ToolTip();
        readonly Panel drawing = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        ProjectReportData project;
        int language;
        CableJointSnapshot structure;
        ReportCableDiagramRow[] diagramRows = new ReportCableDiagramRow[0];
        string structureError;
        int headingHeight=40,rowHeight=88,labelWidth=106;
        const TextFormatFlags ScrolledText = TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping;
        internal ReportCableStructurePreview()
        {
            DoubleBuffered = true; BackColor = ReportTheme.Surface;
            Controls.Add(drawing); drawing.Paint += PaintStructure;
            drawing.Resize += delegate { MeasureStructure(); drawing.Invalidate(); };
        }
        internal void SetProject(ProjectReportData value, int languageIndex)
        {
            project = value; language = languageIndex;
            // Both the table and the drawing use the same loaded metadata.
            // Disk changes are surfaced by the owner, never merged silently.
            structure=project==null?null:project.CableStructure;
            structureError=project==null?"":project.CableStructureError;
            diagramRows=DiagramRows(structure);
            double[] positions = structure == null ? new double[0] : structure.Blocks.SelectMany(b=>b.Positions.Skip(1).Take(b.Positions.Count-2)).ToArray();
            MeasureStructure();
            tip.SetToolTip(drawing, project == null ? "" : project.StartName + " → " + project.EndName +
                Environment.NewLine + string.Join("; ", positions.Select(p => p.ToString("0.0", CultureInfo.CurrentCulture) + " m")));
            drawing.Invalidate();
        }
        void MeasureStructure()
        {
            if(drawing==null)return;
            int rows=Math.Max(1,diagramRows.Length);
            int largest=diagramRows.Length==0?0:diagramRows.Max(b=>b.Positions.Length);
            labelWidth=Math.Max(106,TextRenderer.MeasureText("50000.0 m",Font).Width+16);
            int width=Math.Max(drawing.ClientSize.Width,Math.Max(500,largest*(labelWidth+6)+160));
            int half=Math.Max(100,(width-48)/2);
            headingHeight=Math.Max(40,project==null?40:Math.Max(TextRenderer.MeasureText(project.StartName,Font,new Size(half,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height,TextRenderer.MeasureText(project.EndName,Font,new Size(half,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height)+10);
            rowHeight=Math.Max(88,Font.Height*2+50);
            var size=new Size(Math.Max(500,largest*(labelWidth+6)+160),headingHeight+22+rows*rowHeight);
            if(drawing.AutoScrollMinSize!=size)drawing.AutoScrollMinSize=size;
        }
        protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);MeasureStructure();Invalidate();}
        void PaintStructure(object sender, PaintEventArgs e)
        {
            if (project == null) return;
            if(structure==null){TextRenderer.DrawText(e.Graphics,ReportLanguages.Ui(language,"电缆结构暂不可显示：","Cable structure unavailable: ")+structureError,Font,ClientRectangle,ReportTheme.Muted,TextFormatFlags.WordBreak);return;}
            var g = e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; g.TranslateTransform(drawing.AutoScrollPosition.X, drawing.AutoScrollPosition.Y);
            int width = Math.Max(drawing.ClientSize.Width, drawing.AutoScrollMinSize.Width), half = (width - 48) / 2;
            using (var ink = new SolidBrush(ReportTheme.Ink))
            using (var thin = new Pen(ReportTheme.Muted, 1))
            {
                var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | ScrolledText;
                TextRenderer.DrawText(g, project.StartName, Font, new Rectangle(12, 4, half, headingHeight-6), ReportTheme.Ink, flags);
                TextRenderer.DrawText(g, project.EndName, Font, new Rectangle(width - half - 12, 4, half, headingHeight-6), ReportTheme.Ink, flags | TextFormatFlags.Right);
                double maximumLength=diagramRows.Max(b=>b.Length);
                int row=0;
                foreach(var block in diagramRows) {
                int y = headingHeight+Font.Height+16+rowHeight*row++; int left = Math.Max(85,TextRenderer.MeasureText("0.0 m",Font).Width+32);
                int span = width - 60 - left;
                int right = left + Math.Max(1,(int)Math.Round(span*block.Length/maximumLength));
                bool integrated=block.Integrated;
                Color phaseColor=integrated?ReportTheme.Muted:PhaseColor(block.Phase);
                DrawCable(g,left,right,y,integrated,phaseColor,project.StartAccessory,project.EndAccessory);
                string phase=integrated?"ALL":PhaseCaption(language,block.Phase);
                TextRenderer.DrawText(g,phase,Font,new Rectangle(left,y-Font.Height-10,span/2,Font.Height+6),ReportTheme.Ink,TextFormatFlags.SingleLine|ScrolledText);
                var positions = block.Positions;
                for (int i = 0; i < positions.Length; i++) {
                    float x = left + (float)(positions[i] / block.Length) * (right - left);
                    DrawJoint(g,x,y,phaseColor);
                    // Evenly spaced labels keep clustered joints legible; leaders retain true positions.
                    float labelX = left + (i + .5f) * span / Math.Max(1, positions.Length);
                    g.DrawLine(thin, x, y + 8, labelX, y + 21);
                    string label = positions[i].ToString("0.0", CultureInfo.CurrentCulture) + " m";
                    TextRenderer.DrawText(g, label, Font, new Rectangle((int)labelX - labelWidth/2, y + 24, labelWidth, Font.Height+6), ReportTheme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | ScrolledText);
                }
                TextRenderer.DrawText(g, "0.0 m", Font, new Point(2, y-7), ReportTheme.Muted, ScrolledText);
                TextRenderer.DrawText(g, block.Length.ToString("0.0", CultureInfo.CurrentCulture) + " m", Font, new Rectangle(width-40-labelWidth, y-Font.Height-10, labelWidth, Font.Height+6), ReportTheme.Ink, TextFormatFlags.Right | TextFormatFlags.SingleLine | ScrolledText);
                }
            }
        }
        internal static ReportCableDiagramRow[] DiagramRows(CableJointSnapshot snapshot)
        {
            if(snapshot==null)return new ReportCableDiagramRow[0];
            // A common three-core drawing is valid only when every actual block
            // has the same geometry. Equal total length alone cannot turn
            // contradictory historical joint positions into common joints.
            bool integrated=snapshot.Mode==CableJointMode.SharedThreeCore&&snapshot.Blocks.Count>0&&
                snapshot.Blocks.All(b=>b.Positions.SequenceEqual(snapshot.Blocks[0].Positions));
            if(integrated)return new[]{new ReportCableDiagramRow {Phase="ALL",Length=snapshot.Blocks[0].Length,Integrated=true,
                Positions=snapshot.Blocks[0].Positions.Skip(1).Take(snapshot.Blocks[0].Positions.Count-2).ToArray()}};
            return snapshot.Blocks.Select(b=>new ReportCableDiagramRow {Phase=b.Phase,Length=b.Length,
                Positions=b.Positions.Skip(1).Take(b.Positions.Count-2).ToArray()}).ToArray();
        }
        internal static string PhaseCaption(int language,string phase)
        {
            string canonical=(phase??string.Empty).Trim().ToUpperInvariant();
            if(canonical=="A"||canonical=="A相")return ReportLanguages.Ui(language,"A相","L1");
            if(canonical=="B"||canonical=="B相")return ReportLanguages.Ui(language,"B相","L2");
            if(canonical=="C"||canonical=="C相")return ReportLanguages.Ui(language,"C相","L3");
            if(canonical=="ALL")return "ALL";
            return string.IsNullOrWhiteSpace(phase)?ReportLanguages.Ui(language,"未指定相别","Unspecified phase"):phase;
        }
        internal static Color PhaseColor(string phase)
        {
            return phase=="A"||phase=="A相"?Color.FromArgb(209,154,18):phase=="B"||phase=="B相"?Color.FromArgb(43,154,104):phase=="C"||phase=="C相"?Color.FromArgb(207,77,81):Color.FromArgb(96,117,138);
        }
        internal static void DrawCable(Graphics g,float left,float right,float y,bool integrated,Color phaseColor,string startType=null,string endType=null)
        {
            CableVisualStyle.DrawBody(g,left,right,y,integrated,phaseColor);
            CableVisualStyle.DrawTermination(g,left,y,-1,startType);CableVisualStyle.DrawTermination(g,right,y,1,endType);
        }
        static void DrawTermination(Graphics g,float x,float y,bool pointsRight,Color color)
        {
            int d=pointsRight?1:-1;
            var shape=new[]{new PointF(x-d*18,y-4),new PointF(x-d*4,y-7),new PointF(x+d*16,y-4),new PointF(x+d*16,y+4),new PointF(x-d*4,y+7),new PointF(x-d*18,y+4)};
            using(var fill=new SolidBrush(Color.FromArgb(222,color)))using(var edge=new Pen(Color.FromArgb(91,68,66))){g.FillPolygon(fill,shape);g.DrawPolygon(edge,shape);for(int ring=-1;ring<=1;ring++)g.DrawLine(edge,x+d*ring*6,y-7,x+d*ring*6,y+7);}
        }
        internal static void DrawJoint(Graphics g,float x,float y,Color color)
        {
            using(var fill=new SolidBrush(Color.FromArgb(220,229,233)))using(var edge=new Pen(color,1.3f))
            {g.FillRectangle(fill,x-6,y-10,12,20);g.DrawRectangle(edge,x-6,y-10,12,20);g.DrawLine(edge,x-3,y-9,x-3,y+9);g.DrawLine(edge,x+3,y-9,x+3,y+9);}
        }
        protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
    }
}
