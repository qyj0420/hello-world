using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace HVX.PdReview
{
    // In-memory pressure-trial acceleration only. Never persisted in a project.
    // The interactive/history-assisted analysis path is not cached.
    internal static class PressureAnalysisCache
    {
        static readonly object Gate=new object();
        static readonly Dictionary<string,double[,]> Entries=new Dictionary<string,double[,]>();
        static readonly Queue<string> Order=new Queue<string>();
        const int Capacity=128;

        internal static double[,] Analyze(string scope,ReviewAnalysisRequest request,Func<ReviewAnalysisRequest,ReviewAnalysisResult> analyze,out bool hit)
        {
            hit=false;
            if(request!=null)HVX.Storage.PressureRecordPublication.RequireReadable(request.LocationFilePath);
            string key=String.IsNullOrEmpty(scope)?null:Key(scope,request);
            double[,] cached;
            if(key!=null)lock(Gate)if(Entries.TryGetValue(key,out cached)){hit=true;return (double[,])cached.Clone();}
            var result=analyze(request);
            if(!result.Success)throw new InvalidDataException(Path.GetFileName(request.LocationFilePath)+": "+result.Status);
            // Only bounded parameter rows are retained, never high-rate samples.
            var matrix=result.Parameters;
            if(key!=null && matrix.GetLength(0)<=100 && matrix.GetLength(1)==36 && key==Key(scope,request)){
                lock(Gate){
                    if(!Entries.ContainsKey(key)){
                        while(Entries.Count>=Capacity)Entries.Remove(Order.Dequeue());
                        Entries.Add(key,(double[,])matrix.Clone());Order.Enqueue(key);
                    }
                }
            }
            return matrix;
        }

        internal static string Key(string scope,ReviewAnalysisRequest request)
        {
            if(request==null || request.AHistoryBatch!=null)return null;
            // Until a dedicated raw/CAL/settings fingerprint cache is present,
            // always revalidate bound capacitor inputs. A DEM-only cache hit must
            // never hide a changed original ADC, parameter file or calibration.
            if(ReviewCapacitorDemInput.IsDem(request.LocationFilePath))return null;
            HVX.Storage.PressureRecordPublication.RequireReadable(request.LocationFilePath);
            var text=new StringBuilder();Append(text,scope);
            // Include ALL fields, including options added in future. Unknown
            // reference types disable caching instead of guessing their identity.
            foreach(var field in typeof(ReviewAnalysisRequest).GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).OrderBy(f=>f.Name,StringComparer.Ordinal)){
                if(field.Name=="Cancellation")continue; // Operation lifetime, not an algorithm input.
                Append(text,field.Name);if(!Append(text,field.GetValue(request)))return null;
            }
            if(request.AlgorithmIndex==9){Append(text,ReviewEAnalysis.EPlusVersion);Append(text,ReviewEAnalysis.EPlusSourceIdentity);}
            Append(text,typeof(PressureAnalysisCache).Assembly.ManifestModule.ModuleVersionId.ToString());
            string root=Path.GetFullPath(request.ProjectPath);
            if(File.Exists(root))root=Path.GetDirectoryName(root);
            var files=new List<string>{Path.GetFullPath(request.LocationFilePath),Path.Combine(root,"ITEM.in"),Path.Combine(root,"CABLE.in"),ReviewAnalysisEngine.ExpectedAlgorithmPath};
            files.AddRange(Directory.GetFiles(root,"*.in"));
            files.AddRange(Directory.GetFiles(root,"*.cal"));
            string live=Path.Combine(root,"Calibration","Live");
            if(Directory.Exists(live))files.AddRange(Directory.GetFiles(live,"*.hvcal"));
            // DEM can supply oscillation frequency and acquisition metadata.
            // Its appearance, removal, or edit must invalidate old LOC results.
            string dem=Path.ChangeExtension(Path.GetFullPath(request.LocationFilePath),".dem");
            Append(text,dem);Append(text,File.Exists(dem));
            if(File.Exists(dem))files.Add(dem);
            foreach(string path in files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase)){
                Append(text,path);
                // A missing dependency must run the normal validator, never
                // reuse a previous success after a file has disappeared.
                if(!File.Exists(path))return null;
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                using(var sha=SHA256.Create())Append(text,Convert.ToBase64String(sha.ComputeHash(stream)));
            }
            using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }

        static bool Append(StringBuilder text,object value)
        {
            if(value==null){text.Append("N;");return true;}
            var quality=value as ReviewAcquisitionQuality;
            if(quality!=null){
                // This is the one known bounded metadata object. Include every
                // scalar field so adding quality evidence cannot silently share
                // a normal-result cache key. Unknown object types remain denied.
                text.Append("HVX.AcquisitionQuality.v1{");
                foreach(var field in typeof(ReviewAcquisitionQuality).GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).OrderBy(f=>f.Name,StringComparer.Ordinal)){
                    var fieldType=field.FieldType;
                    if(fieldType!=typeof(string)&&!fieldType.IsPrimitive&&!fieldType.IsEnum)return false;
                    Append(text,field.Name);if(!Append(text,field.GetValue(quality)))return false;
                }
                text.Append('}');return true;
            }
            var array=value as Array;
            if(array!=null){text.Append("A").Append(array.Length).Append(':');foreach(var item in array)if(!Append(text,item))return false;return true;}
            var type=value.GetType();
            if(type!=typeof(string)&&!type.IsPrimitive&&!type.IsEnum)return false;
            string s=value is double?((double)value).ToString("R",CultureInfo.InvariantCulture):Convert.ToString(value,CultureInfo.InvariantCulture);
            text.Append(type.FullName).Append(':').Append(s.Length).Append(':').Append(s).Append(';');return true;
        }
    }
}
