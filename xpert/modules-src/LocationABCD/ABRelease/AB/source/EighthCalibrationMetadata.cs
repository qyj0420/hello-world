using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Method 8 only: exact legacy CAL names, with units decoded by the audited
    // parser. Do not change common readers or assume LOC and CAL share a clock.
    private static double A8CalibrationSampleRate(Dictionary<string,string> header)
    {
        if(header==null)throw new InvalidDataException("CAL sampling-rate header is missing.");
        var keys=header.Keys.Where(k=>Regex.IsMatch(k.Replace('（','(').Replace('）',')').Trim(),
            @"^(?:SR|Delta[\s_-]*x)\s*(?:\([^)]*\))?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)).ToArray();
        if(keys.Length==0)throw new InvalidDataException("CAL sampling-rate metadata is missing; expected SR or Delta_x/Delta x.");
        // Even equal numeric aliases can hide differing units or stale saves.
        // Require one unambiguous declaration rather than dictionary order.
        if(keys.Length!=1)throw new InvalidDataException("CAL contains multiple sampling-rate aliases; clock is ambiguous.");
        var single=new Dictionary<string,string>{{keys[0],header[keys[0]]}};
        double rate=AuditedHeaderSampleRate(single,keys[0]);
        if(!Finite(rate)||rate<=0)throw new InvalidDataException("CAL native sampling rate is invalid.");
        return rate;
    }
}
