using System;
using System.Collections.Generic;
using HVX.PdReview;

// Pure numerical reference test. No product entry point, UI, worker or SDK.
static class R58PulseTimeFrequencyOffline
{
    static int checks;
    static void Check(bool pass,string label){if(!pass)throw new Exception(label);checks++;}
    static Dictionary<string,object> Event(int first,int last)
    {return new Dictionary<string,object>{{"incident_window",new object[]{first,last}},{"incident_descriptor",new Dictionary<string,object>{{"edge_truncated",false}}}};}
    static double[] Gaussian(int count,double fs,double sigma,double amplitude,double baseline)
    {var data=new double[count];for(int i=0;i<count;i++){double t=(i-count/2)/fs;data[i]=baseline+amplitude*Math.Exp(-t*t/(2*sigma*sigma));}return data;}
    static int Main()
    {
        try {
            const double fs=250000000,sigma=40e-9;var samples=Gaussian(257,fs,sigma,100,3.25);var original=(double[])samples.Clone();
            double time,bw;Check(ReviewPulseTimeFrequency.TryMeasure(samples,0,128,fs,3.25,Event(0,256),out time,out bw),"valid Gaussian packet measured");
            double expectedTime=sigma/Math.Sqrt(2)*1e9,expectedBw=1/(2*Math.PI*sigma*Math.Sqrt(2));
            Check(Math.Abs(time/expectedTime-1)<.003,"energy RMS duration agrees with independent Gaussian formula within 0.3%");
            Check(Math.Abs(bw/expectedBw-1)<.003,"spectral RMS bandwidth agrees with independent Gaussian formula within 0.3%");
            double t2,b2;var inverted=Gaussian(257,fs,sigma,-17,-19);
            Check(ReviewPulseTimeFrequency.TryMeasure(inverted,0,128,fs,-19,Event(0,256),out t2,out b2),"inverted DC-shifted packet measured");
            Check(Math.Abs(t2/time-1)<1e-10&&Math.Abs(b2/bw-1)<1e-10,"polarity/amplitude/DC invariance");
            var wider=Gaussian(257,fs,2*sigma,100,3.25);
            Check(ReviewPulseTimeFrequency.TryMeasure(wider,0,128,fs,3.25,Event(0,256),out t2,out b2),"second packet measured");
            Check(t2>1.9*time&&b2<.53*bw,"different packet widths produce different measured coordinates");
            var partial=new double[257];Array.Copy(samples,partial,257);
            Check(ReviewPulseTimeFrequency.TryMeasure(partial,1000,1128,fs,3.25,Event(1000,1256),out t2,out b2),"archived global-to-local window mapping");
            Check(Math.Abs(t2-time)<1e-10&&Math.Abs(b2-bw)<1e-6,"absolute record time does not affect features");
            Check(!ReviewPulseTimeFrequency.TryMeasure(samples,0,128,0,3.25,Event(0,256),out t2,out b2)&&Double.IsNaN(t2)&&Double.IsNaN(b2),"invalid sampling clock is unavailable, not zero");
            Check(!ReviewPulseTimeFrequency.TryMeasure(samples,0,128,fs,3.25,new Dictionary<string,object>(),out t2,out b2),"missing packet ledger is unavailable");
            Check(!ReviewPulseTimeFrequency.TryMeasure(samples,0,128,fs,3.25,Event(0,260),out t2,out b2),"truncated raw support is unavailable");
            var edge=Event(0,256);((Dictionary<string,object>)edge["incident_descriptor"])["edge_truncated"]=true;
            Check(!ReviewPulseTimeFrequency.TryMeasure(samples,0,128,fs,3.25,edge,out t2,out b2),"physically truncated packet is not manufactured");
            for(int i=0;i<samples.Length;i++)Check(BitConverter.DoubleToInt64Bits(samples[i])==BitConverter.DoubleToInt64Bits(original[i]),"input samples unchanged");
            Console.WriteLine("R58_FT_SYNTHETIC_PASS="+checks+";TeqNs="+time.ToString("R")+";BandwidthHz="+bw.ToString("R")+";expectedTeqNs="+expectedTime.ToString("R")+";expectedBandwidthHz="+expectedBw.ToString("R")+";HardwareUsed=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
