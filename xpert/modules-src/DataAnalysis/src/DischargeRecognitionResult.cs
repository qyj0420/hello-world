using System;

namespace HVX.PdReview
{
    /// <summary>One immutable PRPD recognition snapshot; model scores are not calibrated failure probabilities.</summary>
    public sealed class DischargeRecognitionResult
    {
        public bool Success { get; private set; }
        public string DisplayLabel { get; private set; }
        public string StatusCode { get; private set; }
        public string StatusMessage { get; private set; }
        public string LabelKeys { get; private set; }
        public string ModelSha256 { get; private set; }
        public string SourceScope { get; private set; }
        public int PointCount { get; private set; }
        public string Diagnostic { get; private set; }
        public string CreatedUtc { get; private set; }
        readonly DischargeClassScore[] classScores;
        // Independent multi-label model scores, not normalized probabilities.
        public DischargeClassScore[] ClassScores {get{return (DischargeClassScore[])classScores.Clone();}}
        internal string ContextKey { get; private set; }
        internal DischargeRecognitionResult(bool success,string label,string code,string message,string keys,string sha,
            string scope,int count,string diagnostic,string context,DischargeClassScore[] scores=null)
        {
            Success=success;DisplayLabel=label??"";StatusCode=code??"";StatusMessage=message??"";LabelKeys=keys??"";
            ModelSha256=sha??"";SourceScope=scope??"";PointCount=count;Diagnostic=diagnostic??"";ContextKey=context??"";
            CreatedUtc=DateTime.UtcNow.ToString("o",System.Globalization.CultureInfo.InvariantCulture);
            classScores=scores==null?new DischargeClassScore[0]:(DischargeClassScore[])scores.Clone();
        }
    }

    public sealed class DischargeClassScore
    {
        public string Phase{get;private set;}
        public string LabelKey{get;private set;}
        public string DisplayLabel{get;private set;}
        public double Score{get;private set;}
        internal DischargeClassScore(string phase,string key,string label,double score)
        {Phase=phase;LabelKey=key;DisplayLabel=label;Score=score;}
    }

    internal sealed class RecognitionSnapshot
    {
        internal int[] Rows,Phases;
        internal double[] Angles,Charges;
        internal string Context,Scope,ModelPath,Project;
        internal bool English;
    }
}
