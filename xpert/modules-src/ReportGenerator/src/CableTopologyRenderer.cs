using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using HVX.ProjectCables;

namespace HVX.ReportGenerator
{
    internal static class CableTopologyRenderer
    {
        internal const int Width = 3000;
        internal const int Height = 560;

        // The preview and document use the same saved block projection. Never
        // merge separate phase joints or substitute ITEM's global length.
        internal static ReportCableDiagramRow[] ReadRows(ProjectReportData project)
        {
            return ReportCableStructurePreview.DiagramRows(project.CableStructure);
        }

        internal static string Render(ProjectReportData project, int languageIndex, string outputPath)
        {
            if(project==null)throw new ArgumentNullException("project");
            project.RequireCurrentMetadata();
            ReportCableDiagramRow[] rows;
            string error=project.CableStructureError;
            try { rows=ReadRows(project); }
            catch(Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException)
            { rows=new ReportCableDiagramRow[0];error=ex.Message; }
            int largest=rows.Length==0?0:rows.Max(r=>r.Positions.Length);
            int columns=Math.Max(1,(Width-500)/260),lanes=Math.Max(1,(largest+columns-1)/columns);
            int rowHeight=128+lanes*54,height=Math.Max(Height,150+Math.Max(1,rows.Length)*rowHeight);
            string full=Path.GetFullPath(outputPath);Directory.CreateDirectory(Path.GetDirectoryName(full));
            using(var bitmap=new Bitmap(Width,height,PixelFormat.Format24bppRgb))
            {
                bitmap.SetResolution(300f,300f);
                using(var g=Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.White);g.SmoothingMode=SmoothingMode.AntiAlias;
                    g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    Draw(g,project,languageIndex,rows,error,rowHeight,columns);
                }
                bitmap.Save(full,ImageFormat.Png);
            }
            return full;
        }

        static void Draw(Graphics g,ProjectReportData project,int language,ReportCableDiagramRow[] rows,string error,int rowHeight,int columns)
        {
            using(var label=NewFont(36,FontStyle.Bold))
            using(var small=NewFont(33,FontStyle.Regular))
            using(var ink=new SolidBrush(ReportTheme.Ink))
            using(var muted=new SolidBrush(ReportTheme.Muted))
            using(var leftText=new StringFormat{Alignment=StringAlignment.Near,LineAlignment=StringAlignment.Center})
            using(var rightText=new StringFormat{Alignment=StringAlignment.Far,LineAlignment=StringAlignment.Center})
            using(var center=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})
            {
                g.DrawString(project.StartName??"",label,ink,new RectangleF(70,12,Width/2-100,94),leftText);
                g.DrawString(project.EndName??"",label,ink,new RectangleF(Width/2+30,12,Width/2-100,94),rightText);
                if(rows.Length==0)
                {
                    string detail=!ReportLanguages.IsChinese(language)&&ReportAttachmentText.ContainsHan(error)?"":error;
                    g.DrawString(ReportLanguages.Ui(language,"电缆结构数据缺失，未绘制推测结构。","Cable structure unavailable; no inferred geometry drawn.")+Environment.NewLine+detail,
                        small,muted,new RectangleF(100,155,Width-200,290),leftText);return;
                }
                double maximum=rows.Max(r=>r.Length);const float left=250,span=2500;
                for(int index=0;index<rows.Length;index++)
                {
                    var row=rows[index];float y=180+index*rowHeight,right=left+(float)(span*row.Length/maximum);
                    Color color=row.Integrated?ReportTheme.Muted:ReportCableStructurePreview.PhaseColor(row.Phase);
                    var state=g.Save();g.ScaleTransform(3,3);
                    ReportCableStructurePreview.DrawCable(g,left/3,right/3,y/3,row.Integrated,color,project.StartAccessory,project.EndAccessory);
                    foreach(double position in row.Positions)
                        ReportCableStructurePreview.DrawJoint(g,(left+(float)(position/maximum*span))/3,y/3,color);
                    g.Restore(state);
                    string phase=row.Integrated?"ALL":ReportCableStructurePreview.PhaseCaption(language,row.Phase);
                    g.DrawString(phase,label,ink,new RectangleF(left,y-74,span/2,52),leftText);
                    g.DrawString(row.Length.ToString("0.0",CultureInfo.InvariantCulture)+" m",label,ink,new RectangleF(Width-610,y-74,410,52),rightText);
                    g.DrawString("0.0 m",small,muted,new RectangleF(0,y-25,130,52),rightText);
                    for(int j=0;j<row.Positions.Length;j++)
                    {
                        float x=left+(float)(row.Positions[j]/maximum*span);
                        int count=Math.Min(columns,row.Positions.Length-(j/columns)*columns);
                        float labelX=left+(j%columns+.5f)*span/count;
                        float labelY=y+48+(j/columns)*54;
                        using(var line=new Pen(ReportTheme.Muted,2))g.DrawLine(line,x,y+31,labelX,labelY);
                        string text=row.Positions[j].ToString("0.0",CultureInfo.InvariantCulture)+" m";
                        var bounds=new RectangleF(labelX-128,labelY,256,52);
                        g.FillRectangle(Brushes.White,bounds);g.DrawString(text,small,ink,bounds,center);
                    }
                }
            }
        }
        static Font NewFont(float size,FontStyle style)
        {
            try{return new Font("Microsoft YaHei",size,style,GraphicsUnit.Pixel);}
            catch(ArgumentException){return new Font(FontFamily.GenericSansSerif,size,style,GraphicsUnit.Pixel);}
        }
    }
}
