using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // New audited facades only. Never silently remove a bad sample and move every
    // subsequent cursor left. The frozen sixth reader is intentionally unchanged.
    private static double[] AuditedNumericBody(string[] lines,int headerLines,int minimumSamples,int maximumSamples=int.MaxValue)
    {
        if(lines==null||lines.Length<headerLines+minimumSamples)
            throw new InvalidDataException("Waveform is truncated.");
        int last=lines.Length-1;
        while(last>=headerLines&&String.IsNullOrWhiteSpace(lines[last]))last--;
        var values=new List<double>();
        for(int i=headerLines;i<=last;i++)
        {
            if(values.Count>=maximumSamples)throw new InvalidDataException("Waveform sample budget exceeded.");
            string cell=lines[i].Split('\t')[0].Trim();double value;
            if(!Double.TryParse(cell,NumberStyles.Float,Inv,out value)||!Finite(value))
                throw new InvalidDataException("Invalid or missing waveform sample at file line "+(i+1)+"; sample indices were not compacted.");
            values.Add(value);
        }
        if(values.Count<minimumSamples)throw new InvalidDataException("Waveform has too few samples.");
        return values.ToArray();
    }
    private static ParsedLocation AuditedReadLocation(string path,int maximumSamples=int.MaxValue)
    {
        string[] lines=ReadAllLinesSmart(path);
        double[] body=AuditedNumericBody(lines,11,16,maximumSamples);
        // Parse the header from the same file snapshot as the checked body.
        // A second read could combine metadata and samples from different saves.
        var header=Header(lines,11);
        var parsed=new ParsedLocation {Path=path};
        ParseMeasurementName(Path.GetFileNameWithoutExtension(path),out parsed.Phase,out parsed.VoltageKv);
        if(String.IsNullOrEmpty(parsed.Phase))throw new InvalidDataException("LOC filename requires an explicit phase token.");
        parsed.PhaseDeg=FirstFinite(NumContains(header,"Phase"),NumContains(header,"Angle"));
        parsed.CableLength=FirstFinite(NumContains(header,"Cable-length"),NumContains(header,"Cable length"));
        parsed.VelocityMPerUs=A4HeaderVelocity(header);
        parsed.SampleRate=AuditedHeaderSampleRate(header,"Delta x");
        parsed.Samples=body.Select(v=>v/100000d).ToArray();
        parsed.CycleReference=Xiongxin.OWTS.Data.LocationCycleReference.ParseHeader(lines,parsed.Samples.Length,parsed.SampleRate);
        return parsed;
    }
    private static double AuditedHeaderSampleRate(Dictionary<string,string> header,string fragment)
    {
        string key=header.Keys.FirstOrDefault(k=>k.IndexOf(fragment,StringComparison.OrdinalIgnoreCase)>=0);
        if(key==null)return double.NaN;
        string label=key.Replace('（','(').Replace('）',')').Trim();
        string value=header[key].Trim();
        Match amount=Regex.Match(value,@"^(?<number>[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s*(?<unit>GHz|MHz|kHz|Hz|ns|us|µs|μs|ms|s)?\s*$",RegexOptions.IgnoreCase);
        if(!amount.Success)throw new InvalidDataException("Invalid sampling-rate header: "+key);
        double n;
        if(!Double.TryParse(amount.Groups["number"].Value,NumberStyles.Float,Inv,out n)||!Finite(n)||n<=0)
            throw new InvalidDataException("Sampling rate/interval must be positive and finite.");
        Match labelUnit=Regex.Match(label,@"\((GHz|MHz|kHz|Hz|ns|us|µs|μs|ms|s)\)",RegexOptions.IgnoreCase);
        if(Regex.IsMatch(label,@"\([^)]*\)")&&!labelUnit.Success&&!Regex.IsMatch(label,@"^SR\s*\(N\)$",RegexOptions.IgnoreCase))
            throw new InvalidDataException("Unsupported sampling-rate label unit: "+key);
        string unit=amount.Groups["unit"].Value.ToLowerInvariant(),declared=labelUnit.Groups[1].Value.ToLowerInvariant();
        if(unit.Length>0&&declared.Length>0&&unit!=declared)throw new InvalidDataException("Conflicting sampling-rate units.");
        if(unit.Length==0)unit=declared;
        // Unlabelled historic Delta-x/SR(N) records retain their known legacy
        // interval-or-Hz convention. Explicit units never undergo that heuristic.
        double rate=unit=="ghz"?n*1e9:unit=="mhz"?n*1e6:unit=="khz"?n*1e3:unit=="hz"?n:
            unit=="ns"?1e9/n:unit=="us"||unit=="µs"||unit=="μs"?1e6/n:unit=="ms"?1e3/n:unit=="s"?1/n:NormalizeSampleRate(n);
        if(!Finite(rate)||rate<=0)throw new InvalidDataException("Sampling-rate conversion overflow.");
        return rate;
    }
    private static double AuditedResolveSampleRate(params double[] decodedHz)
    {
        foreach(double rate in decodedHz)if(Finite(rate)&&rate>0)return rate;
        throw new InvalidDataException("No positive decoded sampling rate (Hz) is available.");
    }
}
