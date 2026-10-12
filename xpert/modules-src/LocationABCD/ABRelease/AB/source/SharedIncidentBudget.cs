using System.Collections.Generic;

namespace HVX.SignalEvidence
{
    internal static class IndependentIncidentBudget
    {
        // Physical sample identity, not matrix row number or packet alias.
        internal static int Count(IEnumerable<int> visitedPeaks,IEnumerable<int> reflectionPeaks)
        {
            var independent=new HashSet<int>(visitedPeaks);
            independent.ExceptWith(reflectionPeaks);
            return independent.Count;
        }
    }
}
