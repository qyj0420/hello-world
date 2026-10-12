using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Windows.Forms;

// Display-only synthetic fixtures. No device delegates, SDK or product Main.
internal static class PressureWaveformDisplayRulesSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static Type waveType;
    static void Check(bool value,string why){checks++;if(!value)throw new InvalidOperationException(why);}
    static object Get(object obj,string name){var property=obj.GetType().GetProperty(name,All);return property!=null?property.GetValue(obj,null):obj.GetType().GetField(name,All).GetValue(obj);}
    static object Call(object obj,string name,params object[] args){return obj.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(obj,args);}
    static object Rule(string name,params object[] args){return waveType.GetMethod(name,All).Invoke(null,args);}
    static double Pd(double required){return (double)Rule("AutomaticPdDisplayMaximum",required);}
    static double Volt(double actual){return (double)Rule("VoltageDisplayMaximum",actual);}
    static double Ratio(double actual,double maximum){return (double)Rule("VoltageDisplayYRatio",actual,maximum);}
    static void Mouse(Control view,string method,MouseButtons button,Point at){Call(view,method,new MouseEventArgs(button,1,at.X,at.Y,0));}
    static int Y(Rectangle plot,double pc,double maximum){return plot.Bottom-(int)Math.Round(pc/maximum*plot.Height);}

    static IEnumerable<MethodBase> Calls(MethodInfo method)
    {
        var opcodes=typeof(OpCodes).GetFields(BindingFlags.Static|BindingFlags.Public).Where(f=>f.FieldType==typeof(OpCode))
            .Select(f=>(OpCode)f.GetValue(null)).ToDictionary(o=>(ushort)o.Value);
        byte[] il=method.GetMethodBody().GetILAsByteArray();int at=0;
        while(at<il.Length){
            ushort code=il[at++];if(code==0xfe)code=(ushort)(0xfe00|il[at++]);OpCode op=opcodes[code];
            int count=0;
            switch(op.OperandType){
                case OperandType.InlineNone:break;
                case OperandType.ShortInlineBrTarget:case OperandType.ShortInlineI:case OperandType.ShortInlineVar:count=1;break;
                case OperandType.InlineVar:count=2;break;
                case OperandType.InlineI8:case OperandType.InlineR:count=8;break;
                case OperandType.InlineSwitch:count=4+4*BitConverter.ToInt32(il,at);break;
                default:count=4;break;
            }
            if(op.OperandType==OperandType.InlineMethod)yield return method.Module.ResolveMethod(BitConverter.ToInt32(il,at));
            at+=count;
        }
    }

    static void AxisRules()
    {
        double[] steps={100,500,1000,2000,5000,10000,20000,50000,100000,150000};
        Check(Pd(0)==100&&Pd(1)==100&&Pd(99.99)==100,"empty/small data uses minimum 100 pC display, not hardware range");
        for(int i=0;i<steps.Length;i++){
            Check(Pd(steps[i])==steps[i],"inclusive exact display boundary "+steps[i]);
            double next=i+1<steps.Length?steps[i+1]:steps[i]+.25;
            Check(Pd(steps[i]+.25)==next,"above display boundary advances or uses non-clipping fallback "+steps[i]);
        }
        Check(Pd(200000.125)==200000.125,"above 150 nC retains full real extent without clipping or fabricated next step");
        Check(Pd(Double.NaN)==100&&Pd(Double.PositiveInfinity)==100&&Pd(-1)==100,"invalid drawing request does not fabricate a physical range");
        Check(Volt(0)==5&&Volt(.02)==5&&Volt(4.3)==5,"zero and small voltage preserve at least +/-5 kV visibility");
        Check(Volt(6.2)==7&&Volt(20.1)==21&&Volt(23.999)==24,"noninteger measured peaks ceil to whole kV");
        Check(Volt(5)==6&&Volt(10)==12&&Volt(20)==24,"exact integer peak first receives 20 percent display headroom");
        Check(Volt(Double.MaxValue)==Double.MaxValue,"finite extreme does not overflow display headroom");
        Check(Ratio(12,12)==0&&Math.Abs(Ratio(0,12)-1d/3)<1e-12&&Math.Abs(Ratio(-12,12)-2d/3)<1e-12&&Ratio(-24,12)==1,
            "voltage pixels use +U top, zero one-third, -U two-thirds, -2U bottom");
        var labels=(double[])Rule("VoltageDisplayLabels",12d,6);
        Check(labels.Length==7&&labels.Max()==12&&labels.Min()==-12&&labels.Contains(0d),"right labels cover +/-U and true zero, not the unlabeled -2U margin");
        Check(labels.All(v=>Ratio(v,12)>=0&&Ratio(v,12)<=2d/3+1e-12),"all voltage labels use actual asymmetrical voltage coordinates");
    }

    static void DisplayCopies(Control view,string qa)
    {
        double[] measured={0,10,0,-8,0,3,0},pd={0,0,80,0,0,0,0};
        double[] vcopy=(double[])measured.Clone(),pcopy=(double[])pd.Clone();
        Call(view,"SetData",measured,1000d,pd,2000d);
        double[] retained=(double[])Get(view,"voltage"),display=(double[])Get(view,"displayVoltage");
        Check(measured.SequenceEqual(vcopy)&&pd.SequenceEqual(pcopy)&&retained.SequenceEqual(vcopy),"display preparation never modifies source or retained measured voltage/PD");
        Check(!Object.ReferenceEquals(retained,display)&&display.Length==retained.Length&&display[0]==retained[0]&&display.Last()==retained.Last(),
            "smoothed voltage has separate storage and same native sample count/endpoints");
        for(int i=1;i<display.Length-1;i++)Check(display[i]==retained[i-1]*.25+retained[i]*.5+retained[i+1]*.25,"three-sample display low-pass, no fitted sine at "+i);
        Check((double)Get(view,"cachedVoltageAbs")==12&&retained.Max()==10&&retained.Min()==-8,"voltage scale uses original peak before display smoothing");
        Check((double)Get(view,"voltageRateHz")==1000&&(double)Get(view,"dischargeRateHz")==2000&&(double)Get(view,"cachedFullDuration")==.003,
            "two actual timebases and shared-short extent are unchanged");
        Check(view.AccessibleDescription.Contains("仅显示")&&!view.AccessibleDescription.Contains("Voltage"),"Chinese display smoothing description has no mixed-language text");
        view.GetType().GetProperty("LanguageIndex",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(view,1,null);
        Check(view.AccessibleDescription.Contains("display-only")&&!view.AccessibleDescription.Any(c=>c>='\u4e00'&&c<='\u9fff'),"English display smoothing description remains identifiable without Chinese");
        view.GetType().GetProperty("LanguageIndex",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(view,0,null);
        var prepared=Rule("PrepareData",measured,1000d,pd,2000d,CancellationToken.None);
        Check(((double[])Get(prepared,"Voltage")).SequenceEqual(retained)&&((double[])Get(prepared,"DisplayVoltage")).SequenceEqual(display),"managed async and sync preparation use identical display-only copies");
        Check((double)Get(prepared,"VoltageAbs")==12&&(double)Get(prepared,"FullDuration")==.003,"async axes preserve original peak and duration");
        Call(view,"InstallPreparedData",prepared);
        Check(Object.ReferenceEquals(Get(prepared,"Voltage"),Get(view,"voltage"))&&Object.ReferenceEquals(Get(prepared,"DisplayVoltage"),Get(view,"displayVoltage")),"install keeps measured and drawing arrays separate");
        using(var cancel=new CancellationTokenSource()){
            cancel.Cancel();bool cancelled=false;
            try{Rule("PrepareData",measured,1000d,pd,2000d,cancel.Token);}catch(TargetInvocationException e){cancelled=e.InnerException is OperationCanceledException;}
            Check(cancelled,"canceled pure preparation exits without partial UI install");
        }
        Call(view,"SetData",measured,Double.NaN,pd,2000d);Call(view,"SetDisplayWindow","MAXIMUM",0d);
        Check(!(bool)Get(view,"cachedTimeKnown")&&(double)Get(view,"voltageRateHz")==0&&(double)Get(view,"cachedFullDuration")==6&&(double)Get(view,"DisplayStart")==0,
            "unknown timebase remains sample index with no invented rate/frequency");
        Call(view,"SetData",new double[9],1000d,new[]{0d,0,80,0,0,0,0,0,0},1000d);
        Check((double)Get(view,"cachedVoltageAbs")==5&&((double[])Get(view,"displayVoltage")).All(v=>v==0),"zero voltage stays a zero thin trajectory, not fabricated oscillation");
        Call(view,"SetDischargeScale",false,Double.NaN);
        Check((double)Get(view,"DisplayPdMaximum")==100,"automatic actual PD peak selects 100 pC display");
        Call(view,"SetManualThresholdGuide",true,500d);
        Check((double)Get(view,"DisplayPdMaximum")==500,"exact checked threshold fits same inclusive automatic drawing step");
        Call(view,"SetManualThresholdGuide",true,500.01d);
        Check((double)Get(view,"DisplayPdMaximum")==1000,"checked threshold beyond step advances display without changing input range");
        Call(view,"SetDischargeScale",true,123.456789d);
        Check((double)Get(view,"DisplayPdMaximum")==123.456789,"manual calibrated full scale remains exact, not rounded to automatic plot step");
        Check(((double[])Get(view,"discharge"))[2]==80&&(double)Get(view,"cachedPdMaximum")==80,"threshold/axis changes never alter measured PD metric");
        Call(view,"SetManualThresholdGuide",false,0d);Call(view,"SetDischargeScale",false,Double.NaN);
        Check(!Calls(waveType.GetMethod("DrawPdPulsePlot",All)).Any(m=>m.Name=="FillEllipse"),"PD drawing emits vertical stems without endpoint marker dots");
        var map=(PointF)Call(view,"MapVoltagePoint",0,new Rectangle(0,0,300,300),.008,5d);
        Check(map.Y==100,"actual zero voltage line uses one-third plot height in drawing implementation");
        using(var bitmap=new Bitmap(view.Width,view.Height)){view.DrawToBitmap(bitmap,view.ClientRectangle);bitmap.Save(Path.Combine(qa,"display-zero-and-pd.png"));}
        Call(view,"SetData",new[]{0d,12d,0d,-12d,0d,9d,0d,-9d,0d},1000d,new[]{0d,200000.125,0d,0d,0d,0d,0d,0d,0d},1000d);
        Check((double)Get(view,"DisplayPdMaximum")==200000.125,"integrated auto scale includes actual peak above final listed step");
        using(var bitmap=new Bitmap(view.Width,view.Height)){view.DrawToBitmap(bitmap,view.ClientRectangle);bitmap.Save(Path.Combine(qa,"display-nonzero-over150nc.png"));}
    }

    static void DragCoordinates(Control view)
    {
        Call(view,"SetData",new[]{0d,12d,0d,-12d,0d,0d},1000d,new[]{0d,0d,600d,0d,0d,0d},1000d);
        Call(view,"SetDischargeScale",false,Double.NaN);Call(view,"SetManualThresholdGuide",true,250d);
        Check((double)Get(view,"DisplayPdMaximum")==1000,"drag fixture uses new 1 nC automatic plot step");
        double[] raw=(double[])Get(view,"voltage"),pd=(double[])Get(view,"discharge");double peak=(double)Get(view,"cachedPdMaximum");
        Rectangle plot=(Rectangle)Call(view,"ManualThresholdPlotBounds");int x=plot.Left+plot.Width/2;
        Mouse(view,"OnMouseDown",MouseButtons.Left,new Point(x,Y(plot,250,1000)));
        Check((bool)Get(view,"manualThresholdDragging"),"automatic pC threshold uses PD axis, not asymmetric voltage zero");
        int to=Y(plot,750,1000);Mouse(view,"OnMouseMove",MouseButtons.Left,new Point(x,to));
        double expected=Math.Round((plot.Bottom-to)/(double)plot.Height*1000,2);
        Check((double)Get(view,"ManualThresholdGuidePc")==expected&&(double)Get(view,"DisplayPdMaximum")==1000,"drag freezes automatic step and pC coordinate until commit");
        Mouse(view,"OnMouseUp",MouseButtons.Left,new Point(x,to));
        Call(view,"SetDischargeScale",true,123.456789d);Call(view,"SetManualThresholdGuide",true,30d);
        plot=(Rectangle)Call(view,"ManualThresholdPlotBounds");x=plot.Left+plot.Width/2;
        Mouse(view,"OnMouseDown",MouseButtons.Left,new Point(x,Y(plot,30,123.456789)));
        Mouse(view,"OnMouseMove",MouseButtons.Left,new Point(x,plot.Top-20));
        Check((double)Get(view,"ManualThresholdGuidePc")==123.46&&(double)Get(view,"DisplayPdMaximum")==123.456789,"manual drag keeps exact hardware-calibrated mapping, only threshold follows existing two-decimal editor");
        Mouse(view,"OnMouseUp",MouseButtons.Left,new Point(x,plot.Top-20));
        Check(Object.ReferenceEquals(raw,Get(view,"voltage"))&&Object.ReferenceEquals(pd,Get(view,"discharge"))&&(double)Get(view,"cachedPdMaximum")==peak,
            "dragging all new axes preserves original samples/statistics");
    }

    [STAThread]static int Main(string[] args)
    {
        try{
            if(args.Length!=2)throw new ArgumentException("candidate root and fresh QA fixture required");
            string root=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);if(Directory.Exists(qa))throw new InvalidOperationException("fresh fixture required");Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            waveType=assembly.GetType("HVX.Controls.PressureWaveformView",true);
            Application.EnableVisualStyles();
            using(var view=(Control)Activator.CreateInstance(waveType,true))
            using(var form=new Form{ClientSize=new Size(1000,400),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)}){
                view.Dock=DockStyle.Fill;form.Controls.Add(view);form.Show();Application.DoEvents();
                // Functional negative on the previous package occurs here,
                // before relying on any newly added helper/type existence.
                Call(view,"SetData",new[]{0d,0,0,0,0},1000d,new[]{0d,0,64d,0,0},1000d);
                Call(view,"SetDischargeScale",false,Double.NaN);
                Check((double)Get(view,"DisplayPdMaximum")==100,"automatic 64 pC data must select 100 pC display step");
                AxisRules();DisplayCopies(view,qa);DragCoordinates(view);form.Close();
            }
            Console.WriteLine("PRESSURE_WAVEFORM_DISPLAY_RULES_PASS="+checks+";HardwareAccessed=False;SDKCalled=False;ProductEntryExecuted=False;UserProjectsWritten=False;FullRegression=False;PhysicalAcceptance=False;QA="+qa);return 0;
        }catch(Exception error){Console.Error.WriteLine("PRESSURE_WAVEFORM_DISPLAY_RULES_FAIL after "+checks+" checks: "+error);return 1;}
    }
}
