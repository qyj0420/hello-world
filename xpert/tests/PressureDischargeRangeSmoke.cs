using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class PressureDischargeRangeSmoke
{
    static int checks;
    static void Check(bool value,string name){checks++;if(!value)throw new InvalidOperationException(name);}
    static object Get(object o,string n){return o.GetType().GetProperty(n).GetValue(o,null);}
    static void Set(object o,string n,object value){o.GetType().GetProperty(n).SetValue(o,value,null);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethod(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Invoke(o,args);}
    static void Reject(Action operation,string name){bool rejected=false;try{operation();}catch(TargetInvocationException e){rejected=e.InnerException is ArgumentException||e.InnerException is InvalidOperationException;}Check(rejected,name);}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string root=Path.GetFullPath(args[0]);string dll=Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll");if(!File.Exists(dll))dll=Path.Combine(root,"Xiongxin.OWTS.PressureTest.dll");
            Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(dll),"HVX.PdReview.V1.0.dll"));var assembly=Assembly.LoadFrom(dll);
            using(var form=new Form{ClientSize=new Size(1256,720),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var control=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true)))
            {
                form.Controls.Add(control);control.Dock=DockStyle.Fill;Set(control,"EmbeddedMode",true);form.Show();Application.DoEvents();
                Check(!(bool)Get(control,"IsDischargeCalibrated")&&(string)Get(control,"SelectedRangeCode")=="UNCALIBRATED","no fabricated initial calibration");
                Check(((string[])Call(control,"GetRangeLabels")).All(s=>s=="未校准"),"uncalibrated labels");
                Check((int)Get(control,"SelectedPicoRangeCode")==-1&&Double.IsNaN((double)Get(control,"SelectedExactFullScalePc")),"no uncalibrated hardware mapping");
                Reject(()=>Call(control,"SetSelectedRange","500pC"),"legacy charge code not mapped implicitly");
                Reject(()=>Call(control,"SetSelectedRange","PICO:4"),"no manual range before calibration");
                DateTimeOffset time=DateTimeOffset.UtcNow.AddMinutes(-1);
                var chargeField=control.GetType().GetField("calibrationReferenceChargePc",BindingFlags.NonPublic|BindingFlags.Instance);
                var hashField=control.GetType().GetField("calibrationSourceHash",BindingFlags.NonPublic|BindingFlags.Instance);
                string hash=Convert.ToBase64String(new byte[32]);
                Reject(()=>Call(control,"SetDischargeCalibrationWithEvidence",1000d,"QA",time,"A",false,0d,hash),"invalid calibration charge rejected");
                Reject(()=>Call(control,"SetDischargeCalibrationWithEvidence",1000d,"QA",time,"A",false,250d,Convert.ToBase64String(new byte[3])),"invalid hash length rejected");
                Call(control,"SetDischargeCalibrationWithEvidence",1000d,"QA",time,"A",false,250d,hash);
                Check((double)chargeField.GetValue(control)==250&&(string)hashField.GetValue(control)==hash,"calibration evidence atomically installed");
                Call(control,"SetDischargeCalibration",1000d,"new coefficient without reference evidence",time,"A",false);
                Check(Double.IsNaN((double)chargeField.GetValue(control))&&(string)hashField.GetValue(control)=="","legacy setter cannot retain stale reference charge/hash");
                Call(control,"SetDischargeCalibrationWithEvidence",1000d,"QA",time,"A",false,250d,hash);
                Call(control,"ClearDischargeCalibration");
                Check(Double.IsNaN((double)chargeField.GetValue(control))&&(string)hashField.GetValue(control)=="","clear invalidates all calibration evidence");
                foreach(double invalid in new[]{0d,-1d,Double.NaN,Double.PositiveInfinity,Double.MaxValue})Reject(()=>Call(control,"SetDischargeCalibration",invalid,"QA",time,"A",false),"invalid coefficient");
                Reject(()=>Call(control,"SetDischargeCalibration",123d,"",time,"A",false),"source required");
                Reject(()=>Call(control,"SetDischargeCalibration",123d,"QA",default(DateTimeOffset),"A",false),"time required");
                Reject(()=>Call(control,"SetDischargeCalibration",123d,"QA",DateTimeOffset.UtcNow.AddDays(1),"A",false),"future time rejected");
                Reject(()=>Call(control,"SetDischargeCalibration",123d,"QA",time,"B",false),"phase must match");
                Reject(()=>Call(control,"SetDischargeCalibration",123d,"QA",time,"A",true),"simulated coefficient rejected for live context");
                Call(control,"SetDischargeCalibration",123.456,"QA incident peak: explicit pC/V",time,"A",false);
                Check((bool)Get(control,"IsDischargeCalibrated"),"valid explicit coefficient accepted");
                var ranges=(double[])Call(control,"GetPicoFullScaleVoltages");Check(ranges.SequenceEqual(new[]{.01,.02,.05,.1,.2,.5,1,2,5,10,20}),"official Pico full scale list");
                var codes=(string[])Call(control,"GetRangeCodes");Check(codes.Length==11&&codes.Select((s,i)=>s=="PICO:"+i).All(x=>x),"unambiguous hardware codes");
                var exact=(double[])Call(control,"GetExactDischargeRangesPc");var display=(double[])Call(control,"GetDisplayDischargeRangesPc");
                var labels=(string[])Call(control,"GetRangeLabels");
                Check(labels[0]==""&&labels[10].StartsWith("±")&&labels[10].EndsWith(" nC")&&!labels[10].Contains("V"),"charge-only labels hide ranges below 100 pC");
                for(int i=0;i<ranges.Length;i++)
                {
                    Check(exact[i]==ranges[i]*123.456,"exact pC/V multiply "+i);
                    Check(exact[i]<100?Double.IsNaN(display[i]):display[i]>=100,"minimum selectable charge "+i);
                    if(exact[i]>=100)Check(display[i]%(exact[i]<1000?50:100)==0,"display step only "+i);
                    if(exact[i]<100){int before=(int)Get(control,"SelectedPicoRangeCode");Reject(()=>Call(control,"SetSelectedRange",codes[i]),"below-floor direct selection rejected "+i);Check((int)Get(control,"SelectedPicoRangeCode")==before,"rejected selection retains valid range "+i);continue;}
                    Call(control,"SetSelectedRange",codes[i]);Check((int)Get(control,"SelectedPicoRangeCode")==i&&(double)Get(control,"SelectedFullScaleVoltageV")==ranges[i]&&(double)Get(control,"SelectedExactFullScalePc")==exact[i],"selected exact mapping "+i);
                    string json=(string)Call(control,"CaptureActionParameters");Check(json.Contains("\"picoRangeCode\":"+i)&&json.Contains("\"exactFullScalePc\":")&&!json.Contains("NaN"),"range metadata valid JSON "+i);
                    var selector=control.Controls.Find("RangeSelectorButton",true).FirstOrDefault();
                    Check(selector!=null&&!selector.Text.Contains("V")&&
                        (exact[i]<100?selector.Text.Contains("请选择"):selector.Text.Contains(labels[i])),"charge-only selected label "+i);
                    Check(selector.AccessibleDescription.Contains("123.456")&&selector.AccessibleDescription.Contains("pC/V"),"exact coefficient exposed for inspection "+i);
                }
                Check((double)Get(control,"DischargeCalibrationPcPerVolt")==123.456&&((DateTimeOffset)Get(control,"DischargeCalibrationAcquiredUtc")).Equals(time),"calibration provenance retained");
                Call(control,"SetDischargeCalibration",10000d,"QA linear source",time,"A",false);
                Call(control,"SetSelectedRange","PICO:5");
                Check(((string[])Call(control,"GetRangeLabels"))[5]=="±5.0 nC"&&(double)Get(control,"SelectedExactFullScalePc")==5000,"display change preserves exact hardware mapping");
                Set(control,"AdvancedMode",true);Set(control,"AutomaticRange",false);
                var menu=(ContextMenuStrip)control.GetType().GetField("rangeMenu",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(control);
                for(int i=0;i<menu.Items.Count;i++)
                {
                    var item=(ToolStripMenuItem)menu.Items[i];
                    Check(item.Enabled&&item.Available&&item.Text.StartsWith("±")&&!item.Text.Contains("V"),"actual manual menu item usable "+i);
                    item.PerformClick();Application.DoEvents();
                    Check((int)Get(control,"SelectedPicoRangeCode")==i&&item.Checked,"manual menu click selects exact Pico code "+i);
                }
                Call(control,"SetDischargeCalibration",2500d,"QA midpoint",time,"A",false);
                Check(((string[])Call(control,"GetRangeLabels"))[5]=="±1.2 nC","1.25 nC midpoint rounds down as requested");
                Check(!menu.Items[0].Available&&!menu.Items[1].Available&&menu.Items[2].Available,"small actual ranges hidden instead of duplicated 100 pC options");
                exact[0]=999999;Check(((double[])Call(control,"GetExactDischargeRangesPc"))[0]!=999999,"returned arrays are not writable state");
                Reject(()=>Call(control,"SetSelectedRange","PICO:11"),"unavailable 50V code rejected");
                Set(control,"SelectedPhaseCode","B");Check(!(bool)Get(control,"IsDischargeCalibrated")&&(int)Get(control,"SelectedPicoRangeCode")==-1,"phase change invalidates calibration");
                Check(((string)Call(control,"CaptureActionParameters")).Contains("\"exactFullScalePc\":null"),"missing calibrated values serialized null");
                Set(control,"SelectedPhaseCode","A");Check(!(bool)Get(control,"IsDischargeCalibrated"),"returning phase does not restore stale calibration implicitly");
                Call(control,"SetDischargeCalibration",123.456,"QA",time,"A",false);Set(control,"SimulationMode",true);Check(!(bool)Get(control,"IsDischargeCalibrated"),"simulation transition invalidates live calibration");
                Call(control,"SetDischargeCalibration",12.345,"QA simulation",time,"A",true);Check((bool)Get(control,"IsDischargeCalibrated"),"explicit simulation coefficient allowed only in simulation");
                Set(control,"LanguageIndex",1);Call(control,"ClearDischargeCalibration");Check(((string[])Call(control,"GetRangeLabels")).All(s=>s=="Not calibrated"),"uncalibrated English labels");
                Check((long)Get(control,"PressureRequestSequence")==0,"mapping and calibration never request high voltage");form.Close();
            }
            Console.WriteLine("PRESSURE_DISCHARGE_RANGE_PASS="+checks);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
