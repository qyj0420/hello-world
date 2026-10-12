using System;
using System.Collections.Generic;
using System.Globalization;
using HVX.PD.PulseToolkit;

namespace HVX.PdReview
{
    // Spectrum matrix contract: column 23 is energy RMS duration in ns;
    // column 24 is one-sided spectral RMS bandwidth in Hz. Neither is the
    // DAC oscillation frequency or an incident half-height width.
    internal static class ReviewPulseTimeFrequency
    {
        internal const string Contract="PulseToolkit-2.0.0-incident-energy-rms-ns-hz-v1";
        internal const int MaximumPacketSamples=131072;
        internal static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        internal static bool Integer(object value,out int result)
        {
            result=0;if(value==null)return false;
            try{double number=Convert.ToDouble(value,CultureInfo.InvariantCulture);
                if(!Finite(number)||number<0||number>Int32.MaxValue||number!=Math.Truncate(number))return false;
                result=(int)number;return true;
            }catch(Exception error) when(error is FormatException||error is InvalidCastException||error is OverflowException){return false;}
        }
        internal static bool Window(Dictionary<string,object> pulse,int incident,out int first,out int last)
        {
            first=last=0;object value;
            if(pulse==null||!pulse.TryGetValue("incident_window",out value))return false;
            var bounds=value as object[];
            if(bounds==null||bounds.Length!=2||!Integer(bounds[0],out first)||!Integer(bounds[1],out last)||
                first>incident||incident>last||(long)last-first+1<8||(long)last-first+1>MaximumPacketSamples)return false;
            object descriptor;
            if(pulse.TryGetValue("incident_descriptor",out descriptor)) {
                var fields=descriptor as Dictionary<string,object>;object truncated;
                if(fields!=null&&fields.TryGetValue("edge_truncated",out truncated)&&Object.Equals(truncated,true))return false;
            }
            return true;
        }
        internal static bool TryMeasure(double[] samples,int sampleFirst,int incident,double sampleRateHz,double baseline,
            Dictionary<string,object> pulse,out double timeNs,out double bandwidthHz)
        {
            timeNs=bandwidthHz=Double.NaN;int first,last;
            if(samples==null||sampleFirst<0||!Finite(sampleRateHz)||sampleRateHz<=0||!Finite(baseline)||
                !Window(pulse,incident,out first,out last)||first<sampleFirst||(long)last-sampleFirst>=samples.Length)return false;
            int count=last-first+1;
            // The packet ledger supplies the measured raw support and baseline.
            // Numerical zero guards keep PulseToolkit's edge-baseline estimator
            // away from a short packet's peak; they are not acquired samples.
            // Zero padding does not add pulse energy or change the time origin.
            int guard=Math.Max(16,count);var packet=new double[count+2*guard];
            for(int i=0;i<count;i++) {
                double value=samples[first-sampleFirst+i];if(!Finite(value))return false;
                packet[guard+i]=value-baseline;
            }
            var result=new PulseAnalysisEngine().Analyze(packet,sampleRateHz);
            if(!result.Success||!Finite(result.EquivalentTimeNs)||result.EquivalentTimeNs<=0||
                !Finite(result.EquivalentBandwidthHz)||result.EquivalentBandwidthHz<=0)return false;
            timeNs=result.EquivalentTimeNs;bandwidthHz=result.EquivalentBandwidthHz;return true;
        }
    }
}
