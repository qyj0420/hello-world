using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Explicit, per-instance opt-in. Never rewrite the file or silently change
    // the shared parser / frozen method 6. Sample ordinals must remain intact.
    private static ParsedLocation A8ReadLocation(string path,bool confirmedLegacy,out string velocityRule)
    {
        A8ValidateTextFileSize(path);
        string[] lines=ReadAllLinesSmart(path);
        double[] samples=AuditedNumericBody(lines,11,16,EighthMaximumSamples);
        var header=A8Header(lines,11);
        var parsed=new ParsedLocation{Path=path};
        ParseMeasurementName(Path.GetFileNameWithoutExtension(path),out parsed.Phase,out parsed.VoltageKv);
        if(String.IsNullOrEmpty(parsed.Phase))throw new InvalidDataException("LOC filename requires an explicit phase.");
        parsed.PhaseDeg=FirstFinite(NumContains(header,"Phase"),NumContains(header,"Angle"));
        parsed.CableLength=FirstFinite(NumContains(header,"Cable-length"),NumContains(header,"Cable length"));
        velocityRule="explicit_original_velocity_units_no_override";
        var named=header.Where(p=>Regex.IsMatch(p.Key,@"velocity|波速|^v(?:\s|\()",RegexOptions.IgnoreCase)).ToArray();
        bool legacy=named.Any(p=>Regex.IsMatch(p.Key.Replace('（','(').Replace('）',')'),@"\(\s*m\s*/\s*msec\s*\)",RegexOptions.IgnoreCase));
        if(legacy)
        {
            if(!confirmedLegacy)throw new InvalidDataException("Legacy LOC m/msec label needs explicit Method8ConfirmedLegacyVelocityUnits acknowledgement; no data changed.");
            if(named.Length!=1)throw new InvalidDataException("Ambiguous multiple velocity headers; explicit legacy mapping is insufficient.");
            string original=named[0].Key;
            if(!Regex.IsMatch(named[0].Value.Trim(),@"^(?:"+A4NumericToken+@")\s*$",RegexOptions.IgnoreCase))
                throw new InvalidDataException("Confirmed legacy velocity requires one finite bare numeric value; no guessed suffix units.");
            string mapped=Regex.Replace(original.Replace('（','(').Replace('）',')'),@"\(\s*m\s*/\s*msec\s*\)","(m/us)",RegexOptions.IgnoreCase);
            double velocity;
            if(!A4TryVelocity(mapped+":"+named[0].Value,out velocity))throw new InvalidDataException("Invalid explicitly confirmed legacy velocity value.");
            parsed.VelocityMPerUs=velocity;
            velocityRule="user_confirmed_legacy_LOC_m_msec_stores_m_us; original_label="+original+"; original_value="+named[0].Value;
        }
        else parsed.VelocityMPerUs=A8HeaderVelocity(header);
        if(Finite(parsed.VelocityMPerUs)&&(parsed.VelocityMPerUs<50||parsed.VelocityMPerUs>300))
            throw new InvalidDataException("LOC velocity outside 50..300 m/us; no unit or geometry guess permitted.");
        parsed.SampleRate=AuditedHeaderSampleRate(header,"Delta x");
        parsed.Samples=samples.Select(v=>v/100000d).ToArray();
        parsed.CycleReference=Xiongxin.OWTS.Data.LocationCycleReference.ParseHeader(lines,parsed.Samples.Length,parsed.SampleRate);
        return parsed;
    }

    private static Dictionary<string,string> A8Header(string[] lines,int count)
    {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(int i=0;i<Math.Min(count,lines.Length);i++)
        {
            string text=lines[i];int tab=text.IndexOf('\t'),colon=text.IndexOf(':');int split=tab>=0?tab:colon;
            if(split<0)continue;
            string key=text.Substring(0,split).Trim().TrimEnd(':','：').Replace('（','(').Replace('）',')');
            string group=Regex.IsMatch(key,@"velocity|波速|^v(?:\s|\()",RegexOptions.IgnoreCase)?"velocity":
                Regex.IsMatch(key,@"^Delta\s*x|^SR(?:\s|\()",RegexOptions.IgnoreCase)?"sampling":
                Regex.IsMatch(key,@"Cable[- ]length",RegexOptions.IgnoreCase)?"cable_length":
                Regex.IsMatch(key,@"Start_point",RegexOptions.IgnoreCase)?"cal_start":
                Regex.IsMatch(key,@"Final_point",RegexOptions.IgnoreCase)?"cal_end":null;
            if(group!=null&&!seen.Add(group))throw new InvalidDataException("Duplicate or ambiguous critical header: "+group+"; no first/last value wins.");
        }
        return Header(lines,count);
    }

    private static double A8HeaderVelocity(Dictionary<string,string> header)
    {
        var entries=header.Where(p=>Regex.IsMatch(p.Key,@"velocity|波速|^v(?:\s|\()",RegexOptions.IgnoreCase)).ToArray();
        if(entries.Length>1)throw new InvalidDataException("Ambiguous multiple velocity headers.");
        if(entries.Length==1&&!Regex.IsMatch(entries[0].Value.Trim(),@"^(?:"+A4NumericToken+@")\s*(?:"+A4VelocityUnit+@")?\s*$",RegexOptions.IgnoreCase))
            throw new InvalidDataException("Velocity value contains unsupported or trailing text.");
        return A4HeaderVelocity(header);
    }
}
