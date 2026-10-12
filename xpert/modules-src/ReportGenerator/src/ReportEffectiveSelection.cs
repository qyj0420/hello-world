using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace HVX.ReportGenerator
{
    internal static class ReportEffectiveSelection
    {
        internal static void Read(ReportAnalysisPackage package,string directory,Dictionary<string,object> manifest,
            Dictionary<string,object> session,double[,] matrix,List<string> warnings)
        {
            try{
                if(session==null||matrix==null)throw new InvalidDataException("Missing saved effective selection; reopen and save this analysis.");
                object value;var rows=session.TryGetValue("settings",out value)?value as object[]:null;
                if(rows==null)throw new InvalidDataException("Missing saved selection settings.");
                var settings=new Dictionary<string,string>(StringComparer.Ordinal);
                foreach(var item in rows){var row=item as object[];if(row==null||row.Length!=2||!(row[0] is string)||!(row[1] is string))throw new InvalidDataException("Invalid selection settings.");settings.Add((string)row[0],(string)row[1]);}
                string version,mask,hash,enabled,strength;double number;
                if(!settings.TryGetValue("EffectiveSelectionVersion",out version))throw new InvalidDataException("Legacy analysis has no saved current selection; reopen and save it before generating a report.");
                if(version!="1"||!settings.TryGetValue("EffectiveSelectionMask",out mask)||mask.Length!=matrix.GetLength(0)||mask.Any(c=>c!='0'&&c!='1')||
                    !settings.TryGetValue("EffectiveSelectionMatrixSha256",out hash)||!String.Equals(hash,MatrixHash(matrix),StringComparison.OrdinalIgnoreCase)||
                    !settings.TryGetValue("LocationFilterEnabled",out enabled)||(enabled!="0"&&enabled!="1")||
                    !settings.TryGetValue("LocationFilterStrength",out strength)||!Double.TryParse(strength,NumberStyles.Float,CultureInfo.InvariantCulture,out number)||Double.IsNaN(number)||number<0||number>100)
                    throw new InvalidDataException("Saved effective selection is invalid or belongs to a different matrix.");
                object filesValue;var files=manifest.TryGetValue("Files",out filesValue)?filesValue as object[]:null;
                if(files==null)throw new InvalidDataException("Missing selection file bindings.");
                foreach(string name in new[]{"parameters.bin","session.json"}){
                    var bindings=files.OfType<Dictionary<string,object>>().Where(f=>f.ContainsKey("Path")&&object.Equals(f["Path"],name)).ToArray();
                    if(bindings.Length!=1)throw new InvalidDataException("Invalid selection file binding.");
                    string path=Path.Combine(directory,name);long length=new FileInfo(path).Length;
                    if(length<0||length>(name=="session.json"?16L*1024*1024:80000016)||Convert.ToInt64(bindings[0]["Bytes"],CultureInfo.InvariantCulture)!=length)
                        throw new InvalidDataException("Selection file size changed.");
                    for(string cursor=path;!String.IsNullOrEmpty(cursor);cursor=Path.GetDirectoryName(cursor))
                        if((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked selection source is unavailable.");
                    using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))
                        if(!String.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",""),bindings[0]["Sha256"] as string,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Selection file hash changed.");
                }
                ReportAcquisitionQuality.Read(package,settings,session,matrix,warnings);
                if(package.AcquisitionReferenceOnlyRows.Any(r=>mask[r]=='1'))throw new InvalidDataException("Risk acquisition was included in an old saved selection; reopen and save the analysis before reporting.");
                int count=mask.Count(c=>c=='1');var selected=new double[count,matrix.GetLength(1)];int next=0;
                for(int r=0;r<mask.Length;r++)if(mask[r]=='1'){for(int c=0;c<matrix.GetLength(1);c++)selected[next,c]=matrix[r,c];next++;}
                package.SetStatisticsMatrix(selected);
                package.EffectiveRows=mask.Select(c=>c=='1').ToArray();
                ReportInputSnapshot.Read(package,settings,matrix,warnings);
                ExcludeUnavailableSources(package,matrix,warnings);
            }catch(Exception error){if(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)throw;
                package.EffectiveSelectionStatus=error.Message;warnings.Add("Current statistics unavailable: "+error.Message);}
        }
        static void ExcludeUnavailableSources(ReportAnalysisPackage package,double[,] raw,List<string> warnings)
        {
            if(package.SkippedSourceRecordCount==0)return;
            // Validate the saved context against its original hashed selection
            // first. Availability is a separate read-only projection, never a
            // repair of forged evidence or a rewrite of the user's archive.
            var unavailable=new HashSet<int>(package.UnavailableSourceRows);
            var saved=package.StatisticsMatrix;int next=0;var keep=new List<int>();
            for(int r=0;r<package.EffectiveRows.Length;r++)if(package.EffectiveRows[r]){
                if(unavailable.Contains(r)){package.EffectiveRows[r]=false;package.UnavailableResultRowCount++;}
                else keep.Add(next);
                next++;
            }
            var selected=new double[keep.Count,saved.GetLength(1)];
            for(int r=0;r<keep.Count;r++)for(int c=0;c<saved.GetLength(1);c++)selected[r,c]=saved[keep[r],c];
            package.SetStatisticsMatrix(selected);
            package.EffectiveSelectionStatus="saved_current_selection_v1_available_sources";
            if(package.HasReportInputSnapshot){
                package.PendingSelectedRecordCount=Math.Max(0,package.PendingSelectedRecordCount-package.SkippedPendingSelectedRecordCount);
                var keys=new HashSet<string>(package.UnavailableSourceRows.Select(r=>ReportAcquisitionQuality.Key(raw,r)),StringComparer.Ordinal);
                var input=package.ConclusionInput;
                var rows=Enumerable.Range(0,input.GetLength(0)).Where(r=>!keys.Contains(ReportAcquisitionQuality.Key(input,r))).ToArray();
                var available=new double[rows.Length,input.GetLength(1)];
                for(int r=0;r<rows.Length;r++)for(int c=0;c<input.GetLength(1);c++)available[r,c]=input[rows[r],c];
                package.ConclusionInput=available;
                for(int p=0;p<3;p++)if(!rows.Any(r=>input[r,0]==p+1))package.ConclusionJoints[p]=new double[0];
            }
            if(package.UnavailableResultRowCount>0){
                // The old summary/images describe the old complete scope and
                // cannot be presented as results for the surviving recordings.
                package.HasSavedConclusionSnapshot=false;package.ConclusionText=String.Empty;package.ConclusionRegions=null;
                warnings.Add("Saved conclusion omitted because its current result scope includes unavailable recordings.");
            }
        }
        internal static void ValidateCharts(ReportAnalysisPackage package,Dictionary<string,object> manifest,ReportImageInfo[] images,List<string> warnings)
        {
            if(!package.HasEffectiveSelectionSnapshot)return;
            object value;var charts=manifest.TryGetValue("Charts",out value)?value as object[]:null;if(charts==null)return;
            var unavailable=new HashSet<int>(package.UnavailableSourceRows);
            foreach(var image in images){
                try{
                    var chart=charts.OfType<Dictionary<string,object>>().Single(c=>c.ContainsKey("Sequence")&&Convert.ToInt32(c["Sequence"],CultureInfo.InvariantCulture)==image.Sequence);
                    var rows=chart.ContainsKey("Rows")?chart["Rows"] as object[]:null;
                    if(rows==null)throw new InvalidDataException("Chart has no saved row identities.");
                    foreach(var item in rows){double row=Convert.ToDouble(item,CultureInfo.InvariantCulture);
                        if(!Double.IsNaN(row)&&!Double.IsInfinity(row)&&row>=0&&row<package.EffectiveRows.Length&&row==Math.Truncate(row)&&unavailable.Contains((int)row)){
                            image.SourceUnavailable=true;throw new InvalidDataException("Chart includes unavailable source recordings.");}
                        if(Double.IsNaN(row)||Double.IsInfinity(row)||row<0||row>=package.EffectiveRows.Length||row!=Math.Truncate(row)||!package.EffectiveRows[(int)row])
                            throw new InvalidDataException("Chart contains rows outside the saved current selection.");}
                }catch(Exception error){if(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)throw;
                    image.IsHistoricalOrStale=true;image.IncludedInReport=false;image.StatusReason=error.Message;
                    warnings.Add("Chart "+image.Sequence+" excluded from current report: "+error.Message);}
            }
        }
        static string MatrixHash(double[,] matrix)
        {
            using(var sha=SHA256.Create()){
                var prefix=new byte[8];Buffer.BlockCopy(new[]{matrix.GetLength(0),matrix.GetLength(1)},0,prefix,0,8);sha.TransformBlock(prefix,0,8,null,0);
                var bytes=new byte[65536];long total=matrix.LongLength*8;
                for(long offset=0;offset<total;){int count=(int)Math.Min(bytes.Length,total-offset);Buffer.BlockCopy(matrix,checked((int)offset),bytes,0,count);sha.TransformBlock(bytes,0,count,null,0);offset+=count;}
                sha.TransformFinalBlock(new byte[0],0,0);return BitConverter.ToString(sha.Hash).Replace("-","");
            }
        }
    }
}
