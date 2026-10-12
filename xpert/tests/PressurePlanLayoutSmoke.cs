using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Real WinForms geometry with synthetic ordered rows only. No hardware/SDK.
internal static class PressurePlanLayoutSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks; static readonly List<string> failures=new List<string>();
    static void Check(bool ok,string message){checks++;if(!ok)failures.Add(message);}
    static object Field(object c,string n){return c.GetType().GetField(n,All).GetValue(c);}
    static void Set(object c,string n,object value){c.GetType().GetProperty(n).SetValue(c,value,null);}
    static object Get(object c,string n){return c.GetType().GetProperty(n).GetValue(c,null);}
    static object Call(object c,string n,params object[] a){return c.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(c,a);}
    static IEnumerable<Control> Descendants(Control c){foreach(Control child in c.Controls){yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    static void Settle(Control c){for(int i=0;i<4;i++){Call(c,"RefreshPressureSidebarLayout");c.PerformLayout();Application.DoEvents();}}
    static Rectangle InModule(Control c,Control module){return new Rectangle(module.PointToClient(c.PointToScreen(Point.Empty)),c.ClientSize);}
    static void Fits(Control c,Control module,string tag)
    {
        Check(c.Visible&&c.ClientSize.Width>0&&c.ClientSize.Height>0,tag+" visible "+c.Name);
        Rectangle bounds=c.RectangleToScreen(c.ClientRectangle);
        for(Control p=c.Parent;p!=null;p=p.Parent){Check(p.RectangleToScreen(p.ClientRectangle).Contains(bounds),tag+" clipped "+c.Name+" within "+p.Name);if(p==module)break;}
    }
    static void Capture(Control c,string path){using(var image=new Bitmap(c.Width,c.Height)){c.DrawToBitmap(image,new Rectangle(Point.Empty,c.Size));image.Save(path);}}
    [STAThread] static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string root=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"plan-layout-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(qa);
            var asm=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            // Includes zero, repeats and descending voltages: layout must not
            // sort, merge or mutate the eleven real grid rows.
            double[] u0={0,.4,.5,.6,.8,1,1.2,1.5,1.2,.5,0};int[] runs={1,2,3,1,2,3,1,2,3,2,1};double[] rms=u0.Select(v=>v*8.7).ToArray();
            foreach(Size size in new[]{new Size(1180,720),new Size(1200,720),new Size(1920,1080),new Size(1366,768)})foreach(float scale in new[]{1f,1.25f,1.5f})foreach(int language in new[]{0,1,18})
            using(var f=new Form{ClientSize=size,AutoScaleMode=AutoScaleMode.None,ShowInTaskbar=false,Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual})
            using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.PressureTestControl",true)))
            {
                string tag=size.Width+"x"+size.Height+"-scale"+scale.ToString(CultureInfo.InvariantCulture)+"-lang"+language;
                Set(c,"EmbeddedMode",true);Set(c,"SimulationMode",true);Set(c,"FrequencyReminderEnabled",false);Set(c,"MaximumPeakVoltageKv",50d);Set(c,"BaseU0VoltageKv",8.7d);Set(c,"LanguageIndex",language);
                Call(c,"SetPressurePlan",u0,runs,rms);Call(c,"SetSelectedPlanStep",0);Call(c,"SetPressureProgramMode","PLAN");
                foreach(var item in Descendants(c).Concat(new[]{c}).Select(x=>new{Control=x,Font=x.Font}).ToArray())item.Control.Font=new Font(item.Font.FontFamily,item.Font.SizeInPoints*scale,item.Font.Style);
                c.Dock=DockStyle.None;c.Bounds=new Rectangle(Point.Empty,size);f.Controls.Add(c);f.Show();c.Size=size;Settle(c);
                var grid=(DataGridView)Field(c,"pressurePlanGrid");var settings=(TableLayoutPanel)c.Controls.Find("PressureSettingsHost",true).Single();var right=c.Controls.Find("PressureSidebar",true).Single();
                Check(c.ClientSize==size,tag+" actual viewport");Check(grid.Visible&&grid.Rows.Count==11,tag+" eleven real plan rows");Fits(grid,c,tag);
                var left=c.Controls.Find("RealtimeSidebar",true).Single();var center=c.Controls.Find("CenterChartsHost",true).Single();
                if(scale==1f&&size.Width>=1366){Check(left.Width>=160&&left.Width<=190,tag+" narrow left sidebar "+left.Width);Check(right.Width>=215&&right.Width<=(language==1?290:270),tag+" narrow plan sidebar "+right.Width);Check(center.Width>=size.Width-510,tag+" wider central charts "+center.Width);}
                int planSidebarWidth=right.Width;
                int completeRows=Enumerable.Range(0,grid.Rows.Count).Count(i=>{var r=grid.GetRowDisplayRectangle(i,false);return r.Height==grid.Rows[i].Height&&r.Top>=grid.ColumnHeadersHeight&&r.Bottom<=grid.ClientSize.Height;});
                Console.WriteLine(tag+" GRID="+grid.ClientSize+" ROWS="+completeRows+" SETTINGS="+settings.ClientSize+" ROW_HEIGHTS="+String.Join(",",settings.GetRowHeights()));
                Check(completeRows>=(size.Width==1920?11:5),tag+" visible complete plan rows, actual="+completeRows);
                foreach(DataGridViewColumn column in grid.Columns)
                {
                    Size heading=TextRenderer.MeasureText(column.HeaderText,grid.ColumnHeadersDefaultCellStyle.Font,new Size(Math.Max(1,column.Width-6),Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding);
                    Check(heading.Height<=grid.ColumnHeadersHeight-4,tag+" readable heading "+column.HeaderText);
                    for(int row=0;row<grid.Rows.Count;row++)
                    {
                        var cell=grid.Rows[row].Cells[column.Index];Size text=TextRenderer.MeasureText(Convert.ToString(cell.Value),grid.DefaultCellStyle.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding);
                        Check(text.Width<=column.Width-6&&text.Height<=grid.Rows[row].Height-4,tag+" readable complete cell "+row+"/"+column.Index);
                    }
                }
                Check(settings.GetRowHeights().Last()<=2,tag+" no spare-height filler outside PLAN");
                var essentials=new[]{"normalModeButton","advancedModeButton","automaticRangeButton","manualRangeButton","rangeSelectorButton","pressureActionButton","automaticPressureCheck"}.Select(n=>(Control)Field(c,n)).Concat(new[]{"PhaseAButton","PhaseBButton","PhaseCButton"}.Select(n=>c.Controls.Find(n,true).Single())).ToArray();
                var tabs=c.Controls.Find("CompactSettingsTabs",true).Single();
                var persistent=essentials.Skip(5).ToArray();
                // The existing compact layout puts Mode and Range on separate
                // reachable pages. An intentionally inactive page is not a
                // clipped visible control: exercise the actual navigation too.
                foreach(var control in tabs.Visible?persistent:essentials)Fits(control,c,tag);
                Rectangle gridBounds=InModule(grid,c);foreach(var control in essentials.Where(x=>x.Visible))Check(!gridBounds.IntersectsWith(InModule(control,c)),tag+" plan never overlaps "+control.Name);
                if(tabs.Visible)
                {
                    foreach(string page in new[]{"CompactModeTab","CompactRangeTab"})
                    {
                        var tab=c.Controls.Find(page,true).Single();Fits(tab,c,tag+" navigation");Check(tab.Enabled,tag+" reachable "+page);
                        Call(tab,"OnClick",EventArgs.Empty);Settle(c);
                        Check(!grid.Visible,tag+" inactive grid hidden on "+page);
                        foreach(var control in persistent)Fits(control,c,tag+" "+page);
                        foreach(var control in page=="CompactModeTab"?essentials.Take(2):essentials.Skip(2).Take(3))Fits(control,c,tag+" "+page);
                        Capture(c,Path.Combine(qa,tag+"-"+page+".png"));
                    }
                    var plan=c.Controls.Find("CompactPlanTab",true).Single();Fits(plan,c,tag+" return plan");Call(plan,"OnClick",EventArgs.Empty);Settle(c);
                    Check(grid.Visible&&grid.Rows.Count==11,tag+" return preserves plan");Fits(grid,c,tag+" restored plan");
                    foreach(var control in persistent)Fits(control,c,tag+" restored plan");
                }
                foreach(var panel in Descendants(right).OfType<ScrollableControl>())Check(!panel.AutoScroll&&!panel.HorizontalScroll.Visible&&!panel.VerticalScroll.Visible,tag+" no sidebar scrollbar "+panel.Name);
                Check(((double[])Call(c,"GetPressurePlanMultipliers")).SequenceEqual(u0)&&((int[])Call(c,"GetPressurePlanRepetitions")).SequenceEqual(runs),tag+" preserves ordered plan/zero/repeats");
                Capture(c,Path.Combine(qa,tag+"-plan.png"));
                Call(c,"SetPressureProgramMode","CUSTOM");Settle(c);
                Check(right.Width==planSidebarWidth,tag+" plan and custom sidebars retain the same width="+right.Width);
                Fits(right,c,tag+" custom sidebar");
                foreach(string step in new[]{"CustomVoltageDecrease","CustomVoltageIncrease"}){
                    var button=c.Controls.Find(step,true).Single();Fits(button,c,tag+" custom stepper");
                    Check(button.Height>=button.Font.Height+6,tag+" full stepper symbol "+step);
                }
                var editor=(Control)Field(c,"customVoltageValueButton");Fits(editor,c,tag+" custom");
                int textWidth=TextRenderer.MeasureText(editor.Text,editor.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width;
                Check(editor.Text==((double)Get(c,"CustomVoltageValue")).ToString("0.0",CultureInfo.InvariantCulture)+"U0"&&textWidth<=editor.ClientSize.Width-14,tag+" complete custom U0 text without multiplication sign fits, text="+textWidth+" available="+(editor.ClientSize.Width-14));
                Call(c,"SetCustomVoltageValue",9.9d);Settle(c);textWidth=TextRenderer.MeasureText(editor.Text,editor.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width;
                Check(textWidth<=editor.ClientSize.Width-14,tag+" wider custom value reflows without truncation");
                Capture(c,Path.Combine(qa,tag+"-custom.png"));
                Check((long)Get(c,"PressureRequestSequence")==0&&!(bool)Get(c,"IsBusy"),tag+" layout sends no pressure action");
                f.Close();
            }
            using(var f=new Form{ClientSize=new Size(1065,416),AutoScaleMode=AutoScaleMode.None,ShowInTaskbar=false,Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual})
            using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.PressureTestControl",true)))
            {
                Set(c,"EmbeddedMode",true);Set(c,"SimulationMode",true);Set(c,"FrequencyReminderEnabled",false);Set(c,"BaseU0VoltageKv",8.7d);c.Dock=DockStyle.Fill;f.Controls.Add(c);f.Show();Settle(c);
                var editor=(Control)Field(c,"customVoltageValueButton");int width=TextRenderer.MeasureText(editor.Text,editor.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width;
                Console.WriteLine("1065x416 CUSTOM_TEXT="+width+" AVAILABLE="+(editor.ClientSize.Width-14));
                Check(editor.Text==((double)Get(c,"CustomVoltageValue")).ToString("0.0",CultureInfo.InvariantCulture)+"U0"&&width<=editor.ClientSize.Width-14,"small host shows complete U0 without multiplication sign or ellipsis");Capture(c,Path.Combine(qa,"1065x416-custom.png"));
            }
            foreach(var failure in failures)Console.WriteLine("FAIL "+failure);Console.WriteLine("PRESSURE_PLAN_LAYOUT_CHECKS="+checks+";FAILURES="+failures.Count+";HARDWARE=False;QA="+qa);return failures.Count==0?0:1;
        }catch(Exception e){Console.Error.WriteLine(e);return 2;}
    }
}
