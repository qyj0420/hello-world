using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class AnalysisReadableLayoutSmoke
{
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
    static IEnumerable<Control> All(Control root){foreach(Control c in root.Controls){yield return c;foreach(var n in All(c))yield return n;}}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(o,args);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name).SetValue(o,value,null);}
    static object Get(object o,string name){return o.GetType().GetProperty(name).GetValue(o,null);}
    static void Pump(){for(int i=0;i<5;i++)Application.DoEvents();}
    static void Save(Control c,string path){using(var b=new Bitmap(c.Width,c.Height)){c.DrawToBitmap(b,new Rectangle(Point.Empty,c.Size));b.Save(path);}}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string root=Path.GetFullPath(args[0]);string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            string dll=Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll");if(!File.Exists(dll))dll=Path.Combine(root,"HVX.PdReview.V1.0.dll");
            var asm=Assembly.LoadFrom(dll);var type=asm.GetType("HVX.PdReview.PdReviewControl",true);
            foreach(int language in Enumerable.Range(0,19))foreach(var size in new[]{new Size(1120,416),new Size(1256,620),new Size(1850,990)})
            using(var form=new Form{AutoScaleMode=AutoScaleMode.None,ClientSize=size,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var review=(Control)Activator.CreateInstance(type))
            {
                Set(review,"PromptSavedAnalysisOnProjectLoad",false);Set(review,"EmbeddedMode",true);Set(review,"LanguageIndex",language);
                review.Dock=DockStyle.Fill;form.Controls.Add(review);form.Show();Pump();
                var p=new double[18,36];var w=new double[18,2048];
                for(int r=0;r<18;r++)
                {
                    p[r,0]=1+r/6;p[r,1]=r/3;p[r,2]=13.4;p[r,3]=45;p[r,5]=414;p[r,6]=170;p[r,7]=125000000;
                    p[r,9]=r;p[r,10]=100;p[r,11]=710;p[r,12]=4.88;p[r,13]=200+r;p[r,15]=180;p[r,16]=180;p[r,20]=100;p[r,21]=.92;p[r,31]=0;p[r,32]=2047;p[r,33]=.91;p[r,35]=r%2;
                    for(int i=0;i<2048;i++)w[r,i]=180*Math.Exp(-Math.Pow((i-100)/14.0,2))+60*Math.Exp(-Math.Pow((i-710)/23.0,2))+Math.Sin(i*.14)*1.5;
                }
                Check((bool)Call(review,"LoadDataMatrices",p,w),"synthetic waveform loads");
                Check((bool)Call(review,"SelectSourceRow",4),"synthetic pulse selected");
                if(language==18)Check((string)Get(Get(review,"Spectrum"),"StatisticsFilterCaption")=="統計過濾","Traditional Chinese built-in text");
                var tree=All(review).OfType<TreeView>().Single();tree.ExpandAll();tree.SelectedNode.EnsureVisible();Pump();
                Check(tree.SelectedNode!=null&&tree.SelectedNode.Level==3,"actual deep pulse branch selected");
                Check(tree.Indent<=18,"reduced deep-tree indentation");
                Check(All(review).Any(c=>c.Name=="LocationSelectionDetails"&&c.Text.Contains("13.4")),"current selection metadata visible");
                foreach(int filter in new[]{1,2})
                {
                    const string sidebarStatusFixture="A_18.30kV_2026-05-05-reference.loc";
                    Set(review,"LastError",sidebarStatusFixture);
                    Set(review,"WaveformFilterMode",filter);Pump();
                    Call(review,"RefreshReviewUi");Pump();
                    var decision=(FlowLayoutPanel)type.GetField("compactDecisionRow",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(review);
                    var buttons=new[]{"acceptButton","deleteButton","rangeFilterButton"}.Select(name=>(Control)type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(review)).ToArray();
                    Check(decision.FlowDirection==FlowDirection.TopDown&&!decision.WrapContents,"decision buttons stack without wrapping");
                    foreach(var button in buttons)Check(button.Width==decision.ClientSize.Width-button.Margin.Horizontal,"decision button fills its own row");
                    Check(buttons[1].Top>=buttons[0].Bottom&&buttons[2].Top>=buttons[1].Bottom,"accept delete filter occupy separate rows");
                    Check(decision.ClientRectangle.Contains(buttons[2].Bounds),"filter third row fully visible");
                    Check(!All(review).Single(c=>c.Name=="ReviewStatus").Visible,"extra sidebar status stays hidden after refresh/filter/language changes");
                    Check((string)Get(review,"LastError")==sidebarStatusFixture,"hiding the status does not erase diagnostics");
                    foreach(Control c in All(review).Where(c=>c.Visible&&(c.Name=="ReviewHighPassValue"||c.Name=="ReviewLowPassValue"||c.Name.StartsWith("ReviewMetric"))))
                        Check(c.Parent.ClientRectangle.Contains(c.Bounds),"full input/metric: "+c.Name+" "+c.Bounds);
                    foreach(var c in All(review).Where(c=>c.Visible&&c is Button&&(c.Name=="ReviewAccept"||c.Name=="ReviewEvaluate"||c.Name=="ReviewReport")))
                        Check(c.Width>=TextRenderer.MeasureText(c.Text,c.Font,Size.Empty,TextFormatFlags.NoPadding).Width+12,"full button label: "+c.Name);
                    if(((ScrollableControl)review).HorizontalScroll.Visible)
                    {
                        Console.WriteLine("viewport="+review.ClientSize+" minimum="+Get(review,"RecommendedMinimumSize"));
                        Console.WriteLine("font="+review.Font.SizeInPoints+" sidebar="+Call(review,"SidebarLayoutWidth"));
                        foreach(var c in All(review).Where(c=>c.Name=="ReviewAnalysisTreePanel"||c.Name=="ReviewWorkspaceBody"||c.GetType().Name=="PdMapControl"||c.GetType().Name=="UnifiedSpectrumControl"))Console.WriteLine(c.Name+" type="+c.GetType().Name+" bounds="+c.Bounds+" minimum="+c.MinimumSize+" padding="+c.Padding);
                        foreach(var c in All(review).Where(c=>c.Name=="ReviewSettings"||c.Name=="ReviewActions"||c.Name=="ReviewArchiveActions"))
                        {Console.WriteLine(c.Name+" "+c.Bounds);foreach(Control child in c.Controls)Console.WriteLine("  "+child.Name+" "+child.Bounds+" visible="+child.Visible+" margin="+child.Margin);}
                        Save(review,Path.Combine(output,"failed.png"));
                    }
                    Check(!((ScrollableControl)review).HorizontalScroll.Visible,"no workspace horizontal scrollbar");
                    Save(review,Path.Combine(output,"analysis-"+language+"-"+size.Width+"-filter"+filter+".png"));
                }
                var actionsTab=All(review).OfType<Button>().Single(c=>c.Name=="ReviewCompactActions");
                if(actionsTab.Visible)
                {
                    actionsTab.PerformClick();Pump();
                    foreach(var c in All(review).Where(c=>c.Visible&&c is Button&&(c.Name=="ReviewAccept"||c.Name=="ReviewEvaluate"||c.Name=="ReviewReport")))
                        Check(c.Width>=TextRenderer.MeasureText(c.Text,c.Font,Size.Empty,TextFormatFlags.NoPadding).Width+12,"compact action text: "+c.Name);
                    foreach(Control c in All(review).Where(c=>c.Visible&&c.Name.StartsWith("ReviewMetric")))
                        Check(c.Parent.ClientRectangle.Contains(c.Bounds),"compact metric unit complete: "+c.Name);
                    Save(review,Path.Combine(output,"analysis-"+language+"-"+size.Width+"-actions.png"));
                    var spectrumTab=All(review).OfType<Button>().Single(c=>c.Name=="ReviewCompactSpectrum");spectrumTab.PerformClick();Pump();
                    var spectrum=(Control)Get(review,"Spectrum");Check(spectrum.Parent.ClientRectangle.Contains(spectrum.Bounds),"compact spectrum fully inside visible host");
                    Check(!((ScrollableControl)review).HorizontalScroll.Visible,"compact spectrum no horizontal scrollbar");
                    Check(All(review).Single(c=>c.Name=="SpectrumCompactMode").Visible,"all chart modes available in compact selector");
                    Save(review,Path.Combine(output,"analysis-"+language+"-"+size.Width+"-spectrum.png"));
                }
                var infoType=asm.GetType("HVX.PdReview.SavedAnalysisInfo",true);var records=Array.CreateInstance(infoType,5);
                string longName="2026-09-28_11-25-59 · 10kV联络线 · 13.4kV · 人工复核分析结果 / Reviewed discharge location results";
                for(int i=0;i<5;i++){var info=Activator.CreateInstance(infoType);Set(info,"Name",longName+" "+i);Set(info,"CreatedLocal",new DateTime(2026,9,28,11,25,59));Set(info,"Id","offline-"+i);records.SetValue(info,i);}
                using(var dialog=(Form)Call(review,"CreateSavedAnalysisDialog",records))
                {
                    dialog.StartPosition=FormStartPosition.Manual;dialog.Location=new Point(-30000,-30000);dialog.Show(form);Pump();
                    var list=All(dialog).OfType<ListView>().Single();list.Items[0].Selected=true;Pump();
                    Check(list.Columns.Cast<ColumnHeader>().Sum(c=>c.Width)<=list.ClientSize.Width,"archive columns fit viewport");
                    Check(All(dialog).Single(c=>c.Name=="SelectedAnalysisName").Text.Contains(longName),"full archive name available");
                    Save(dialog,Path.Combine(output,"archive-"+language+".png"));dialog.Close();
                }
                form.Close();
            }
            Console.WriteLine("ANALYSIS_READABLE_PASS="+checks);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
