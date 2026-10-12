using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class ChargeCountOverlaySmoke
{
    static int checks;
    static void Check(bool ok,string name){checks++;if(!ok)throw new InvalidOperationException(name);}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethod(name).Invoke(o,args);}
    static object Get(object o,string name){return o.GetType().GetProperty(name).GetValue(o,null);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name).SetValue(o,value,null);}
    static double[,] Bins(object o){return (double[,])Call(o,"GetChargeCountBins");}
    static int Total(double[,] bins){return Enumerable.Range(0,bins.GetLength(0)).Sum(r=>(int)bins[r,4]);}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            if(args.Length!=2)throw new ArgumentException("Pass the distribution root and an owned evidence directory.");
            string distribution=Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar);
            string evidence=Path.GetFullPath(args[1]);
            if(!evidence.StartsWith(distribution+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Screenshots must remain inside the tested build directory.");
            Directory.CreateDirectory(evidence);
            Application.EnableVisualStyles();
            var assembly=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            var t=assembly.GetType("HVX.UnifiedSpectrum.UnifiedSpectrumControl",true);
            using(var c=(Control)Activator.CreateInstance(t))using(var host=new Form{ShowInTaskbar=false,ClientSize=new Size(1150,660)})
            {
                Set(c,"DisplayModeIndex",5);Set(c,"ChargeBinCount",4);Set(c,"AcceptedOnly",true);
                double[] q={0,3,3,6,12,12,3,3,3,3,3};
                var p=new double[q.Length,36];
                for(int r=0;r<q.Length;r++){p[r,0]=r<6||r>=8?1:2;p[r,1]=r;p[r,2]=r==7?12.2:6.1;p[r,5]=1000;p[r,9]=r;p[r,13]=200;p[r,15]=p[r,16]=q[r];p[r,35]=1;}
                p[8,35]=0;p[9,13]=2000;p[10,0]=0; // rejected, outside cable, duplicate ALL summary
                var original=(double[,])p.Clone();Call(c,"LoadDataMatrix",p);Set(c,"ViewPlaneIndex",3);
                c.Dock=DockStyle.Fill;host.Controls.Add(c);host.Show();Application.DoEvents();
                var bins=Bins(c);Check(Total(bins)==8,"one count per eligible source pulse; rejected/out-of-cable/ALL duplicate excluded");
                Check((int)Get(c,"ChargeCountMaximum")==2,"maximum cell count matches independent hand count");
                Check((bool)Get(c,"ChargeCountOverlayAvailable")&&(bool)Get(c,"ShowChargeCountOverlay"),"overlay available by default in ZY");
                var a=Enumerable.Range(0,bins.GetLength(0)).Where(r=>bins[r,0]==1).ToArray();
                Check(a.Length==4&&a.Select(r=>(int)bins[r,4]).SequenceEqual(new[]{1,2,1,2}),"A bins [0,3),[3,6),[6,9),[9,12] match reference");
                Check(a.All(r=>bins[r,1]==6.1)&&bins[a.Last(),3]==12,"exact voltage grouping and inclusive upper endpoint");
                Set(c,"ShowChargeCountOverlay",false);Check(Total(Bins(c))==8&&(int)Get(c,"VisiblePointCount")==8,"hiding overlay does not change scatter/statistics");
                Set(c,"ShowChargeCountOverlay",true);var copy=Bins(c);copy[0,4]=999;Check(Total(Bins(c))==8,"bin API returns a defensive copy");
                Set(c,"PhaseVisibility",new[]{true,false,false});Check(Total(Bins(c))==6,"phase toggles also filter counts");
                Set(c,"PhaseVisibility",new[]{true,true,true});Set(c,"ViewPlaneIndex",0);Check(!(bool)Get(c,"ChargeCountOverlayAvailable")&&Total(Bins(c))==8,"3D retains raw points without distorting bins");
                Set(c,"ViewPlaneIndex",3);Set(c,"ChargeBinCount",1);Check(Total(Bins(c))==8&&(int)Get(c,"ChargeCountMaximum")==6,"single-bin boundary reference");
                Set(c,"ChargeBinCount",4);Set(c,"LanguageIndex",1);Set(c,"LanguageIndex",0);
                foreach(int width in new[]{520,800,1150})
                {host.ClientSize=new Size(width,660);Application.DoEvents();using(var image=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(image,new Rectangle(Point.Empty,host.Size));image.Save(Path.Combine(evidence,"count-overlay-"+width+".png"));}Check(Total(Bins(c))==8,"resize does not rebucket source data");}
                Check(p.Cast<double>().Zip(original.Cast<double>(),(x,y)=>x.Equals(y)).All(x=>x),"input is never mutated");
                var zero=new double[2,36];for(int r=0;r<2;r++){zero[r,0]=1;zero[r,1]=r;zero[r,2]=0;zero[r,5]=1000;zero[r,9]=r;zero[r,13]=0;zero[r,35]=1;}
                Call(c,"LoadDataMatrix",zero);Check(Total(Bins(c))==2&&(int)Get(c,"ChargeCountMaximum")==2,"all-zero charge and voltage produce finite occupied bin");
                Call(c,"LoadDataMatrix",new double[0,36]);Check(Bins(c).GetLength(0)==0&&(int)Get(c,"ChargeCountMaximum")==0,"empty input has no fabricated count cells");
                Set(c,"DisplayModeIndex",0);Check(Bins(c).GetLength(0)==0,"unrelated charts retain their existing contract");
                host.Close();
            }
            Console.WriteLine("CHARGE_COUNT_OVERLAY_PASS="+checks+"; HardwareAccessed=False");return 0;
        }catch(Exception ex){Console.Error.WriteLine("FAIL check "+checks+": "+ex);return 1;}
    }
}
