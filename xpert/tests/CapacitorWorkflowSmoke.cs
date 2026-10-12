using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class CapacitorWorkflowSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    const string LocName="A_12.5kV_2026-09-29-10'00'00.loc";
    static void Check(bool value,string message){checks++;if(!value)throw new InvalidOperationException(message);}
    static object Field(object value,string name){return value.GetType().GetField(name,All).GetValue(value);}
    static void Set(object value,string name,object data){value.GetType().GetField(name,All).SetValue(value,data);}
    static object Property(object value,string name){return value.GetType().GetProperty(name,All).GetValue(value,null);}
    static void Put(object value,string name,object data){value.GetType().GetProperty(name,All).SetValue(value,data,null);}
    static object Invoke(object target,string name,params object[] args){var type=target as Type??target.GetType();return type.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target is Type?null:target,args);}
    static bool Rejected(Action action){try{action();return false;}catch(TargetInvocationException error){return error.InnerException is InvalidDataException;}catch(InvalidDataException){return true;}}
    static void Pump(Func<bool> done){var limit=DateTime.UtcNow.AddSeconds(20);while(!done()&&DateTime.UtcNow<limit){Application.DoEvents();Thread.Sleep(10);}Check(done(),"async work completes");Application.DoEvents();}
    static string Project(string parent,string name,bool capacitor)
    {
        string root=Path.Combine(parent,name);Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"负载类型 = "+(capacitor?"电容局放":"一体三芯")+"\r\n电缆长度 = 17\r\n",new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\r\n1\tStart\tTerminal\t0\r\n2\tBody\tXLPE\r\n1\tEnd\tTerminal\t17\r\n");return root;
    }
    static object Record(Assembly browser,string root,bool capacitor)
    {
        Type type=browser.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true);object record=Activator.CreateInstance(type);
        Set(record,"MeasurementKind",capacitor?"Capacitor":"Cable");Set(record,"Phase","A");Set(record,"Level","5nC");Set(record,"DeviceSerial","QA-OFFLINE");
        Set(record,"FilterId","PD-HP10K-SYMMETRIC-FIR4-v1");Set(record,"SampleRateHz",1e7);Set(record,"CoefficientPcPerV",100d);Set(record,"ChargePc",5000d);
        Set(record,"IncidentSeconds",20e-6);Set(record,"IncidentPeakIndex",200);Set(record,"SnrDb",40d);Set(record,"PulseCount",3);Set(record,"CreatedUtc",DateTime.UtcNow.ToString("o"));
        Set(record,"LengthM",capacitor?0d:17d);Set(record,"VelocityMPerUs",capacitor?0d:170d);Set(record,"ReflectionSeconds",capacitor?0d:20.2e-6);
        Set(record,"ReflectionPeakIndex",capacitor?-1:202);Set(record,"Correlation",capacitor?0d:.9);
        var average=new double[1024];average[200]=50;average[201]=-20;if(!capacitor)average[202]=25;Set(record,"Average",average);
        Set(record,"ProjectHash",Invoke(type,"Identity",root));
        Type rawType=browser.GetType("HVX.CalibrationStorage.CalibrationRawReference",true);Array raws=Array.CreateInstance(rawType,3);
        for(int i=0;i<3;i++){string name="qa-raw-"+i+".csv",path=Path.Combine(root,name);File.WriteAllText(path,"0,0\r\n1,1\r\n");object raw=Activator.CreateInstance(rawType);Set(raw,"RelativePath",name);Set(raw,"Sha256",Invoke(type,"HashFile",path));raws.SetValue(raw,i);}Set(record,"RawFiles",raws);return record;
    }
    static double[] WriteLoc(string root)
    {
        var samples=new double[400000];int[] peaks={50000,150000,250000};
        for(int i=0;i<samples.Length;i++){samples[i]=.02*Math.Sin(i*.71)+30*Math.Sin(2*Math.PI*500*i/1e7);foreach(int peak in peaks){double d=(i-peak)/7d;samples[i]+=(peak==150000?-200:100)*(Math.Exp(-d*d/2)-.5*Math.Exp(-(d-3)*(d-3)/2));}}
        var header=new[]{"Cable length\t0","Velocity (m/us)\t0","Delta x\t1E-7","Phase\t10","Voltage\t12.5","F(Hz)\t50","Sample units\tpC*100000","Reserved\t0","Reserved2\t0","Reserved3\t0","Data"};
        File.WriteAllText(Path.Combine(root,LocName),String.Join("\r\n",header)+"\r\n"+String.Join("\r\n",samples.Select(v=>(v*100000).ToString("R",CultureInfo.InvariantCulture))),new UTF8Encoding(true));return samples;
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            string release=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            var browser=Assembly.LoadFrom(Path.Combine(release,"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            string isolated=Path.Combine(qa,"no-location-runtime");Directory.CreateDirectory(isolated);
            string reviewPath=Path.Combine(isolated,"HVX.PdReview.V1.0.dll");File.Copy(Path.Combine(release,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"),reviewPath);
            var review=Assembly.LoadFrom(reviewPath);
            string root=Project(qa,"capacitor",true),cableRoot=Project(qa,"cable",false);object cap=Record(browser,root,true),cable=Record(browser,cableRoot,false);
            Type recordType=cap.GetType();Check((string)Field(Activator.CreateInstance(recordType),"MeasurementKind")=="Cable","legacy default remains Cable");
            string capPath=(string)Invoke(cap,"Save",root);Invoke(cable,"Save",cableRoot);
            object loaded=Invoke(recordType,"Load",root,capPath,true);Check((bool)Property(loaded,"IsCapacitor"),"formal capacitor record saved and verified without echo or geometry");
            Check((int)Field(loaded,"ReflectionPeakIndex")==-1,"capacitor reflection sentinel preserved");
            Set(cap,"SnrDb",19d);Check(Rejected(()=>Invoke(cap,"Validate",root,true)),"capacitor low quality rejected");Set(cap,"SnrDb",40d);
            Set(cap,"CoefficientPcPerV",0d);Check(Rejected(()=>Invoke(cap,"Validate",root,true)),"capacitor requires calibration coefficient");Set(cap,"CoefficientPcPerV",100d);
            Set(cap,"LengthM",1d);Check(Rejected(()=>Invoke(cap,"Validate",root,true)),"capacitor forbids fabricated length");Set(cap,"LengthM",0d);
            Set(cable,"ReflectionPeakIndex",-1);Check(Rejected(()=>Invoke(cable,"Validate",cableRoot,true)),"cable still rejects missing reflection");Set(cable,"ReflectionPeakIndex",202);
            Set(cable,"VelocityMPerUs",150d);Check(Rejected(()=>Invoke(cable,"Validate",cableRoot,true)),"cable still rejects inconsistent timing velocity");Set(cable,"VelocityMPerUs",170d);
            Check(Rejected(()=>Invoke(cap,"Validate",cableRoot,true)),"capacitor record cannot be used in cable project");
            // Historical XML with no MeasurementKind still validates as cable.
            string cablePath=(string)Invoke(cable,"Save",cableRoot);byte[] file=File.ReadAllBytes(cablePath);string xml=Encoding.UTF8.GetString(file,44,file.Length-44).Replace("  <MeasurementKind>Cable</MeasurementKind>\r\n","").Replace("  <MeasurementKind>Cable</MeasurementKind>\n","");
            Check(!xml.Contains("<MeasurementKind>"),"test historical XML has no kind member");byte[] payload=Encoding.UTF8.GetBytes(xml);
            using(var writer=new BinaryWriter(File.Create(Path.Combine(cableRoot,"legacy.hvcal"))))using(var sha=SHA256.Create()){writer.Write(Encoding.ASCII.GetBytes("HVCLIVE1"));writer.Write(payload.Length);writer.Write(sha.ComputeHash(payload));writer.Write(payload);}
            Check((string)Field(Invoke(recordType,"Load",cableRoot,Path.Combine(cableRoot,"legacy.hvcal"),true),"MeasurementKind")=="Cable","historical record reload unchanged");
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var host=new Form{ClientSize=new Size(1200,800),ShowInTaskbar=false})
            using(var control=(Control)Activator.CreateInstance(browser.GetType("HVX.Controls.ProjectDataBrowserControl",true))) {
                host.Controls.Add(control);control.Dock=DockStyle.Fill;host.Show();Check((bool)Invoke(control,"LoadProject",root),"capacitor browser project loads");Check((bool)Invoke(control,"SelectFile",capPath),"capacitor calibration selected");Pump(()=>!(bool)Property(control,"IsBusy"));
                object wave=Field(control,"selectedWave");Check((string)Field(wave,"MeasurementKind")=="Capacitor","browser knows capacitor mode");
                Check(Double.IsNaN((double)Field(wave,"ReflectionSeconds"))&&Double.IsNaN((double)Field(wave,"CableLengthMetres"))&&Double.IsNaN((double)Field(wave,"VelocityMPerUs")),"browser has no fake reflection length or velocity");
                Check(!((Control)Field(control,"jointButton")).Visible,"capacitor browser has no joint action");
                string info=((Control)Field(control,"information")).Text;Check(info.Contains("pC/V")&&!info.Contains("170"),"capacitor summary shows coefficient and no fake geometry");
                using(var image=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(image,new Rectangle(Point.Empty,host.Size));image.Save(Path.Combine(qa,"capacitor-browser.png"));}
            }
            var raw=WriteLoc(root);Type engine=review.GetType("HVX.PdReview.ReviewAnalysisEngine",true),requestType=review.GetType("HVX.PdReview.ReviewAnalysisRequest",true);
            object[] env={root,6,null};Check((bool)engine.GetMethod("CheckEnvironment",All).Invoke(null,env),"capacitor environment works without location runtime");
            Check(!((string)env[2]).Contains("environment check failed"),"capacitor route selected");
            Check(Directory.GetFiles(isolated).Length==1,"analysis probe has no Task17 or developer-path fallback");
            object[] cableEnv={cableRoot,6,null};Check(!(bool)engine.GetMethod("CheckEnvironment",All).Invoke(null,cableEnv),"cable still requires the real location runtime");
            object request=Activator.CreateInstance(requestType);Set(request,"ProjectPath",root);Set(request,"LocationFilePath",Path.Combine(root,LocName));Set(request,"LocationDataId",7L);Set(request,"ManualThresholdEnabled",true);Set(request,"ManualThreshold",20d);
            object result=Invoke(engine,"Analyze",request);Check((bool)Field(result,"Success"),(string)Field(result,"Status"));
            var p=(double[,])Field(result,"Parameters");Check(p.GetLength(0)==3,"three incident packets retained without reflections");
            for(int r=0;r<3;r++){Check(p[r,13]==0&&Double.IsNaN(p[r,11])&&Double.IsNaN(p[r,5])&&Double.IsNaN(p[r,6]),"no fabricated location timing or geometry");Check(p[r,0]==1&&p[r,1]==7&&p[r,2]==12.5&&p[r,35]==1,"phase voltage identity and acceptance retained");Check(Math.Abs(p[r,15])>50&&Math.Abs(p[r,15])<210,"LOC charge not multiplied by 100 pC/V twice");Check(Math.Abs(p[r,3]-(10+360*50*p[r,10]/1e7)%360)<1e-9,"phase follows actual rate frequency and origin");}
            Check(((string)Field(result,"Status")).Contains("location-algorithm=not-called"),"explicit no-localization diagnostic");
            Type hp=review.GetType("HVX.SignalProcessing.PdLinearHighPass",true);double[] filtered=(double[])Invoke(hp,"Apply",raw,1e7);
            Type templateType=review.GetType("HVX.SignalProcessing.NoisePulseTemplate",true);object template=Activator.CreateInstance(templateType);
            int anchor=(int)p[0,10],radius=60;Set(template,"Id","qa-noise");Set(template,"FilterId","PD-HP10K-SYMMETRIC-FIR4-v1");Set(template,"SampleRateHz",1e7);Set(template,"PeakIndex",radius);Set(template,"Samples",filtered.Skip(anchor-radius).Take(radius*2+1).ToArray());Array templates=Array.CreateInstance(templateType,1);templates.SetValue(template,0);
            Invoke(review.GetType("HVX.SignalProcessing.NoisePulseTemplates",true),"Save",root,false,templates);object excluded=Invoke(engine,"Analyze",request);Check((bool)Field(excluded,"Success"),"noise template filtering successful");Check(((double[,])Field(excluded,"Parameters")).GetLength(0)==0,"99-percent signed-polarity-invariant noise template excludes matching packets");
            Set(cap,"BandwidthMHz",1d);
            Set(cap,"LowPassFilterId",browser.GetType("HVX.SignalProcessing.PdLinearLowPass",true).GetField("FilterId",All).GetValue(null));Invoke(cap,"Save",root);
            object bandExcluded=Invoke(engine,"Analyze",request);Check((bool)Field(bandExcluded,"Success"),"low-pass capacitor analysis succeeds");Check(((double[,])Field(bandExcluded,"Parameters")).GetLength(0)==0,"noise matching retains common high-pass basis with optional low-pass calibration");
            using(var host=new Form{ClientSize=new Size(1400,900),ShowInTaskbar=false})
            using(var control=(Control)Activator.CreateInstance(review.GetType("HVX.PdReview.PdReviewControl",true))) {
                host.Controls.Add(control);control.Dock=DockStyle.Fill;host.Show();Put(control,"PromptSavedAnalysisOnProjectLoad",false);Put(control,"ProjectPath",root);
                Check((bool)Invoke(control,"SetProjectLocationIds",new[]{Path.Combine(root,LocName)},new[]{7d}),"review binds source LOC identity");
                var f=(double[,])Field(result,"Filtered");int rows=p.GetLength(0),width=f.GetLength(1);var waves=new double[rows*3,width];
                foreach(int block in new[]{0,1,2})for(int r=0;r<rows;r++)for(int c=0;c<width;c++)waves[block*rows+r,c]=f[r,c];
                Check((bool)Invoke(control,"ForceReloadDataMatrices",p,waves),"capacitor matrix accepted by complete review UI: "+Property(control,"LastError"));
                Check((int)Property(control,"SelectedSourceRowIndex")>=0,"no geometry completeness rejection of capacitor pulse");
                object spectrum=Property(control,"Spectrum");
                foreach(int mode in new[]{0,1,2,3,4,5,6,7,8,9,10,11,12}) {Put(spectrum,"DisplayModeIndex",mode);Application.DoEvents();var view=Field(spectrum,"view");var points=(System.Collections.ICollection)Field(view,"Points");Check(points.Count>0,"capacitor spectrum mode "+mode+" retains data");if((string)Field(view,"XKey")=="L")foreach(object point in points)Check((double)Field(point,"X")==0,"location-axis points stay at 0 m placeholder");}
                Check((bool)Invoke(control,"DeleteCurrent"),"capacitor pulse can be removed from statistics");Check((bool)Property(control,"CanAccept"),"capacitor does not require reflection to accept");Check((bool)Invoke(control,"AcceptCurrent"),"capacitor accepted without fabricated reflection");
                object pdmap=Property(control,"PdMap");Invoke(pdmap,"MoveReflectionToIndex",30);Check(!(bool)Property(pdmap,"HasSelectedReflection"),"capacitor cannot invent reflection by cursor interaction");
                object[] session={null,null,null};Check((bool)control.GetType().GetMethod("ExportAnalysisSessionUtf8",All).Invoke(control,session),"capacitor statistics serialize without NaN formatting errors: "+Property(control,"LastError"));
                Check(((byte[])session[2]).Length>0&&Double.IsNaN(((double[,])session[0])[0,11]),"session preserves missing reflection as NaN, not fake zero");
                Check((bool)Invoke(control,"LoadAnalysisSessionUtf8",root,session[0],session[1],session[2]),"capacitor session round-trip reload: "+Property(control,"LastError"));
                Check((bool)Invoke(control,"SelectSourceRow",0),"reloaded capacitor pulse can be displayed without geometry");
                Check((bool)Property(pdmap,"CapacitorStatisticsMode"),"PdMap retains capacitor no-localization mode after session load");
                using(var image=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(image,new Rectangle(Point.Empty,host.Size));image.Save(Path.Combine(qa,"capacitor-analysis.png"));}
            }
            Shade(browser,qa);
            Console.WriteLine("PASS CapacitorWorkflowSmoke checks="+checks+"; hardware=false; evidence="+qa);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static void Shade(Assembly browser,string qa)
    {
        using(var control=(Control)Activator.CreateInstance(browser.GetType("CalibrationWaveform.CalibrationWaveformControl",true))) {
            control.Size=new Size(1000,650);double[] x=Enumerable.Range(0,501).Select(i=>i*.1).ToArray(),y=new double[501];
            Invoke(control,"SetWaveformXY",x,y);Invoke(control,"SetCableParameters",170d,1700d);Invoke(control,"SetCursorPositions",2d,10d);
            Check((double)Property(control,"CableBoundaryX")==22d,"cable boundary uses incident + 2L/v, not reflection");
            var plot=(Rectangle)Invoke(control,"GetPlotRectangle",control.ClientRectangle);
            Color inside,outside;int px=plot.Left+(int)(16d/50*plot.Width),py=plot.Top+17;
            using(var image=new Bitmap(1000,650))using(var g=Graphics.FromImage(image)) {
                Invoke(control,"DrawGridAndAxes",g,plot,-1d,1d);inside=image.GetPixel(px,py);outside=image.GetPixel(plot.Left+(int)(32d/50*plot.Width),py);Check(inside!=outside,"cable interior has visible shading");image.Save(Path.Combine(qa,"cable-boundary-shading.png"));
            }
            Invoke(control,"SetCursorPositions",2d,8d);Check((double)Property(control,"CableBoundaryX")==22d,"moving reflection alone does not redefine physical cable boundary");
            Invoke(control,"SetCableParameters",170d,850d);Check((double)Property(control,"CableBoundaryX")==12d,"length updates boundary");
            using(var image=new Bitmap(1000,650))using(var g=Graphics.FromImage(image)){Invoke(control,"DrawGridAndAxes",g,plot,-1d,1d);Check(image.GetPixel(px,py)==outside,"shortened boundary clears former shaded area");}
            Invoke(control,"SetCableParameters",85d,850d);Invoke(control,"SetCursorPositions",4d,12d);Check((double)Property(control,"CableBoundaryX")==24d,"velocity and incident changes update boundary");
            Invoke(control,"SetViewClamped",10d,30d);
            using(var image=new Bitmap(1000,650))using(var g=Graphics.FromImage(image)){g.Clear(Color.Magenta);Invoke(control,"DrawGridAndAxes",g,plot,-1d,1d);Check(image.GetPixel(plot.Left-2,py).ToArgb()==Color.Magenta.ToArgb(),"shading clips at plot edge during zoom");}
        }
    }
}
