using System;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Linq;
using System.Windows.Forms;

internal static class PressureLatestDisplayContractSmoke
{
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    [STAThread] static int Main(string[] args)
    {
        try{
            var root=System.IO.Path.GetFullPath(args[0]);
            var assembly=Assembly.LoadFrom(System.IO.Path.Combine(root,"modules/PressureTest/Xiongxin.OWTS.PressureTest.dll"));
            var type=assembly.GetType("HVX.Controls.PressureTestControl",true);
            using(var control=(Control)Activator.CreateInstance(type)){
                var grid=(DataGridView)type.GetField("pressurePlanGrid",Hidden).GetValue(control);
                foreach(DataGridViewColumn c in grid.Columns)Check(c.SortMode==DataGridViewColumnSortMode.NotSortable,"plan execution order cannot be sorted");
                var metric=type.GetMethod("MetricText",BindingFlags.Static|BindingFlags.NonPublic);
                Check((string)metric.Invoke(null,new object[]{1218.8,"0"})=="1219","charge integer");
                Check((string)metric.Invoke(null,new object[]{390.3,"0.00"})=="390.30","frequency two decimals");
                Check((string)metric.Invoke(null,new object[]{2.267,"0.00"})=="2.27","loss two decimals");
                Check((string)metric.Invoke(null,new object[]{double.NaN,"0.00"})=="—","unknown not zero");
                type.GetMethod("SetPressurePlan",new[]{typeof(double[]),typeof(int[]),typeof(double[])}).Invoke(control,new object[]{new[]{0d,.5,1d},new[]{1,1,3},new[]{0d,4.35,8.7}});
                type.GetMethod("SetPlanVoltageDisplayMode").Invoke(control,new object[]{"PEAK"});
                Check((string)grid.Rows[1].Cells[2].Value=="6.2","peak one decimal");
                Check((string)grid.Rows[1].Cells[0].Value=="0.5","multiplier stays with voltage");
                type.GetMethod("SetPlanVoltageDisplayMode").Invoke(control,new object[]{"RMS"});
                Check((string)grid.Rows[1].Cells[2].Value=="4.4","RMS one decimal");
                const double rate=80000;
                type.GetField("lastVoltageKv",Hidden).SetValue(control,Enumerable.Range(0,4800).Select(i=>10*Math.Cos(2*Math.PI*400*i/rate)).ToArray());
                type.GetField("lastSampleRateHz",Hidden).SetValue(control,rate);
                type.GetField("cycleTargetPeakKv",Hidden).SetValue(control,10d);
                type.GetField("requestedTargetPeakVoltageKv",Hidden).SetValue(control,10d);
                type.GetField("cyclePressureAttempt",Hidden).SetValue(control,true);
                type.GetField("cycleAcquisitionId",Hidden).SetValue(control,"fixture");
                type.GetField("lastPressureAcquisitionId",Hidden).SetValue(control,"fixture");
                var captureField=type.GetField("lastPressureCapture",Hidden);
                var capture=Activator.CreateInstance(captureField.FieldType,true);
                captureField.FieldType.GetField("ChargePc",Hidden).SetValue(capture,new double[4800]);
                var rawField=captureField.FieldType.GetField("Capture",Hidden);
                var raw=Activator.CreateInstance(rawField.FieldType,true);
                rawField.FieldType.GetField("PicoRateHz").SetValue(raw,rate);
                rawField.SetValue(capture,raw);captureField.SetValue(control,capture);
                foreach(int cycles in new[]{1,2}){
                    type.GetField("locationSaveCycleCount",Hidden).SetValue(control,cycles);
                    type.GetMethod("EvaluateLocationCycleWindow",Hidden).Invoke(control,null);
                    var window=type.GetField("locationCycleWindow",Hidden).GetValue(control);
                    var crossings=(double[])window.GetType().GetField("CrossingSeconds",Hidden).GetValue(window);
                    Check(crossings.Length==cycles*2+1,"real capture retains selected N complete cycles");
                }
            }
            var waveform=assembly.GetType("HVX.Controls.PressureWaveformView",true);
            using(var view=(Control)Activator.CreateInstance(waveform,true)){
                var bounds=new Rectangle(10,20,500,300);
                foreach(bool manual in new[]{false,true}){
                    waveform.GetField("manualDischargeScale",Hidden).SetValue(view,manual);
                    foreach(string name in new[]{"CreatePdTrack","CreateVoltageTrack"})
                        Check((Rectangle)waveform.GetMethod(name,Hidden).Invoke(view,new object[]{bounds})==bounds,"full-height independent axes");
                }
            }
            var browser=Assembly.LoadFrom(System.IO.Path.Combine(root,"modules/DataBrowser/Xiongxin.OWTS.DataBrowser.dll")).GetType("HVX.Controls.BrowserReader",true);
            var parse=browser.GetMethod("NewRecord",BindingFlags.Static|BindingFlags.NonPublic);
            foreach(string time in new[]{"10'38'08","10'38'08.1","10'38'08.1117276"}){
                var record=parse.Invoke(null,new object[]{System.IO.Path.Combine(root,"A_6.152kV_2026-10-08-"+time+".loc")});
                Check((double)record.GetType().GetField("VoltageKv").GetValue(record)==6.152,"fractional filename voltage");
                Check((DateTime)record.GetType().GetField("Timestamp").GetValue(record)!=DateTime.MinValue,"fractional filename time");
            }
            Console.WriteLine("PASS "+checks+" checks; managed UI only, no hardware.");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
