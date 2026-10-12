using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace HVX.Task17.Location;

/// <summary>
/// Explicit project-level review AFTER all LOC analyses of ONE project and ONE
/// execution configuration. This is not AI training or DAC ClusterStreng.
/// Never feed the returned flags into physical timing or subsequent LOC calls.
/// </summary>
public sealed class ProjectConsistencyReviewer
{
    private sealed class Query
    {
        internal int Row,Left,Right,Support;
        internal double Radius;
    }
    private static bool Finite(double x) {return !Double.IsNaN(x)&&!Double.IsInfinity(x);}
    private static bool Physical(double[,] p,int r)
    {
        return p[r,35]==1&&Finite(p[r,0])&&p[r,0]>=0&&p[r,0]<=3&&p[r,0]==Math.Truncate(p[r,0])&&
            Finite(p[r,1])&&p[r,1]>=0&&p[r,1]<=9007199254740991d&&p[r,1]==Math.Truncate(p[r,1])&&
            Finite(p[r,5])&&p[r,5]>0&&Finite(p[r,6])&&p[r,6]>0&&Finite(p[r,7])&&p[r,7]>0&&
            Finite(p[r,3])&&p[r,3]>=0&&p[r,3]<360&&
            Finite(p[r,10])&&p[r,10]>=0&&p[r,10]==Math.Truncate(p[r,10])&&
            Finite(p[r,11])&&p[r,11]>p[r,10]&&p[r,11]<=Int32.MaxValue&&p[r,11]==Math.Truncate(p[r,11])&&
            Finite(p[r,12])&&p[r,12]==p[r,11]-p[r,10]&&
            // Methods 1-5/7 publish a rounded span; method8 publishes an exact
            // span. This is a necessary matrix check, not a replacement for the
            // analyzer's exact piecewise geometry check or source provenance.
            Finite(p[r,14])&&p[r,14]>0&&p[r,14]<=Int32.MaxValue&&p[r,12]<=p[r,14]+1e-9&&
            Finite(p[r,13])&&p[r,13]>=0&&p[r,13]<=p[r,5]&&
            Finite(p[r,33])&&p[r,33]>0&&p[r,33]<=1;
    }
    private static int Bound(int[] sorted,double[,] p,double x,bool upper)
    {
        int a=0,b=sorted.Length;
        while(a<b){int m=a+(b-a)/2;if(p[sorted[m],13]<x||(upper&&p[sorted[m],13]==x))a=m+1;else b=m;}
        return a;
    }
    private static void Add(int[] tree,int index,int amount)
    {for(int i=index+1;i<tree.Length;i+=i&-i)tree[i]+=amount;}
    private static int Sum(int[] tree,int count)
    {int s=0;for(int i=count;i>0;i-=i&-i)s+=tree[i];return s;}

    /// <summary>
    /// applyReview=false returns an unchanged clone plus advisory results.
    /// applyReview=true may only downgrade column35, never promote or move rows.
    /// Method6 (column26=5) is untouched, including its original flags.
    /// executionIdentity must identify the common source revision, timing mode
    /// and settings used for every row. Mixing projects/configurations is invalid.
    /// </summary>
    public void Review(double[,] input,string projectIdentity,string executionIdentity,
        bool applyReview,int minimumDistinctRecords,out double[,] output,out string diagnostics)
    {
        if(input==null||input.GetLength(1)!=36||input.GetLength(0)>100000)
            throw new ArgumentException("Expected up to 100000 rows with exactly 36 columns.");
        if(String.IsNullOrWhiteSpace(projectIdentity)||String.IsNullOrWhiteSpace(executionIdentity)||
            projectIdentity.Length>4096||executionIdentity.Length>4096)
            throw new ArgumentException("Explicit project and execution identities are required; never mix projects or timing/configuration versions.");
        if(minimumDistinctRecords<2||minimumDistinctRecords>1000)
            throw new ArgumentOutOfRangeException("minimumDistinctRecords");
        output=(double[,])input.Clone();
        var details=new List<object>();int downgraded=0,endpointRetained=0;
        var physical=new List<int>();
        for(int r=0;r<input.GetLength(0);r++)
        {
            double method=input[r,26];
            if(method==5)continue;
            bool supported=method==0||method==1||method==2||method==3||method==4||method==6||method==7;
            if(!supported)throw new ArgumentException("Unknown algorithm index in project review input.");
            if(input[r,35]!=1)continue;
            if(Physical(input,r)){physical.Add(r);continue;}
            if(applyReview){output[r,35]=0;downgraded++;}
            details.Add(new {row=r,reason="invalid_physical_input_no_valid_localization",distinct_record_support=0,endpoint_preserved=false});
        }
        foreach(var group in physical.GroupBy(r=>new {Phase=input[r,0],Algorithm=input[r,26],Length=input[r,5]}))
        {
            int[] members=group.ToArray();
            var queries=new List<Query>();
            var resolved=new List<int>();
            foreach(int r in members)
            {
                double resolution=input[r,6]*1e6/(2*input[r,7]);
                double radius=Math.Max(3*resolution,.01*input[r,5]);
                // Unresolved sampling is not a licence to call every row an
                // endpoint or to merge the whole cable into one cluster.
                if(!Finite(radius)||radius>.10*input[r,5])
                {
                    if(applyReview){output[r,35]=0;downgraded++;}
                    details.Add(new {row=r,reason="insufficient_spatial_resolution_for_project_support",distinct_record_support=0,endpoint_preserved=false});
                    continue;
                }
                resolved.Add(r);queries.Add(new Query {Row=r,Radius=radius});
            }
            int[] sorted=resolved.OrderBy(r=>input[r,13]).ThenBy(r=>input[r,1]).ToArray();
            foreach(Query q in queries){q.Left=Bound(sorted,input,input[q.Row,13]-q.Radius,false);
                q.Right=Bound(sorted,input,input[q.Row,13]+q.Radius,true)-1;}
            var tree=new int[sorted.Length+1];var last=new Dictionary<double,int>();int sweep=-1;
            foreach(Query q in queries.OrderBy(q=>q.Right).ThenBy(q=>q.Left))
            {
                while(sweep<q.Right)
                {
                    sweep++;double record=input[sorted[sweep],1];int previous;
                    if(last.TryGetValue(record,out previous))Add(tree,previous,-1);
                    Add(tree,sweep,1);last[record]=sweep;
                }
                q.Support=Sum(tree,q.Right+1)-Sum(tree,q.Left);
            }
            foreach(Query q in queries.OrderBy(q=>q.Row))
            {
                bool endpoint=Math.Min(input[q.Row,13],input[q.Row,5]-input[q.Row,13])<=q.Radius;
                bool retain=endpoint||q.Support>=minimumDistinctRecords;
                if(endpoint)endpointRetained++;
                if(!retain&&applyReview){output[q.Row,35]=0;downgraded++;}
                details.Add(new {row=q.Row,reason=endpoint?"physically_accepted_endpoint_preserved":
                    retain?"independent_records_support":"insufficient_independent_records_review_not_deleted",
                    distinct_record_support=q.Support,radius_m=q.Radius,endpoint_preserved=endpoint});
            }
        }
        diagnostics=new JavaScriptSerializer {MaxJsonLength=16*1024*1024}.Serialize(new {
            schema="task17-explicit-project-review-v1",project_identity=projectIdentity,execution_identity=executionIdentity,
            enabled=applyReview,minimum_distinct_records=minimumDistinctRecords,downgraded_rows=downgraded,
            endpoint_rows_preserved=endpointRetained,method6_untouched=true,position_or_score_modified=false,
            promotion_allowed=false,hidden_history=false,not_equivalent_to_DAC_ClusterStreng=true,
            waveform_shape_validated_here=false,exact_piecewise_geometry_revalidated_here=false,
            source_provenance_verified_here=false,requires_verified_analyzer_output_from_single_execution=true,
            warning="independent record support is not proof of PD or absence of interference; rounded span validation is necessary but not sufficient; retain all review evidence",
            rows=details.ToArray()});
    }
}
