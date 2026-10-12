using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.PD.LocationFiltering;
using HVX.UnifiedSpectrum;

namespace HVX.PdReview
{
    // Engineering display advice, not a trained classifier or a physical verdict.
    public sealed class ReviewDisplayRecommendation
    {
        public string Policy {get;set;} = "spatial-display-v1";
        public string AlgorithmId {get;set;} = "A";
        public string DiagnosticsJson {get;set;} = "{}";
        public int UnknownRows {get;set;}
        public bool Filter {get;set;}
        public bool Classification {get;set;}
        public int AcceptedRows {get;set;}
        public int RetainedRows {get;set;}
        public int IndependentRecords {get;set;}
        public int StableClusters {get;set;}
        public double Strength {get;set;}
        public string ReasonCode {get;set;}
        public string Error {get;set;}
        internal double[,] Scores;
        internal LocationFilterBResult FilterBResult;
        internal bool[] CompleteMask;
        internal bool[] CandidateMask;
        public bool[] GetCandidateMask(){return CandidateMask==null?null:(bool[])CandidateMask.Clone();}
        public string Describe(bool chinese)
        {
            if(AlgorithmId=="B")return (chinese?"定位过滤算法B：保留 ":"Location filter B: retained ")+RetainedRows+"/"+AcceptedRows+
                (chinese?"，未知 ":"; unknown ")+UnknownRows+(chinese?"。ClusterStrength为空间密度倍率，不是置信度；未知边界保留并待复核。":". ClusterStrength is a spatial density multiplier, not confidence; unknown boundary candidates remain visible for review.");
            string why=ReasonCode=="MixedStable"?(chinese?"部分位置重复出现，可筛去零散点":"Repeated locations found; isolated points can be hidden"):
                ReasonCode=="NoEffect"?(chinese?"当前强度下保留全部数据":"All data retained at the current strength"):
                ReasonCode=="WouldHideAll"?(chinese?"当前强度会隐藏全部数据，建议关闭过滤":"Current strength would hide all data; keep filtering off"):
                ReasonCode=="Unavailable"?(chinese?"暂无法给出建议，请手动选择":"Advice unavailable; please select manually"):
                (chinese?"数据较少或位置分散，建议保留全部数据":"Too few records or scattered locations; retain all data");
            return (chinese?"建议：统计过滤":"Suggested: statistics ")+(Filter?(chinese?"开":"on"):(chinese?"关":"off"))+
                (chinese?"，分类管理":"; classification ")+(Classification?(chinese?"开":"on"):(chinese?"关":"off"))+"。 "+why+
                (chinese?"；过滤后保留 ":"; retained after filtering ")+RetainedRows+"/"+AcceptedRows+
                (chinese?"。不修改原始数据。":". Original data is unchanged.");
        }
        public static ReviewDisplayRecommendation Evaluate(double[,] matrix,bool[] complete,double strength,double low,double high)
        {return Evaluate(matrix,complete,strength,low,high,"A",null);}
        public static ReviewDisplayRecommendation Evaluate(double[,] matrix,bool[] complete,double strength,double low,double high,string algorithmId,LocationFilterBOptions options)
        {
            PdReviewControl.ValidateLocationFilterAlgorithm(algorithmId);
            if(algorithmId=="B")
            {
                ValidateStrength(strength);
                var parameters=(options??new LocationFilterBOptions()).Clone();parameters.Strength=strength;
                PdReviewControl.ValidateLocationFilterBOptions(parameters);
                var filtered=LocationClusterStrengthFilterB.Evaluate(matrix,complete,parameters);
                var advice=new ReviewDisplayRecommendation{AlgorithmId="B",Policy="recovered-clusterstrength-B-"+LocationClusterStrengthFilterB.Version,
                    Strength=strength,AcceptedRows=filtered.EligibleRows,RetainedRows=filtered.RetainedRows,UnknownRows=filtered.UnknownRows,
                    FilterBResult=filtered,CandidateMask=(bool[])filtered.KeepMask.Clone(),CompleteMask=(bool[])complete.Clone(),DiagnosticsJson=filtered.DiagnosticsJson};
                var categoriesB=new HashSet<int>();
                for(int r=0;r<matrix.GetLength(0);r++)if(complete[r]&&matrix[r,35]==1){int category=UnifiedSpectrumControl.ClassifySourceRow(matrix,r,low,high);if(category>=0)categoriesB.Add(category);}
                advice.Classification=categoriesB.Count>=2;
                advice.ReasonCode=filtered.UnknownRows>0?"BUnknownBoundary":filtered.RetainedRows==0?"BWouldHideAll":filtered.RetainedRows==filtered.EligibleRows?"BNoEffect":"BDensitySelection";
                advice.Filter=advice.ReasonCode=="BDensitySelection";
                return advice;
            }
            // Match the core contract before cloning masks or allocating a
            // scoring copy, including otherwise-unselected extension columns.
            LocationClusterFilterEngine.ValidateInputMatrix(matrix);
            ValidateStrength(strength);
            if(complete==null||complete.Length!=matrix.GetLength(0))
                throw new ArgumentException("Invalid review matrix.");
            var result=new ReviewDisplayRecommendation{Strength=strength};
            result.CompleteMask=(bool[])complete.Clone();
            int[] indices=Enumerable.Range(0,matrix.GetLength(0)).Where(i=>complete[i]).ToArray();
            var input=new double[indices.Length,matrix.GetLength(1)];
            for(int r=0;r<indices.Length;r++)for(int c=0;c<input.GetLength(1);c++)input[r,c]=c==22?Double.NaN:matrix[indices[r],c];
            double[,] retained,gated,scores,clusters;int[] rows;string status;
            new LocationClusterFilterEngine().FilterLocationMatrix(input,0,out retained,out gated,out rows,out scores,out clusters,out status);
            result.Scores=new double[matrix.GetLength(0),8];result.CandidateMask=new bool[matrix.GetLength(0)];
            var records=new HashSet<string>(StringComparer.Ordinal);var categories=new HashSet<int>();
            for(int r=0;r<indices.Length;r++){
                int original=indices[r];for(int c=0;c<8;c++)result.Scores[original,c]=scores[r,c];
                if(input[r,35]!=1)continue;
                result.AcceptedRows++;
                int category=UnifiedSpectrumControl.ClassifySourceRow(matrix,original,low,high);if(category>=0)categories.Add(category);
                if(scores[r,1]!=1)continue;
                records.Add(input[r,0].ToString("R",System.Globalization.CultureInfo.InvariantCulture)+":"+input[r,1].ToString("R",System.Globalization.CultureInfo.InvariantCulture));
                bool keep=strength==0||scores[r,6]==2||scores[r,5]>=strength;
                result.CandidateMask[original]=keep;if(keep)result.RetainedRows++;
            }
            result.IndependentRecords=records.Count;
            for(int r=0;r<clusters.GetLength(0);r++)if(clusters[r,5]>=3&&clusters[r,7]>=strength)result.StableClusters++;
            result.Classification=categories.Count>=2;
            result.ReasonCode=result.RetainedRows==0?"WouldHideAll":result.RetainedRows==result.AcceptedRows?"NoEffect":
                result.IndependentRecords<3||result.StableClusters==0?"InsufficientSupport":"MixedStable";
            result.Filter=result.ReasonCode=="MixedStable";
            return result;
        }
        public static bool[] FilterMask(double[,] matrix,double strength)
        {return FilterMask(matrix,strength,"A",null);}
        public static bool[] FilterMask(double[,] matrix,double strength,string algorithmId,LocationFilterBOptions options)
        {
            LocationClusterFilterEngine.ValidateInputMatrix(matrix);
            ValidateStrength(strength);
            return Evaluate(matrix,Enumerable.Repeat(true,matrix.GetLength(0)).ToArray(),strength,30,60,algorithmId,options).CandidateMask;
        }
        static void ValidateStrength(double strength)
        {if(Double.IsNaN(strength)||Double.IsInfinity(strength)||strength<0||strength>100)throw new ArgumentOutOfRangeException("strength","Display filter strength must be finite and within 0..100.");}
    }

    // One background job per view; coalesce rapid updates and reject stale replies.
    public sealed class ReviewRecommendationWorker : IDisposable
    {
        readonly Control owner;readonly Func<Func<ReviewDisplayRecommendation>> capture;readonly Action<ReviewDisplayRecommendation> apply;
        readonly Timer timer;long revision;volatile bool running,disposed;bool dirty;
        public ReviewRecommendationWorker(Control owner,Func<Func<ReviewDisplayRecommendation>> capture,Action<ReviewDisplayRecommendation> apply)
        {
            this.owner=owner;this.capture=capture;this.apply=apply;timer=new Timer{Interval=120};timer.Tick+=(s,e)=>Start();
            owner.HandleCreated+=HandleCreated;owner.HandleDestroyed+=HandleDestroyed;owner.Disposed+=OwnerDisposed;
        }
        public void Request(){if(disposed)return;revision++;dirty=true;if(owner.IsHandleCreated&&!running){timer.Stop();timer.Start();}}
        public void Invalidate(){revision++;dirty=false;timer.Stop();}
        void HandleCreated(object sender,EventArgs args){if(dirty)Request();}
        void HandleDestroyed(object sender,EventArgs args){Invalidate();}
        void OwnerDisposed(object sender,EventArgs args){Dispose();}
        async void Start()
        {
            timer.Stop();if(disposed||owner.Disposing||running||!dirty||!owner.IsHandleCreated)return;
            dirty=false;running=true;long current=revision;
            ReviewDisplayRecommendation result;
            try{
                // Dialogs and embedded hosts need not supply a synchronization
                // context. Explicitly marshal completion to this control's UI
                // thread; never create child handles from a pool thread.
                var work=capture();result=await Task.Run(work).ConfigureAwait(false);
            }catch(Exception error){
                result=new ReviewDisplayRecommendation{ReasonCode="Unavailable",Error=error.GetType().Name};
            }
            if(disposed||owner.IsDisposed||owner.Disposing){running=false;return;}
            try{owner.BeginInvoke((Action)(()=>{
                running=false;if(disposed||owner.IsDisposed||owner.Disposing||!owner.IsHandleCreated)return;
                try{if(current==revision)apply(result);}
                finally{if(!disposed&&dirty)timer.Start();}
            }));}
            catch(InvalidOperationException){running=false;} // handle gone; no UI work is permitted
        }
        public void Dispose(){if(disposed)return;disposed=true;revision++;timer.Dispose();owner.HandleCreated-=HandleCreated;owner.HandleDestroyed-=HandleDestroyed;owner.Disposed-=OwnerDisposed;}
    }
}
