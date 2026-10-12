using System;
using System.Collections;
using System.Collections.Generic;

namespace Method8.Experimental.FullRecordLedger
{
    // Measurement-only experiment. No recovery, pair search, propagation or output API.
    public static class FullRecordMeasurement
    {
        const int Quiet=3,MaximumRecordSamples=8388608,MaximumPacketSamples=65536,LedgerPageSlots=256;
        const long ArrayHeaderCharge=32,DescriptorCharge=384;
        public sealed class ResourceBudget
        {
            public long MaximumContextBytes=384L*1024*1024;
            public long MaximumMetadataBytes=32L*1024*1024;
            public long MaximumWorkUnits=1000000000;
        }
        public sealed class MeasurementContext
        {
            internal double[] Residual,LocalNoise,Background;
            public int SampleCount {get;internal set;}
            public double SampleRateHz {get;internal set;}
            public bool CalibrationRecord {get;internal set;}
            public double MeasuredSupportSeconds {get;internal set;}
            public double Baseline {get;internal set;}
            public double Noise {get;internal set;}
            public bool QuietBaselineStable {get;internal set;}
            public int LocalNoiseWindowSamples {get;internal set;}
            public bool BaselineCorrectionUsed {get;internal set;}
            public string BaselineCorrectionSummary {get;internal set;}
            public long OwnedContextBytes {get;internal set;}
            public long PeakLogicalContextBytes {get;internal set;}
            public long PreparationSampleVisits {get;internal set;}
            public long PreparationSortWorkUnits {get;internal set;}
            public long PreparationWorkUnits {get{return PreparationSampleVisits+PreparationSortWorkUnits;}}
            public double ResidualAt(int index){if(index<0||index>=SampleCount)throw new ArgumentOutOfRangeException("index");return Residual[index];}
            public double NoiseAt(int index){if(index<0||index>=SampleCount)throw new ArgumentOutOfRangeException("index");return LocalNoise==null?Noise:LocalNoise[index];}
            public double BackgroundAt(int index){if(index<0||index>=SampleCount)throw new ArgumentOutOfRangeException("index");return Background==null?Baseline:Background[index];}
        }
        public sealed class PreparationResult
        {
            public bool Complete;public string Reason;
            public MeasurementContext Context;
            public long SampleVisits,SortWorkUnits,PeakLogicalContextBytes;
        }
        public sealed class Packet
        {
            internal MeasurementContext Owner;internal int[] Fronts;
            public int Id {get;internal set;}
            public int Start {get;internal set;} public int End {get;internal set;}
            public int Peak {get;internal set;} public int Onset {get;internal set;}
            public int WidthSamples {get;internal set;} public int ActiveSamples {get;internal set;}
            public double Amplitude {get;internal set;} public double AbsAmplitude {get;internal set;}
            public double Noise {get;internal set;} public double SampleRateHz {get;internal set;}
            public double LocalBaseline {get;internal set;} public double TimingUncertaintySeconds {get;internal set;}
            public bool OnsetIdentifiable {get;internal set;}
            public int OnsetEarliest {get;internal set;} public int OnsetLatest {get;internal set;}
            public double OnsetTimingUncertaintySeconds {get;internal set;}
            public string OnsetReason {get;internal set;}
            public bool Valid {get;internal set;} public bool EdgeClipped {get;internal set;} public bool FlatTop {get;internal set;}
            public string Reason {get;internal set;}
            public int ObservedFrontCount {get{return Fronts.Length;}}
            public int ObservedFrontAt(int index){if(index<0||index>=Fronts.Length)throw new ArgumentOutOfRangeException("index");return Fronts[index];}
            public double SampleAt(int offset){if(offset<0||offset>=WidthSamples)throw new ArgumentOutOfRangeException("offset");return Owner.Residual[Start+offset];}
            // Intentionally no Samples array or hidden materialization property.
        }
        public sealed class PacketLedger : IEnumerable<Packet>
        {
            readonly List<Packet[]> pages=new List<Packet[]>();int count;
            internal void Add(Packet p){if(count%LedgerPageSlots==0)pages.Add(new Packet[LedgerPageSlots]);pages[count/LedgerPageSlots][count%LedgerPageSlots]=p;count++;}
            public int Count {get{return count;}}
            public Packet this[int index]{get{if(index<0||index>=count)throw new ArgumentOutOfRangeException("index");return pages[index/LedgerPageSlots][index%LedgerPageSlots];}}
            public IEnumerator<Packet> GetEnumerator(){for(int i=0;i<count;i++)yield return this[i];}
            IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
        }
        public sealed class MeasurementResult
        {
            public bool Complete;public string Reason;public bool? BaselineStable;
            public PacketLedger Ledger=new PacketLedger();
            public int PagesVisited,ScannedSamples,OccupiedSamples,DiscoveredSupportsBeforeFailure;
            public long StoredSupportSamples,MetadataBytes,ScanSampleVisits,PreparationWorkUnits,TotalWorkUnits;
            public string Accounting="Owned arrays charged 32+8*n bytes; ledger pages 32+8*256 plus conservative list-reference charge; packet descriptors 384 bytes plus front array 32+4*n. Logical allocation accounting is not measured CLR RSS. Work=sample visits plus conservative 2*n*ceil(log2(n)) sort units. Caller raw allocation excluded; no packet sample arrays.";
        }
        sealed class Stop:Exception {internal Stop(string reason):base(reason){}}
        sealed class Meter
        {
            internal ResourceBudget Budget;internal long Owned,PeakOwned,Visits,SortUnits,Metadata;
            internal Meter(ResourceBudget b){Budget=b;}
            internal void Visit(long n){if(n<0||n>Budget.MaximumWorkUnits-Visits-SortUnits)throw new Stop("measurement_work_budget_exceeded_no_prefix");Visits+=n;}
            internal void Sort(int n){int lg=0;long pow=1;while(pow<n){pow*=2;lg++;}long units=2L*n*lg;if(units>Budget.MaximumWorkUnits-Visits-SortUnits)throw new Stop("measurement_sort_work_budget_exceeded_no_prefix");SortUnits+=units;}
            internal void Own(long bytes){if(bytes<0||bytes>Budget.MaximumContextBytes-Owned)throw new Stop("context_storage_budget_exceeded_no_prefix");Owned+=bytes;PeakOwned=Math.Max(PeakOwned,Owned);}
            internal void Release(long bytes){Owned-=bytes;}
            internal void Meta(long bytes){if(bytes<0||bytes>Budget.MaximumMetadataBytes-Metadata)throw new Stop("packet_metadata_budget_exceeded_no_prefix");Metadata+=bytes;}
        }
        static bool Finite(double x){return !Double.IsNaN(x)&&!Double.IsInfinity(x);}
        static bool ValidBudget(ResourceBudget b){return b!=null&&b.MaximumContextBytes>=0&&b.MaximumContextBytes<=1073741824L&&b.MaximumMetadataBytes>=0&&b.MaximumMetadataBytes<=268435456L&&b.MaximumWorkUnits>=0&&b.MaximumWorkUnits<=10000000000L;}
        static long ArrayBytes(int n){return ArrayHeaderCharge+8L*n;}
        static double[] Allocate(int n,Meter m){m.Own(ArrayBytes(n));return new double[n];}
        static void Release(double[] a,Meter m){if(a!=null)m.Release(ArrayBytes(a.Length));}
        static double Median(double[] values,Meter m){m.Sort(values.Length);Array.Sort(values);int half=values.Length/2;return values.Length%2!=0?values[half]:values[half-1]/2+values[half]/2;}
        static double EstimateNoise(double[] raw,Meter m)
        {
            int count=Math.Min(65536,raw.Length-1);var differences=Allocate(count,m);
            for(int i=0;i<count;i++){m.Visit(2);int index=1+(int)((long)i*(raw.Length-1)/count);double d=raw[index]-raw[index-1];if(!Finite(d))throw new Stop("baseline_numeric_overflow");differences[i]=d;}
            var copy=Allocate(count,m);m.Visit(count);Array.Copy(differences,copy,count);double center=Median(copy,m);Release(copy,m);
            for(int i=0;i<count;i++){m.Visit(1);differences[i]=Math.Abs(differences[i]-center);}
            double sigma=1.4826*Median(differences,m)/Math.Sqrt(2);Release(differences,m);if(!Finite(sigma))throw new Stop("baseline_numeric_overflow");
            int width=Math.Min(256,Math.Max(16,raw.Length/64)),blocks=Math.Min(257,Math.Max(1,raw.Length/width));
            var amplitudes=Allocate(blocks,m);var values=Allocate(width,m);
            for(int b=0;b<blocks;b++)
            {
                int start=blocks==1?0:(int)((long)b*(raw.Length-width)/(blocks-1));
                m.Visit(width);Array.Copy(raw,start,values,0,width);double local=Median(values,m);
                for(int j=0;j<width;j++){m.Visit(1);values[j]=Math.Abs(raw[start+j]-local);}
                amplitudes[b]=1.4826*Median(values,m);
            }
            double amplitudeSigma=Median(amplitudes,m);Release(values,m);Release(amplitudes,m);
            if(!Finite(amplitudeSigma))throw new Stop("baseline_numeric_overflow");return Math.Max(1e-15,Math.Max(sigma,amplitudeSigma));
        }
        public static PreparationResult Prepare(double[] raw,double fs,bool calibrationRecord,double measuredSupportSeconds,ResourceBudget budget)
        {
            var result=new PreparationResult();if(!ValidBudget(budget)){result.Reason="invalid_resource_budget";return result;}
            var meter=new Meter(budget);var c=new MeasurementContext{SampleCount=raw==null?0:raw.Length,SampleRateHz=fs,CalibrationRecord=calibrationRecord,MeasuredSupportSeconds=measuredSupportSeconds,BaselineCorrectionSummary="global_robust_median"};
            try
            {
                if(raw==null||raw.Length<16||raw.Length>MaximumRecordSamples||!Finite(fs)||fs<=0||!Finite(1/fs)||!Finite(raw.Length/fs))throw new Stop("invalid_raw_record_or_sample_rate");
                if(!Finite(measuredSupportSeconds)||measuredSupportSeconds<0)throw new Stop("invalid_measured_support_seconds");
                for(int i=0;i<raw.Length;i++){meter.Visit(1);if(!Finite(raw[i]))throw new Stop("nonfinite_raw_sample");}
                int count=Math.Min(65536,raw.Length);var sample=Allocate(count,meter);
                for(int i=0;i<count;i++){meter.Visit(1);sample[i]=raw[(int)((long)i*raw.Length/count)];}c.Baseline=Median(sample,meter);
                double scale=0;for(int i=0;i<raw.Length;i++){meter.Visit(1);double centered=raw[i]-c.Baseline;if(!Finite(centered))throw new Stop("baseline_corrected_sample_overflow");scale=Math.Max(scale,Math.Abs(centered));}
                // Preserve the frozen calculation order, including this unused MAD scratch fill.
                for(int i=0;i<count;i++){meter.Visit(1);sample[i]=Math.Abs(raw[(int)((long)i*raw.Length/count)]-c.Baseline);}
                c.Noise=Math.Max(EstimateNoise(raw,meter),Math.Max(1e-15,scale*1e-12));
                if(!Finite(c.Noise)||!Finite(c.Baseline))throw new Stop("baseline_numeric_overflow");Release(sample,meter);
                c.Residual=Allocate(raw.Length,meter);for(int i=0;i<raw.Length;i++){meter.Visit(1);c.Residual[i]=raw[i]-c.Baseline;}
                if(!calibrationRecord)
                {
                    int width=Math.Min(raw.Length,Math.Min(256,Math.Max(32,raw.Length/128))),blocks=Math.Min(257,Math.Max(1,raw.Length/width));
                    var centers=Allocate(blocks,meter);var at=Allocate(blocks,meter);var values=Allocate(width,meter);
                    double smallest=Double.PositiveInfinity,largest=Double.NegativeInfinity,largestStep=0;
                    for(int b=0;b<blocks;b++)
                    {
                        int start=blocks==1?0:(int)((long)b*(raw.Length-width)/(blocks-1));meter.Visit(width);Array.Copy(raw,start,values,0,width);
                        centers[b]=Median(values,meter);at[b]=start+(width-1)/2.0;smallest=Math.Min(smallest,centers[b]);largest=Math.Max(largest,centers[b]);if(b>0)largestStep=Math.Max(largestStep,Math.Abs(centers[b]-centers[b-1]));
                    }
                    if(blocks>=8&&largest-smallest>6*c.Noise&&largestStep<=3*c.Noise)
                    {
                        c.Background=Allocate(raw.Length,meter);int segment=0;
                        for(int i=0;i<raw.Length;i++)
                        {
                            meter.Visit(1);while(segment+1<blocks&&at[segment+1]<i)segment++;int next=Math.Min(blocks-1,segment+1);
                            double fraction=next==segment?0:Math.Max(0,Math.Min(1,(i-at[segment])/(at[next]-at[segment])));
                            double bg=centers[segment]*(1-fraction)+centers[next]*fraction;c.Background[i]=bg;c.Residual[i]=raw[i]-bg;
                        }
                        c.BaselineCorrectionUsed=true;c.BaselineCorrectionSummary="native_contiguous_block_median_smooth_background_interpolation; raw_samples_and_time_indices_unchanged; charge_referenced_to_local_background";
                    }
                    else if(largest-smallest>6*c.Noise)c.BaselineCorrectionSummary="global_robust_median; discontinuous_or_pulse_contaminated_background_not_interpolated";
                    Release(values,meter);Release(centers,meter);Release(at,meter);
                }
                int quietWidth=Math.Min(256,Math.Max(8,raw.Length/128)),quietBlocks=Math.Min(257,Math.Max(1,raw.Length/quietWidth));
                var quietCenters=Allocate(quietBlocks,meter);int quietCount=0;var quietValues=Allocate(quietWidth,meter);
                for(int b=0;b<quietBlocks;b++)
                {
                    int start=quietBlocks==1?0:(int)((long)b*(raw.Length-quietWidth)/(quietBlocks-1));meter.Visit(quietWidth);Array.Copy(c.Residual,start,quietValues,0,quietWidth);double local=Median(quietValues,meter);
                    for(int j=0;j<quietWidth;j++){meter.Visit(1);quietValues[j]=Math.Abs(c.Residual[start+j]-local);}double spread=1.4826*Median(quietValues,meter);
                    if(spread<=1.5*c.Noise&&Math.Abs(local)<=6*c.Noise)quietCenters[quietCount++]=local;
                }
                c.QuietBaselineStable=quietCount>=Math.Min(3,quietBlocks);
                if(quietCount>=3){meter.Sort(quietCount);Array.Sort(quietCenters,0,quietCount);double spread=quietCenters[(quietCount-1)*9/10]-quietCenters[(quietCount-1)/10];c.QuietBaselineStable=spread<=6*c.Noise;}
                Release(quietValues,meter);Release(quietCenters,meter);
                if(!calibrationRecord)
                {
                    double requested=Math.Max(512,8*measuredSupportSeconds*fs);int width=(int)Math.Min(raw.Length,requested),stride=Math.Max(1,width/2);c.LocalNoiseWindowSamples=width;
                    int blocks=1+(raw.Length-1)/stride;var levels=Allocate(blocks,meter);var values=Allocate(width,meter);
                    for(int b=0;b<blocks;b++)
                    {
                        int center=b*stride,start=Math.Max(0,Math.Min(raw.Length-width,center-width/2));
                        for(int j=0;j<width;j++){meter.Visit(1);values[j]=Math.Abs(c.Residual[start+j]);}levels[b]=Math.Max(c.Noise,1.4826*Median(values,meter));
                    }
                    c.LocalNoise=Allocate(raw.Length,meter);
                    for(int i=0;i<raw.Length;i++){meter.Visit(1);int b=Math.Min(blocks-1,i/stride),next=Math.Min(blocks-1,b+1);double f=(i-b*stride)/(double)stride;c.LocalNoise[i]=levels[b]*(1-f)+levels[next]*f;}
                    Release(values,meter);Release(levels,meter);
                }
                for(int i=0;i<raw.Length;i++){meter.Visit(2);if(!Finite(c.Residual[i])||!Finite(c.NoiseAt(i))||c.NoiseAt(i)<=0)throw new Stop("nonfinite_native_measurement_context");}
                c.OwnedContextBytes=meter.Owned;c.PeakLogicalContextBytes=meter.PeakOwned;c.PreparationSampleVisits=meter.Visits;c.PreparationSortWorkUnits=meter.SortUnits;
                result.Context=c;result.Complete=true;result.Reason="whole_record_context_prepared_owned_residual";
            }
            catch(Stop e){result.Complete=false;result.Context=null;result.Reason=e.Message;}
            result.SampleVisits=meter.Visits;result.SortWorkUnits=meter.SortUnits;result.PeakLogicalContextBytes=meter.PeakOwned;return result;
        }
        static Packet MeasurePacket(MeasurementContext c,int first,int last,int id,Meter meter,PacketLedger ledger)
        {
            int start=Math.Max(0,first-Quiet),end=Math.Min(c.SampleCount-1,last+Quiet),width=end-start+1;
            if(width>MaximumPacketSamples)throw new Stop("unresolved_packet_exceeds_support_budget");
            // Charge fixed metadata before allocating the descriptor; front storage charged later.
            meter.Meta(DescriptorCharge+(id%LedgerPageSlots==0?ArrayHeaderCharge+8L*LedgerPageSlots+32:0));
            var p=new Packet{Owner=c,Id=id,Start=start,End=end,WidthSamples=width,SampleRateHz=c.SampleRateHz,Noise=c.Noise,EdgeClipped=first<Quiet||last+Quiet>=c.SampleCount};
            double top=-1;
            for(int i=start;i<=end;i++){meter.Visit(2);double v=c.Residual[i];if(Math.Abs(v)>2.5*c.NoiseAt(i))p.ActiveSamples++;if(Math.Abs(v)>top){top=Math.Abs(v);p.Peak=i;p.Amplitude=v;}}
            p.AbsAmplitude=Math.Abs(p.Amplitude);p.Noise=c.NoiseAt(p.Peak);p.LocalBaseline=c.BackgroundAt(p.Peak);int flatRun=0;
            for(int i=start;i<=end;i++){meter.Visit(1);if(Math.Abs(Math.Abs(c.Residual[i])-p.AbsAmplitude)<=Math.Max(1e-15,p.AbsAmplitude*1e-10))flatRun++;else flatRun=0;if(flatRun>=3)p.FlatTop=true;}
            double gate=Math.Max(3*p.Noise,.05*p.AbsAmplitude);p.Onset=first;
            for(int i=first;i<=p.Peak;i++){meter.Visit(1);if(Math.Abs(c.Residual[i])>=gate){p.Onset=i;break;}}
            int offset=p.Onset-start;meter.Visit(offset>0?2:0);double slope=offset>0?Math.Abs(p.SampleAt(offset)/p.AbsAmplitude-p.SampleAt(offset-1)/p.AbsAmplitude):0;
            p.TimingUncertaintySeconds=Math.Max(.5,Math.Min(width,slope>0?(p.Noise/p.AbsAmplitude)/slope:width))/c.SampleRateHz;
            p.Valid=!p.EdgeClipped&&!p.FlatTop&&p.ActiveSamples>=3&&p.AbsAmplitude>=6*p.Noise;
            p.Reason=p.EdgeClipped?"record_edge_truncation":p.FlatTop?"flat_top_clipping_or_unresolved_plateau":p.ActiveSamples<3?"fewer_than_three_active_samples":p.AbsAmplitude<6*p.Noise?"low_snr":"observed_guarded_support";
            p.OnsetEarliest=p.OnsetLatest=p.Onset;double resolved=Math.Max(6*p.Noise,2*gate);int stop=p.Peak;
            for(int i=p.Onset;i<=p.Peak;i++){meter.Visit(1);if(Math.Abs(c.Residual[i])>=resolved){stop=i;break;}}
            bool above=false,returned=false;int observedStarts=0;
            for(int i=p.Onset;i<=stop;i++)
            {
                meter.Visit(1);double value=Math.Abs(c.Residual[i]);
                if(value>=gate+p.Noise&&!above){if(observedStarts==0||returned)observedStarts++;if(returned)p.OnsetLatest=i;above=true;returned=false;}
                else if(above&&value<Math.Max(0,gate-p.Noise)){above=false;returned=true;}
            }
            p.OnsetIdentifiable=p.Valid&&observedStarts<=1;p.OnsetReason=!p.Valid?"invalid_packet_support":observedStarts>1?"multiple_noise_resolved_weak_prefix_restarts_before_first_lobe":"single_resolved_leading_front";
            int frontCount=0,positive=0,negative=0,lastFront=p.Onset;above=false;
            for(int i=p.Onset;i<=p.Peak;i++)
            {
                meter.Visit(1);double value=c.Residual[i];if(Math.Abs(value)>=gate+p.Noise&&!above){frontCount++;lastFront=i;if(value>0)positive++;else negative++;above=true;}else if(above&&Math.Abs(value)<Math.Max(0,gate-p.Noise))above=false;
            }
            meter.Meta(ArrayHeaderCharge+4L*frontCount);p.Fronts=new int[frontCount];int write=0;above=false;
            for(int i=p.Onset;i<=p.Peak;i++){meter.Visit(1);double value=c.Residual[i];if(Math.Abs(value)>=gate+p.Noise&&!above){p.Fronts[write++]=i;above=true;}else if(above&&Math.Abs(value)<Math.Max(0,gate-p.Noise))above=false;}
            if(positive>1||negative>1){p.OnsetLatest=Math.Max(p.OnsetLatest,lastFront);p.OnsetIdentifiable=false;p.OnsetReason="multiple_same_polarity_fronts_in_full_prepeak_support";}
            p.OnsetTimingUncertaintySeconds=p.TimingUncertaintySeconds+Math.Max(p.Onset-p.OnsetEarliest,p.OnsetLatest-p.Onset)/p.SampleRateHz;
            // Preserve normal arithmetic and bits. Reject unrepresentable derived values
            // before any descriptor can be committed; Scan discards the entire pending ledger.
            // All sample-view values were checked by Prepare; integer fields are bounded indices.
            if(!Finite(p.Amplitude)||!Finite(p.AbsAmplitude)||!Finite(p.Noise)||!Finite(p.SampleRateHz)||!Finite(p.LocalBaseline)||!Finite(p.TimingUncertaintySeconds)||!Finite(p.OnsetTimingUncertaintySeconds))
                throw new Stop("nonfinite_packet_numeric_field_no_prefix");
            return p;
        }
        public static MeasurementResult Scan(MeasurementContext context,int pageSamples,ResourceBudget budget)
        {
            var r=new MeasurementResult();if(context==null||!ValidBudget(budget)||pageSamples<1||pageSamples>MaximumRecordSamples){r.Reason="invalid_context_page_or_budget";return r;}
            r.PreparationWorkUnits=context.PreparationWorkUnits;var meter=new Meter(budget){Visits=context.PreparationSampleVisits,SortUnits=context.PreparationSortWorkUnits};
            var pending=new PacketLedger();bool inside=false;int first=0,last=0,quiet=0;double maxSnr=0;long support=0;
            try
            {
                if(context.OwnedContextBytes>budget.MaximumContextBytes)throw new Stop("context_storage_budget_exceeded_no_prefix");
                if(context.PreparationWorkUnits>budget.MaximumWorkUnits)throw new Stop("preparation_work_exceeds_scan_budget_no_prefix");
                Action finish=()=>{
                    if(maxSnr<4.5)return;
                    r.DiscoveredSupportsBeforeFailure++;
                    Packet p=MeasurePacket(context,first,last,pending.Count,meter,pending);
                    if(pending.Count>0&&p.Start<=pending[pending.Count-1].End)throw new Stop("standard_support_overlap_invariant_failed_no_prefix");
                    if(p.WidthSamples>context.SampleCount-support)throw new Stop("standard_support_sum_exceeds_record_no_prefix");
                    support+=p.WidthSamples;pending.Add(p);
                };
                for(int pageStart=0;pageStart<context.SampleCount;)
                {
                    int pageEnd=(int)Math.Min((long)context.SampleCount,(long)pageStart+pageSamples);r.PagesVisited++;
                    for(int pos=pageStart;pos<pageEnd;pos++)
                    {
                        meter.Visit(2);double a=Math.Abs(context.Residual[pos]),noise=context.NoiseAt(pos);r.ScannedSamples=pos+1;
                        if(!inside){if(a<=2.5*noise)continue;inside=true;first=last=pos;quiet=0;maxSnr=0;}
                        if(a>2.5*noise){last=pos;quiet=0;r.OccupiedSamples++;}else quiet++;
                        maxSnr=Math.Max(maxSnr,a/noise);
                        if(quiet>=2*Quiet+1){finish();inside=false;}
                    }
                    pageStart=pageEnd;
                }
                if(inside)finish();
                r.BaselineStable=context.QuietBaselineStable&&(!(!context.CalibrationRecord&&r.OccupiedSamples>context.SampleCount*.40));
                r.Ledger=pending;r.StoredSupportSamples=support;r.Complete=true;r.Reason="measured_complete_whole_record_ledger";
            }
            catch(Stop e){r.Complete=false;r.Reason=e.Message;r.Ledger=new PacketLedger();r.StoredSupportSamples=0;r.BaselineStable=null;}
            r.MetadataBytes=meter.Metadata;r.ScanSampleVisits=meter.Visits-context.PreparationSampleVisits;r.TotalWorkUnits=meter.Visits+meter.SortUnits;return r;
        }
    }
}
