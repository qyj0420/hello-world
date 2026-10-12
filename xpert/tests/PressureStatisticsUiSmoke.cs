using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class PressureStatisticsUiSmoke
{
    static int checks;
    static void Check(bool value,string name){checks++;if(!value)throw new InvalidOperationException(name);}
    static object Get(object o,string n){return o.GetType().GetProperty(n).GetValue(o,null);}
    static void Set(object o,string n,object value){o.GetType().GetProperty(n).SetValue(o,value,null);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethod(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Invoke(o,args);}
    static IEnumerable<Control> All(Control o){foreach(Control c in o.Controls){yield return c;foreach(var n in All(c))yield return n;}}
    static void Pump(){for(int i=0;i<5;i++)Application.DoEvents();}
    static void Save(Control c,string path){using(var bitmap=new Bitmap(c.Width,c.Height)){c.DrawToBitmap(bitmap,new Rectangle(Point.Empty,c.Size));bitmap.Save(path);}}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string root=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            string pdll=Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll");if(!File.Exists(pdll))pdll=Path.Combine(root,"Xiongxin.OWTS.PressureTest.dll");
            string adll=Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll");if(!File.Exists(adll))adll=Path.Combine(root,"HVX.PdReview.V1.0.dll");
            var analysis=Assembly.LoadFrom(adll);var pressure=Assembly.LoadFrom(pdll);
            var matrix=new double[6,36];var wave=new double[6,2048];
            for(int r=0;r<6;r++)
            {
                matrix[r,0]=1+r%3;matrix[r,1]=r+1;matrix[r,2]=13.4;matrix[r,3]=45+r*5;matrix[r,5]=414;matrix[r,6]=170;matrix[r,7]=125000000;
                matrix[r,9]=r;matrix[r,10]=100;matrix[r,11]=500;matrix[r,12]=400;matrix[r,13]=170+r*5;matrix[r,15]=100+r*15;matrix[r,16]=100+r*15;
                matrix[r,23]=100;matrix[r,24]=100000;matrix[r,32]=2048;matrix[r,33]=r==5?Double.NaN:r*.2;matrix[r,35]=r%2;
            }
            var original=(double[,])matrix.Clone();
            using(var comparison=(Control)Activator.CreateInstance(analysis.GetType("HVX.PdReview.PdReviewControl",true)))
            {
                Set(Get(comparison,"Spectrum"),"AutomaticConclusions",false);
                Check((bool)Call(comparison,"LoadDataMatrices",matrix,wave),"reference analysis loads");
                foreach(int language in Enumerable.Range(0,19))foreach(var size in new[]{new Size(1120,416),new Size(1256,720),new Size(1850,990)})
                using(var form=new Form{AutoScaleMode=AutoScaleMode.None,ClientSize=size,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
                using(var control=(Control)Activator.CreateInstance(pressure.GetType("HVX.Controls.PressureTestControl",true)))
                {
                    Set(control,"EmbeddedMode",true);Set(control,"LanguageIndex",language);control.Dock=DockStyle.Fill;form.Controls.Add(control);form.Show();Pump();
                    Call(control,"SetSpectrumDataMatrix",matrix);
                    var volts=new double[2048];var discharge=new double[2048];for(int i=0;i<volts.Length;i++){volts[i]=20*Math.Exp(-i/1500d)*Math.Sin(i*.015);discharge[i]=30*Math.Sin(i*.4);}
                    Call(control,"SetWaveforms",volts,discharge,100000d);Pump();
                    long sequence=(long)Get(control,"PressureRequestSequence");
                    foreach(double strength in new[]{0d,50d,90d})
                    {
                        Set(control,"StatisticsFilterStrength",strength);Set(comparison,"LocationFilterStrength",strength);Pump();
                        Check(((int[])Get(control,"StatisticsFilteredSourceRows")).SequenceEqual((int[])Get(comparison,"FilteredSourceRowIndices")),"same analysis filter rows "+strength);
                    }
                    Set(control,"StatisticsFilterEnabled",false);Check(((int[])Get(control,"StatisticsFilteredSourceRows")).Length==matrix.GetLength(0),"filter disabled restores all source rows");
                    Set(control,"StatisticsFilterStrength",50d);Check((bool)Get(control,"StatisticsFilterEnabled"),"setting strength enables filter");
                    Check((long)Get(control,"PressureRequestSequence")==sequence,"statistics changes do not issue pressure requests");
                    var spectrum=All(control).Single(c=>c.GetType().Name=="UnifiedSpectrumControl");Check(spectrum.Font.SizeInPoints>=9.5,"readable statistics font");
                    Check(control.Font.SizeInPoints>=9.5,"readable base controls font");
                    Check(!((ScrollableControl)control).HorizontalScroll.Visible,"no workspace horizontal scrollbar");
                    Save(control,Path.Combine(output,"pressure-"+language+"-"+size.Width+"-wave.png"));
                    var spectrumTab=All(control).OfType<Button>().FirstOrDefault(c=>c.Name=="CompactSpectrumTab");if(spectrumTab!=null&&spectrumTab.Visible){spectrumTab.PerformClick();Pump();}
                    var bar=All(control).Single(c=>c.Name=="PressureStatisticsFilterBar");
                    Check(bar.Visible&&bar.Parent.ClientRectangle.Contains(bar.Bounds),"statistics filter visible inside host");
                    foreach(Control child in bar.Controls)Check(bar.ClientRectangle.Contains(child.Bounds),"filter child fits "+child.Name);
                    var filter=All(control).Single(c=>c.Name=="PressureStatisticsFilter");Check(filter.Width>=TextRenderer.MeasureText(filter.Text,filter.Font,Size.Empty,TextFormatFlags.NoPadding).Width+20,"complete filter caption");
                    if(language==18)Check(filter.Text=="統計過濾","Traditional Chinese statistics caption");
                    Check(spectrum.Parent.ClientRectangle.Contains(spectrum.Bounds),"statistics chart fits host");
                    Save(control,Path.Combine(output,"pressure-"+language+"-"+size.Width+"-spectrum.png"));
                    form.Close();
                }
            }
            Check(matrix.Cast<double>().Zip(original.Cast<double>(),(a,b)=>a.Equals(b)).All(x=>x),"source data unchanged");
            Console.WriteLine("PRESSURE_STATISTICS_UI_PASS="+checks);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
