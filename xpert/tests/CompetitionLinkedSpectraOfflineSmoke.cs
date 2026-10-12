using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class CompetitionLinkedSpectraOfflineSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static object Get(object x,string name){return x.GetType().GetProperty(name,All).GetValue(x,null);}
    static void Set(object x,string name,object value){x.GetType().GetProperty(name,All).SetValue(x,value,null);}
    static object Field(object x,string name){return x.GetType().GetField(name,All).GetValue(x);}
    static void SetField(object x,string name,object value){x.GetType().GetField(name,All).SetValue(x,value);}
    static object Call(object x,string name,params object[] args){return x.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(x,args);}
    static string Hash(string p){using(var s=File.OpenRead(p))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s));}
    static void WriteMatrix(string p,double[,] a){using(var w=new BinaryWriter(File.Create(p))){w.Write(Encoding.ASCII.GetBytes("HVXMAT01"));w.Write(a.GetLength(0));w.Write(a.GetLength(1));foreach(double d in a)w.Write(d);}}
    static double[,] Matrix(){var a=new double[8,36];for(int r=0;r<8;r++){a[r,0]=r==7?2:1;a[r,1]=1;a[r,2]=8.7;a[r,3]=25+r*25;a[r,5]=400;a[r,9]=r;a[r,13]=r==4?-1:80+r*30;a[r,15]=r%2==0?100+r:-100-r;a[r,16]=999;a[r,23]=100+r*100;a[r,24]=1e6+r*1e6;a[r,35]=r==6?0:1;}return a;}
    static string Fixture(string qa){string root=Path.Combine(qa,"synthetic"),d=Path.Combine(root,"Analysis report","linked");Directory.CreateDirectory(d);WriteMatrix(Path.Combine(d,"parameters.bin"),Matrix());WriteMatrix(Path.Combine(d,"pdmap.bin"),new double[0,36]);File.WriteAllText(Path.Combine(d,"session.json"),"{}");var f=new[]{"parameters.bin","pdmap.bin","session.json"}.Select(n=>new{Path=n,Bytes=new FileInfo(Path.Combine(d,n)).Length,Sha256=Hash(Path.Combine(d,n)).Replace("-","")}).ToArray();File.WriteAllText(Path.Combine(d,"analysis.json"),new JavaScriptSerializer().Serialize(new{Schema="HVX.NamedAnalysis",Version=1,Id="linked",Name="Synthetic",CreatedUtc="2026-10-11T00:00:00Z",SourceRowCount=8,Files=f,Charts=new object[0],ExpandedNodes=new string[0],SelectedIdentity=new double[0],DisplayMode=0,ViewPlane=0}));return root;}
    [STAThread]static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);var asm=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            string project=Fixture(qa);var hashes=Directory.GetFiles(project,"*",SearchOption.AllDirectories).ToDictionary(x=>x,Hash);
            using(var standardFont=new Font("Microsoft YaHei UI",10.5f))
            using(var largeFont=new Font("Microsoft YaHei UI",16f))
            using(var doubledFont=new Font("Microsoft YaHei UI",21f))
            using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.PdReview.CompetitionDefectRecognitionControl",true)))
            using(var f=new Form{Location=new Point(-20000,-20000),StartPosition=FormStartPosition.Manual,ClientSize=new Size(1400,900),ShowInTaskbar=false}){
                f.Controls.Add(c);c.Dock=DockStyle.Fill;f.Show();Application.DoEvents();
                Check((int)Get(c,"LeftSpectrumMode")==11&&(int)Get(c,"RightSpectrumMode")==4,"PRPD / F-T defaults");
                Check((bool)Call(c,"LoadProject",project)&&(bool)Call(c,"LoadSavedAnalysis","linked"),"read-only synthetic named archive loads");
                var left=Field(c,"spectrum");var right=Field(c,"secondarySpectrum");
                int[] modes={0,1,2,3,4,5,6,9,10,11,12};
                Check(((int[])Call(left,"GetVisibleModeIndices")).SequenceEqual(modes)&&((int[])Call(right,"GetVisibleModeIndices")).SequenceEqual(modes),"both selectors match ordinary data-analysis modes");
                foreach(int mode in modes){Set(c,"LeftSpectrumMode",mode);Check((int)Get(c,"LeftSpectrumMode")!=(int)Get(c,"RightSpectrumMode"),"left mode distinct "+mode);Check((bool)Get(left,"SelfCheckPassed")&&(bool)Get(right,"SelfCheckPassed"),"both data adapters valid "+mode);}
                Set(c,"LeftSpectrumMode",11);Set(c,"RightSpectrumMode",4);Set(c,"LeftSpectrumMode",4);Check((int)Get(c,"RightSpectrumMode")==11,"duplicate mode swaps rather than duplicates");
                foreach(var plot in new[]{left,right}){
                    var choice=(ComboBox)Field(plot,"compactModeChoice");
                    for(int i=0;i<modes.Length;i++){
                        choice.SelectedIndex=i;Application.DoEvents();
                        Check((int)Get(plot,"DisplayModeIndex")==modes[i]&&choice.SelectedIndex==i&&choice.Text==(string)Call(plot,"GetModeButtonText",modes[i]),"native dropdown commits a nonblank matching mode "+modes[i]);
                        Check((int)Get(c,"LeftSpectrumMode")!=(int)Get(c,"RightSpectrumMode"),"native dropdown never duplicates the other chart "+modes[i]);
                    }
                }
                Set(c,"LeftSpectrumMode",11);Set(c,"RightSpectrumMode",4);
                Call(right,"AddSelectionRegion",.9e6,3.1e6,90d,310d,false);Set(right,"RegionActionCommand",0);
                Check(((int[])Get(left,"VisibleSourceRowIndices")).SequenceEqual(new[]{0,1,2})&&((int[])Get(right,"VisibleSourceRowIndices")).SequenceEqual(new[]{0,1,2}),"F-T region synchronizes exact original row ids to PRPD");
                var input=Call(c,"CaptureInput");Check(((int[])Field(input,"Rows")).SequenceEqual(new[]{0,1,2})&&((double[])Field(input,"Charges")).SequenceEqual(new[]{100d,-101d,102d}),"recognition input follows region and original signed charge");
                Call(c,"ClearLinkedSelection");Check((int)Get(left,"VisibleSourceRowCount")==7&&(int)Get(right,"VisibleSourceRowCount")==7,"show all restores accepted rows");
                Set(c,"LeftSpectrumMode",2);Call(left,"AddSelectionRegion",20d,80d,-110d,110d,false);Set(left,"RegionActionCommand",0);
                Check(((int[])Get(left,"VisibleSourceRowIndices")).SequenceEqual(new[]{0,1,2})&&((int[])Get(right,"VisibleSourceRowIndices")).SequenceEqual(new[]{0,1,2}),"Q-PHI region synchronizes to F-T");
                Set(left,"PhaseFilterIndex",2);Check((int)Get(right,"PhaseFilterIndex")==2&&(int)Get(left,"VisibleSourceRowCount")==0&&(int)Get(right,"VisibleSourceRowCount")==0,"phase filters synchronize without reintroducing region rows");
                Set(right,"PhaseFilterIndex",-1);Check((int)Get(left,"PhaseFilterIndex")==-1,"reverse phase filter synchronization");
                Call(c,"ClearLinkedSelection");Call(left,"SetPhaseVisibility",new[]{false,true,false});Check(((bool[])Get(right,"PhaseVisibility")).SequenceEqual(new[]{false,true,false})&&(int)Get(left,"VisibleSourceRowCount")==1,"phase visibility synchronization");Call(left,"SetPhaseVisibility",new[]{true,true,true});
                bool denied=false;try{Set(right,"RegionActionCommand",1);}catch(TargetInvocationException e){denied=e.InnerException is InvalidOperationException;}Check(denied,"full spectra retain read-only delete guard");
                Check(Matrix().Cast<double>().SequenceEqual(((double[,])Get(left,"ResultParameterMatrix")).Cast<double>())&&Matrix().Cast<double>().SequenceEqual(((double[,])Get(right,"ResultParameterMatrix")).Cast<double>()),"both source snapshots unchanged including acceptance/compensated charge");
                Check(hashes.All(x=>Hash(x.Key)==x.Value),"saved source archive bytes never modified");
                var reader=asm.GetType("HVX.PdReview.PdReviewControl",true).GetMethod("ReadRecognitionScores",All);
                var scores=(Array)reader.Invoke(null,new object[]{new Dictionary<string,object>{{"scores_json","{\"tip\":0.8,\"internal\":0.6,\"surface\":\"0.9\",\"floating\":1.1,\"noise\":0.2,\"invalid\":0}"}},"A",false});
                Check(scores.Length==4,"only supplied finite bounded numeric scores accepted; multi-label scores not normalized");Check((double)Get(scores.GetValue(0),"Score")==.8&&(double)Get(scores.GetValue(1),"Score")==.6,"raw model scores unchanged");
                var empty=(Array)reader.Invoke(null,new object[]{new Dictionary<string,object>{{"result","尖端放电"}},"A",true});Check(empty.Length==0,"labels without scores never synthesize 100 percent");
                var invalid=(Array)reader.Invoke(null,new object[]{new Dictionary<string,object>{{"tip",Double.NaN},{"internal",Double.PositiveInfinity},{"surface",-1d},{"noise",true}},"A",true});Check(invalid.Length==0,"invalid score outputs not shown");
                var resultType=asm.GetType("HVX.PdReview.DischargeRecognitionResult",true);
                var result=resultType.GetConstructors(All).Single().Invoke(new object[]{true,"A: 尖端放电","Ready","INTERNAL RULE SECRET","tip","hash","scope",3,"diagnostic","context",scores});
                string text=(string)Call(c,"RecognitionSummary",result);Check(text.Contains("80.0%")&&text.Contains("60.0%")&&!text.Contains("INTERNAL")&&!text.Contains("scope")&&!text.Contains("diagnostic"),"customer summary includes actual class scores but no internal rules");
                var returned=(Array)Get(result,"ClassScores");returned.SetValue(null,0);Check(((Array)Get(result,"ClassScores")).GetValue(0)!=null,"score array returned defensively");
                Set(c,"LeftSpectrumMode",0);Set(c,"RightSpectrumMode",4);
                Check(!((int[])Get(left,"VisibleSourceRowIndices")).Contains(4)&&((int[])Get(right,"VisibleSourceRowIndices")).Contains(4),"Q-L omits unknown location while F-T retains genuine pulse; no fabricated position");
                input=Call(c,"CaptureInput");Check(((int[])Field(input,"Rows")).Contains(4)&&((double[])Field(input,"Charges")).Contains(104d)&&((int[])Get(c,"SharedSourceRowIndices")).Length==7,"recognition consumes shared finite PHI/Q independently of either plot's extra coordinate");
                Check(((Control)Field(c,"recognize")).Enabled&&((Control)Field(c,"add")).Enabled,"recognition and learning availability follow shared input");
                foreach(int mode in new[]{0,1,2,3,4,5,6,9,10,12}){Set(c,"RightSpectrumMode",mode);input=Call(c,"CaptureInput");Check(((int[])Field(input,"Rows")).Contains(4)&&((double[])Field(input,"Angles")).Contains(125d),"right non-PRPD mode retains shared PHI/Q input "+mode);}
                Set(c,"LeftSpectrumMode",11);Set(c,"RightSpectrumMode",4);
                Action<string,Action> stale=(name,change)=>{SetField(c,"recognition",result);int old=(int)Field(c,"generation");change();Check(!(bool)Call(c,"Current",old)&&Get(c,"CurrentDischargeRecognition")==null,"stale recognition cannot publish after "+name);};
                stale("nonempty region",()=>{Call(right,"AddSelectionRegion",.9e6,3.1e6,90d,310d,false);Set(right,"RegionActionCommand",0);});
                stale("empty region",()=>{Call(right,"AddSelectionRegion",20e6,30e6,2000d,3000d,false);Set(right,"RegionActionCommand",0);});
                Check(((int[])Get(c,"SharedSourceRowIndices")).Length==0&&!((Control)Field(c,"recognize")).Enabled&&!((Control)Field(c,"add")).Enabled,"empty region stays empty in both charts and cannot identify/learn");
                stale("phase switch",()=>Set(left,"PhaseFilterIndex",2));Call(c,"ClearLinkedSelection");Set(left,"PhaseFilterIndex",-1);
                stale("saved archive reload",()=>Check((bool)Call(c,"LoadSavedAnalysis","linked"),"same archive reload succeeds"));
                Check(((int[])Get(c,"SharedSourceRowIndices")).Length==7&&((int[])Get(c,"SelectedSourceRowIndices")).Length==0,"reload clears shared selection and prior verdict");
                // 1x / ~1.5x / 2x text metrics exercise the high-DPI layout
                // boundary without pretending to change Windows monitor DPI.
                foreach(var font in new[]{standardFont,largeFont,doubledFont})foreach(int language in new[]{0,1,18})foreach(int width in new[]{760,1400}){
                    float fontSize=font.SizeInPoints;
                    c.Font=font;Set(c,"LanguageIndex",language);f.ClientSize=new Size(width,900);Set(c,"LeftSpectrumMode",11);Set(c,"RightSpectrumMode",4);c.PerformLayout();Application.DoEvents();var a=(Control)left;var b=(Control)right;
                    using(var bmp=new Bitmap(c.Width,c.Height)){c.DrawToBitmap(bmp,c.ClientRectangle);bmp.Save(Path.Combine(qa,"linked-"+language+"-"+width+"-"+fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"));}
                    Check(a.Width>=300&&b.Width>=300&&!a.Bounds.IntersectsWith(b.Bounds)&&a.Parent==b.Parent,"two visible non-overlapping charts "+language+"/"+width+"/"+fontSize+" left="+a.Bounds+" right="+b.Bounds+" table="+a.Parent.Bounds);
                    var viewport=(ScrollableControl)Field(c,"viewport");Check(a.Right<=a.Parent.Width&&b.Right<=b.Parent.Width&&viewport.AutoScrollMinSize.Width>=a.Parent.Right,"minimum chart width is reachable without overlap");
                    foreach(var plot in new[]{left,right}){
                        var choice=(ComboBox)Field(plot,"compactModeChoice");var caption=(Label)Field(plot,"compactModeCaption");
                        Check(choice.Visible&&caption.Visible&&choice.SelectedIndex>=0&&!String.IsNullOrWhiteSpace(choice.Text)&&choice.Text==(string)Get(plot,"DisplayModeName"),"native selector shows actual selected mode, never blank "+language+"/"+width+"/"+fontSize);
                        int textWidth=TextRenderer.MeasureText(choice.Text,choice.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width;
                        Check(choice.Width>=textWidth+28&&choice.Parent.ClientRectangle.Contains(choice.Bounds)&&!choice.Bounds.IntersectsWith(caption.Bounds),"mode text, arrow and label fit without overlap "+language+"/"+width+"/"+fontSize);
                        Check(choice.Width<=Math.Max(108,modes.Max(i=>TextRenderer.MeasureText((string)Call(plot,"GetModeButtonText",i),choice.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width)+42)&&choice.DropDownWidth==choice.Width,"selector and popup width bounded to readable content instead of half-canvas "+language+"/"+width+"/"+fontSize);
                    }
                }
                Check((string)((Control)Field(c,"summary")).Text==""&&!((Control)Field(c,"summary")).Visible,"working card omits detailed internal summary");
            }
            Console.WriteLine("PASS "+checks+" linked-spectrum and score-contract offline checks; InferenceInvoked=False; LearningInvoked=False; HardwareAccessed=False; UserProjectsWritten=False");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
