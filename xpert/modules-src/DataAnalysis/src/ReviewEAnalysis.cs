using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HVX.PdReview
{
    // E/E+ execute their real frozen Python source through the protected runtime.
    // This adapter maps engineering proposals, never physical-confirmation truth.
    internal static class ReviewEAnalysis
    {
        internal const string Version="E-Eplus-20261010-r01";
        internal const string EPlusVersion="Eplus-complete-final-pair-composition-20261011-r01";
        internal const string EPlusSourceIdentity="F69A945B5FBD67C114F64B736C747CDA51124883B5AAFC22F6F962D652884D2F";
        internal static string VersionForAlgorithm(string algorithm){return algorithm=="E+"?EPlusVersion:Version;}
        internal const int MaximumLedgerBytes=16*1024*1024,MaximumCompressedLedgerBytes=512*1024;
        internal static bool IsMethod(int index){return index==8||index==9;}
        internal static string ModulesRoot { get { return Path.GetDirectoryName(Path.GetDirectoryName(ReviewAnalysisEngine.ControlSourcePath)); } }
        internal static void CheckEnvironment()
        {
            if(IntPtr.Size!=8)throw new PlatformNotSupportedException("E/E+ require the x64 application.");
            string runtime=Path.Combine(ModulesRoot,"r71");
            if(!File.Exists(Path.Combine(runtime,"python.exe")))throw new FileNotFoundException("E/E+ runtime is missing; use the complete test EXE.");
            for(int i=71;i<=86;i++)if(!File.Exists(Path.Combine(runtime,"q"+i+".cp312-win_amd64.pyd")))
                throw new FileNotFoundException("E/E+ algorithm component is missing: q"+i);
            var entry=Assembly.GetEntryAssembly();
            if(entry==null||entry.GetManifestResourceInfo("HVX.Research.Pins")==null)
                throw new InvalidDataException("E/E+ require the integrated host's embedded runtime pins.");
        }
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        static double Number(Dictionary<string,object> source,string name,double missing=Double.NaN)
        {object value;return source.TryGetValue(name,out value)&&value!=null?Convert.ToDouble(value,CultureInfo.InvariantCulture):missing;}
        static double[,] Empty(int rows,int columns)
        {var m=new double[rows,columns];for(int r=0;r<rows;r++)for(int c=0;c<columns;c++)m[r,c]=Double.NaN;return m;}
        // No candidate is removed to fit a review-session quota. The complete
        // upstream object is compressed losslessly inside the existing status.
        static Dictionary<string,object> PackLedger(Dictionary<string,object> evidence)
        {
            byte[] bytes=new UTF8Encoding(false,true).GetBytes(ReviewERuntime.Serializer().Serialize(evidence));
            if(bytes.Length>MaximumLedgerBytes)throw new InvalidDataException("E full ledger exceeds the 16 MiB decoded limit; no candidates were committed.");
            using(var hash=SHA256.Create())using(var output=new MemoryStream()) {
                using(var gzip=new GZipStream(output,CompressionLevel.Optimal,true))gzip.Write(bytes,0,bytes.Length);
                if(output.Length>MaximumCompressedLedgerBytes)throw new InvalidDataException("E full ledger exceeds the 512 KiB compressed limit; no candidates were committed.");
                return new Dictionary<string,object>{{"encoding","gzip-utf8-json-v1"},{"decoded_bytes",bytes.Length},
                    {"sha256",BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","")},{"data",Convert.ToBase64String(output.ToArray())}};
            }
        }
        internal static Dictionary<string,object> UnpackLedger(Dictionary<string,object> packed)
        {
            object encoding,count,sha,data;
            if(packed==null||packed.Count!=4||!packed.TryGetValue("encoding",out encoding)||!Object.Equals(encoding,"gzip-utf8-json-v1")||
                !packed.TryGetValue("decoded_bytes",out count)||!(count is int)||((int)count)<2||((int)count)>MaximumLedgerBytes||
                !packed.TryGetValue("sha256",out sha)||!(sha is string)||((string)sha).Length!=64||
                ((string)sha).Any(c=>!"0123456789ABCDEF".Contains(c))||!packed.TryGetValue("data",out data)||!(data is string)||
                ((string)data).Length>((MaximumCompressedLedgerBytes+2)/3)*4)
                throw new InvalidDataException("Invalid or oversized E ledger container.");
            byte[] compressed=Convert.FromBase64String((string)data);
            if(compressed.Length>MaximumCompressedLedgerBytes)throw new InvalidDataException("E ledger compressed input is oversized.");
            using(var input=new MemoryStream(compressed,false))using(var gzip=new GZipStream(input,CompressionMode.Decompress))using(var output=new MemoryStream()) {
                var buffer=new byte[8192];int read;
                while((read=gzip.Read(buffer,0,buffer.Length))>0) {
                    if(output.Length+read>MaximumLedgerBytes||output.Length+read>(int)count)throw new InvalidDataException("Expanded E ledger exceeds its bound.");
                    output.Write(buffer,0,read);
                }
                byte[] bytes=output.ToArray();
                using(var hash=SHA256.Create())if(bytes.Length!=(int)count||BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","")!=(string)sha)
                    throw new InvalidDataException("E ledger length/hash mismatch.");
                var result=ReviewERuntime.Serializer().DeserializeObject(new UTF8Encoding(false,true).GetString(bytes)) as Dictionary<string,object>;
                if(result==null)throw new InvalidDataException("E ledger must be a JSON object.");return result;
            }
        }
        internal static int ValidateLedgerStatus(string status)
        {
            if(String.IsNullOrEmpty(status)||status.IndexOf("HVX.LocationE.Adapter/1",StringComparison.Ordinal)<0)return 0;
            var parsed=ReviewERuntime.Serializer().DeserializeObject(status) as Dictionary<string,object>;
            if(parsed==null||!Object.Equals(parsed["schema"],"HVX.LocationE.Adapter/1"))throw new InvalidDataException("Invalid E status schema.");
            var packed=parsed["upstream_evidence"] as Dictionary<string,object>;UnpackLedger(packed);
            return (int)packed["decoded_bytes"];
        }
        // Restore only a disposable spectrum input. The archived/working matrix,
        // its source hash, decisions and original waveform remain unchanged.
        // Callers supply statuses mapped by a validated phase:file identity.
        internal static double[,] CreateTimeFrequencyDisplay(double[,] source,double[,] waves,IDictionary<string,string> statusesByIdentity)
        {
            var display=(double[,])source.Clone();int rows=source.GetLength(0);
            var ledgers=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            var eventMaps=new Dictionary<string,Dictionary<string,Dictionary<string,object>>>(StringComparer.Ordinal);
            bool waveShape=waves!=null&&(waves.GetLength(0)==rows||waves.GetLength(0)==3L*rows);
            for(int row=0;row<rows;row++) {
                if(source[row,26]!=8&&source[row,26]!=9)continue;
                string key=source[row,0].ToString("R",CultureInfo.InvariantCulture)+":"+source[row,1].ToString("R",CultureInfo.InvariantCulture),status;
                display[row,23]=display[row,24]=Double.NaN;
                if(statusesByIdentity==null||!statusesByIdentity.TryGetValue(key,out status))continue;
                Dictionary<string,object> ledger;Dictionary<string,Dictionary<string,object>> events;
                if(!ledgers.TryGetValue(key,out ledger)) {
                    ledger=null;events=null;
                    try {
                        var parsed=ReviewERuntime.Serializer().DeserializeObject(status) as Dictionary<string,object>;object contract,schema,algorithm,version;
                        if(parsed!=null&&parsed.TryGetValue("schema",out schema)&&Object.Equals(schema,"HVX.LocationE.Adapter/1")&&
                            parsed.TryGetValue("algorithm",out algorithm)&&Object.Equals(algorithm,source[row,26]==8?"E":"E+")&&
                            parsed.TryGetValue("version",out version)&&Object.Equals(version,source[row,26]==8?Version:EPlusVersion)) {
                            if(parsed.TryGetValue("time_frequency_contract",out contract)&&Object.Equals(contract,ReviewPulseTimeFrequency.Contract)) {
                                ledger=new Dictionary<string,object>{{"current_contract",true}};
                            } else {
                                object packed,rawEvents;
                                if(parsed.TryGetValue("upstream_evidence",out packed))ledger=UnpackLedger(packed as Dictionary<string,object>);
                                if(ledger!=null&&ledger.TryGetValue("events",out rawEvents)&&rawEvents is object[]) {
                                    events=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
                                    foreach(object raw in (object[])rawEvents) {
                                        var pulse=raw as Dictionary<string,object>;if(pulse==null)continue;
                                        int incident,reflection;object value;
                                        if(!pulse.TryGetValue("incident",out value)||!ReviewPulseTimeFrequency.Integer(value,out incident)||
                                            !pulse.TryGetValue("reflection",out value)||!ReviewPulseTimeFrequency.Integer(value,out reflection))continue;
                                        string pair=incident+":"+reflection;
                                        Dictionary<string,object> prior;
                                        // Ambiguous duplicate pair evidence must not manufacture features.
                                        if(events.TryGetValue(pair,out prior)){int a,b,c,d;if(prior==null||
                                            !ReviewPulseTimeFrequency.Window(prior,incident,out a,out b)||!ReviewPulseTimeFrequency.Window(pulse,incident,out c,out d)||a!=c||b!=d)events[pair]=null;}
                                        else events.Add(pair,pulse);
                                    }
                                }
                            }
                        }
                    } catch(Exception error) when(error is ArgumentException||error is InvalidDataException||error is FormatException||error is OverflowException||error is InvalidOperationException) {
                        ledger=null;events=null;
                    }
                    ledgers.Add(key,ledger);eventMaps.Add(key,events);
                }
                events=eventMaps[key];if(ledger==null)continue;
                if(ledger.ContainsKey("current_contract")){display[row,23]=source[row,23];display[row,24]=source[row,24];continue;}
                if(!waveShape||events==null)continue;
                int incidentIndex,reflectionIndex,sampleFirst,sampleLast;
                if(!ReviewPulseTimeFrequency.Integer(source[row,10],out incidentIndex)||!ReviewPulseTimeFrequency.Integer(source[row,11],out reflectionIndex)||
                    !ReviewPulseTimeFrequency.Integer(source[row,31],out sampleFirst)||!ReviewPulseTimeFrequency.Integer(source[row,32],out sampleLast)||
                    sampleLast<sampleFirst||(long)sampleLast-sampleFirst>=waves.GetLength(1)||source[row,20]!=incidentIndex-sampleFirst)continue;
                Dictionary<string,object> matched;int first,last;
                if(!events.TryGetValue(incidentIndex+":"+reflectionIndex,out matched)||matched==null||
                    !ReviewPulseTimeFrequency.Window(matched,incidentIndex,out first,out last)||first<sampleFirst||last>sampleLast)continue;
                double rawPeak=Math.Abs(waves[row,incidentIndex-sampleFirst]),ledgerPeak=Math.Abs(Number(matched,"q_pc"));
                if(!Finite(rawPeak)||!Finite(ledgerPeak)||Math.Abs(rawPeak-ledgerPeak)>1e-9*Math.Max(1,ledgerPeak))continue;
                var packet=new double[last-first+1];for(int sample=first;sample<=last;sample++)packet[sample-first]=waves[row,sample-sampleFirst];
                double timeNs,bandwidthHz;
                if(ReviewPulseTimeFrequency.TryMeasure(packet,first,incidentIndex,source[row,7],Number(ledger,"baseline_pc"),matched,out timeNs,out bandwidthHz)) {
                    display[row,23]=timeNs;display[row,24]=bandwidthHz;
                }
            }
            return display;
        }
        internal static ReviewAnalysisResult Analyze(ReviewAnalysisRequest request)
        {
            using(var trace=ReviewAnalysisDebugTrace.Begin("E-file","algorithm="+request.AlgorithmIndex)) {
            CheckEnvironment();
            trace.Point("environment-check");
            if(request.UseFirstArrivalTiming||request.ApplyLinearFilter||request.ConnectingCableLengthM!=0)
                throw new ArgumentException("E/E+ 使用原始峰值计时；不支持首达计时、分析前滤波或连接线扣除，请使用 A/B 处理这些选项。");
            if(!request.ManualThresholdEnabled&&request.AutomaticThresholdMultiplier!=Math.Truncate(request.AutomaticThresholdMultiplier))
                throw new ArgumentException("E/E+ 自动阈值倍数须为整数（0 使用上游默认 5）。");
            string root=File.Exists(request.ProjectPath)?Path.GetDirectoryName(request.ProjectPath):request.ProjectPath;
            var data=ReviewLocationFileReader.Read(request.LocationFilePath,root);
            trace.Point("strict-LOC-read","samples="+data.ChargeSamples.Length);
            data.VoltageKv=ReviewEProjectInput.Voltage(request.LocationFilePath,data.VoltageKv);
            string phase=Path.GetFileNameWithoutExtension(request.LocationFilePath).Split('_')[0].ToUpperInvariant();
            phase=phase=="L1"?"A":phase=="L2"?"B":phase=="L3"?"C":phase=="COMMON"?"ALL":phase;
            int phaseCode=phase=="A"?1:phase=="B"?2:phase=="C"?3:phase=="ALL"?0:-1;
            if(phaseCode<0)throw new InvalidDataException("E LOC requires an explicit A/B/C/ALL phase.");
            string[] files=Directory.GetFiles(root,"*",SearchOption.TopDirectoryOnly);
            string cablePath=files.Single(p=>Path.GetFileName(p).Equals("CABLE.in",StringComparison.OrdinalIgnoreCase));
            using(var cableGuard=new FileStream(cablePath,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                double length,speed;ReviewEProjectInput.ReadGeometry(cablePath,phase,data.VelocityMPerUs,out length,out speed);
                double frequency=request.OscillationFrequencyHz;
                string demPath=ReviewEProjectInput.MatchedDem(files,request.LocationFilePath,phase,data.VoltageKv);
                FileStream demGuard=null;
                try {
                    if(!request.ZeroVoltageRecord&&!(frequency>0)&&!String.IsNullOrEmpty(demPath)) {
                        demGuard=new FileStream(demPath,FileMode.Open,FileAccess.Read,FileShare.Read);
                        frequency=ReviewEProjectInput.ReadDemFrequency(demPath);
                    }
                    if(request.ZeroVoltageRecord)frequency=Double.NaN;
                    // Unknown phase remains unknown; never manufacture a zero-degree observation.
                    if(!(frequency>0)||!Finite(frequency))frequency=Double.NaN;
                    ReviewEProjectInput.ValidatePhaseClock(frequency,data.SampleRateHz,data.ChargeSamples.Length);
                    trace.Point("project-inputs");
                    var options=new Dictionary<string,object>{{"manual_threshold_enabled",request.ManualThresholdEnabled},
                        {"manual_threshold_pc",request.ManualThreshold},{"detection_factor",(int)request.AutomaticThresholdMultiplier},
                        {"envelope_tolerance",request.AttenuationTolerancePercent}};
                    var payload=new Dictionary<string,object>{{"algorithm",request.AlgorithmIndex==8?"E":"E+"},
                        {"config",new Dictionary<string,object>{{"sample_rate_hz",data.SampleRateHz},{"cable_length_m",length},{"velocity_m_per_us",speed},{"settings",options}}},
                        {"records",new object[]{new Dictionary<string,object>{{"record_id",Path.GetFileName(request.LocationFilePath)},{"samples",data.ChargeSamples}}}}};
                    string json=ReviewERuntime.RunAsync(ModulesRoot,null,payload,request.Cancellation).GetAwaiter().GetResult();
                    trace.Point("runtime-complete","outputCharacters="+json.Length);
                    var evidence=ReviewERuntime.Serializer().DeserializeObject(json) as Dictionary<string,object>;
                    if(evidence==null)throw new InvalidDataException("E returned no evidence ledger.");
                    trace.Point("evidence-json-decoded");
                    var result=Adapt(request,data,phaseCode,length,speed,frequency,evidence);
                    trace.Point("result-adapted-and-ledger-packed","rows="+result.Parameters.GetLength(0));
                    trace.End("success");return result;
                } finally {if(demGuard!=null)demGuard.Dispose();}
            }
            }
        }
        internal static ReviewAnalysisResult Adapt(ReviewAnalysisRequest r,ReviewLocationFileData data,int phase,double length,double speed,double frequency,Dictionary<string,object> evidence)
        {
            object rawEvents;
            if(!evidence.TryGetValue("events",out rawEvents)||!(rawEvents is object[]))throw new InvalidDataException("E candidate ledger is missing.");
            var all=((object[])rawEvents).Cast<Dictionary<string,object>>().ToList();
            var candidates=all.OrderByDescending(e=>Object.Equals(e["engineering_eligible"],true)).ThenByDescending(e=>Math.Abs(Number(e,"q_pc"))).ThenBy(e=>Number(e,"incident")).ToList();
            var unpaired=((object[])evidence["attempts"]).Cast<Dictionary<string,object>>().Where(a=>!Object.Equals(a["status"],"E_complete_passing_candidate_inventory"));
            foreach(var a in unpaired)candidates.Add(new Dictionary<string,object>{{"incident",a["incident"]},{"q_pc",a["raw_amplitude_pc"]},{"engineering_eligible",false},{"unpaired",true}});
            var chosen=candidates.Take(r.MaxPulses).ToArray();
            int roundTrip=Convert.ToInt32(evidence["roundtrip_samples"]),width=0;
            var ranges=new List<int[]>();
            foreach(var e in chosen){int i=(int)Number(e,"incident"),first=Math.Max(0,i-200),last=Math.Min(data.ChargeSamples.Length-1,i+roundTrip+500);ranges.Add(new[]{first,last});width=Math.Max(width,last-first+1);}
            var p=Empty(chosen.Length,36);var raw=Empty(chosen.Length,width);var equivalent=Empty(chosen.Length,1);
            var roles=new List<object>();
            for(int row=0;row<chosen.Length;row++) {
                var e=chosen[row];int i=(int)Number(e,"incident"),first=ranges[row][0],last=ranges[row][1];double j=Number(e,"reflection"),q=Math.Abs(Number(e,"q_pc"));
                double degrees=Finite(frequency)?((360*frequency*i/data.SampleRateHz)%360+360)%360:Double.NaN;
                bool selectedQuadrant=Finite(degrees)?r.PhaseQuadrants[Math.Min(3,(int)(degrees/90))]:r.PhaseQuadrants.All(v=>v);
                bool eligible=Object.Equals(e["engineering_eligible"],true)&&selectedQuadrant;
                Dictionary<string,object> finalScore=null;object scoreObject;
                if(r.AlgorithmIndex==9) {
                    if(e.TryGetValue("final_pair_score",out scoreObject))finalScore=scoreObject as Dictionary<string,object>;
                    eligible=eligible&&finalScore!=null&&Object.Equals(finalScore["state"],"measured")&&Object.Equals(finalScore["qualified"],true);
                }
                p[row,0]=phase;p[row,1]=r.LocationDataId;p[row,2]=r.ZeroVoltageRecord?0:data.VoltageKv;p[row,3]=degrees;
                p[row,4]=Number(evidence,"threshold_pc");p[row,5]=length;p[row,6]=speed;p[row,7]=data.SampleRateHz;p[row,8]=r.LocationDataId;p[row,9]=row;
                p[row,10]=i;p[row,11]=j;p[row,12]=Finite(j)?j-i:Double.NaN;p[row,13]=Number(e,"x_raw_m");
                p[row,15]=q;p[row,16]=q;p[row,17]=1;p[row,20]=i-first;p[row,22]=Finite(j)&&q>0?Math.Abs(data.ChargeSamples[(int)j])/q:Double.NaN;
                double equivalentTime,equivalentBandwidth;
                if(ReviewPulseTimeFrequency.TryMeasure(data.ChargeSamples,0,i,data.SampleRateHz,Number(evidence,"baseline_pc"),e,out equivalentTime,out equivalentBandwidth)) {
                    p[row,23]=equivalentTime;p[row,24]=equivalentBandwidth;
                }
                p[row,25]=0;p[row,26]=r.AlgorithmIndex;p[row,27]=r.AlgorithmIndex==9?1:0;p[row,28]=Number(evidence,"noise_mad_pc");
                p[row,29]=Number(evidence,"baseline_pc")+p[row,4];p[row,30]=Number(evidence,"baseline_pc")-p[row,4];p[row,31]=first;p[row,32]=last;
                p[row,33]=r.AlgorithmIndex==9?(finalScore==null?Double.NaN:Number(finalScore,"score")):Number(e,"e_objective_weight");
                p[row,34]=1;p[row,35]=r.AutomaticAcceptance&&eligible?1:0;
                for(int k=first;k<=last;k++)raw[row,k-first]=data.ChargeSamples[k];
                roles.Add(new Dictionary<string,object>{{"row",row},{"source_identity_supported",false},{"source_identity_requires_review",true},{"engineering_eligible",eligible},
                    {"upstream_event_id",Number(e,r.AlgorithmIndex==9?"rescore_event_id":"e_event_id")},{"incident",i},{"reflection",j}});
            }
            var status=new Dictionary<string,object>{{"schema","HVX.LocationE.Adapter/1"},{"algorithm",r.AlgorithmIndex==8?"E":"E+"},{"version",r.AlgorithmIndex==9?EPlusVersion:Version},
                {"current_analysis",new Dictionary<string,object>{{"output_threshold",Number(evidence,"threshold_pc")},{"output_pulse_roles",new Dictionary<string,object>{{"rows",roles}}}}},
                {"upstream_evidence",PackLedger(evidence)},{"output_row_cap",r.MaxPulses},{"ledger_candidates",candidates.Count},{"output_rows",chosen.Length},
                {"omitted_from_matrix_retained_in_ledger",Math.Max(0,candidates.Count-chosen.Length)},
                {"geometry_mode","current-phase CABLE length; CABLE or current LOC velocity; time-equivalent single speed, as upstream E+ current-project replay"},
                {"timing_basis","raw_peak"},{"correlation_column21_available",false},{"score33_meaning","research ratio/(1+ratio), not Pearson correlation or probability"},
                {"time_frequency_contract",ReviewPulseTimeFrequency.Contract},
                {"time_frequency_measured_rows",Enumerable.Range(0,chosen.Length).Count(row=>Finite(p[row,23])&&Finite(p[row,24]))},
                {"physical_identity_confirmed",false},{"calibration_shape_applied",false},{"compensated_charge_applied",false}};
            if(r.AlgorithmIndex==9){status["candidate_source_identity"]=EPlusSourceIdentity;status["auxiliary_lowpass_applied"]=false;status["timing_basis"]="raw_packet_representative_peak";
                status["score33_meaning"]="final_pair_score.score S in [0,1]; UI displays 100*S; research rank, not probability or physical accuracy";
                status["ownership_version"]="Eplus-final-pair-linear-score-ownership-r01";}
            var result=new ReviewAnalysisResult{Success=true,Parameters=p,Raw=raw,Filtered=(double[,])raw.Clone(),Equivalent=equivalent,
                AppliedThreshold=Number(evidence,"threshold_pc"),Status=ReviewStatusJson.Serialize(status)};
            if(result.Status.Length>1024*1024)throw new InvalidDataException("E full candidate ledger exceeds the review-session limit; no rows were committed.");
            ReviewAnalysisEngine.ValidateOutput(result,r.MaxPulses);return result;
        }
    }
}
