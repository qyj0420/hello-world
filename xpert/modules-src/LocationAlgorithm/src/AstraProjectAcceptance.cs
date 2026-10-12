using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private sealed class A4AcceptanceQuery
    {
        internal double[] Row;
        internal int Left,Right,Support;
        internal double Radius;
    }
    private static void A4FenwickAdd(int[] tree,int index,int amount)
    {for(int i=index+1;i<tree.Length;i+=i&-i)tree[i]+=amount;}
    private static int A4FenwickPrefix(int[] tree,int count)
    {int value=0;for(int i=count;i>0;i-=i&-i)value+=tree[i];return value;}
    private static int A4PositionBound(double[][] sorted,double value,bool upper)
    {
        int low=0,high=sorted.Length;
        while(low<high){int mid=low+(high-low)/2;if(sorted[mid][13]<value||(upper&&sorted[mid][13]==value))low=mid+1;else high=mid;}
        return low;
    }
    private static string A4ProjectAcceptance(List<double[]> rows,bool automatic)
    {
        const int minimumRecords=3;
        // Advisory only: historical rows contain display positions, not enough
        // information to reconstruct canonical raw peaks. Never overwrite caller
        // acceptance or make current physical acceptance depend on display timing.
        var groups=rows.Where(r=>r[26]>=0&&r[26]<=4&&Finite(r[0])&&Finite(r[1])&&Finite(r[5])&&r[5]>0)
            .GroupBy(r=>new {Phase=r[0],Length=Math.Round(r[5],6),Algorithm=r[26]}).OrderBy(g=>g.Key.Phase).ThenBy(g=>g.Key.Length).ThenBy(g=>g.Key.Algorithm).ToList();
        bool provisional=automatic&&groups.Count==0;int physicalTotal=0,confirmedTotal=0;List<string> reports=new List<string>();
        foreach(var group in groups)
        {
            List<double[]> members=group.ToList();
            int records=members.Select(r=>r[1]).Distinct().Count();
            bool useProject=automatic&&records>=minimumRecords;
            bool groupProvisional=automatic&&!useProject;provisional|=groupProvisional;
            double[][] physical=members.Where(r=>Finite(r[33])&&r[33]>=A4MinimumOutputConfidence&&Finite(r[13])&&
                Finite(r[6])&&r[6]>0&&Finite(r[7])&&r[7]>0).OrderBy(r=>r[13]).ThenBy(r=>r[1]).ToArray();
            List<A4AcceptanceQuery> queries=new List<A4AcceptanceQuery>();
            foreach(double[] row in physical)
            {
                double radius=Math.Max(3*row[6]*1e6/(2*row[7]),.01*row[5]);
                queries.Add(new A4AcceptanceQuery {Row=row,Radius=radius,
                    Left=A4PositionBound(physical,row[13]-radius,false),Right=A4PositionBound(physical,row[13]+radius,true)-1});
            }
            // Distinct-ID range queries use a Fenwick last-occurrence sweep: O(n log n), independent of call order.
            int[] tree=new int[physical.Length+1];Dictionary<double,int> last=new Dictionary<double,int>();int sweep=-1;
            foreach(A4AcceptanceQuery query in queries.OrderBy(q=>q.Right).ThenBy(q=>q.Left))
            {
                while(sweep<query.Right)
                {
                    sweep++;double id=physical[sweep][1];int old;
                    if(last.TryGetValue(id,out old))A4FenwickAdd(tree,old,-1);
                    A4FenwickAdd(tree,sweep,1);last[id]=sweep;
                }
                query.Support=A4FenwickPrefix(tree,query.Right+1)-A4FenwickPrefix(tree,query.Left);
            }
            int confirmed=queries.Count(q=>q.Row[35]==1);physicalTotal+=physical.Length;confirmedTotal+=confirmed;
            StringBuilder report=new StringBuilder();
            report.Append("{\"phase\":").Append(NumberJson(group.Key.Phase)).Append(",\"cable_length_m\":").Append(NumberJson(group.Key.Length))
                .Append(",\"represented_distinct_records\":").Append(records).Append(",\"project_selection_applied\":false,\"sufficient_records_for_advisory\":").Append(useProject?"true":"false")
                .Append(",\"provisional\":").Append(groupProvisional?"true":"false").Append(",\"physical_pass_rows\":").Append(physical.Length)
                .Append(",\"accepted_rows\":").Append(confirmed).Append(",\"row_support\":[");
            bool first=true;
            foreach(A4AcceptanceQuery query in queries.OrderBy(q=>q.Row[9]))
            {
                if(!first)report.Append(',');first=false;
                report.Append(NumberArrayJson(new[]{query.Row[9],query.Row[1],(double)query.Support,query.Radius,query.Row[35]}));
            }
            report.Append("]}");reports.Add(report.ToString());
        }
        return "{\"enabled\":"+(automatic?"true":"false")+",\"advisory_only\":true,\"modifies_acceptance\":false,\"caller_acceptance_preserved\":true,\"cluster_coordinate_basis\":\"current_canonical_peak_and_explicit_history_display_positions\",\"provisional\":"+(provisional?"true":"false")+
            ",\"minimum_distinct_records\":3,\"radius_rule\":\"max(3*velocity_m_per_us*1e6/(2*sample_hz),0.01*cable_length_m)\""+
            ",\"physical_confidence_column\":33,\"project_acceptance_column\":35,\"row_support_fields\":[\"pdmap_id\",\"record_id\",\"distinct_record_support\",\"radius_m\",\"accepted\"]"+
            ",\"physical_pass_rows\":"+physicalTotal+",\"accepted_rows\":"+confirmedTotal+",\"groups\":["+String.Join(",",reports)+"]}";
    }
}
