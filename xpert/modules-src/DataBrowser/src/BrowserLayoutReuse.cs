using System;
using System.Drawing;
using System.Windows.Forms;
using LabVIEWItemInformation;

namespace HVX.Controls
{
    public sealed partial class ProjectDataBrowserControl
    {
        // Display-only snapshots. No waveform, parsed project, or device evidence is cached here.
        NavigationMeasureSnapshot navigationMeasure;
        ProjectInformationLayoutSnapshot projectInformationLayout;

        int MeasureNavigationSelection(string text,int width)
        {
            Font font=navigationSelection.Font;
            int dpi=navigationSelection.DeviceDpi;
            if(navigationMeasure==null||!navigationMeasure.Matches(text,width,font,dpi,languageIndex))
                navigationMeasure=new NavigationMeasureSnapshot(text,width,font,dpi,languageIndex);
            return navigationMeasure.Height;
        }

        sealed class NavigationMeasureSnapshot
        {
            readonly string text;readonly int width,dpi,language;readonly Font font;
            internal readonly int Height;
            internal NavigationMeasureSnapshot(string text,int width,Font font,int dpi,int language)
            {
                this.text=text;this.width=width;this.font=font;this.dpi=dpi;this.language=language;
                Height=TextRenderer.MeasureText(text,font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix).Height;
            }
            internal bool Matches(string value,int availableWidth,Font currentFont,int currentDpi,int currentLanguage)
            {
                return text==value&&width==availableWidth&&font.Equals(currentFont)&&dpi==currentDpi&&language==currentLanguage;
            }
        }

        sealed class ProjectInformationLayoutSnapshot
        {
            readonly int width,height,dpi,language,itemLanguage;
            readonly long changeSequence;
            readonly bool loaded,metadataReadOnly,insulationReadOnly,horizontalScrollVisible;
            readonly Font font;
            readonly Size contentSize;
            readonly Size scrollExtent;
            internal ProjectInformationLayoutSnapshot(ItemInformationControl item,Panel viewport,int width,int height,int language)
            {
                this.width=width;this.height=height;this.language=language;
                dpi=item.DeviceDpi;itemLanguage=item.LanguageId;changeSequence=item.ChangeSequence;
                loaded=item.IsLoaded;metadataReadOnly=item.MetadataReadOnly;insulationReadOnly=item.InsulationReadOnly;
                font=item.Font;contentSize=item.Size;scrollExtent=viewport.AutoScrollMinSize;horizontalScrollVisible=viewport.HorizontalScroll.Visible;
            }
            internal bool Matches(ItemInformationControl item,Panel viewport,int currentWidth,int currentHeight,int currentLanguage)
            {
                // Applying another document increments ChangeSequence, including reloads at the same path.
                // Also verify output geometry: parent docking/scroll state cannot make a stale layout look current.
                return width==currentWidth&&height==currentHeight&&language==currentLanguage&&itemLanguage==item.LanguageId
                    &&dpi==item.DeviceDpi&&font.Equals(item.Font)&&changeSequence==item.ChangeSequence&&loaded==item.IsLoaded
                    &&metadataReadOnly==item.MetadataReadOnly&&insulationReadOnly==item.InsulationReadOnly
                    &&contentSize==item.Size&&scrollExtent==viewport.AutoScrollMinSize&&horizontalScrollVisible==viewport.HorizontalScroll.Visible;
            }
        }
    }
}
