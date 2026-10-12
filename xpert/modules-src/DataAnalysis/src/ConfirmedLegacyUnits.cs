using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    // Only immutable input hashes explicitly confirmed by the user. No path or
    // magnitude guessing and no blanket interpretation of unknown legacy units.
    internal static class ConfirmedLegacyUnits
    {
        static readonly Lazy<HashSet<string>> Pins=new Lazy<HashSet<string>>(()=>{
            using(var stream=typeof(ConfirmedLegacyUnits).Assembly.GetManifestResourceStream("HVX.LegacyUnitPins")){
                if(stream==null)throw new InvalidDataException("缺少历史记录单位核验清单。");
                using(var reader=new StreamReader(stream))return new HashSet<string>(new JavaScriptSerializer().Deserialize<string[]>(reader.ReadToEnd()),StringComparer.OrdinalIgnoreCase);
            }
        });
        internal static bool Contains(Stream input)
        {
            using(var sha=SHA256.Create())return Pins.Value.Contains(BitConverter.ToString(sha.ComputeHash(input)).Replace("-",""));
        }
    }
}
