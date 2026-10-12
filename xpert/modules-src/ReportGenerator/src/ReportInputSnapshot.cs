using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace HVX.ReportGenerator
{
    internal static class ReportInputSnapshot
    {
        // Called only after both session.json and parameters.bin have passed the
        // effective-selection file hashes. Never consult current-machine defaults.
        internal static void Read(ReportAnalysisPackage package,Dictionary<string,string> settings,double[,] raw,List<string> warnings)
        {
            string text;
            if(!settings.TryGetValue("ReportInputContext",out text))
            {
                package.ReportInputStatus="Legacy analysis has no saved projection context; reopen and save it before generating a new conclusion.";
                warnings.Add(package.ReportInputStatus);return;
            }
            package.ReportInputContextPresent=true;
            try
            {
                if(raw.GetLength(1)<36)throw new InvalidDataException("Report matrix requires 36 columns.");
                var json=new JavaScriptSerializer{MaxJsonLength=16*1024*1024,RecursionLimit=16};
                var c=json.DeserializeObject(text) as Dictionary<string,object>;
                string expected;
                if(c==null||!Object.Equals(Value(c,"Schema"),"HVX.ReportInput.1")||
                    !settings.TryGetValue("EffectiveSelectionMatrixSha256",out expected)||
                    !String.Equals(Value(c,"MatrixSha256") as string,expected,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Report input does not belong to the saved matrix.");
                if(!Object.Equals(Value(c,"Available"),true))throw new InvalidDataException(Value(c,"UnavailableReason") as string??"Saved report input is unavailable.");
                object qualityRows;if(c.TryGetValue("AcquisitionReferenceOnlyRows",out qualityRows)&&!Rows(qualityRows,raw.GetLength(0)).SequenceEqual(package.AcquisitionReferenceOnlyRows))
                    throw new InvalidDataException("Saved report risk identities differ from their acquisition quality evidence.");
                if(!(Value(c,"SnapEnabled") is bool))throw new InvalidDataException("Missing saved joint-approximation switch.");
                double endpoint=Number(Value(c,"EndpointTolerancePercent")),joint=Number(Value(c,"JointTolerancePercent"));
                if(endpoint<0||endpoint>100||joint<0||joint>100)throw new InvalidDataException("Invalid saved approximation tolerance.");
                var indices=Rows(Value(c,"ProjectionRows"),raw.GetLength(0));
                double[] before=Numbers(Value(c,"RawPositionsM")),after=Numbers(Value(c,"PositionsM"));
                if(indices.Length!=before.Length||indices.Length!=after.Length)throw new InvalidDataException("Projection row count differs.");
                var effective=(double[,])raw.Clone();var projected=new HashSet<int>();
                for(int i=0;i<indices.Length;i++)
                {
                    int r=indices[i];double length=raw[r,5];
                    if(!package.EffectiveRows[r]||!SameSerializedNumber(before[i],raw[r,13]))throw new InvalidDataException("Saved projection differs from its raw source.");
                    // A retained out-of-range/invalid row is not silently repaired.
                    // Its coordinate may only change within the saved error bands.
                    if(!after[i].Equals(before[i])&&(!Finite(length)||length<=0||after[i]<0||after[i]>length||
                        Math.Abs(after[i]-before[i])>length*Math.Max(2,Math.Max(endpoint,joint))/100+1e-9))
                        throw new InvalidDataException("Saved projection exceeds its approximation band.");
                    effective[r,13]=SameSerializedNumber(after[i],before[i])?raw[r,13]:after[i];projected.Add(r);
                }
                for(int r=0;r<raw.GetLength(0);r++)
                    if(package.EffectiveRows[r]&&Finite(raw[r,13])&&!projected.Contains(r))throw new InvalidDataException("Saved projection is incomplete.");
                var conclusionRows=Rows(Value(c,"ConclusionRows"),raw.GetLength(0));
                var expectedRows=Enumerable.Range(0,raw.GetLength(0)).Where(r=>package.EffectiveRows[r]&&raw[r,35]==1&&ValidLocatedRow(raw,r)).ToArray();
                if(!conclusionRows.SequenceEqual(expectedRows))throw new InvalidDataException("Conclusion row scope is incomplete or reordered.");
                var input=new double[conclusionRows.Length,raw.GetLength(1)];
                for(int i=0;i<conclusionRows.Length;i++)
                {
                    int r=conclusionRows[i];
                    if(!package.EffectiveRows[r]||raw[r,35]!=1||!ValidLocatedRow(raw,r))throw new InvalidDataException("Conclusion references an excluded, unaccepted or invalid row.");
                    for(int col=0;col<raw.GetLength(1);col++)input[i,col]=effective[r,col];
                    input[i,22]=Double.NaN; // amplitude ratio is not probability
                }
                var savedJoints=Value(c,"Joints") as object[];
                if(savedJoints==null||savedJoints.Length!=3)throw new InvalidDataException("Missing saved phase geometry.");
                var joints=new double[3][];
                for(int p=0;p<3;p++)
                {
                    joints[p]=Numbers(savedJoints[p]);
                    if(joints[p].Length>10000||joints[p].Any(x=>x<0)||joints[p].Skip(1).Where((x,i)=>x<=joints[p][i]).Any())throw new InvalidDataException("Invalid saved joint ordering.");
                    var lengths=conclusionRows.Where(r=>raw[r,0]==p+1).Select(r=>raw[r,5]).Distinct().ToArray();
                    if(lengths.Length>1&&lengths.Any(x=>Math.Abs(x-lengths[0])>Math.Max(1e-6,lengths[0]*1e-9))||lengths.Length>0&&(joints[p].Length<2||joints[p][0]!=0||Math.Abs(joints[p].Last()-lengths[0])>Math.Max(1e-6,lengths[0]*1e-9))||lengths.Length==0&&joints[p].Length!=0)
                        throw new InvalidDataException("Saved joints do not match their phase cable geometry.");
                }
                double voltage=Number(Value(c,"OperatingVoltageKv"));if(voltage<0)throw new InvalidDataException("Invalid saved U0.");
                double selectedCount=Number(Value(c,"SelectedRecordCount")),pendingCount=Number(Value(c,"PendingSelectedRecordCount"));
                if(selectedCount<0||selectedCount>1000000||selectedCount!=Math.Truncate(selectedCount)||pendingCount<0||pendingCount>selectedCount||pendingCount!=Math.Truncate(pendingCount))throw new InvalidDataException("Invalid saved analysis completion scope.");
                int count=package.EffectiveRows.Count(x=>x);var statistics=new double[count,raw.GetLength(1)];int next=0;
                for(int r=0;r<raw.GetLength(0);r++)if(package.EffectiveRows[r]){for(int col=0;col<raw.GetLength(1);col++)statistics[next,col]=effective[r,col];next++;}
                package.SetStatisticsMatrix(statistics);
                package.ConclusionInput=input;package.ConclusionJoints=joints;package.ConclusionOperatingVoltage=voltage;
                package.ReportJointApproximationEnabled=(bool)c["SnapEnabled"];
                package.ReportJointTolerancePercent=joint;package.ReportEndpointTolerancePercent=endpoint;
                package.HasReportInputSnapshot=true;package.ReportInputStatus="Saved final selection and canonical projection verified.";
                package.PendingSelectedRecordCount=(int)pendingCount;
            }
            catch(Exception ex)
            {
                if(ex is OutOfMemoryException||ex is StackOverflowException||ex is AccessViolationException)throw;
                package.ReportInputStatus=ex.Message;package.HasReportInputSnapshot=false;
                warnings.Add("Report input unavailable: "+ex.Message);
            }
        }
        static object Value(Dictionary<string,object> d,string key){object v;if(!d.TryGetValue(key,out v))throw new InvalidDataException("Missing report input member: "+key);return v;}
        static bool Finite(double v){return !Double.IsNaN(v)&&!Double.IsInfinity(v);}
        // JSON decimal parsing can differ from the hashed binary double by a few
        // ULPs. This is representation tolerance, not a physical position band.
        static bool SameSerializedNumber(double a,double b){return Finite(a)&&Finite(b)&&Math.Abs(a-b)<=8*2.2204460492503131e-16*Math.Max(1,Math.Max(Math.Abs(a),Math.Abs(b)));}
        static double Number(object v){if(v==null||v is bool||v is string)throw new InvalidDataException("Invalid report number.");double n=Convert.ToDouble(v,CultureInfo.InvariantCulture);if(!Finite(n))throw new InvalidDataException("Nonfinite report number.");return n;}
        static double[] Numbers(object v){var a=v as object[];if(a==null||a.Length>200000)throw new InvalidDataException("Invalid report array.");return a.Select(Number).ToArray();}
        static int[] Rows(object v,int count){var a=Numbers(v);if(a.Any(x=>x<0||x>=count||x!=Math.Truncate(x)))throw new InvalidDataException("Invalid report row identity.");var rows=a.Select(x=>(int)x).ToArray();if(rows.Distinct().Count()!=rows.Length)throw new InvalidDataException("Duplicate report row.");return rows;}
        static bool ValidLocatedRow(double[,] m,int r)
        {
            double phase=m[r,0],length=m[r,5],position=m[r,13],incident=m[r,10],reflection=m[r,11];
            return Finite(phase)&&phase>=1&&phase<=3&&phase==Math.Truncate(phase)&&Finite(length)&&length>0&&Finite(position)&&position>=-.02*length&&position<=1.02*length&&
                Finite(m[r,1])&&Finite(m[r,2])&&m[r,2]>.05&&Finite(m[r,3])&&Finite(m[r,15])&&m[r,15]>0&&Finite(m[r,23])&&m[r,23]>=0&&Finite(m[r,24])&&m[r,24]>=0&&
                Finite(m[r,7])&&m[r,7]>0&&Finite(incident)&&incident>=0&&incident<=Int32.MaxValue&&incident==Math.Truncate(incident)&&
                Finite(reflection)&&reflection>=0&&reflection<=Int32.MaxValue&&reflection==Math.Truncate(reflection)&&Finite(m[r,12])&&m[r,12]>=0&&reflection-incident==m[r,12];
        }
    }
}
