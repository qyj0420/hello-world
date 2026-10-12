using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    // Source-derived project metadata only, not another localization algorithm.
    // The exact source hashes and adapted methods are recorded in LocationE.
    // Direct static calls survive protection; no reflection on private AB names.
    internal static class ReviewEProjectInput
    {
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        static string Json(string value){return new JavaScriptSerializer().Serialize(value);}
        static string NumberJson(double value){return Finite(value)?value.ToString("R",Inv):"null";}
        internal static double Voltage(string path,double headerValue)
        {
            Match name=Regex.Match(Path.GetFileName(path),@"^(?:ALL|COMMON|A|B|C|L1|L2|L3)_(?<voltage>\d+(?:\.\d+)?)kV_",RegexOptions.IgnoreCase);
            double named;if(!name.Success||!Double.TryParse(name.Groups["voltage"].Value,NumberStyles.Float,Inv,out named)||!Finite(named))
                throw new InvalidDataException("E LOC requires a named voltage/file identity.");
            if(Finite(headerValue)&&Math.Abs(named-headerValue)>.011+1e-12)throw new InvalidDataException("E LOC header/file voltage identity conflicts.");
            return Finite(headerValue)?headerValue:named;
        }
        internal static void ReadGeometry(string path,string phase,double currentLocSpeed,out double length,out double speed)
        {
            CableMetadata cable=A4ReadPhaseCable(path,phase);
            length=cable.CableLength;speed=cable.VelocityMPerUs;
            // Same current LOC header fallback as AstraProjectInput; never ITEM
            // or another phase. Historical CABLE blocks often omit velocity.
            if(!Finite(speed)||speed<=0)speed=currentLocSpeed;
            if(!Finite(length)||length<1||length>100000||!Finite(speed)||speed<50||speed>300)
                throw new InvalidDataException("E/E+ 需要当前相别或明确共用 CABLE 的有效长度，以及该 CABLE 或当前 LOC 的波速；不借用其他相别。");
            double[] joints=EnsureCableEnds(cable.JointPositionsIncludingEnds,length);
            double[] velocities=A4SegmentVelocities(cable,joints,speed);
            double travel=0;
            for(int k=0;k<velocities.Length;k++) {
                if(!Finite(velocities[k])||velocities[k]<50||velocities[k]>300)throw new InvalidDataException("Invalid E segment velocity.");
                travel+=(joints[k+1]-joints[k])/velocities[k];
            }
            speed=length/travel;
            if(!Finite(speed)||speed<50||speed>300)throw new InvalidDataException("Invalid E equivalent travel-time speed.");
        }
        internal static string MatchedDem(string[] files,string identity,string phase,double voltage)
        {return A4MatchedDem(files,identity,phase,voltage);}
        internal static double ReadDemFrequency(string path)
        {
            using(var reader=OpenText(path))for(int i=0;i<16;i++) {
                string line=reader.ReadLine();if(line==null)break;
                if(line.Length>4096)throw new InvalidDataException("DEM header line exceeds its bound.");
                int split=line.IndexOf('\t');if(split<0)split=line.IndexOf(':');
                if(split>=0&&line.Substring(0,split).IndexOf("Hz",StringComparison.OrdinalIgnoreCase)>=0)
                    return ParseNumber(line.Substring(split+1));
            }
            return Double.NaN;
        }
        internal static void ValidatePhaseClock(double frequency,double rate,int count)
        {
            if(Finite(frequency)&&(frequency<0||frequency>rate/2||!Finite(360*frequency*Math.Max(0,count-1)/rate)))
                throw new ArgumentException("Oscillation frequency cannot be represented on the native LOC clock.");
        }
        static StreamReader OpenText(string path)
        {
            byte[] probe=new byte[8192];int count;
            using(var stream=File.OpenRead(path))count=stream.Read(probe,0,probe.Length);
            Encoding encoding=new UTF8Encoding(false,true);
            try{encoding.GetDecoder().GetCharCount(probe,0,count,false);}
            catch(DecoderFallbackException){encoding=Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);}
            return new StreamReader(path,encoding,true);
        }
        static string[] ReadAllLinesSmart(string path)
        {
            if(new FileInfo(path).Length>4*1024*1024)throw new InvalidDataException("CABLE metadata exceeds 4 MiB.");
            var lines=new List<string>();
            using(var reader=OpenText(path)){string line;while((line=reader.ReadLine())!=null) {
                if(line.Length>16384)throw new InvalidDataException("CABLE metadata line exceeds its bound.");lines.Add(line);
            }}
            return lines.ToArray();
        }
	private sealed class CableMetadata
	{
		internal string SelectedPhase = "";
		internal string SelectionSource = "missing_requested_phase";
		internal bool SamePhaseAvailable;
		internal int DuplicateBlocksCollapsed;
		internal string PhaseLengthsJson = "{}";
		internal double CableLength;

		internal double VelocityMPerUs;

		internal List<CableAccessory> Accessories;

		internal List<CableSegment> Segments;

		internal List<double> JointPositionsIncludingEnds;

		internal List<double> InternalJointPositions;
	}

	private sealed class CableAccessory
	{
		internal string Name;

		internal string Type;

		internal double PositionM;
	}

	private sealed class CableSegment
	{
		internal string InsulationType;

		internal double StartM;

		internal double EndM;
	}
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
    private static double[] A4SegmentVelocities(CableMetadata cable,double[] joints,double fallback)
    {
        double[] values=Enumerable.Repeat(fallback,joints.Length-1).ToArray();
        for(int i=0;i<values.Length;i++)
        {
            CableSegment segment=cable.Segments.FirstOrDefault(s=>s.StartM<=joints[i]+1e-6&&s.EndM>=joints[i+1]-1e-6);
            if(segment==null)continue;
            string text=segment.InsulationType??"";
            double v;if(A4TryVelocity(text,out v))values[i]=v;
            else if(Regex.IsMatch(text,@"velocity|波速|\bv\s*[:=]|m\s*/",RegexOptions.IgnoreCase))
                throw new InvalidDataException("Invalid or unsupported segment velocity value/unit.");
        }
        return values;
    }
	private static List<double> UniqueSorted(IEnumerable<double> values)
	{
		List<double> list = new List<double>();
		foreach (double item in from v in values.Where(Finite)
			orderby v
			select v)
		{
			if (list.Count == 0 || Math.Abs(list[list.Count - 1] - item) > 1E-06)
			{
				list.Add(item);
			}
		}
		return list;
	}
	private static bool IsJointName(string name)
	{
		if ((name ?? string.Empty).IndexOf("接头", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return (name ?? string.Empty).IndexOf("joint", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		return true;
	}
	private static double[] EnsureCableEnds(IEnumerable<double> positions, double cableLength)
	{
		List<double> source = UniqueSorted((positions ?? new double[0]).Concat(new double[2] { 0.0, cableLength }));
		source = source.Where((double x) => x >= 0.0 && x <= cableLength).ToList();
		if (source.Count < 2)
		{
			return new double[2] { 0.0, cableLength };
		}
		source[0] = 0.0;
		source[source.Count - 1] = cableLength;
		return source.ToArray();
	}
	private static void ParseMeasurementName(string name, out string phase, out double voltage)
	{
		phase = "";
		voltage = double.NaN;
		string[] array = (name ?? string.Empty).Split('_');
		if (array.Length != 0)
		{
			phase = A4StrictPhase(array[0]);
		}
		if (array.Length > 1)
		{
			voltage = ParseNumber(array[1]);
		}
	}
	private static double ParseNumber(string s)
	{
		if (string.IsNullOrWhiteSpace(s))
		{
			return double.NaN;
		}
		Match match = Regex.Match(s.Replace('：', ':'), "[-+]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[eE][-+]?\\d+)?");
		if (!match.Success || !double.TryParse(match.Value, NumberStyles.Float, Inv, out var result))
		{
			return double.NaN;
		}
		return result;
	}
    private static string A4Named(IEnumerable<string> files,string name,bool required)
    {
        string path=files.FirstOrDefault(f=>String.Equals(Path.GetFileName(f),name,StringComparison.OrdinalIgnoreCase));
        if(required&&path==null)throw new FileNotFoundException("Project top-level "+name+" was not found.");return path;
    }
    private static string A4MatchedDem(string[] files,string identity,string phase,double voltage)
    {
        string exact=A4Named(files,Path.GetFileNameWithoutExtension(identity)+".dem",false);if(exact!=null)return exact;
        DateTime when;if(!A4MeasurementTime(identity,out when))return null;
        return files.Where(f=>String.Equals(Path.GetExtension(f),".dem",StringComparison.OrdinalIgnoreCase)).Select(f=>
        {
            string p;double v;DateTime t;ParseMeasurementName(Path.GetFileNameWithoutExtension(f),out p,out v);
            double diff=A4MeasurementTime(f,out t)?Math.Abs((t-when).TotalSeconds):double.PositiveInfinity;
            return new {Path=f,Phase=p,Voltage=v,Diff=diff};
        }).Where(x=>String.Equals(x.Phase,phase,StringComparison.OrdinalIgnoreCase)&&Math.Abs(x.Voltage-voltage)<1e-6&&x.Diff<=1)
          .OrderBy(x=>x.Diff).ThenBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).Select(x=>x.Path).FirstOrDefault();
    }
    private static bool A4MeasurementTime(string path,out DateTime time)
    {
        string fileName=Path.GetFileNameWithoutExtension(path);
        Match compact=Regex.Match(fileName,@"(20\d{12})$");
        if(compact.Success&&DateTime.TryParseExact(compact.Value,"yyyyMMddHHmmss",Inv,System.Globalization.DateTimeStyles.None,out time))return true;
        time=default(DateTime);Match m=Regex.Match(Path.GetFileNameWithoutExtension(path),@"(20\d{2})[-_](\d{2})[-_](\d{2})[-_](\d{2})[':_-](\d{2})[':_-](\d{2})$");
        if(!m.Success)return false;
        try{time=new DateTime(int.Parse(m.Groups[1].Value),int.Parse(m.Groups[2].Value),int.Parse(m.Groups[3].Value),int.Parse(m.Groups[4].Value),int.Parse(m.Groups[5].Value),int.Parse(m.Groups[6].Value));return true;}catch{return false;}
    }
    }
}

