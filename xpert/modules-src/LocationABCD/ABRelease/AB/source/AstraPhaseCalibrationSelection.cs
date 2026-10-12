using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HVX.Unified.Internal;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Task24's revised policy is implemented locally, not delegated to its older
    // deployed selector. Every object below belongs to one call; no state is cached.
    private sealed class A4CableBlock
    {
        internal string Phase;
        internal double Velocity=double.NaN;
        internal readonly List<CableAccessory> Accessories=new List<CableAccessory>();
        internal readonly List<CableSegment> Segments=new List<CableSegment>();
        internal string Material="";
    }
    private static string A4StrictPhase(string value)
    {
        string text=(value??"").Trim().ToUpperInvariant();
        if(text=="A"||text=="A相"||text=="L1")return "A";
        if(text=="B"||text=="B相"||text=="L2")return "B";
        if(text=="C"||text=="C相"||text=="L3")return "C";
        if(text=="ALL"||text=="ALL相"||text=="COMMON"||text=="全相"||text=="三相"||text=="共用")return "ALL";
        return "";
    }
    private const string A4NumericToken=@"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?";
    private const string A4VelocityUnit=@"m\s*/\s*(?:usec|us|μs|µs|s|nsec|ns)|米\s*/\s*(?:微秒|秒|纳秒)";
    private static double A4VelocityUnitScale(string unit)
    {
        unit=Regex.Replace(unit??"",@"\s+","").ToLowerInvariant();
        if(unit=="m/s"||unit=="米/秒")return 1e-6;
        if(unit=="m/ns"||unit=="m/nsec"||unit=="米/纳秒")return 1000;
        if(unit.Length==0||unit=="m/us"||unit=="m/usec"||unit=="m/μs"||unit=="m/µs"||unit=="米/微秒")return 1;
        return double.NaN;
    }
    private static bool A4TryVelocity(string text,out double velocity)
    {
        velocity=double.NaN;text=(text??"").Replace('（','(').Replace('）',')');
        // A named value may have its unit before the separator (CAL header) or
        // after the value (segment material). Unlabelled bare numbers are not velocity.
        Match named=Regex.Match(text,@"(?:\bvelocity\b|波速|\bv\b)\s*(?:\((?<before>[^)]+)\))?\s*[:=：\t]\s*(?<number>"+A4NumericToken+@")(?=$|[\s,;；)m米])\s*(?:(?<after>"+A4VelocityUnit+@")(?![A-Za-z]))?",RegexOptions.IgnoreCase);
        Match prefix=Regex.Match(text,@"(?<![\w/])(?<before>"+A4VelocityUnit+@")\s*[:=：\t]\s*(?<number>"+A4NumericToken+@")(?=$|[\s,;；)m米])\s*(?:(?<after>"+A4VelocityUnit+@")(?![A-Za-z]))?",RegexOptions.IgnoreCase);
        Match units=named.Success?named:prefix.Success?prefix:Regex.Match(text,@"(?<![\w.])(?<number>"+A4NumericToken+@")\s*(?<after>"+A4VelocityUnit+@")(?![A-Za-z])",RegexOptions.IgnoreCase);
        if(!units.Success)return false;
        if(!units.Groups["after"].Success&&Regex.IsMatch(text.Substring(units.Index+units.Length),@"^\s*[A-Za-zμµ米]+\s*/"))return false;
        double number;if(!Double.TryParse(units.Groups["number"].Value,NumberStyles.Float,Inv,out number)||!Finite(number)||number<=0)return false;
        double beforeScale=A4VelocityUnitScale(units.Groups["before"].Value),afterScale=A4VelocityUnitScale(units.Groups["after"].Value);
        if(!Finite(beforeScale)||!Finite(afterScale)||units.Groups["before"].Success&&units.Groups["after"].Success&&beforeScale!=afterScale)return false;
        double scale=units.Groups["after"].Success?afterScale:beforeScale;
        velocity=number*scale;return Finite(velocity)&&velocity>0;
    }
    private static double A4HeaderVelocity(Dictionary<string,string> header)
    {
        foreach(var item in header)
            if(Regex.IsMatch(item.Key,@"velocity|波速|^v(?:\s|\()",RegexOptions.IgnoreCase))
            {
                double velocity;if(A4TryVelocity(item.Key+":"+item.Value,out velocity))return velocity;
                throw new InvalidDataException("Invalid or unsupported velocity value/unit: "+item.Key);
            }
        return double.NaN;
    }
    private static double A4HeaderTimeSeconds(Dictionary<string,string> header,string fragment)
    {
        string key=header.Keys.FirstOrDefault(k=>k.IndexOf(fragment,StringComparison.OrdinalIgnoreCase)>=0);
        if(key==null)return double.NaN;
        string text=header[key].Trim();key=key.Replace('（','(').Replace('）',')');
        Match value=Regex.Match(text,@"^(?<number>"+A4NumericToken+@")\s*(?<unit>s|sec|ms|msec|us|usec|μs|µs|ns|nsec|秒|毫秒|微秒|纳秒)?\s*$",RegexOptions.IgnoreCase);
        if(!value.Success)throw new InvalidDataException("Invalid or unsupported time value/unit: "+key);
        double number;if(!Double.TryParse(value.Groups["number"].Value,NumberStyles.Float,Inv,out number)||!Finite(number))return double.NaN;
        string unit=value.Groups["unit"].Value.ToLowerInvariant();
        if(unit.Length==0)
        {
            Match label=Regex.Match(key,@"\((s|sec|ms|msec|us|usec|μs|µs|ns|nsec|秒|毫秒|微秒|纳秒)\)",RegexOptions.IgnoreCase);
            if(label.Success)unit=label.Groups[1].Value.ToLowerInvariant();
            else if(key.Contains("(")||key.Contains(")"))throw new InvalidDataException("Unsupported time label unit: "+key);
        }
        double scale=unit=="ms"||unit=="msec"||unit=="毫秒"?1e-3:
            unit=="us"||unit=="usec"||unit=="μs"||unit=="µs"||unit=="微秒"?1e-6:
            unit=="ns"||unit=="nsec"||unit=="纳秒"?1e-9:1;
        return number*scale;
    }
    private static CableMetadata A4EmptyCable()
    {
        return new CableMetadata {CableLength=double.NaN,VelocityMPerUs=double.NaN,
            Accessories=new List<CableAccessory>(),Segments=new List<CableSegment>(),
            JointPositionsIncludingEnds=new List<double>(),InternalJointPositions=new List<double>()};
    }
    private static bool A4SameCableBlock(A4CableBlock a,A4CableBlock b)
    {
        if(!(a.Velocity.Equals(b.Velocity))||a.Accessories.Count!=b.Accessories.Count||a.Segments.Count!=b.Segments.Count)return false;
        for(int i=0;i<a.Accessories.Count;i++)
            if(a.Accessories[i].PositionM!=b.Accessories[i].PositionM||a.Accessories[i].Name!=b.Accessories[i].Name||a.Accessories[i].Type!=b.Accessories[i].Type)return false;
        for(int i=0;i<a.Segments.Count;i++)
            if(a.Segments[i].StartM!=b.Segments[i].StartM||a.Segments[i].EndM!=b.Segments[i].EndM||a.Segments[i].InsulationType!=b.Segments[i].InsulationType)return false;
        return true;
    }
    private static CableMetadata A4ReadPhaseCable(string path,string requestedPhase)
    {
        var blocks=new List<A4CableBlock>();A4CableBlock block=null;
        foreach(string original in ReadAllLinesSmart(path))
        {
            string line=original.Trim();if(line.Length==0)continue;
            Match record=Regex.Match(line,@"^([012])\s+(.*)$");
            if(record.Success&&record.Groups[1].Value=="0")
            {
                string phase=A4StrictPhase(record.Groups[2].Value);
                block=phase.Length==0?null:new A4CableBlock {Phase=phase};
                if(block!=null)blocks.Add(block);continue;
            }
            // An unknown or absent phase header never becomes A or COMMON.
            if(block==null)continue;
            if(record.Success)
            {
                string text=record.Groups[2].Value.Trim();
                if(record.Groups[1].Value=="2")
                {block.Material=Regex.Replace(text,@"^(?:本体|Body)\s*","",RegexOptions.IgnoreCase).Trim();continue;}
                if(record.Groups[1].Value=="1")
                {
                    Match endpoint=Regex.Match(text,@"([-+]?(?:\d+(?:\.\d*)?|\.\d+))\s*(?:m|米)?\s*$",RegexOptions.IgnoreCase);
                    double position;if(!endpoint.Success||!Double.TryParse(endpoint.Groups[1].Value,NumberStyles.Float,Inv,out position)||!Finite(position)||position<0)
                        throw new InvalidDataException("Invalid CABLE endpoint in phase "+block.Phase+".");
                    string label=text.Substring(0,endpoint.Index).Trim();string[] cells=Regex.Split(label,@"\s+").Where(v=>v.Length>0).ToArray();
                    string name=cells.Length>1?String.Join(" ",cells.Take(cells.Length-1)):label;
                    string kind=cells.Length>0?cells[cells.Length-1]:"";
                    if(block.Accessories.Count>0)
                    {
                        double start=block.Accessories[block.Accessories.Count-1].PositionM;
                        if(position<=start)throw new InvalidDataException("CABLE endpoints must increase inside phase "+block.Phase+".");
                        block.Segments.Add(new CableSegment {StartM=start,EndM=position,InsulationType=block.Material});
                    }
                    block.Accessories.Add(new CableAccessory {Name=name,Type=kind,PositionM=position});block.Material="";continue;
                }
            }
            if(Regex.IsMatch(line,@"m\s*/\s*(?:usec|us|μs|µs|s|ns)|波速|velocity|^v\s*\(",RegexOptions.IgnoreCase))
            {
                double velocity;if(!A4TryVelocity(line,out velocity))throw new InvalidDataException("Invalid CABLE velocity/unit in phase "+block.Phase+".");
                if(Finite(block.Velocity)&&block.Velocity!=velocity)throw new InvalidDataException("Conflicting CABLE velocities in phase "+block.Phase+".");
                block.Velocity=velocity;
            }
        }
        var unique=new Dictionary<string,A4CableBlock>();var duplicates=new Dictionary<string,int>();
        foreach(A4CableBlock candidate in blocks)
        {
            A4CableBlock existing;
            if(unique.TryGetValue(candidate.Phase,out existing))
            {
                if(!A4SameCableBlock(existing,candidate))throw new InvalidDataException("Conflicting duplicate CABLE blocks for phase "+candidate.Phase+"; no blocks were merged.");
                duplicates[candidate.Phase]++;
            }
            else{unique.Add(candidate.Phase,candidate);duplicates.Add(candidate.Phase,0);}
        }
        string target=A4StrictPhase(requestedPhase);A4CableBlock selected=null;CableMetadata result=A4EmptyCable();
        result.SamePhaseAvailable=target.Length>0&&unique.ContainsKey(target)&&unique[target].Accessories.Count>0;
        if(target.Length>0&&unique.TryGetValue(target,out selected)&&selected.Accessories.Count>0)result.SelectionSource="same_phase_cable_block";
        else if(unique.TryGetValue("ALL",out selected)&&selected.Accessories.Count>0)result.SelectionSource="explicit_common_cable_block";
        else selected=null;
        result.PhaseLengthsJson="{"+String.Join(",",new[]{"A","B","C","ALL"}.Select(p=>Json(p)+":"+
            NumberJson(unique.ContainsKey(p)&&unique[p].Accessories.Count>0?unique[p].Accessories.Max(a=>a.PositionM):double.NaN)))+"}";
        if(selected==null)return result;
        result.SelectedPhase=selected.Phase;result.DuplicateBlocksCollapsed=duplicates[selected.Phase];
        result.Accessories=selected.Accessories;result.Segments=selected.Segments;result.VelocityMPerUs=selected.Velocity;
        result.CableLength=selected.Accessories.Max(a=>a.PositionM);
        result.JointPositionsIncludingEnds=UniqueSorted(selected.Accessories.Select(a=>a.PositionM).Concat(new[]{0d,result.CableLength}));
        result.InternalJointPositions=UniqueSorted(selected.Accessories.Where(a=>IsJointName(a.Name)&&a.PositionM>0&&a.PositionM<result.CableLength).Select(a=>a.PositionM));
        return result;
    }

    private sealed class A4PhaseCalCandidate
    {
        internal ParsedCalibration Calibration;
        internal string Path,Phase,Reason;
        internal bool Valid;
        internal bool ReferenceReliable;
        internal string ReferenceReason="not_assessed";
        internal string SeventhCapabilitiesJson="null";
        internal double Quality=double.NaN;
        internal DateTime Time;
    }
    private static A4PhaseCalCandidate A4ReadPhaseCalCandidate(string path,bool seventhCapabilities=false)
    {
        var candidate=new A4PhaseCalCandidate {Path=Path.GetFullPath(path),Reason="invalid_name",Time=DateTime.MinValue};
        Match name=Regex.Match(Path.GetFileNameWithoutExtension(path),@"^(A|B|C|L1|L2|L3|ALL)_([0-9]+(?:\.[0-9]+)?)(PC|NC)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        if(!name.Success)return candidate;
        candidate.Phase=A4StrictPhase(name.Groups[1].Value);
        try
        {
            string[] lines=ReadAllLinesSmart(path);if(lines.Length<45){candidate.Reason="truncated";return candidate;}
            Dictionary<string,string> header=Header(lines,13);
            double value=NumContains(header,"Cal.value"),named=ParseNumber(name.Groups[2].Value)*(name.Groups[3].Value.Equals("NC",StringComparison.OrdinalIgnoreCase)?1000:1);
            double coefficient=NumContains(header,"NI_Calib_factor"),rate=NormalizeSampleRate(NumContains(header,"SR"));
            double velocity=A4HeaderVelocity(header),start=A4HeaderTimeSeconds(header,"Start_point"),end=A4HeaderTimeSeconds(header,"Final_point");
            if(!Finite(value)||value<=0||!Finite(named)||named<=0||Math.Abs(value-named)>Math.Max(.01,Math.Max(value,named)*.001))
            {candidate.Reason="invalid_or_name_mismatched_calibration_value";return candidate;}
            if(!Finite(coefficient)||coefficient<=0||!Finite(rate)||rate<=0||!Finite(velocity)||velocity<80||velocity>250||!Finite(start)||!Finite(end)||start<0||end<=start)
            {candidate.Reason="invalid_calibration_header";return candidate;}
            var waveform=new List<double>();
            for(int i=13;i<lines.Length;i++)
            {
                string cell=lines[i].Split('\t')[0].Trim();if(cell.Length==0)continue;
                double sample;if(!Double.TryParse(cell,NumberStyles.Float,Inv,out sample)||!Finite(sample))
                {candidate.Reason="nonfinite_or_invalid_waveform_sample";return candidate;}
                waveform.Add(sample);
            }
            if(waveform.Count<32||start*rate>=waveform.Count||end*rate>=waveform.Count)
            {candidate.Reason="waveform_or_timing_out_of_bounds";return candidate;}
            ParsedCalibration cal=ReadCalibration(path);cal.Phase=candidate.Phase;cal.Samples=waveform.ToArray();candidate.Calibration=cal;
            candidate.Quality=A4Task24CalQuality(cal.Samples,start,end,rate);
            // Legacy composite quality is a tie-order diagnostic, not CAL
            // recognizability. In particular, a small high-SNR echo is usable.
            if(!Finite(candidate.Quality))candidate.Quality=0;
            // Test the actual downstream CAL reference before choosing the highest
            // charge file. This permits trying a lower charge valid CAL in the same
            // phase tier instead of silently losing the whole correlation reference.
            var check=new A4Context {Calibration=cal,Attenuation=true,Fs=rate};
            if(seventhCapabilities)
            {
                var capability=A7BuildCalibrationCapabilities(check);
                candidate.SeventhCapabilitiesJson=capability.DiagnosticJson;
                if(!capability.TimingValid){candidate.Reason="seventh_calibration_time_unusable: "+capability.Reference.Reason;return candidate;}
                candidate.ReferenceReliable=capability.TemplateReliable&&capability.AttenuationReliable;
                candidate.ReferenceReason=capability.Reference.Reason;
            }
            else
            {
                A4AttenuationScoringReference measured=A4BuildAttenuationScoringReference(check);
                if(!measured.Valid){candidate.Reason="calibration_structure_unusable: "+measured.Reason;return candidate;}
                if(measured.IncidentSnr<3||measured.ReflectionSnr<3)
                {candidate.Reason="guarded_calibration_snr_below_three";return candidate;}
                A6Correlation correlation=A6CalibrationCorrelation(check,measured);
                candidate.ReferenceReliable=measured.IncidentSnr>=6&&measured.ReflectionSnr>=6&&correlation.Valid&&
                    correlation.Fixed>=A6Settings.MinimumUsableCalibrationCorrelation&&correlation.Lower>0;
                candidate.ReferenceReason=candidate.ReferenceReliable?"guarded_reference_usable":
                    measured.IncidentSnr<6||measured.ReflectionSnr<6?"guarded_calibration_snr_below_six":
                    "guarded_calibration_correlation_unusable";
            }
            string date=header.ContainsKey("Date")?header["Date"]:"",time=header.ContainsKey("Time")?header["Time"]:"";
            string stamp=(date+" "+time).Replace('：',':').Trim();DateTime when;
            candidate.Time=DateTime.TryParse(stamp,Inv,DateTimeStyles.AllowWhiteSpaces|DateTimeStyles.AssumeLocal,out when)||
                DateTime.TryParse(stamp,CultureInfo.GetCultureInfo("zh-CN"),DateTimeStyles.AllowWhiteSpaces|DateTimeStyles.AssumeLocal,out when)?when.ToUniversalTime():File.GetLastWriteTimeUtc(path);
            candidate.Valid=true;candidate.Reason="valid";return candidate;
        }
        catch(IOException ex){candidate.Reason="unreadable: "+RuntimeBridge.OneLine(ex);return candidate;}
        catch(UnauthorizedAccessException ex){candidate.Reason="unreadable: "+RuntimeBridge.OneLine(ex);return candidate;}
        catch(ArgumentException ex){candidate.Reason="invalid: "+RuntimeBridge.OneLine(ex);return candidate;}
    }
    private static double A4Task24CalQuality(double[] waveform,double start,double end,double fs)
    {
        if(waveform==null||waveform.Length<64||waveform.Any(v=>!Finite(v))||!Finite(start)||!Finite(end)||
            !Finite(fs)||fs<=0||start<0||end<=start||end*fs>=waveform.Length)return 0;
        int incident=(int)Math.Round(start*fs),reflection=(int)Math.Round(end*fs);
        int radius=Math.Max(8,(int)Math.Round(fs*0.00000015));
        if(incident-radius<16||reflection+radius> waveform.Length-17||reflection-incident<=2*radius+32)return 0;
        double[] quiet=waveform.Where((v,i)=>Math.Abs(i-incident)>radius*2&&Math.Abs(i-reflection)>radius*2).ToArray();
        if(quiet.Length<32)return 0;
        double median=Median(quiet),direct=0,reflected=0;
        for(int i=incident-radius;i<=incident+radius;i++)direct=Math.Max(direct,Math.Abs(waveform[i]-median));
        for(int i=reflection-radius;i<=reflection+radius;i++)reflected=Math.Max(reflected,Math.Abs(waveform[i]-median));
        if(direct<=0||reflected<=0)return 0;
        double noise=Math.Max(1e-15,1.4826*Median(quiet.Select(v=>Math.Abs(v-median)).ToArray()));
        double loss=direct>0?100*(1-reflected/direct):double.NaN;
        double snr1=direct/noise,snr2=reflected/noise;
        // Baseline-independent pulse evidence is a prerequisite, not a score
        // which marker spacing or an apparently plausible amplitude ratio offsets.
        if(snr1<3||snr2<3)return 0;
        return Math.Round(20+25*Clamp((snr1-3)/17,0,1)+25*Clamp((snr2-3)/17,0,1)+
            15*(Finite(loss)&&loss>-20&&loss<99?1:0)+15*(reflection>incident+5?1:0),1);
    }
    private static ParsedCalibration A4ResolvePhaseCalibration(string root,string requestedPhase,double targetLength,
        out string resolvedPath,out string source,out string priority,out int candidateCount,out string diagnostic,bool seventhCapabilities=false)
    {
        string phase=A4StrictPhase(requestedPhase);
        // Enumeration failures are project access errors, not optional missing CAL.
        var candidates=Directory.EnumerateFiles(root,"*",SearchOption.TopDirectoryOnly)
            .Where(p=>String.Equals(Path.GetExtension(p),".cal",StringComparison.OrdinalIgnoreCase))
            .OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).Select(p=>A4ReadPhaseCalCandidate(p,seventhCapabilities)).ToList();
        var valid=candidates.Where(c=>c.Valid).ToList();candidateCount=valid.Count;
        var same=valid.Where(c=>c.Phase==phase&&(phase=="A"||phase=="B"||phase=="C")).ToList();
        var all=valid.Where(c=>c.Phase=="ALL").ToList();
        var other=valid.Where(c=>c.Phase!=phase&&(c.Phase=="A"||c.Phase=="B"||c.Phase=="C")&&Finite(c.Calibration.CableLength)&&c.Calibration.CableLength>0).ToList();
        A4PhaseCalCandidate chosen=null;priority="no_valid_calibration";
        if(same.Count>0){priority="same_phase_reliable_reference_then_max_valid_value";chosen=A4OrderPhaseCal(same).First();}
        else if(all.Count>0){priority="all_phase_reliable_reference_then_max_valid_value";chosen=A4OrderPhaseCal(all).First();}
        else if(Finite(targetLength)&&targetLength>0&&(phase=="A"||phase=="B"||phase=="C")&&other.Count>0)
        {
            priority="other_phase_nearest_cal_header_length_then_reliable_reference_then_max_valid_value";
            chosen=other.OrderBy(c=>Math.Abs(c.Calibration.CableLength-targetLength)).ThenByDescending(c=>c.ReferenceReliable).ThenByDescending(c=>c.Calibration.CalibrationValuePc)
                .ThenByDescending(c=>c.Quality).ThenByDescending(c=>c.Time).ThenBy(c=>c.Path,StringComparer.OrdinalIgnoreCase).First();
        }
        else if(other.Count>0)priority="missing_target_length_no_cross_phase_fallback";
        ParsedCalibration result;
        if(chosen==null)
        {
            string reason=candidates.Count==0?"automatic_calibration_not_found":priority;int ignored;
            result=A4UnavailableCalibration(requestedPhase,reason,out resolvedPath,out source,out priority,out ignored);
        }
        else{result=chosen.Calibration;resolvedPath=chosen.Path;source=seventhCapabilities?"automatic_seventh_separate_calibration_capabilities":"automatic_task24_revised_policy_local";}
        diagnostic="{\"policy\":\""+(seventhCapabilities?
            "same_phase_then_ALL_then_other_nearest_CAL_header_length; guarded_time_capability_required; template_and_attenuation_reliability_separate; reliable_reference_then_max_valid_value_within_phase_tier; quality_time_path_ties":
            "same_phase_then_ALL_then_other_nearest_CAL_header_length; guarded_structure_required; reliable_reference_then_max_valid_value_within_phase_tier; quality_time_path_ties")+
            "\",\"implementation\":\"local_policy_not_deployed_task24_dll\",\"search_scope\":\"project_top_level\",\"requested_phase\":"+Json(phase)+
            ",\"target_length_m\":"+NumberJson(targetLength)+",\"same_phase_available\":"+(same.Count>0?"true":"false")+",\"all_phase_available\":"+(all.Count>0?"true":"false")+
            ",\"selected_phase\":"+Json(chosen==null?"":chosen.Phase)+",\"selection_reason\":"+Json(priority)+",\"selected_cal_length_m\":"+NumberJson(chosen==null?double.NaN:chosen.Calibration.CableLength)+
            ",\"selected_value_pc\":"+NumberJson(chosen==null?double.NaN:chosen.Calibration.CalibrationValuePc)+",\"selected_quality\":"+NumberJson(chosen==null?double.NaN:chosen.Quality)+
            ",\"selected_reference_reliable\":"+(chosen!=null&&chosen.ReferenceReliable?"true":"false")+",\"selected_reference_reason\":"+Json(chosen==null?"no_selected_calibration":chosen.ReferenceReason)+
            ",\"selected_calibration_time_utc\":"+Json(chosen==null?"":chosen.Time.ToString("yyyy-MM-ddTHH:mm:ssZ",Inv))+
            ",\"candidates\":["+String.Join(",",candidates.Select(c=>"{\"path\":"+Json(c.Path)+",\"phase\":"+Json(c.Phase??"")+",\"valid\":"+(c.Valid?"true":"false")+",\"reason\":"+Json(c.Reason)+
            ",\"quality\":"+NumberJson(c.Quality)+",\"reference_reliable\":"+(c.ReferenceReliable?"true":"false")+",\"reference_reason\":"+Json(c.ReferenceReason)+",\"cal_length_m\":"+NumberJson(c.Calibration==null?double.NaN:c.Calibration.CableLength)+
            (seventhCapabilities?",\"seventh_capabilities\":"+c.SeventhCapabilitiesJson:"")+"}"))+"]}";
        return result;
    }
    private static IOrderedEnumerable<A4PhaseCalCandidate> A4OrderPhaseCal(IEnumerable<A4PhaseCalCandidate> candidates)
    {return candidates.OrderByDescending(c=>c.ReferenceReliable).ThenByDescending(c=>c.Calibration.CalibrationValuePc).ThenByDescending(c=>c.Quality).ThenByDescending(c=>c.Time).ThenBy(c=>c.Path,StringComparer.OrdinalIgnoreCase);}
    private static string A4GeometryDiagnostic(CableMetadata cable,ParsedLocation loc,ParsedCalibration cal,double length,double velocity)
    {
        string lengthSource=Finite(cable.CableLength)&&cable.CableLength>0?cable.SelectionSource:
            Finite(loc.CableLength)&&loc.CableLength>0?"current_phase_loc_header":"same_phase_or_ALL_cal_header";
        string velocitySource=Finite(cable.VelocityMPerUs)&&cable.VelocityMPerUs>0?cable.SelectionSource:
            Finite(loc.VelocityMPerUs)&&loc.VelocityMPerUs>0?"current_phase_loc_header":"same_phase_or_ALL_cal_header";
        return "{\"requested_phase\":"+Json(loc.Phase)+",\"cable_selected_phase\":"+Json(cable.SelectedPhase)+",\"same_phase_available\":"+(cable.SamePhaseAvailable?"true":"false")+
            ",\"cable_source\":"+Json(cable.SelectionSource)+",\"length_source\":"+Json(lengthSource)+",\"length_m\":"+NumberJson(length)+",\"velocity_source\":"+Json(velocitySource)+",\"velocity_m_per_us\":"+NumberJson(velocity)+
            ",\"duplicate_blocks_collapsed\":"+cable.DuplicateBlocksCollapsed+",\"cable_phase_lengths_m\":"+cable.PhaseLengthsJson+",\"unlabelled_ITEM_geometry_used\":false"+
            ",\"charge_compensation_model\":\"CAL_round_trip_loss_uniform_per_metre_endpoint_abs_R_assumed_1\",\"charge_compensation_exponent_divisor\":40"+
            ",\"charge_compensation_model_identified_from_data\":false,\"raw_charge_preserved\":true,\"segment_velocity_used_as_segment_loss\":false}";
    }
}
