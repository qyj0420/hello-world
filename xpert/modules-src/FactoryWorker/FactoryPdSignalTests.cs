using System;
using System.Linq;
using HVX.SignalProcessing;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryPdSignalTests
    {
        internal static string Run()
        {
            int n=0;Action<bool,string> check=(ok,text)=>{if(!ok)throw new Exception("10 kHz filter: "+text);n++;};
            const double rate=1000000;
            var input=new double[4000];var random=new Random(2871);
            for(int i=0;i<input.Length;i++)input[i]=random.NextDouble()-.5;
            var frozen=(double[])input.Clone();var actual=PdLinearHighPass.Apply(input,rate);
            // Independent direct symmetric FIR convolution; production uses
            // sliding weighted sums. Compare interior to avoid edge assumptions.
            int window=PdLinearHighPass.WindowLength(rate)-2,radius=window/2;
            double omega=2*Math.PI*10000/rate,target=Math.Pow(1-1/Math.Sqrt(2),.25);
            double edge=(Math.Sin(window*omega/2)/Math.Sin(omega/2)-target*window)/(2*(target-Math.Cos((radius+1)*omega)));
            var box=new double[window+2];for(int i=1;i<box.Length-1;i++)box[i]=1/(window+2*edge);box[0]=box[box.Length-1]=edge/(window+2*edge);
            var kernel=new[]{1.0};for(int pass=0;pass<4;pass++)kernel=Convolve(kernel,box);
            int center=kernel.Length/2;double error=0;
            for(int i=center;i<input.Length-center;i++){double low=0;for(int j=0;j<kernel.Length;j++)low+=input[i+j-center]*kernel[j];error=Math.Max(error,Math.Abs(actual[i]-(input[i]-low)));}
            check(error<1e-12,"optimized implementation equals independent FIR convolution");
            check(input.SequenceEqual(frozen),"filter input never overwritten");
            foreach(double fs in new[]{100000.0,1000000.0,125000000.0})
            {
                int count=(int)(fs*.004),guard=PdLinearHighPass.GuardSamples(fs);var x=new double[count];
                for(int i=0;i<count;i++)x[i]=.2+Math.Sin(2*Math.PI*10000*i/fs);
                var y=PdLinearHighPass.Apply(x,fs);double sine=0,cosine=0,power=0;
                for(int i=guard;i<count-guard;i++){double s=Math.Sin(2*Math.PI*10000*i/fs),c=Math.Cos(2*Math.PI*10000*i/fs);sine+=y[i]*s;cosine+=(y[i]-s/Math.Sqrt(2))*c;power+=s*s;}
                check(Math.Abs(sine/power-1/Math.Sqrt(2))<1e-8,"10 kHz -3 dB gain at actual rate "+fs);
                check(Math.Abs(cosine/power)<1e-8,"compensated phase at actual rate "+fs);
            }
            foreach(double frequency in new[]{1000.0,100000.0})
            {
                var x=new double[20000];for(int i=0;i<x.Length;i++)x[i]=Math.Sin(2*Math.PI*frequency*i/rate);
                var y=PdLinearHighPass.Apply(x,rate);double xy=0,xx=0;for(int i=1000;i<19000;i++){xy+=x[i]*y[i];xx+=x[i]*x[i];}
                check(frequency==1000?xy/xx<.02:xy/xx>.99,"low-frequency suppression / high-frequency passband "+frequency);
            }
            var capture=new BenchCapture {PicoRateHz=rate,PicoFullScaleV=1,MaximumAdc=32767,PdAdc=Enumerable.Repeat((short)1234,5000).ToArray()};
            var dc=FactoryPdSignal.Get(capture);check(dc.All(v=>Math.Abs(v)<1e-10),"constant ADC offset removed");
            check(Object.ReferenceEquals(dc,FactoryPdSignal.Get(capture)),"same immutable capture reused across display and analysis");
            capture.PdAdc[2500]=10000;var changed=FactoryPdSignal.Get(capture);
            check(!Object.ReferenceEquals(dc,changed) && changed[2500]>.2 && capture.PdAdc[0]==1234,"cache invalidates actual sample edits, raw unchanged");
            capture.PicoFullScaleV=2;var scaled=FactoryPdSignal.Get(capture);
            check(Math.Abs(scaled[2500]-changed[2500]*2)<1e-12,"cache respects actual ADC scale");
            for(int k=1;k<200;k++)check(Math.Abs(scaled[2500-k]-scaled[2500+k])<1e-12,"impulse symmetry "+k);
            return "R28 shared 10 kHz linear-phase high-pass: "+n+" checks PASS; independent FIR reference, phase/gain/native samples/cache; HardwareAccessed=False";
        }
        private static double[] Convolve(double[] a,double[] b)
        {var c=new double[a.Length+b.Length-1];for(int i=0;i<a.Length;i++)for(int j=0;j<b.Length;j++)c[i+j]+=a[i]*b[j];return c;}
    }
}
