using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.SignalEvidence
{
    internal sealed class SeventhRankCandidate
    {
        internal int Id;
        internal bool HardPassed;
        internal double Correlation,BaseScore,Position;
    }
    internal sealed class SeventhPriorSeed
    {
        internal string EventKey,RecordKey;
        internal int Peak,SupportStart,SupportEnd;
        internal double Position;
    }
    internal sealed class SeventhRankResult
    {
        internal int SelectedId=-1;
        internal bool Ambiguous,HistoryUsed;
        internal string Reason="no_qualified_candidate";
        internal int[] NearCandidateIds=new int[0],SupportCounts=new int[0];
        internal string[] SupportingSeedIds=new string[0];
    }
    internal static class SeventhCandidateRanking
    {
        internal const double CorrelationGap=.02,BaseScoreGap=.05,RadiusFraction=.01;
        internal const int MinimumIndependentEvents=3;
        static bool Finite(double v){return ConfidenceMath.Finite(v);}
        static bool SameEvent(SeventhPriorSeed a,SeventhPriorSeed b)
        {return a.EventKey==b.EventKey||(a.RecordKey==b.RecordKey&&(a.Peak==b.Peak||Math.Max(a.SupportStart,b.SupportStart)<=Math.Min(a.SupportEnd,b.SupportEnd)));}
        internal static SeventhRankResult Select(IEnumerable<SeventhRankCandidate> candidates,IEnumerable<SeventhPriorSeed> seeds,
            string recordKey,int incidentPeak,int supportStart,int supportEnd,double cableLength,bool scoreFirst=false)
        {
            var r=new SeventhRankResult();
            var ranked=(candidates??new SeventhRankCandidate[0]).Where(c=>c!=null&&c.HardPassed&&Finite(c.Correlation)&&
                c.Correlation>=-1&&c.Correlation<=1&&Finite(c.BaseScore)&&c.BaseScore>=0&&c.BaseScore<=1&&Finite(c.Position))
                .OrderByDescending(c=>scoreFirst?c.BaseScore:c.Correlation).ThenByDescending(c=>scoreFirst?c.Correlation:c.BaseScore).ThenBy(c=>c.Id).ToArray();
            if(ranked.Length==0)return r;
            var best=ranked[0];r.SelectedId=best.Id;
            var near=ranked.Where(c=>c.Correlation>=best.Correlation-CorrelationGap&&
                c.BaseScore>=best.BaseScore-BaseScoreGap&&c.BaseScore<=best.BaseScore+BaseScoreGap).ToArray();
            r.NearCandidateIds=near.Select(c=>c.Id).ToArray();r.SupportCounts=new int[near.Length];
            if(near.Length==1){r.Reason="locally_unique_without_history";return r;}
            r.Ambiguous=true;r.Reason="near_equal_qualified_candidates_no_unique_independent_prior";
            if(!Finite(cableLength)||cableLength<=0)return r;
            double radius=cableLength*RadiusFraction;
            var self=new SeventhPriorSeed {EventKey=recordKey+":"+incidentPeak,RecordKey=recordKey,Peak=incidentPeak,SupportStart=supportStart,SupportEnd=supportEnd};
            // Keep self in the connectivity graph until after merging: an
            // overlapping crop can bridge self to otherwise disjoint variants.
            var groups=new List<List<SeventhPriorSeed>> {new List<SeventhPriorSeed>{self}};
            foreach(var seed in seeds??new SeventhPriorSeed[0])
            {
                if(seed==null||String.IsNullOrEmpty(seed.EventKey)||String.IsNullOrEmpty(seed.RecordKey)||!Finite(seed.Position)||
                    seed.SupportStart<0||seed.SupportEnd<seed.SupportStart||seed.Peak<seed.SupportStart||seed.Peak>seed.SupportEnd)continue;
                // Transitive overlap merging is conservative: repeated crops or
                // peak/first-arrival exports of one physical packet count once.
                var touched=groups.Where(g=>g.Any(s=>SameEvent(s,seed))).ToArray();
                var merged=new List<SeventhPriorSeed>{seed};
                foreach(var g in touched){merged.AddRange(g);groups.Remove(g);}groups.Add(merged);
            }
            var unique=groups.Where(g=>!g.Any(s=>Object.ReferenceEquals(s,self))&&g.Max(s=>s.Position)-g.Min(s=>s.Position)<=radius)
                .Select(g=>g.OrderBy(s=>s.EventKey,StringComparer.Ordinal).ThenBy(s=>s.Position).ThenBy(s=>s.Peak)
                    .ThenBy(s=>s.SupportStart).ThenBy(s=>s.SupportEnd).First()).ToArray();
            var supports=new List<SeventhPriorSeed>[near.Length];int winner=-1,winners=0;
            for(int i=0;i<near.Length;i++)
            {
                supports[i]=unique.Where(s=>Math.Abs(s.Position-near[i].Position)<=radius).ToList();r.SupportCounts[i]=supports[i].Count;
                if(supports[i].Count>=MinimumIndependentEvents){winner=i;winners++;}
            }
            if(winners!=1){r.Reason=winners>1?"multiple_candidate_clusters_supported_remains_ambiguous":r.Reason;return r;}
            r.SelectedId=near[winner].Id;r.Ambiguous=false;r.HistoryUsed=true;r.Reason="unique_independent_concentration_breaks_local_tie";
            r.SupportingSeedIds=supports[winner].Select(s=>s.EventKey).OrderBy(s=>s,StringComparer.Ordinal).ToArray();
            return r;
        }
    }
}
