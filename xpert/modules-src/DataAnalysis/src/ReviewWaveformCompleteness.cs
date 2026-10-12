using System;
using System.Text;
using System.Globalization;
using HVX.Geometry;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        sealed class WaveformCompleteness
        {
            internal double Length,Velocity,Rate;
            internal ConnectingCableGeometryResult Model;
            internal long Required,Valid;
            internal bool Retain;
            internal string Status;
        }
        double[,] completenessSource,completenessWaves;
        WaveformCompleteness[] completenessCache=new WaveformCompleteness[0];
        string waveformCompletenessDiagnosticsJson="{}";

        void EnsureWaveformCompletenessCache()
        {
            if(ReferenceEquals(completenessSource,original)&&ReferenceEquals(completenessWaves,matching)&&completenessCache.Length==working.GetLength(0))return;
            completenessSource=original;completenessWaves=matching;
            completenessCache=new WaveformCompleteness[working.GetLength(0)];
        }
        WaveformCompleteness GetWaveformCompleteness(int row)
        {
            var model=pendingConnectingModels.Length==working.GetLength(0)?pendingConnectingModels[row]:null;
            var result=completenessCache[row];
            double length=working[row,5],velocity=working[row,6],rate=working[row,7];
            if(result==null||!result.Length.Equals(length)||!result.Velocity.Equals(velocity)||!result.Rate.Equals(rate)||!ReferenceEquals(result.Model,model))
                completenessCache[row]=result=MeasureWaveformCompleteness(row,length,velocity,rate,model);
            return result;
        }
        bool IsReviewRowComplete(int row)
        {
            if(row<0||row>=working.GetLength(0))return false;
            EnsureWaveformCompletenessCache();return GetWaveformCompleteness(row).Retain;
        }

        // Geometry determines the physical interval; source identity determines
        // the anchor. A manual cursor move must not silently discard that source.
        bool[] BuildWaveformCompletenessMask()
        {
            var mask=new bool[working.GetLength(0)];
            EnsureWaveformCompletenessCache();
            int retained=0,incomplete=0,unknown=0;
            var entries=new StringBuilder();
            for(int r=0;r<mask.Length;r++)
            {
                var result=GetWaveformCompleteness(r);
                mask[r]=result.Retain;
                if(result.Retain)retained++;else if(result.Status=="InsufficientFiniteSamples")incomplete++;else unknown++;
                if(r>0)entries.Append(',');
                entries.Append("{\"source_row\":").Append(r).Append(",\"status\":\"").Append(result.Status)
                    .Append("\",\"required_samples\":").Append(result.Required).Append(",\"finite_samples\":").Append(result.Valid)
                    .Append(",\"retained\":").Append(result.Retain?"true":"false").Append('}');
            }
            waveformCompletenessDiagnosticsJson="{\"minimum_finite_percent\":95,\"interval\":\"source_incident_through_ceil_dut_round_trip_inclusive\",\"zero_amplitude_is_valid\":true,\"source_unchanged\":true,\"retained\":"+
                retained.ToString(CultureInfo.InvariantCulture)+",\"incomplete\":"+incomplete.ToString(CultureInfo.InvariantCulture)+",\"unknown\":"+unknown.ToString(CultureInfo.InvariantCulture)+",\"rows\":["+entries+"]}";
            return mask;
        }
        void AppendWaveformCompletenessDiagnostics()
        {
            string json=FilteringDiagnosticsJson??"{}";
            int end=json.LastIndexOf('}');if(end<0){json="{}";end=1;}
            string prefix=json.Substring(0,end).TrimEnd();
            FilteringDiagnosticsJson=prefix+(prefix.EndsWith("{",StringComparison.Ordinal)?"":",")+
                "\"waveform_completeness\":"+waveformCompletenessDiagnosticsJson+"}";
        }
        WaveformCompleteness MeasureWaveformCompleteness(int row,double length,double velocity,double rate,ConnectingCableGeometryResult model)
        {
            var result=new WaveformCompleteness{Length=length,Velocity=velocity,Rate=rate,Model=model,Status="UnknownGeometry"};
            if(!Finite(rate)||rate<=0){result.Status="UnknownSamplingRate";return result;}
            if(IsCapacitorRow(row)) {
                double packetFirst=original[row,31],packetLast=original[row,32],incident=original[row,10];
                if(!CompletenessSampleIndex(packetFirst)||!CompletenessSampleIndex(packetLast)||packetLast<packetFirst||incident<packetFirst||incident>packetLast||!CompletenessSampleIndex(incident))
                {result.Status="UnknownSourceCrop";return result;}
                result.Required=(long)(packetLast-packetFirst+1);
                int packetSourceCount=original.GetLength(0),packetWaveRow=matching.GetLength(0)==packetSourceCount||matching.GetLength(0)==3L*packetSourceCount?row:(int)original[row,9];
                if(packetWaveRow<0||packetWaveRow>=matching.GetLength(0)){result.Status="UnknownWaveformRow";return result;}
                for(int i=0;i<Math.Min(result.Required,matching.GetLength(1));i++)if(Finite(matching[packetWaveRow,i]))result.Valid++;
                result.Retain=result.Valid==result.Required;result.Status=result.Retain?"CompleteCapacitorIncident":"InsufficientFiniteSamples";return result;
            }
            if(!Finite(length)||length<=0)return result;
            double travelUs;
            if(model!=null)
            {
                if(Math.Abs(model.DutLengthM-length)>Math.Max(1e-6,length*1e-9))return result;
                travelUs=model.GeometryDutRoundTripUs;
            }
            else
            {
                if(!Finite(velocity)||velocity<=0)return result;
                // Same legacy row-6 interpretation as PdMapControl.NormalizeVelocity.
                double speed=velocity<1?1/velocity:velocity;
                travelUs=2*length/speed;
            }
            double samples=travelUs*rate/1e6;
            if(!Finite(samples)||samples<=0||samples>int.MaxValue-1)return result;
            result.Required=(long)Math.Ceiling(samples)+1;
            double start=original[row,31],end=original[row,32];
            if(!CompletenessSampleIndex(start)||!CompletenessSampleIndex(end)||end<start)
            {result.Status="UnknownSourceCrop";return result;}
            double local=original[row,20];
            if(!CompletenessSampleIndex(local))
            {
                double incident=original[row,10];
                local=CompletenessSampleIndex(incident)?incident-start:double.NaN;
            }
            if(!CompletenessSampleIndex(local)){result.Status="UnknownSourceIncident";return result;}
            int sourceCount=original.GetLength(0),waveRow;
            if(matching.GetLength(0)==sourceCount||matching.GetLength(0)==3L*sourceCount)waveRow=row;
            else
            {
                double id=original[row,9];
                if(!CompletenessSampleIndex(id)||id>=matching.GetLength(0)){result.Status="UnknownWaveformRow";return result;}
                waveRow=(int)id;
            }
            long first=(long)local,last=first+result.Required-1;
            long actualLast=Math.Min((long)matching.GetLength(1)-1,(long)end-(long)start);
            long availableLast=Math.Min(last,actualLast);
            for(long i=first;i<=availableLast;i++)if(Finite(matching[waveRow,(int)i]))result.Valid++;
            // Integer arithmetic preserves the exact inclusive 95% threshold.
            result.Retain=result.Valid*100>=result.Required*95;
            result.Status=result.Retain?"Complete":"InsufficientFiniteSamples";
            return result;
        }
        static bool CompletenessSampleIndex(double value)
        {return Finite(value)&&value>=0&&value<=int.MaxValue&&value==Math.Floor(value);}
    }
}
