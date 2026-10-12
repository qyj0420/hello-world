using System;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Xiongxin.OWTS.Desktop
{
    // Pure, display-only adapter for the worker's documented trend payload.
    internal static class PressureFeedbackDisplay
    {
        internal static bool? DividerOnly(string configuration)
        {
            try {
                using(var reader=XmlReader.Create(new StringReader(configuration??""),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null})) {
                    var root=XDocument.Load(reader).Root;
                    var element=root==null?null:root.Element("DividerOnlyFeedback");bool value;
                    return element!=null&&Boolean.TryParse(element.Value,out value)?(bool?)value:null;
                }
            }catch(XmlException){return null;}
        }
        internal static bool TryRead(string payload,bool? dividerOnly,out double kv,out string source)
        {
            kv=Double.NaN;source="";
            var parts=(payload??"").Split(';');
            if(!dividerOnly.HasValue||(parts.Length!=11&&parts.Length!=12&&parts.Length!=13))return false;
            var values=new double[parts.Length];
            for(int i=0;i<values.Length;i++)if(!Double.TryParse(parts[i],NumberStyles.Float,CultureInfo.InvariantCulture,out values[i])||Double.IsNaN(values[i])||Double.IsInfinity(values[i]))return false;
            if(values[0]<0)return false;
            kv=dividerOnly.Value?Math.Abs(values[3]):Math.Min(Math.Max(0,values[1]),Math.Abs(values[3]));
            source=(dividerOnly.Value?"|DIV|":"min(VMON, |DIV|)")+(values.Length>11&&values[11]>=3?" / window median":" / raw diagnostic sample");
            return true;
        }
    }
}
