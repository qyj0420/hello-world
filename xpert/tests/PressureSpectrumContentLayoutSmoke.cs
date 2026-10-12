using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class PressureSpectrumContentLayoutSmoke
{
    static int checks;
    static void Check(bool value,string name){checks++;if(!value)throw new InvalidOperationException(name);}
    static object Get(object value,string name){return value.GetType().GetProperty(name).GetValue(value,null);}
    static void Set(object value,string name,object setting){value.GetType().GetProperty(name).SetValue(value,setting,null);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(value,args);}
    static IEnumerable<Control> All(Control value){foreach(Control child in value.Controls){yield return child;foreach(var nested in All(child))yield return nested;}}
    static Control Named(Control value,string name){return All(value).Single(c=>c.Name==name);}
    static void Pump(){for(int n=0;n<5;n++)Application.DoEvents();}
    static void Layout(Control control,string tag)
    {
        var tab=All(control).OfType<Button>().Single(c=>c.Name=="CompactSpectrumTab");if(tab.Visible)tab.PerformClick();Pump();
        var spectrum=All(control).Single(c=>c.GetType().Name=="UnifiedSpectrumControl");
        var options=Named(control,"PressureAnalysisOptionsBar");var filter=Named(control,"PressureStatisticsFilterBar");
        // These functional assertions also reproduce the old package failure:
        // merely forcing a second Dock layout still overlaps the filter strip.
        Check(spectrum.Parent.ClientRectangle.Contains(spectrum.Bounds),tag+" chart inside content");
        Check(options.Parent==spectrum.Parent&&filter.Parent==spectrum.Parent,tag+" common allocation");
        Check(options.Bottom<=spectrum.Top&&spectrum.Bottom<=filter.Top,tag+" options chart filter do not overlap");
        Check(options.Parent.ClientRectangle.Contains(options.Bounds)&&filter.Parent.ClientRectangle.Contains(filter.Bounds),tag+" strips inside content");
        Check(spectrum.Height>=spectrum.MinimumSize.Height,tag+" readable chart minimum retained");
        Check(spectrum.Font.SizeInPoints>=9.5,tag+" chart font retained");
        foreach(Control child in options.Controls)
        {
            Check(child.Visible,tag+" option retained "+child.Name);
            Check(options.ClientRectangle.Contains(child.Bounds),tag+" option inside complete strip "+child.Name);
        }
        foreach(Control child in filter.Controls)Check(filter.ClientRectangle.Contains(child.Bounds),tag+" filter child inside strip "+child.Name);
        var viewport=(Panel)Named(control,"PressureSpectrumViewport");var content=Named(control,"PressureSpectrumContent");
        Check(content.Parent==viewport,tag+" scroll is local to spectrum");
        Check(!viewport.HorizontalScroll.Visible&&!((ScrollableControl)control).HorizontalScroll.Visible,tag+" no horizontal scrolling; viewport="+viewport.Bounds+" client="+viewport.ClientSize+" display="+viewport.DisplayRectangle+" content="+content.Bounds+" margin="+content.Margin+" workspace="+((ScrollableControl)control).HorizontalScroll.Visible);
        Check(content.Width<=viewport.ClientSize.Width,tag+" content fits usable viewport width");
        Check(viewport.VerticalScroll.Visible==(content.Height>viewport.ClientSize.Height),tag+" vertical scroll only when needed");
        viewport.ScrollControlIntoView(filter);Pump();
        var visible=viewport.RectangleToScreen(viewport.ClientRectangle);
        Check(visible.Contains(filter.RectangleToScreen(filter.ClientRectangle)),tag+" complete filter reachable by local scrolling");
        viewport.ScrollControlIntoView(options);Pump();
        Check(visible.Contains(options.RectangleToScreen(options.ClientRectangle)),tag+" complete options reachable by local scrolling");
        Check((bool)Get(control,"ManualAnalysisThresholdEnabled")&&(double)Get(control,"ManualAnalysisThresholdPc")==123.45d&&(bool)Get(control,"ApplyAnalysisThresholdToAllRounds"),tag+" threshold edits preserved");
        Check(!((CheckBox)Named(control,"PressureSnapJoint")).Checked&&((CheckBox)Named(control,"PressureReversePosition")).Checked&&((CheckBox)Named(control,"PressureCompensatedCharge")).Checked,tag+" display options preserved");
        Check((long)Get(control,"PressureRequestSequence")==0L&&!(bool)Get(control,"IsBusy"),tag+" no acquisition or output request");
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string root=Path.GetFullPath(args[0]);Assembly.LoadFrom(Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            var assembly=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            var matrix=new double[6,36];for(int n=0;n<6;n++){matrix[n,0]=1+n%3;matrix[n,1]=n+1;matrix[n,2]=13.4;matrix[n,5]=414;matrix[n,6]=170;matrix[n,7]=125000000;matrix[n,10]=100;matrix[n,11]=500;matrix[n,12]=400;matrix[n,13]=170+n*5;matrix[n,15]=100+n*15;matrix[n,16]=100+n*15;matrix[n,23]=100;matrix[n,24]=100000;matrix[n,32]=2048;matrix[n,33]=n*.2;}
            var original=(double[,])matrix.Clone();
            foreach(int language in Enumerable.Range(0,19))
            using(var form=new Form{AutoScaleMode=AutoScaleMode.None,ClientSize=new Size(1850,990),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var control=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true)))
            {
                Set(control,"EmbeddedMode",true);Set(control,"LanguageIndex",language);control.Dock=DockStyle.None;form.Controls.Add(control);form.Show();
                Call(control,"SetSpectrumDataMatrix",matrix);Call(control,"SetPressureAnalysisThreshold",true,123.45d,true);
                ((CheckBox)Named(control,"PressureSnapJoint")).Checked=false;((CheckBox)Named(control,"PressureReversePosition")).Checked=true;((CheckBox)Named(control,"PressureCompensatedCharge")).Checked=true;
                foreach(var size in new[]{new Size(1120,416),new Size(1256,720),new Size(1850,990),new Size(1120,416)})
                {
                    control.Size=size;Pump();Layout(control,"lang="+language+" size="+size);
                    if(size.Height==990)Check(!((Panel)Named(control,"PressureSpectrumViewport")).VerticalScroll.Visible,"ample space restores original unscrolled layout "+language);
                }
                form.Close();
            }
            Check(matrix.Cast<double>().SequenceEqual(original.Cast<double>()),"display layout leaves source matrix unchanged");
            Console.WriteLine("PRESSURE_SPECTRUM_CONTENT_LAYOUT_PASS="+checks+";LANGUAGES=19;FIXTURES=76;HARDWARE=False;SDK=False");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
